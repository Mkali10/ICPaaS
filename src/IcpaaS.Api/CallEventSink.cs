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
        if(!await reader.ReadAsync(ct))
        {
            await reader.CloseAsync();await transaction.RollbackAsync(ct);
            return await RegisterInbound(item,ct);
        }
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

    async Task<bool> RegisterInbound(TelephonyEvent item,CancellationToken ct)
    {
        if(item.EventType is not ("call.created" or "call.ringing")||!item.Attributes.TryGetValue("direction",out var direction)||direction!="inbound")return false;
        if(!item.Attributes.TryGetValue("destination",out var did)||string.IsNullOrWhiteSpace(did))return false;
        var caller=item.Attributes.TryGetValue("callerId",out var source)?source:null;
        var engineCall=item.Attributes.TryGetValue("engineCallId",out var engineId)?engineId:item.PlatformCallId.ToString();
        await using var c=await store.Open(ct);
        const string sql=@"INSERT INTO calls(id,tenant_id,process_id,route_id,trunk_id,engine_type,engine_node_id,engine_call_id,direction,from_number,to_number,state,selection_reason,metadata)
SELECT $1,r.tenant_id,r.process_id,r.id,t.id,n.engine_type,n.id,$2,'inbound',$3,d.number_e164,'queued',t.trunk_key,jsonb_build_object('queue','waiting')
FROM dids d JOIN routes r ON r.did_id=d.id JOIN processes p ON p.id=r.process_id JOIN trunks t ON t.id=r.primary_trunk_id JOIN telephony_nodes n ON n.id=t.node_id
WHERE d.number_e164=$4 AND d.enabled AND r.enabled AND r.route_type='inbound' AND p.enabled AND p.process_type IN('inbound','blended') AND t.enabled AND n.enabled
ORDER BY r.priority LIMIT 1 ON CONFLICT(id) DO NOTHING RETURNING tenant_id,process_id";
        await using var insert=new NpgsqlCommand(sql,c);Add(insert,item.PlatformCallId,engineCall,caller,did);
        await using var r=await insert.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return false;var tenant=r.GetGuid(0);var process=r.GetGuid(1);await r.CloseAsync();
        try{await capacity.Reserve(tenant,item.PlatformCallId,process,ct);}
        catch(Exception e){await using var fail=new NpgsqlCommand("UPDATE calls SET state='ended',ended_at=now(),hangup_cause=$2 WHERE id=$1",c);Add(fail,item.PlatformCallId,e.Message[..Math.Min(500,e.Message.Length)]);await fail.ExecuteNonQueryAsync(ct);logger.LogWarning(e,"Inbound capacity rejected call {CallId}",item.PlatformCallId);}
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
            Add(lead,campaign,call);await using var r=await lead.ExecuteReaderAsync(ct);if(await r.ReadAsync(ct)){var contact=r.GetGuid(0);var exhausted=r.GetInt32(1)>=r.GetInt32(2);var retry=r.GetInt32(3);await r.CloseAsync();await using var contactUpdate=new NpgsqlCommand("UPDATE contacts SET state=$2,next_callback_at=CASE WHEN $2='queued' THEN now()+($3*interval '1 minute') ELSE NULL END WHERE id=$1",c,tx);Add(contactUpdate,contact,exhausted?"exhausted":"queued",retry);await contactUpdate.ExecuteNonQueryAsync(ct);}else await r.CloseAsync();
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
