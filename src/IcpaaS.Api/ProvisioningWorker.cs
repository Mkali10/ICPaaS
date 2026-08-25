using System.Net;using System.Net.Sockets;using System.Text;using Npgsql;
namespace IcpaaS.Api;
public sealed class ProvisioningWorker(PlatformStore store,ILogger<ProvisioningWorker> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try{if(!await Process(stoppingToken))await Task.Delay(1000,stoppingToken);}catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){}catch(Exception e){log.LogError(e,"Provisioning worker iteration failed");await Task.Delay(3000,stoppingToken);}
        }
    }
    async Task<bool> Process(CancellationToken ct)
    {
        await using var c=await store.Open(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await using var pick=new NpgsqlCommand("SELECT j.id,j.node_id,j.resource_id,t.remote_endpoint,t.transport FROM provisioning_jobs j JOIN trunks t ON t.id=j.resource_id WHERE j.resource_type='trunk' AND j.state IN('queued','failed') AND j.available_at<=now() ORDER BY j.created_at FOR UPDATE OF j SKIP LOCKED LIMIT 1",c,tx);
        await using var r=await pick.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct)){await tx.RollbackAsync(ct);return false;}var id=r.GetGuid(0);var node=r.GetGuid(1);var trunk=r.GetGuid(2);var endpoint=r.GetString(3);var transport=r.GetString(4);await r.CloseAsync();
        await using(var claim=new NpgsqlCommand("UPDATE provisioning_jobs SET state='processing',attempts=attempts+1,updated_at=now() WHERE id=$1",c,tx)){claim.Parameters.AddWithValue(id);await claim.ExecuteNonQueryAsync(ct);}await tx.CommitAsync(ct);
        try{await Probe(endpoint,transport,ct);await using var done=await store.Open(ct);await using var q=new NpgsqlCommand("BEGIN;UPDATE provisioning_jobs SET state='applied',last_error=NULL,updated_at=now() WHERE id=$1;UPDATE trunks SET status='ready',updated_at=now() WHERE id=$2;UPDATE telephony_nodes SET status='ready',last_seen_at=now(),updated_at=now() WHERE id=$3;COMMIT;",done);q.Parameters.AddWithValue(id);q.Parameters.AddWithValue(trunk);q.Parameters.AddWithValue(node);await q.ExecuteNonQueryAsync(ct);}
        catch(Exception e){await using var failed=await store.Open(ct);await using var q=new NpgsqlCommand("UPDATE provisioning_jobs SET state='failed',last_error=$2,available_at=now()+make_interval(secs=>LEAST(300,5*power(2,LEAST(attempts,6)))::int),updated_at=now() WHERE id=$1;UPDATE trunks SET status='degraded',updated_at=now() WHERE id=$3",failed);q.Parameters.AddWithValue(id);q.Parameters.AddWithValue(e.Message[..Math.Min(500,e.Message.Length)]);q.Parameters.AddWithValue(trunk);await q.ExecuteNonQueryAsync(ct);}return true;
    }
    static async Task Probe(string endpoint,string transport,CancellationToken ct)
    {
        var raw=endpoint.Contains("://")?endpoint:$"sip://{endpoint}";var uri=new Uri(raw);var host=uri.Host;var port=uri.IsDefaultPort?(transport=="tls"?5061:5060):uri.Port;var addresses=await Dns.GetHostAddressesAsync(host,ct);if(addresses.Length==0)throw new InvalidOperationException("SIP hostname did not resolve");
        if(transport is "tcp" or "tls" or "wss"){using var tcp=new TcpClient();await tcp.ConnectAsync(host,port,ct);return;}
        using var udp=new UdpClient(addresses[0].AddressFamily);udp.Connect(addresses[0],port);var branch=Guid.NewGuid().ToString("N");var msg=$"OPTIONS sip:{host} SIP/2.0\r\nVia: SIP/2.0/UDP probe.invalid;branch=z9hG4bK{branch}\r\nFrom: <sip:probe@probe.invalid>;tag={branch[..8]}\r\nTo: <sip:{host}>\r\nCall-ID: {branch}@probe.invalid\r\nCSeq: 1 OPTIONS\r\nMax-Forwards: 1\r\nContent-Length: 0\r\n\r\n";await udp.SendAsync(Encoding.ASCII.GetBytes(msg),ct);var response=await udp.ReceiveAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(4),ct);var text=Encoding.ASCII.GetString(response.Buffer);if(!text.StartsWith("SIP/2.0 "))throw new InvalidOperationException("Endpoint did not return a SIP response");
    }
}
