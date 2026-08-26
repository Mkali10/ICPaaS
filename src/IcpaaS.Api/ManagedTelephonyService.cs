using IcpaaS.Core.Configuration;
using IcpaaS.Core.Telephony;
using IcpaaS.Telephony;
using Microsoft.Extensions.Options;
using Npgsql;

namespace IcpaaS.Api;

public sealed class ManagedTelephonyService(PlatformStore store,ILoggerFactory logs)
{
    public async Task<OriginateResult> Originate(CallRequest request,CancellationToken ct)
    {
        await using var c=await store.Open(ct);
        const string sql=@"SELECT t.trunk_key,t.default_cli,t.secret_ref,t.username,n.engine_type,n.control_endpoint,n.secret_ref node_secret
FROM trunks t JOIN telephony_nodes n ON n.id=t.node_id
WHERE t.tenant_id=$1 AND t.enabled AND n.enabled
AND ($2 IS NULL OR lower(t.trunk_key)=lower($2))
ORDER BY CASE WHEN $2 IS NOT NULL AND lower(t.trunk_key)=lower($2) THEN 0 ELSE 1 END,t.created_at LIMIT 1";
        await using var q=new NpgsqlCommand(sql,c);q.Parameters.AddWithValue(request.TenantId);q.Parameters.AddWithValue((object?)request.TrunkKey??DBNull.Value);
        await using var r=await q.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))throw new InvalidOperationException("No enabled trunk is available for this tenant");
        var trunk=r.GetString(0);var cli=request.CallerId??(r.IsDBNull(1)?null:r.GetString(1));var trunkSecret=r.IsDBNull(2)?null:r.GetString(2);var username=r.IsDBNull(3)?null:r.GetString(3);var engine=r.GetString(4);var control=r.IsDBNull(5)?null:r.GetString(5);var nodeSecret=r.IsDBNull(6)?null:r.GetString(6);await r.CloseAsync();
        if(string.IsNullOrWhiteSpace(control))throw new InvalidOperationException($"Control endpoint is missing for {engine} node");
        var secret=Resolve(nodeSecret??trunkSecret);
        var call=request with{CallerId=cli,PreferredEngine=engine,TrunkKey=trunk};
        var options=engine switch
        {
            "freeswitch"=>new PlatformOptions{Telephony=new(){FreeSwitch=new(){Enabled=true,EslEndpoint=control,EslPassword=secret}}},
            "asterisk"=>new PlatformOptions{Telephony=new(){Asterisk=new(){Enabled=true,AriBaseUrl=control,AriUsername=username,AriPassword=secret,AriApp="icpaas"}}},
            "generic_sip" or "generic-sip"=>new PlatformOptions{Telephony=new(){GenericSip=new(){Enabled=true,ControlWebhook=control,ApiToken=secret}}},
            _=>throw new InvalidOperationException($"Managed runtime does not support engine '{engine}'")
        };
        ITelephonyEngine adapter;
        if(engine=="freeswitch"){var o=Options.Create(options);adapter=new FreeSwitchEngine(o,new FreeSwitchEslConnection(o,logs.CreateLogger<FreeSwitchEslConnection>()));}
        else if(engine=="asterisk"){var o=Options.Create(options);adapter=new AsteriskEngine(o,new AsteriskAriEventConnection(o,logs.CreateLogger<AsteriskAriEventConnection>()));}
        else adapter=new GenericSipEngine(Options.Create(options));
        var health=await adapter.ProbeAsync(ct);if(health.Availability is EngineAvailability.Disabled or EngineAvailability.Unavailable)throw new InvalidOperationException($"{engine} control endpoint unavailable: {health.Message}");
        return await adapter.OriginateAsync(call,ct);
    }
    static string? Resolve(string? reference)
    {
        if(string.IsNullOrWhiteSpace(reference))return null;
        if(!reference.StartsWith("env:",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Secrets must use an env:VARIABLE reference");
        var name=reference[4..];return Environment.GetEnvironmentVariable(name)??throw new InvalidOperationException($"Secret environment variable '{name}' is not set");
    }
}