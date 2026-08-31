using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace IcpaaS.Api;
public sealed record AgentEndpointCreate(Guid? UserId,string Extension,string DisplayName,string Password);
public sealed record ScorecardCreate(string Name,int Version,JsonElement Definition);
public sealed record EvaluationCreate(Guid CallId,Guid ScorecardId,decimal? Score,JsonElement Result);
public sealed record NodeHeartbeat(string NodeKey,string Status,int ActiveChannels,decimal CurrentCps);
public sealed record WebRtcConfig(string Uri,string WebSocketUrl,object[] IceServers,long ExpiresAt);

public sealed class WebRtcService(IConfiguration cfg)
{
    public WebRtcConfig Config(string extension)
    {
        var wss=cfg["ICPaaS:PublicEndpoints:WebSocketUrl"]??throw new InvalidOperationException("WebRTC WebSocket URL is not configured");
        var realm=cfg["ICPaaS:Media:TurnRealm"]??throw new InvalidOperationException("TURN realm missing");
        var secret=cfg["ICPaaS:Media:TurnSharedSecret"]??throw new InvalidOperationException("TURN secret missing");
        var expiry=DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeSeconds();var user=$"{expiry}:{extension}";
        using var h=new HMACSHA1(Encoding.UTF8.GetBytes(secret));var credential=Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(user)));
        return new($"sip:{extension}@{realm}",wss,[new{urls=new[]{$"turn:{realm}:3478?transport=udp",$"turns:{realm}:5349?transport=tcp"},username=user,credential}],expiry);
    }
}

