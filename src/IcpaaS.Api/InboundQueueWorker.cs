using IcpaaS.Core.Telephony;
using Npgsql;

namespace IcpaaS.Api;

public sealed class InboundQueueWorker(PlatformStore store,ManagedTelephonyService telephony,CapacityService capacity,ILogger<InboundQueueWorker> logger):BackgroundService
{
    sealed record Assignment(Guid Tenant,Guid Process,Guid Agent,string Extension,Guid CustomerCall,string Engine,string EngineCall,string Trunk);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {
                await Expire(ct);
                for(var i=0;i<20;i++){var assignment=await Claim(ct);if(assignment is null)break;await Ring(assignment,ct);}
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){}
            catch(Exception e){logger.LogError(e,"Inbound queue cycle failed");}
            await Task.Delay(250,ct);
        }
    }

    async Task<Assignment?> Claim(CancellationToken ct)
    {
        await using var c=await store.Open(ct);await using var tx=await c.BeginTransactionAsync(ct);
        const string callSql=@"SELECT cu.id,cu.tenant_id,cu.process_id,cu.engine_type,cu.engine_call_id,cu.selection_reason,q.strategy
FROM calls cu JOIN processes p ON p.id=cu.process_id JOIN contact_queues q ON q.id=p.queue_id
WHERE cu.direction='inbound' AND cu.state='queued' AND cu.ended_at IS NULL AND NOT (cu.metadata ? 'assignedAgentId') AND q.enabled
AND cu.selected_at+q.max_wait_seconds*interval '1 second'>now() ORDER BY cu.selected_at FOR UPDATE OF cu SKIP LOCKED LIMIT 1";
        await using var pick=new NpgsqlCommand(callSql,c,tx);await using var cr=await pick.ExecuteReaderAsync(ct);if(!await cr.ReadAsync(ct)){await cr.CloseAsync();await tx.RollbackAsync(ct);return null;}
        var call=cr.GetGuid(0);var tenant=cr.GetGuid(1);var process=cr.GetGuid(2);var engine=cr.GetString(3);var engineCall=cr.GetString(4);var trunk=cr.GetString(5);var strategy=cr.GetString(6);await cr.CloseAsync();
        var order=strategy=="fewest_calls"?"(SELECT count(*) FROM calls x WHERE x.metadata->>'agentId'=ap.user_id::text AND x.answered_at IS NOT NULL),ap.last_state_at":"ap.last_state_at,pa.priority";
        var agentSql=$@"SELECT ap.user_id,ae.extension FROM process_agents pa JOIN agent_presence ap ON ap.user_id=pa.user_id AND ap.tenant_id=$1 JOIN agent_endpoints ae ON ae.user_id=ap.user_id AND ae.tenant_id=ap.tenant_id WHERE pa.process_id=$2 AND pa.enabled AND ap.state='available' AND ae.status NOT IN('disabled','break') ORDER BY {order} FOR UPDATE OF ap SKIP LOCKED LIMIT 1";
        await using var agentPick=new NpgsqlCommand(agentSql,c,tx);Add(agentPick,tenant,process);await using var ar=await agentPick.ExecuteReaderAsync(ct);if(!await ar.ReadAsync(ct)){await ar.CloseAsync();await tx.RollbackAsync(ct);return null;}
        var agent=ar.GetGuid(0);var extension=ar.GetString(1);await ar.CloseAsync();
        await using var mark=new NpgsqlCommand("UPDATE calls SET metadata=metadata||jsonb_build_object('assignedAgentId',$2::text,'queue','ringing') WHERE id=$1;UPDATE agent_presence SET state='reserved',process_id=$3,campaign_id=NULL,last_state_at=now() WHERE tenant_id=$4 AND user_id=$2",c,tx);Add(mark,call,agent,process,tenant);await mark.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);
        return new(tenant,process,agent,extension,call,engine,engineCall,trunk);
    }

    async Task Ring(Assignment x,CancellationToken ct)
    {
        var leg=Guid.NewGuid();
        try
        {
            await capacity.Reserve(x.Tenant,leg,x.Process,ct);
            await using(var c=await store.Open(ct)){await using var insert=new NpgsqlCommand("INSERT INTO calls(id,tenant_id,process_id,engine_type,direction,to_number,state,selection_reason,metadata) VALUES($1,$2,$3,$4,'outbound',$5,'reserving',$6,jsonb_build_object('leg','agent','customerCallId',$7::text,'agentId',$8::text,'inbound',true))",c);Add(insert,leg,x.Tenant,x.Process,x.Engine,x.Extension,x.Trunk,x.CustomerCall,x.Agent);await insert.ExecuteNonQueryAsync(ct);}
            var result=await telephony.OriginateAgent(x.Tenant,leg,x.Extension,x.Trunk,x.Engine,ct);
            await using var done=await store.Open(ct);await using var update=new NpgsqlCommand("UPDATE calls SET engine_call_id=$2,state=$3 WHERE id=$1;UPDATE agent_presence SET state='ringing',last_state_at=now() WHERE tenant_id=$4 AND user_id=$5",done);Add(update,leg,result.Call.EngineCallId,result.State,x.Tenant,x.Agent);await update.ExecuteNonQueryAsync(ct);
        }
        catch(Exception e)
        {
            await capacity.Release(x.Tenant,leg,ct);await using var c=await store.Open(ct);await using var fail=new NpgsqlCommand("UPDATE calls SET state='failed',ended_at=now(),hangup_cause=$2 WHERE id=$1;UPDATE calls SET metadata=(metadata-'assignedAgentId')||jsonb_build_object('queue','waiting','lastDeliveryError',$2::text) WHERE id=$3 AND ended_at IS NULL;UPDATE agent_presence SET state='available',last_state_at=now() WHERE tenant_id=$4 AND user_id=$5",c);Add(fail,leg,e.Message[..Math.Min(500,e.Message.Length)],x.CustomerCall,x.Tenant,x.Agent);await fail.ExecuteNonQueryAsync(ct);logger.LogWarning(e,"Could not deliver inbound call {Call} to agent {Agent}",x.CustomerCall,x.Agent);
        }
    }

    async Task Expire(CancellationToken ct)
    {
        await using var c=await store.Open(ct);const string sql=@"UPDATE calls cu SET state='ending',hangup_cause='QUEUE_TIMEOUT',metadata=metadata||jsonb_build_object('queue','timeout') FROM processes p JOIN contact_queues q ON q.id=p.queue_id WHERE cu.process_id=p.id AND cu.direction='inbound' AND cu.state='queued' AND cu.ended_at IS NULL AND cu.selected_at+q.max_wait_seconds*interval '1 second'<=now() RETURNING cu.id,cu.tenant_id,cu.engine_type,cu.engine_call_id,cu.selection_reason";
        await using var q=new NpgsqlCommand(sql,c);await using var r=await q.ExecuteReaderAsync(ct);var rows=new List<(Guid,Guid,string,string,string)>();while(await r.ReadAsync(ct))rows.Add((r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetString(3),r.GetString(4)));await r.CloseAsync();
        foreach(var x in rows)try{await telephony.Control(x.Item2,new(x.Item1,x.Item3,x.Item4),"hangup",new(null,null,null,"QUEUE_TIMEOUT"),x.Item5,ct);}catch(Exception e){logger.LogWarning(e,"Could not hang up timed-out inbound call {Call}",x.Item1);await using var end=new NpgsqlCommand("UPDATE calls SET state='ended',ended_at=now() WHERE id=$1",c);end.Parameters.AddWithValue(x.Item1);await end.ExecuteNonQueryAsync(ct);await capacity.Release(x.Item2,x.Item1,ct);}
    }

    static void Add(NpgsqlCommand q,params object?[] values){foreach(var value in values)q.Parameters.AddWithValue(value??DBNull.Value);}
}
