using IcpaaS.Core.Telephony;
using Npgsql;

namespace IcpaaS.Api;

public sealed class CallEventSink(PlatformStore store,CapacityService capacity,ILogger<CallEventSink> logger)
{
    public async Task Handle(TelephonyEvent item,CancellationToken ct)
    {
        for(var attempt=0;attempt<20;attempt++)
        {
            if(await Apply(item,ct))return;
            await Task.Delay(100,ct);
        }
        logger.LogWarning("Dropping unmatched {EventType} event for platform call {CallId}",item.EventType,item.PlatformCallId);
    }

    async Task<bool> Apply(TelephonyEvent item,CancellationToken ct)
    {
        await using var connection=await store.Open(ct);
        await using var transaction=await connection.BeginTransactionAsync(ct);
        await using var find=new NpgsqlCommand("SELECT tenant_id,campaign_id,process_id,answered_at,ended_at FROM calls WHERE id=$1 FOR UPDATE",connection,transaction);
        find.Parameters.AddWithValue(item.PlatformCallId);
        await using var reader=await find.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct)){await reader.CloseAsync();await transaction.RollbackAsync(ct);return false;}
        var tenant=reader.GetGuid(0);var campaign=reader.IsDBNull(1)?(Guid?)null:reader.GetGuid(1);var process=reader.IsDBNull(2)?(Guid?)null:reader.GetGuid(2);
        var answered=!reader.IsDBNull(3);var ended=!reader.IsDBNull(4);await reader.CloseAsync();
        if(ended){await transaction.RollbackAsync(ct);return true;}

        var state=item.EventType switch{"call.ringing"=>"ringing","call.answered" or "call.bridged"=>"connected","call.held"=>"held","call.resumed"=>"connected","call.ended"=>"ended",_=>null};
        if(state is null){await transaction.RollbackAsync(ct);return true;}
        var cause=item.Attributes.TryGetValue("hangupCause",out var value)?value:null;
        await using(var update=new NpgsqlCommand("UPDATE calls SET state=$2,answered_at=CASE WHEN $2='connected' THEN coalesce(answered_at,$3) ELSE answered_at END,ended_at=CASE WHEN $2='ended' THEN $3 ELSE ended_at END,hangup_cause=CASE WHEN $2='ended' THEN $4 ELSE hangup_cause END WHERE id=$1",connection,transaction))
        {Add(update,item.PlatformCallId,state,item.OccurredAt,cause);await update.ExecuteNonQueryAsync(ct);}

        if(campaign is not null)
        {
            if(state is "ringing" or "connected")
            {
                await using var lead=new NpgsqlCommand("UPDATE campaign_contacts SET state=$3,updated_at=now() WHERE campaign_id=$1 AND last_call_id=$2 AND state IN('dialing','reserved','connected')",connection,transaction);
                Add(lead,campaign,item.PlatformCallId,state=="connected"?"connected":"dialing");await lead.ExecuteNonQueryAsync(ct);
                await SetAgent(connection,transaction,campaign.Value,item.PlatformCallId,state=="connected"?"on_call":"ringing",ct);
            }
            else if(state=="ended")
                await EndCampaignCall(connection,transaction,tenant,campaign.Value,process,item.PlatformCallId,answered,ct);
        }
        await transaction.CommitAsync(ct);
        if(state=="ended")await capacity.Release(tenant,item.PlatformCallId,ct);
        return true;
    }

    static async Task EndCampaignCall(NpgsqlConnection c,NpgsqlTransaction tx,Guid tenant,Guid campaign,Guid? process,Guid call,bool answered,CancellationToken ct)
    {
        if(answered)
        {
            await using var lead=new NpgsqlCommand("UPDATE campaign_contacts SET state='connected',updated_at=now() WHERE campaign_id=$1 AND last_call_id=$2",c,tx);Add(lead,campaign,call);await lead.ExecuteNonQueryAsync(ct);
            await SetAgent(c,tx,campaign,call,"wrap_up",ct);return;
        }
        await SetAgent(c,tx,campaign,call,"available",ct);
        await using(var lead=new NpgsqlCommand(@"UPDATE campaign_contacts cc SET state=CASE WHEN cc.attempts>=p.max_attempts THEN 'failed' ELSE 'queued' END,last_error='Call ended before answer',assigned_agent_id=NULL,updated_at=now() FROM campaigns ca JOIN processes p ON p.id=ca.process_id WHERE cc.campaign_id=$1 AND cc.last_call_id=$2 AND ca.id=cc.campaign_id RETURNING cc.contact_id,cc.attempts,p.max_attempts,p.retry_delay_minutes",c,tx))
        {
            Add(lead,campaign,call);await using var r=await lead.ExecuteReaderAsync(ct);if(await r.ReadAsync(ct)){var contact=r.GetGuid(0),exhausted=r.GetInt32(1)>=r.GetInt32(2),retry=r.GetInt32(3);await r.CloseAsync();await using var contactUpdate=new NpgsqlCommand("UPDATE contacts SET state=$2,next_callback_at=CASE WHEN $2='queued' THEN now()+($3*interval '1 minute') ELSE NULL END WHERE id=$1",c,tx);Add(contactUpdate,contact,exhausted?"exhausted":"queued",retry);await contactUpdate.ExecuteNonQueryAsync(ct);}else await r.CloseAsync();
        }
        await using var finish=new NpgsqlCommand("UPDATE campaigns ca SET state='completed',stopped_at=now(),updated_at=now() WHERE ca.id=$1 AND ca.state='running' AND NOT EXISTS(SELECT 1 FROM campaign_contacts cc WHERE cc.campaign_id=ca.id AND cc.state IN('queued','reserved','dialing','connected','callback'))",c,tx);
        finish.Parameters.AddWithValue(campaign);await finish.ExecuteNonQueryAsync(ct);
    }

    static async Task SetAgent(NpgsqlConnection c,NpgsqlTransaction tx,Guid campaign,Guid call,string state,CancellationToken ct)
    {
        await using var command=new NpgsqlCommand("UPDATE agent_presence ap SET state=$3,campaign_id=$1,last_state_at=now() FROM campaign_contacts cc WHERE cc.campaign_id=$1 AND cc.last_call_id=$2 AND cc.assigned_agent_id=ap.user_id AND ap.tenant_id=(SELECT tenant_id FROM campaigns WHERE id=$1)",c,tx);
        Add(command,campaign,call,state);await command.ExecuteNonQueryAsync(ct);
    }
    static void Add(NpgsqlCommand command,params object?[] values){for(var i=0;i<values.Length;i++)command.Parameters.AddWithValue(values[i]??DBNull.Value);}
}
