using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Npgsql;

namespace IcpaaS.Api;

public sealed class PluginDeliveryWorker(PlatformStore store,IConfiguration configuration,ILogger<PluginDeliveryWorker> logger,IHttpClientFactory clients):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try{if(!await Process(ct))await Task.Delay(1000,ct);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){}
            catch(Exception ex){logger.LogError(ex,"Plugin delivery worker iteration failed");await Task.Delay(3000,ct);}
        }
    }

    async Task<bool> Process(CancellationToken ct)
    {
        await using var connection=await store.Open(ct);await using var transaction=await connection.BeginTransactionAsync(ct);
        await using var pick=new NpgsqlCommand(@"SELECT d.id,d.plugin_id,d.event_type,d.payload::text,p.endpoint_url,p.secret_ref
FROM plugin_deliveries d JOIN plugins p ON p.id=d.plugin_id
WHERE d.state IN('queued','failed') AND d.available_at<=now() AND p.status<>'revoked'
ORDER BY d.created_at FOR UPDATE OF d SKIP LOCKED LIMIT 1",connection,transaction);
        await using var reader=await pick.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct)){await reader.CloseAsync();await transaction.RollbackAsync(ct);return false;}
        var id=reader.GetGuid(0);var pluginId=reader.GetGuid(1);var eventType=reader.GetString(2);var payload=reader.GetString(3);var endpoint=reader.IsDBNull(4)?null:reader.GetString(4);var secretRef=reader.IsDBNull(5)?null:reader.GetString(5);await reader.CloseAsync();
        await using(var claim=new NpgsqlCommand("UPDATE plugin_deliveries SET state='processing',attempts=attempts+1,updated_at=now() WHERE id=$1",connection,transaction)){claim.Parameters.AddWithValue(id);await claim.ExecuteNonQueryAsync(ct);}await transaction.CommitAsync(ct);
        try
        {
            if(endpoint is null)throw new InvalidOperationException("Plugin endpoint missing");
            await Validate(endpoint,ct);
            using var request=new HttpRequestMessage(HttpMethod.Post,endpoint){Content=new StringContent(payload,Encoding.UTF8,"application/json")};
            request.Headers.Add("X-ICPaaS-Event",eventType);request.Headers.Add("X-ICPaaS-Delivery",id.ToString());
            if(Resolve(secretRef) is { } secret)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",secret);
            using var response=await clients.CreateClient("plugins").SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
            var body=await response.Content.ReadAsStringAsync(ct);var excerpt=body[..Math.Min(1000,body.Length)];
            if(!response.IsSuccessStatusCode)throw new HttpRequestException($"Plugin returned {(int)response.StatusCode}: {excerpt}");
            await using var done=await store.Open(ct);await using var command=new NpgsqlCommand("UPDATE plugin_deliveries SET state='delivered',response_code=$2,response_excerpt=$3,last_error=NULL,delivered_at=now(),updated_at=now() WHERE id=$1;UPDATE plugins SET status='ready',last_tested_at=now(),last_error=NULL,updated_at=now() WHERE id=$4",done);Add(command,id,(int)response.StatusCode,excerpt,pluginId);await command.ExecuteNonQueryAsync(ct);
        }
        catch(Exception ex)
        {
            await using var failed=await store.Open(ct);await using var command=new NpgsqlCommand("UPDATE plugin_deliveries SET state=CASE WHEN attempts>=8 THEN 'dead_letter' ELSE 'failed' END,last_error=$2,available_at=now()+make_interval(secs=>LEAST(900,5*power(2,LEAST(attempts,8)))::int),updated_at=now() WHERE id=$1;UPDATE plugins SET status='degraded',last_error=$2,updated_at=now() WHERE id=$3",failed);Add(command,id,ex.Message[..Math.Min(1000,ex.Message.Length)],pluginId);await command.ExecuteNonQueryAsync(ct);
        }
        return true;
    }

    async Task Validate(string endpoint,CancellationToken ct)
    {
        var uri=new Uri(endpoint);if(uri.Scheme!="https"&&uri.Scheme!="http")throw new InvalidOperationException("Unsupported plugin protocol");
        var allowPrivate=configuration.GetValue("ICPaaS:Integrations:AllowPrivateEndpoints",false);
        foreach(var ip in await Dns.GetHostAddressesAsync(uri.Host,ct))if(!allowPrivate&&(IPAddress.IsLoopback(ip)||Private(ip)))throw new InvalidOperationException("Private plugin endpoint is disabled");
    }
    static bool Private(IPAddress ip){if(ip.AddressFamily==AddressFamily.InterNetwork){var b=ip.GetAddressBytes();return b[0]==10||b[0]==127||(b[0]==172&&b[1]>=16&&b[1]<=31)||(b[0]==192&&b[1]==168)||(b[0]==169&&b[1]==254);}return ip.IsIPv6LinkLocal||ip.IsIPv6SiteLocal;}
    static string? Resolve(string? reference){if(string.IsNullOrWhiteSpace(reference))return null;if(reference.StartsWith("env:",StringComparison.OrdinalIgnoreCase))return Environment.GetEnvironmentVariable(reference[4..])??throw new InvalidOperationException("Plugin environment secret not found");throw new InvalidOperationException("Secret provider is unavailable in this deployment");}
    static void Add(NpgsqlCommand command,params object?[] values){foreach(var value in values)command.Parameters.AddWithValue(value??DBNull.Value);}
}
