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
    sealed record Campaign(Guid Id, Guid TenantId, Guid ProcessId, string Mode, decimal Cps, int Channels,
        int MaxAttempts, int RetryMinutes, string? CallerId, int AvailableAgents, int ActiveCalls, int RecentCalls);
    sealed record Reservation(Guid ContactId, string Destination, Guid? AgentId, int Attempt);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var campaign in await RunnableCampaigns(stoppingToken))
                    await Dispatch(campaign, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Campaign execution cycle failed"); }

            await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
        }
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
 FROM campaign_contacts cc JOIN contacts x ON x.id=cc.contact_id JOIN campaigns live ON live.id=cc.campaign_id
 WHERE cc.campaign_id=$1
   AND live.state='running'
   AND ((cc.state='queued' AND (x.next_callback_at IS NULL OR x.next_callback_at<=now()))
        OR (cc.state IN('callback','failed') AND x.next_callback_at<=now())
        OR (cc.state='reserved' AND cc.updated_at<now()-interval '2 minutes'))
   AND cc.attempts<$2
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
