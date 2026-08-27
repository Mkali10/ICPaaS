using IcpaaS.Core.Configuration;
using IcpaaS.Core.Telephony;
using IcpaaS.Telephony;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Collections.Concurrent;

namespace IcpaaS.Api;

public sealed record ManagedOriginateResult(OriginateResult Result,string TrunkKey,string EngineKey);
public sealed class ManagedTelephonyService(PlatformStore store,ILoggerFactory logs,CallEventSink events):IAsyncDisposable
{
    sealed record Binding(string Trunk,string? Cli,string Engine,string Control,string? Username,string? Secret);
    sealed record AdapterEntry(ITelephonyEngine Engine,BackgroundService? Connection);
    readonly ConcurrentDictionary<string,Task<AdapterEntry>> adapters=new();
    readonly CancellationTokenSource shutdown=new();
    public async Task<ManagedOriginateResult> Originate(CallRequest request,CancellationToken ct)
    {
        var binding=await Resolve(request.TenantId,request.Destination,request.TrunkKey,null,ct);
        var adapter=(await Adapter(binding,ct)).Engine;var health=await adapter.ProbeAsync(ct);
        if(health.Availability is EngineAvailability.Disabled or EngineAvailability.Unavailable)throw new InvalidOperationException($"{binding.Engine} control endpoint unavailable: {health.Message}");
        var call=request with{CallerId=request.CallerId??binding.Cli,PreferredEngine=binding.Engine,TrunkKey=binding.Trunk};
        var result=await adapter.OriginateAsync(call,ct);return new(result,binding.Trunk,binding.Engine);
    }
    public async Task Control(Guid tenant,CallRef call,string action,CallControl body,string? trunkKey,CancellationToken ct)
    {
        var binding=await Resolve(tenant,"control",trunkKey,call.EngineKey,ct);var adapter=(await Adapter(binding,ct)).Engine;
        switch(action){
            case "answer":await adapter.AnswerAsync(call,ct);break;
            case "hangup":await adapter.HangupAsync(call,body.Reason??"normal",ct);break;
            case "transfer":await adapter.TransferAsync(call,new(body.Destination??throw new ArgumentException("Destination required")),ct);break;
            case "hold":await adapter.HoldAsync(call,body.Enabled??true,ct);break;
            case "dtmf":await adapter.SendDtmfAsync(call,body.Digits??throw new ArgumentException("Digits required"),ct);break;
            default:throw new ArgumentException("Unknown action");
        }
    }
    async Task<Binding> Resolve(Guid tenant,string destination,string? requested,string? engineKey,CancellationToken ct)
    {
        await using var c=await store.Open(ct);
        const string sql=@"WITH candidates AS (
 SELECT t.*,n.engine_type,n.control_endpoint,n.secret_ref node_secret,-10 rank,0 route_priority
 FROM trunks t JOIN telephony_nodes n ON n.id=t.node_id
 WHERE t.tenant_id=$1 AND t.enabled AND n.enabled AND $2 IS NOT NULL AND lower(t.trunk_key)=lower($2)
 UNION ALL
 SELECT t.*,n.engine_type,n.control_endpoint,n.secret_ref node_secret,0 rank,r.priority route_priority
 FROM routes r JOIN trunks t ON t.id=r.primary_trunk_id JOIN telephony_nodes n ON n.id=t.node_id
 WHERE r.tenant_id=$1 AND r.enabled AND r.route_type='outbound' AND t.enabled AND t.status='ready' AND n.enabled AND ($3='control' OR r.destination_pattern IS NULL OR $3 ~ r.destination_pattern)
 UNION ALL
 SELECT t.*,n.engine_type,n.control_endpoint,n.secret_ref node_secret,1 rank,r.priority route_priority
 FROM routes r JOIN trunks t ON t.id=r.failover_trunk_id JOIN telephony_nodes n ON n.id=t.node_id
 WHERE r.tenant_id=$1 AND r.enabled AND r.route_type='outbound' AND t.enabled AND t.status='ready' AND n.enabled AND ($3='control' OR r.destination_pattern IS NULL OR $3 ~ r.destination_pattern)
 UNION ALL
 SELECT t.*,n.engine_type,n.control_endpoint,n.secret_ref node_secret,10 rank,0 route_priority
 FROM trunks t JOIN telephony_nodes n ON n.id=t.node_id
 WHERE t.tenant_id=$1 AND t.enabled AND n.enabled
)
SELECT trunk_key,default_cli,secret_ref,username,engine_type,control_endpoint,node_secret FROM candidates
WHERE ($4 IS NULL OR engine_type=$4) ORDER BY rank,route_priority,created_at LIMIT 1";
        await using var q=new NpgsqlCommand(sql,c);q.Parameters.AddWithValue(tenant);q.Parameters.AddWithValue((object?)requested??DBNull.Value);q.Parameters.AddWithValue(destination);q.Parameters.AddWithValue((object?)engineKey??DBNull.Value);
        await using var r=await q.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))throw new InvalidOperationException("No enabled primary or failover trunk is available");
        var trunk=r.GetString(0);var cli=r.IsDBNull(1)?null:r.GetString(1);var trunkSecret=r.IsDBNull(2)?null:r.GetString(2);var username=r.IsDBNull(3)?null:r.GetString(3);var engine=r.GetString(4);var control=r.IsDBNull(5)?null:r.GetString(5);var nodeSecret=r.IsDBNull(6)?null:r.GetString(6);
        if(string.IsNullOrWhiteSpace(control))throw new InvalidOperationException($"Control endpoint is missing for {engine} node");
        return new(trunk,cli,engine,control,username,ResolveSecret(nodeSecret??trunkSecret));
    }
    Task<AdapterEntry> Adapter(Binding b,CancellationToken ct)
    {
        var key=$"{b.Engine}|{b.Control}|{b.Username}|{b.Secret?.GetHashCode()}";
        return adapters.GetOrAdd(key,_=>CreateAdapter(b,shutdown.Token));
    }
    async Task<AdapterEntry> CreateAdapter(Binding b,CancellationToken ct)
    {
        var options=b.Engine switch{
            "freeswitch"=>new PlatformOptions{Telephony=new(){FreeSwitch=new(){Enabled=true,EslEndpoint=b.Control,EslPassword=b.Secret}}},
            "asterisk"=>new PlatformOptions{Telephony=new(){Asterisk=new(){Enabled=true,AriBaseUrl=b.Control,AriUsername=b.Username,AriPassword=b.Secret,AriApp="icpaas"}}},
            "generic_sip" or "generic-sip"=>new PlatformOptions{Telephony=new(){GenericSip=new(){Enabled=true,ControlWebhook=b.Control,ApiToken=b.Secret}}},
            _=>throw new InvalidOperationException($"Managed runtime does not support engine '{b.Engine}'")};
        ITelephonyEngine engine;BackgroundService? connection=null;
        if(b.Engine=="freeswitch"){var o=Options.Create(options);connection=new FreeSwitchEslConnection(o,logs.CreateLogger<FreeSwitchEslConnection>());engine=new FreeSwitchEngine(o,(FreeSwitchEslConnection)connection);}
        else if(b.Engine=="asterisk"){var o=Options.Create(options);connection=new AsteriskAriEventConnection(o,logs.CreateLogger<AsteriskAriEventConnection>());engine=new AsteriskEngine(o,(AsteriskAriEventConnection)connection);}
        else engine=new GenericSipEngine(Options.Create(options));
        if(connection is not null){await connection.StartAsync(ct);_ = Pump(engine,shutdown.Token);}
        return new(engine,connection);
    }
    async Task Pump(ITelephonyEngine engine,CancellationToken ct)
    {
        try{await foreach(var item in engine.SubscribeAsync(ct))await events.Handle(item,ct);}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){}
        catch(Exception ex){logs.CreateLogger<ManagedTelephonyService>().LogError(ex,"Managed {Engine} event pump stopped",engine.EngineKey);}
    }
    public async ValueTask DisposeAsync(){shutdown.Cancel();foreach(var entryTask in adapters.Values){if(!entryTask.IsCompletedSuccessfully)continue;var entry=await entryTask;if(entry.Connection is not null)await entry.Connection.StopAsync(CancellationToken.None);if(entry.Engine is IDisposable disposable)disposable.Dispose();}shutdown.Dispose();}
    static string? ResolveSecret(string? reference){if(string.IsNullOrWhiteSpace(reference))return null;if(!reference.StartsWith("env:",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Secrets must use an env:VARIABLE reference");var name=reference[4..];return Environment.GetEnvironmentVariable(name)??throw new InvalidOperationException($"Secret environment variable '{name}' is not set");}
}