public static class OperationsEndpoints
{
    static Guid Tenant(ClaimsPrincipal u)=>Guid.Parse(u.FindFirstValue("tenant_id")??throw new UnauthorizedAccessException("Tenant required"));
    static Guid User(ClaimsPrincipal u)=>Guid.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)??u.FindFirstValue("sub")!);
    static bool Admin(ClaimsPrincipal u)=>u.IsInRole("platform_admin")||u.IsInRole("tenant_owner")||u.IsInRole("tenant_admin");
    public static void MapOperations(this WebApplication app)
    {
        var api=app.MapGroup("/api/v1").RequireAuthorization();
        api.MapGet("/calls",async(ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>
        {
            await using var c=await s.Open(ct);await using var q=new NpgsqlCommand("SELECT id,direction,from_number,to_number,state,engine_type,engine_call_id,answered_at,ended_at,hangup_cause,created_at FROM calls WHERE tenant_id=$1 ORDER BY created_at DESC LIMIT 200",c);q.Parameters.AddWithValue(Tenant(u));return Results.Ok(await Rows(q,ct));
        });
        api.MapPost("/agents",async(AgentEndpointCreate b,ClaimsPrincipal u,PlatformStore s,IConfiguration cfg,CancellationToken ct)=>
        {
            if(!Admin(u)||!Regex.IsMatch(b.Extension,"^[0-9]{3,8}$")||b.Password.Length<10)return Results.BadRequest(new{error="Select an agent and provide a valid extension/password"});
            var realm=cfg["ICPaaS:Media:TurnRealm"]??throw new InvalidOperationException("SIP realm missing");
            var ha1=Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"{b.Extension}:{realm}:{b.Password}"))).ToLowerInvariant();
            var target=b.UserId??User(u);
            await using var c=await s.Open(ct);await using var q=new NpgsqlCommand("INSERT INTO agent_endpoints(tenant_id,user_id,extension,display_name,secret_hash,sip_ha1) SELECT $1,u.id,$3,$4,$5,$6 FROM users u WHERE u.id=$2 AND u.tenant_id=$1 AND 'agent'=ANY(u.roles) AND u.status='active' RETURNING id,extension,display_name,status,transport,user_id",c);Add(q,Tenant(u),target,b.Extension,b.DisplayName,PlatformStore.Hash(b.Password),ha1);var row=await One(q,ct);return row is null?Results.BadRequest(new{error="Selected user is not an active agent in this workspace"}):Results.Created("/api/v1/agents",row);
        });
        api.MapGet("/agents",async(ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>
        {
            await using var c=await s.Open(ct);var sql=Admin(u)?"SELECT id,extension,display_name,status,transport,created_at FROM agent_endpoints WHERE tenant_id=$1 ORDER BY extension":"SELECT id,extension,display_name,status,transport,created_at FROM agent_endpoints WHERE tenant_id=$1 AND user_id=$2 ORDER BY extension";await using var q=new NpgsqlCommand(sql,c);q.Parameters.AddWithValue(Tenant(u));if(!Admin(u))q.Parameters.AddWithValue(User(u));return Results.Ok(await Rows(q,ct));
        });
        api.MapGet("/webrtc/{extension}",async(string extension,ClaimsPrincipal u,PlatformStore s,WebRtcService rtc,CancellationToken ct)=>
        {
            if(!Regex.IsMatch(extension,"^[0-9]{3,8}$"))return Results.BadRequest(new{error="Invalid extension"});
            await using var c=await s.Open(ct);var sql=Admin(u)?"SELECT EXISTS(SELECT 1 FROM agent_endpoints WHERE tenant_id=$1 AND extension=$2)":"SELECT EXISTS(SELECT 1 FROM agent_endpoints WHERE tenant_id=$1 AND user_id=$2 AND extension=$3)";await using var q=new NpgsqlCommand(sql,c);q.Parameters.AddWithValue(Tenant(u));if(Admin(u))q.Parameters.AddWithValue(extension);else{q.Parameters.AddWithValue(User(u));q.Parameters.AddWithValue(extension);}if(!Convert.ToBoolean(await q.ExecuteScalarAsync(ct)))return Results.NotFound(new{error="Endpoint not found"});return Results.Ok(rtc.Config(extension));
        });
        api.MapGet("/quality/scorecards",async(ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>
        {
            await using var c=await s.Open(ct);var platform=u.IsInRole("platform_admin");var sql=platform?"SELECT id,tenant_id,name,version,definition,status,created_at FROM quality_scorecards ORDER BY name,version DESC":"SELECT id,tenant_id,name,version,definition,status,created_at FROM quality_scorecards WHERE tenant_id=$1 ORDER BY name,version DESC";await using var q=new NpgsqlCommand(sql,c);if(!platform)q.Parameters.AddWithValue(Tenant(u));return Results.Ok(await Rows(q,ct));
        });
        api.MapPost("/quality/scorecards",async(ScorecardCreate b,ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>
        {
            if(!Admin(u)||u.IsInRole("platform_admin"))return Results.Forbid();await using var c=await s.Open(ct);await using var q=new NpgsqlCommand("INSERT INTO quality_scorecards(tenant_id,name,version,definition) VALUES($1,$2,$3,$4::jsonb) RETURNING id,name,version,status",c);Add(q,Tenant(u),b.Name,b.Version,b.Definition.GetRawText());return Results.Created("/api/v1/quality/scorecards",await One(q,ct));
        });
        api.MapPost("/quality/evaluations",async(EvaluationCreate b,ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>
        {
            if(u.IsInRole("platform_admin"))return Results.Forbid();await using var c=await s.Open(ct);await using var q=new NpgsqlCommand("INSERT INTO quality_evaluations(tenant_id,call_id,scorecard_id,reviewer_user_id,score,result) SELECT $1,$2,$3,$4,$5,$6::jsonb WHERE EXISTS(SELECT 1 FROM calls WHERE id=$2 AND tenant_id=$1) RETURNING id,state,score,created_at",c);Add(q,Tenant(u),b.CallId,b.ScorecardId,User(u),b.Score,b.Result.GetRawText());return Results.Created("/api/v1/quality/evaluations",await One(q,ct));
        });
        api.MapGet("/audit",async(ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>
        {
            if(u.IsInRole("platform_admin")||!Admin(u)&&!u.IsInRole("auditor"))return Results.Forbid();await using var c=await s.Open(ct);await using var q=new NpgsqlCommand("SELECT id,event_type,resource_type,resource_id,correlation_id,occurred_at,integrity_hash FROM audit_events WHERE tenant_id=$1 ORDER BY occurred_at DESC LIMIT 500",c);q.Parameters.AddWithValue(Tenant(u));return Results.Ok(await Rows(q,ct));
        });
        api.MapGet("/operations",async(ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>
        {
            if(!Admin(u))return Results.Forbid();await using var c=await s.Open(ct);var platform=u.IsInRole("platform_admin");var sql=platform?"SELECT (SELECT count(*) FROM tenants) tenants,(SELECT count(*) FROM tenants WHERE status='active') active_tenants,(SELECT count(*) FROM users WHERE tenant_id IS NOT NULL) users,(SELECT count(*) FROM agent_endpoints) agents,(SELECT count(*) FROM calls WHERE ended_at IS NULL) active_calls,(SELECT count(*) FROM telephony_nodes) nodes,(SELECT count(*) FROM telephony_nodes WHERE status='ready') ready_nodes,(SELECT count(*) FROM trunks) trunks,(SELECT count(*) FROM trunks WHERE status='ready') ready_trunks,(SELECT coalesce(sum(max_channels),0) FROM trunks WHERE enabled) channel_capacity,(SELECT coalesce(sum(max_cps),0) FROM trunks WHERE enabled) cps_capacity,(SELECT count(*) FROM provisioning_jobs WHERE state='failed') failed_provisioning,(SELECT count(*) FROM outbox_events WHERE completed_at IS NULL) pending_events,(SELECT count(*) FROM plugin_deliveries WHERE state IN ('failed','dead_letter')) failed_deliveries,(SELECT pg_database_size(current_database())) database_bytes":"SELECT 1 tenants,1 active_tenants,(SELECT count(*) FROM users WHERE tenant_id=$1) users,(SELECT count(*) FROM agent_endpoints WHERE tenant_id=$1) agents,(SELECT count(*) FROM calls WHERE tenant_id=$1 AND ended_at IS NULL) active_calls,(SELECT count(*) FROM telephony_nodes WHERE tenant_id=$1 OR tenant_id IS NULL) nodes,(SELECT count(*) FROM telephony_nodes WHERE (tenant_id=$1 OR tenant_id IS NULL) AND status='ready') ready_nodes,(SELECT count(*) FROM trunks WHERE tenant_id=$1) trunks,(SELECT count(*) FROM trunks WHERE tenant_id=$1 AND status='ready') ready_trunks,(SELECT coalesce(sum(max_channels),0) FROM trunks WHERE tenant_id=$1 AND enabled) channel_capacity,(SELECT coalesce(sum(max_cps),0) FROM trunks WHERE tenant_id=$1 AND enabled) cps_capacity,(SELECT count(*) FROM provisioning_jobs WHERE tenant_id=$1 AND state='failed') failed_provisioning,(SELECT count(*) FROM outbox_events WHERE tenant_id=$1 AND completed_at IS NULL) pending_events,(SELECT count(*) FROM plugin_deliveries WHERE tenant_id=$1 AND state IN ('failed','dead_letter')) failed_deliveries,(SELECT pg_database_size(current_database())) database_bytes";await using var q=new NpgsqlCommand(sql,c);if(!platform)q.Parameters.AddWithValue(Tenant(u));return Results.Ok(await One(q,ct));
        });
    }
    public static void MapNodeEndpoints(this WebApplication app)
    {
        app.MapPost("/internal/nodes/heartbeat",async(NodeHeartbeat b,HttpRequest req,PlatformStore s,IConfiguration cfg,CancellationToken ct)=>
        {
            if(req.Headers["X-ICPaaS-Node-Key"]!=cfg["ICPaaS:Security:NodeKey"])return Results.Unauthorized();await using var c=await s.Open(ct);await using var q=new NpgsqlCommand("UPDATE telephony_nodes SET status=$2,last_seen_at=now(),capabilities=capabilities||$3::jsonb WHERE node_key=$1 RETURNING id,status,last_seen_at",c);Add(q,b.NodeKey,b.Status,PlatformStore.Json(new{b.ActiveChannels,b.CurrentCps}));return await One(q,ct) is { } row?Results.Ok(row):Results.NotFound();
        });
    }
    static void Add(NpgsqlCommand c,params object?[] v){for(var i=0;i<v.Length;i++)c.Parameters.AddWithValue(v[i]??DBNull.Value);}
    static async Task<object?> One(NpgsqlCommand c,CancellationToken ct){await using var r=await c.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return null;var d=new Dictionary<string,object?>();for(var i=0;i<r.FieldCount;i++)d[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i);return d;}
    static async Task<List<Dictionary<string,object?>>> Rows(NpgsqlCommand c,CancellationToken ct){await using var r=await c.ExecuteReaderAsync(ct);var x=new List<Dictionary<string,object?>>();while(await r.ReadAsync(ct)){var d=new Dictionary<string,object?>();for(var i=0;i<r.FieldCount;i++)d[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i);x.Add(d);}return x;}
}
