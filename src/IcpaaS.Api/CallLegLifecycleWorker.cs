using IcpaaS.Core.Telephony;
using Npgsql;

namespace IcpaaS.Api;

public sealed class CallLegLifecycleWorker(PlatformStore store,ManagedTelephonyService telephony,CapacityService capacity,ILogger<CallLegLifecycleWorker> logger):BackgroundService
{
    sealed record Leg(Guid Id,Guid Tenant,string Engine,string EngineCall,string Trunk);
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try{await RequeueRejectedInbound(ct);await CloseAgentLegs(ct);await CloseCustomerLegs(ct);await FinishInboundWrapUp(ct);}
            catch(OperationCanceledException) when(ct.IsCancellationRequested){}
            catch(Exception e){logger.LogError(e,"Paired call lifecycle cycle failed");}
            await Task.Delay(500,ct);
        }
    }

    async Task RequeueRejectedInbound(CancellationToken ct)
    {
        await using var c=await store.Open(ct);
        const string sql=@"WITH rejected AS(SELECT al.id agent_leg,(al.metadata->>'customerCallId')::uuid customer_call,(al.metadata->>'agentId')::uuid agent_id,al.tenant_id FROM calls al JOIN calls cu ON cu.id=(al.metadata->>'customerCallId')::uuid WHERE al.metadata->>'leg'='agent' AND coalesce((al.metadata->>'inbound')::boolean,false) AND al.ended_at IS NOT NULL AND al.answered_at IS NULL AND cu.ended_at IS NULL AND cu.metadata->>'queue' IN('ringing','assigned') FOR UPDATE OF cu SKIP LOCKED) UPDATE calls cu SET state='queued',metadata=(metadata-'assignedAgentId')||jsonb_build_object('queue','waiting','lastAgentResult','not_answered') FROM rejected r WHERE cu.id=r.customer_call RETURNING r.tenant_id,r.agent_id";
        await using var q=new NpgsqlCommand(sql,c);await using var r=await q.ExecuteReaderAsync(ct);var rows=new List<(Guid,Guid)>();while(await r.ReadAsync(ct))rows.Add((r.GetGuid(0),r.GetGuid(1)));await r.CloseAsync();foreach(var x in rows){await using var available=new NpgsqlCommand("UPDATE agent_presence SET state='available',last_state_at=now() WHERE tenant_id=$1 AND user_id=$2 AND state IN('reserved','ringing')",c);Add(available,x.Item1,x.Item2);await available.ExecuteNonQueryAsync(ct);}
    }

    async Task CloseAgentLegs(CancellationToken ct)
    {
        await using var c=await store.Open(ct);
        const string sql=@"UPDATE calls al SET state='ending',hangup_cause=coalesce(al.hangup_cause,'PEER_ENDED') FROM calls cu WHERE al.metadata->>'leg'='agent' AND cu.id=(al.metadata->>'customerCallId')::uuid AND cu.ended_at IS NOT NULL AND al.ended_at IS NULL AND al.state<>'ending' RETURNING al.id,al.tenant_id,al.engine_type,al.engine_call_id,al.selection_reason,(al.metadata->>'agentId')::uuid";
        await using var q=new NpgsqlCommand(sql,c);await using var r=await q.ExecuteReaderAsync(ct);var rows=new List<(Leg,Guid)>();while(await r.ReadAsync(ct))rows.Add((new(r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetString(3),r.GetString(4)),r.GetGuid(5)));await r.CloseAsync();
        foreach(var x in rows){await SetPresence(c,x.Item1.Tenant,x.Item2,"wrap_up",ct);await Hangup(x.Item1,c,ct);}
    }

    async Task CloseCustomerLegs(CancellationToken ct)
    {
        await using var c=await store.Open(ct);
        const string sql=@"UPDATE calls cu SET state='ending',hangup_cause=coalesce(cu.hangup_cause,'AGENT_ENDED') FROM calls al WHERE al.metadata->>'leg'='agent' AND cu.id=(al.metadata->>'customerCallId')::uuid AND al.ended_at IS NOT NULL AND (al.answered_at IS NOT NULL OR NOT coalesce((al.metadata->>'inbound')::boolean,false)) AND cu.ended_at IS NULL AND cu.state<>'ending' RETURNING cu.id,cu.tenant_id,cu.engine_type,cu.engine_call_id,cu.selection_reason,(al.metadata->>'agentId')::uuid";
        await using var q=new NpgsqlCommand(sql,c);await using var r=await q.ExecuteReaderAsync(ct);var rows=new List<(Leg,Guid)>();while(await r.ReadAsync(ct))rows.Add((new(r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetString(3),r.GetString(4)),r.GetGuid(5)));await r.CloseAsync();
        foreach(var x in rows){await SetPresence(c,x.Item1.Tenant,x.Item2,"wrap_up",ct);await Hangup(x.Item1,c,ct);}
    }

    async Task FinishInboundWrapUp(CancellationToken ct)
    {
        await using var c=await store.Open(ct);await using var q=new NpgsqlCommand(@"UPDATE agent_presence ap SET state='available',campaign_id=NULL,last_state_at=now() FROM processes p JOIN contact_queues q ON q.id=p.queue_id WHERE ap.process_id=p.id AND ap.campaign_id IS NULL AND ap.state='wrap_up' AND ap.last_state_at+q.wrap_up_seconds*interval '1 second'<=now()",c);await q.ExecuteNonQueryAsync(ct);
    }

    async Task Hangup(Leg x,NpgsqlConnection c,CancellationToken ct)
    {
        try{await telephony.Control(x.Tenant,new(x.Id,x.Engine,x.EngineCall),"hangup",new(null,null,null,"PEER_ENDED"),x.Trunk,ct);}
        catch(Exception e){logger.LogWarning(e,"Could not close paired call leg {Call}",x.Id);await using var end=new NpgsqlCommand("UPDATE calls SET state='ended',ended_at=now() WHERE id=$1",c);end.Parameters.AddWithValue(x.Id);await end.ExecuteNonQueryAsync(ct);await capacity.Release(x.Tenant,x.Id,ct);}
    }
    static async Task SetPresence(NpgsqlConnection c,Guid tenant,Guid agent,string state,CancellationToken ct){await using var q=new NpgsqlCommand("UPDATE agent_presence SET state=$3,last_state_at=now() WHERE tenant_id=$1 AND user_id=$2",c);Add(q,tenant,agent,state);await q.ExecuteNonQueryAsync(ct);}
    static void Add(NpgsqlCommand q,params object?[] values){foreach(var value in values)q.Parameters.AddWithValue(value??DBNull.Value);}
}
