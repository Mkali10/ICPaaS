using IcpaaS.Core.Telephony;
using Npgsql;

namespace IcpaaS.Api;

/// <summary>Reserves and originates outbound campaign contacts without allowing two workers to pick the same lead.</summary>
public sealed class CampaignExecutionWorker(
    PlatformStore store,
    ManagedTelephonyService telephony,
    CapacityService capacity,
    ILogger<CampaignExecutionWorker> logger) : BackgroundService
{
    DateTimeOffset nextComplianceSweep=DateTimeOffset.MinValue;
    sealed record Campaign(Guid Id, Guid TenantId, Guid ProcessId, string Mode, decimal Cps, int Channels,
        int MaxAttempts, int RetryMinutes, string? CallerId, int AvailableAgents, int ActiveCalls, int RecentCalls);
    sealed record Reservation(Guid ContactId, string Destination, Guid? AgentId, int Attempt);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReleaseWrapUpAgents(stoppingToken);
                if(DateTimeOffset.UtcNow>=nextComplianceSweep){await ApplyComplianceBlocks(stoppingToken);nextComplianceSweep=DateTimeOffset.UtcNow.AddSeconds(5);}
                foreach (var campaign in await RunnableCampaigns(stoppingToken))
                    await Dispatch(campaign, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Campaign execution cycle failed"); }

            await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
        }
    }

    async Task ReleaseWrapUpAgents(CancellationToken ct)
    {
        await using var connection=await store.Open(ct);
        await using var command=new NpgsqlCommand(@"UPDATE agent_presence ap SET state='available',last_state_at=now()
FROM campaigns ca JOIN processes p ON p.id=ca.process_id LEFT JOIN contact_queues q ON q.id=p.queue_id
WHERE ap.campaign_id=ca.id AND ap.state='wrap_up'
  AND ap.last_state_at + (coalesce(q.wrap_up_seconds,20)*interval '1 second')<=now()
  AND NOT EXISTS(SELECT 1 FROM campaign_contacts cc WHERE cc.campaign_id=ca.id AND cc.assigned_agent_id=ap.user_id AND cc.state='connected')",connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    async Task<List<Campaign>> RunnableCampaigns(CancellationToken ct)
    {
        await using var connection = await store.Open(ct);
        const string sql = @"
SELECT c.id,c.tenant_id,c.process_id,c.dialer_mode,
       least(coalesce(c.max_cps,ts.cps_limit),coalesce(p.max_cps,ts.cps_limit),ts.cps_limit) cps,
       least(coalesce(c.max_channels,ts.channel_limit),ts.channel_limit) channels,
       p.max_attempts,p.retry_delay_minutes,d.number_e164,
       (SELECT count(*) FROM agent_presence ap JOIN process_agents pa ON pa.user_id=ap.user_id AND pa.process_id=p.id AND pa.enabled
         WHERE ap.tenant_id=c.tenant_id AND ap.state='available') available_agents,
       (SELECT count(*) FROM calls ca WHERE ca.campaign_id=c.id AND ca.ended_at IS NULL) active_calls,
       (SELECT count(*) FROM calls ca WHERE ca.campaign_id=c.id AND ca.created_at>=now()-interval '1 second') recent_calls
FROM campaigns c
JOIN processes p ON p.id=c.process_id AND p.enabled
JOIN tenant_settings ts ON ts.tenant_id=c.tenant_id
LEFT JOIN dids d ON d.id=p.did_id AND d.enabled
WHERE c.state='running' AND (c.scheduled_at IS NULL OR c.scheduled_at<=now())
  AND c.dialer_mode IN('progressive','predictive','agentless')
  AND extract(isodow FROM now() AT TIME ZONE p.calling_timezone)::smallint=ANY(p.calling_days)
  AND (CASE WHEN p.calling_start<p.calling_end
       THEN (now() AT TIME ZONE p.calling_timezone)::time>=p.calling_start AND (now() AT TIME ZONE p.calling_timezone)::time<p.calling_end
       ELSE (now() AT TIME ZONE p.calling_timezone)::time>=p.calling_start OR (now() AT TIME ZONE p.calling_timezone)::time<p.calling_end END)
ORDER BY c.started_at NULLS LAST,c.created_at";
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<Campaign>();
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetGuid(0),reader.GetGuid(1),reader.GetGuid(2),reader.GetString(3),reader.GetDecimal(4),
                reader.GetInt32(5),reader.GetInt32(6),reader.GetInt32(7),reader.IsDBNull(8)?null:reader.GetString(8),
                Convert.ToInt32(reader.GetInt64(9)),Convert.ToInt32(reader.GetInt64(10)),Convert.ToInt32(reader.GetInt64(11))));
        return result;
    }

    async Task Dispatch(Campaign campaign, CancellationToken ct)
    {
        var agentLimit = campaign.Mode switch
        {
            "progressive" => campaign.AvailableAgents,
            "predictive" => campaign.AvailableAgents * 2,
            _ => campaign.Channels
        };
        var slots = Math.Min(campaign.Channels - campaign.ActiveCalls, agentLimit - campaign.ActiveCalls);
        var cpsSlots = Math.Max(0, (int)Math.Ceiling(campaign.Cps) - campaign.RecentCalls);
        slots = Math.Min(slots, cpsSlots);
        for (var i = 0; i < slots; i++)
        {
            var reservation = await Reserve(campaign, ct);
            if (reservation is null) break;
            await Originate(campaign, reservation, ct);
        }
    }

    async Task<Reservation?> Reserve(Campaign campaign, CancellationToken ct)
    {
        await using var connection = await store.Open(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        const string sql = @"
WITH candidate AS (
 SELECT cc.contact_id
 FROM campaign_contacts cc JOIN contacts x ON x.id=cc.contact_id JOIN campaigns live ON live.id=cc.campaign_id JOIN processes proc ON proc.id=live.process_id
 WHERE cc.campaign_id=$1
   AND live.state='running'
   AND ((cc.state='queued' AND (x.next_callback_at IS NULL OR x.next_callback_at<=now()))
        OR (cc.state IN('callback','failed') AND x.next_callback_at<=now())
        OR (cc.state='reserved' AND cc.updated_at<now()-interval '2 minutes'))
   AND cc.attempts<$2
   AND (NOT proc.require_consent OR x.consent_status='granted')
   AND x.consent_status<>'revoked'
   AND NOT EXISTS(SELECT 1 FROM tenant_dnc d WHERE d.tenant_id=live.tenant_id AND d.phone_normalized=regexp_replace(x.phone_number,'[^0-9]','','g') AND (d.expires_at IS NULL OR d.expires_at>now()))
 ORDER BY CASE WHEN cc.state='callback' THEN 0 ELSE 1 END,x.next_callback_at NULLS LAST,cc.queued_at
 FOR UPDATE OF cc SKIP LOCKED LIMIT 1
), available_agent AS (
 SELECT ap.user_id FROM agent_presence ap JOIN process_agents pa ON pa.user_id=ap.user_id
 WHERE ap.tenant_id=$3 AND pa.process_id=$4 AND pa.enabled AND ap.state='available'
 ORDER BY pa.priority,ap.last_state_at FOR UPDATE OF ap SKIP LOCKED LIMIT 1
), claimed AS (
 UPDATE campaign_contacts cc SET state='reserved',attempts=cc.attempts+1,
   assigned_agent_id=CASE WHEN $5='agentless' THEN NULL ELSE (SELECT user_id FROM available_agent) END,
   last_error=NULL,updated_at=now()
 FROM candidate WHERE cc.campaign_id=$1 AND cc.contact_id=candidate.contact_id
   AND ($5='agentless' OR EXISTS(SELECT 1 FROM available_agent))
 RETURNING cc.contact_id,cc.assigned_agent_id,cc.attempts
)
SELECT cl.contact_id,x.phone_number,cl.assigned_agent_id,cl.attempts FROM claimed cl JOIN contacts x ON x.id=cl.contact_id";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        Add(command,campaign.Id,campaign.MaxAttempts,campaign.TenantId,campaign.ProcessId,campaign.Mode);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) { await transaction.RollbackAsync(ct); return null; }
        var result = new Reservation(reader.GetGuid(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetGuid(2),reader.GetInt32(3));
        await reader.CloseAsync();
        if (result.AgentId is { } agent)
        {
            await using var presence = new NpgsqlCommand("UPDATE agent_presence SET state='reserved',campaign_id=$2,process_id=$3,last_state_at=now() WHERE tenant_id=$1 AND user_id=$4 AND state='available'",connection,transaction);
            Add(presence,campaign.TenantId,campaign.Id,campaign.ProcessId,agent);
            if (await presence.ExecuteNonQueryAsync(ct) != 1) { await transaction.RollbackAsync(ct); return null; }
        }
        await transaction.CommitAsync(ct);
        return result;
    }

    async Task ApplyComplianceBlocks(CancellationToken ct)
    {
        await using var connection=await store.Open(ct);await using var transaction=await connection.BeginTransactionAsync(ct);
        const string candidate=@"CREATE TEMP TABLE compliance_blocked ON COMMIT DROP AS
SELECT cc.campaign_id,cc.contact_id,ca.tenant_id,
 CASE WHEN EXISTS(SELECT 1 FROM tenant_dnc d WHERE d.tenant_id=ca.tenant_id AND d.phone_normalized=regexp_replace(x.phone_number,'[^0-9]','','g') AND (d.expires_at IS NULL OR d.expires_at>now())) THEN 'dnc'
      WHEN x.consent_status='revoked' THEN 'consent_revoked'
      ELSE 'consent_missing' END rule
FROM campaign_contacts cc JOIN campaigns ca ON ca.id=cc.campaign_id JOIN processes p ON p.id=ca.process_id JOIN contacts x ON x.id=cc.contact_id
WHERE ca.state='running' AND cc.state IN('queued','callback','reserved') AND
 (EXISTS(SELECT 1 FROM tenant_dnc d WHERE d.tenant_id=ca.tenant_id AND d.phone_normalized=regexp_replace(x.phone_number,'[^0-9]','','g') AND (d.expires_at IS NULL OR d.expires_at>now()))
  OR x.consent_status='revoked' OR (p.require_consent AND x.consent_status<>'granted'))";
        await using(var make=new NpgsqlCommand(candidate,connection,transaction)){await make.ExecuteNonQueryAsync(ct);}
        await using(var audit=new NpgsqlCommand("INSERT INTO dialing_compliance_events(tenant_id,campaign_id,contact_id,rule,decision,detail) SELECT tenant_id,campaign_id,contact_id,rule,'blocked',jsonb_build_object('source','campaign_worker') FROM compliance_blocked",connection,transaction)){await audit.ExecuteNonQueryAsync(ct);}
        await using(var release=new NpgsqlCommand("UPDATE agent_presence ap SET state='available',last_state_at=now() FROM campaign_contacts cc JOIN compliance_blocked b ON b.campaign_id=cc.campaign_id AND b.contact_id=cc.contact_id WHERE cc.assigned_agent_id=ap.user_id AND ap.tenant_id=b.tenant_id AND ap.state='reserved'",connection,transaction)){await release.ExecuteNonQueryAsync(ct);}
        await using(var block=new NpgsqlCommand("UPDATE campaign_contacts cc SET state='skipped',assigned_agent_id=NULL,last_error='Compliance block: '||b.rule,updated_at=now() FROM compliance_blocked b WHERE cc.campaign_id=b.campaign_id AND cc.contact_id=b.contact_id",connection,transaction)){await block.ExecuteNonQueryAsync(ct);}
        await transaction.CommitAsync(ct);
    }

    async Task Originate(Campaign campaign, Reservation reservation, CancellationToken ct)
    {
        var callId = Guid.NewGuid();
        try
        {
            if (!await IsRunning(campaign.Id,ct)) { await ReturnReservation(campaign,reservation,ct); return; }
            await capacity.Reserve(campaign.TenantId,callId,campaign.ProcessId,ct);
            var request = new CallRequest(callId,campaign.TenantId,reservation.Destination,campaign.CallerId,
                campaign.Id.ToString(),campaign.ProcessId.ToString(),null,null,campaign.Cps);
            var selected = await telephony.Originate(request,ct);
            await using var connection = await store.Open(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using (var call = new NpgsqlCommand(@"INSERT INTO calls(id,tenant_id,process_id,campaign_id,engine_type,engine_call_id,direction,from_number,to_number,state,selection_reason,metadata)
VALUES($1,$2,$3,$4,$5,$6,'outbound',$7,$8,$9,$10,jsonb_build_object('contactId',$11::text,'agentId',$12::text))",connection,transaction))
            {
                Add(call,callId,campaign.TenantId,campaign.ProcessId,campaign.Id,selected.EngineKey,selected.Result.Call.EngineCallId,
                    campaign.CallerId,reservation.Destination,selected.Result.State,selected.TrunkKey,reservation.ContactId,reservation.AgentId);
                await call.ExecuteNonQueryAsync(ct);
            }
            await using (var update = new NpgsqlCommand("UPDATE campaign_contacts SET state='dialing',last_call_id=$3,updated_at=now() WHERE campaign_id=$1 AND contact_id=$2 AND state='reserved'; UPDATE contacts SET state='dialing',attempt_count=attempt_count+1,last_called_at=now() WHERE id=$2",connection,transaction))
            { Add(update,campaign.Id,reservation.ContactId,callId); await update.ExecuteNonQueryAsync(ct); }
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await capacity.Release(campaign.TenantId,callId,ct);
            await Fail(campaign,reservation,ex.Message,ct);
            logger.LogWarning(ex,"Campaign {CampaignId} could not originate contact {ContactId}",campaign.Id,reservation.ContactId);
        }
    }

    async Task<bool> IsRunning(Guid campaignId,CancellationToken ct)
    {
        await using var connection=await store.Open(ct);
        await using var command=new NpgsqlCommand("SELECT state='running' FROM campaigns WHERE id=$1",connection);
        command.Parameters.AddWithValue(campaignId);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    async Task ReturnReservation(Campaign campaign,Reservation reservation,CancellationToken ct)
    {
        await using var connection=await store.Open(ct);
        await using var transaction=await connection.BeginTransactionAsync(ct);
        await using(var lead=new NpgsqlCommand("UPDATE campaign_contacts SET state='queued',attempts=greatest(attempts-1,0),assigned_agent_id=NULL,updated_at=now() WHERE campaign_id=$1 AND contact_id=$2 AND state='reserved'",connection,transaction))
        { Add(lead,campaign.Id,reservation.ContactId);await lead.ExecuteNonQueryAsync(ct); }
        if(reservation.AgentId is { } agent)
        {
            await using var presence=new NpgsqlCommand("UPDATE agent_presence SET state='available',last_state_at=now() WHERE tenant_id=$1 AND user_id=$2 AND state='reserved'",connection,transaction);
            Add(presence,campaign.TenantId,agent);await presence.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    async Task Fail(Campaign campaign, Reservation reservation, string error, CancellationToken ct)
    {
        await using var connection = await store.Open(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var exhausted = reservation.Attempt >= campaign.MaxAttempts;
        await using (var command = new NpgsqlCommand("UPDATE campaign_contacts SET state=$3,last_error=$4,assigned_agent_id=NULL,updated_at=now() WHERE campaign_id=$1 AND contact_id=$2; UPDATE contacts SET state=$5,next_callback_at=CASE WHEN $5='queued' THEN now()+($6*interval '1 minute') ELSE NULL END WHERE id=$2",connection,transaction))
        { Add(command,campaign.Id,reservation.ContactId,exhausted?"failed":"queued",error[..Math.Min(error.Length,1000)],exhausted?"exhausted":"queued",campaign.RetryMinutes); await command.ExecuteNonQueryAsync(ct); }
        if (reservation.AgentId is { } agent)
        {
            await using var presence = new NpgsqlCommand("UPDATE agent_presence SET state='available',last_state_at=now() WHERE tenant_id=$1 AND user_id=$2 AND state='reserved'",connection,transaction);
            Add(presence,campaign.TenantId,agent);await presence.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    static void Add(NpgsqlCommand command, params object?[] values)
    { for (var i=0;i<values.Length;i++) command.Parameters.AddWithValue(values[i]??DBNull.Value); }
}
