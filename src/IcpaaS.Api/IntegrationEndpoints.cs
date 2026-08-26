using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace IcpaaS.Api;

public sealed record PluginConfigure(string PluginKey,string DisplayName,string EndpointUrl,string? SecretRef,string[] Events,JsonElement Settings);
public sealed record QualityNoteCreate(string NoteType,string Body);
public sealed record QualityStateChange(string State);

public static class IntegrationEndpoints
{
    static readonly HashSet<string> Supported=new(StringComparer.OrdinalIgnoreCase){"whatsapp","email","zoho","odoo","webhook"};
    static Guid Tenant(ClaimsPrincipal u)=>Guid.Parse(u.FindFirstValue("tenant_id")??throw new UnauthorizedAccessException("Tenant required"));
    static Guid User(ClaimsPrincipal u)=>Guid.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)??u.FindFirstValue("sub")!);
    static bool Admin(ClaimsPrincipal u)=>u.IsInRole("platform_admin")||u.IsInRole("tenant_owner")||u.IsInRole("tenant_admin");

    public static void MapIntegrations(this WebApplication app)
    {
        var group=app.MapGroup("/api/v1").RequireAuthorization();
        group.MapGet("/plugins",async(ClaimsPrincipal user,PlatformStore store,CancellationToken ct)=>
        {
            await using var connection=await store.Open(ct);
            var platform=user.IsInRole("platform_admin");var sql=platform?"SELECT id,tenant_id,plugin_key,category,display_name,endpoint_url,secret_ref,settings,subscribed_events,status,last_tested_at,last_error,created_at,updated_at FROM plugins ORDER BY display_name":"SELECT id,tenant_id,plugin_key,category,display_name,endpoint_url,secret_ref,settings,subscribed_events,status,last_tested_at,last_error,created_at,updated_at FROM plugins WHERE tenant_id=$1 ORDER BY display_name";await using var command=new NpgsqlCommand(sql,connection);
            if(!platform)command.Parameters.AddWithValue(Tenant(user));return Results.Ok(await Rows(command,ct));
        });
        group.MapPost("/plugins",async(PluginConfigure body,ClaimsPrincipal user,PlatformStore store,CancellationToken ct)=>
        {
            if(!Admin(user))return Results.Forbid();
            if(!Supported.Contains(body.PluginKey)||!Endpoint(body.EndpointUrl)||!SecretReference(body.SecretRef)||string.IsNullOrWhiteSpace(body.DisplayName))
                return Results.BadRequest(new{error="Invalid plugin configuration"});
            var eventNames=(body.Events??[]).Where(x=>Regex.IsMatch(x,"^[a-z][a-z0-9_.-]{1,80}$")).Distinct().Take(50).ToArray();
            await using var connection=await store.Open(ct);
            await using var command=new NpgsqlCommand(@"INSERT INTO plugins(tenant_id,plugin_key,category,display_name,manifest,endpoint_url,secret_ref,settings,subscribed_events,status)
VALUES($1,$2,$3,$4,$5::jsonb,$6,$7,$8::jsonb,$9,'configured')
ON CONFLICT(tenant_id,plugin_key) DO UPDATE SET display_name=excluded.display_name,endpoint_url=excluded.endpoint_url,secret_ref=excluded.secret_ref,settings=excluded.settings,subscribed_events=excluded.subscribed_events,status='configured',last_error=NULL,updated_at=now()
RETURNING id,plugin_key,display_name,status,endpoint_url,secret_ref,subscribed_events",connection);
            Add(command,Tenant(user),body.PluginKey.ToLowerInvariant(),Category(body.PluginKey),body.DisplayName.Trim(),PlatformStore.Json(new{version=1,provider=body.PluginKey}),body.EndpointUrl,body.SecretRef,body.Settings.GetRawText(),eventNames);
            return Results.Ok(await One(command,ct));
        });
        group.MapPost("/plugins/{id:guid}/test",async(Guid id,ClaimsPrincipal user,PlatformStore store,CancellationToken ct)=>
        {
            if(!Admin(user))return Results.Forbid();await using var connection=await store.Open(ct);
            await using var command=new NpgsqlCommand("INSERT INTO plugin_deliveries(tenant_id,plugin_id,event_type,payload) SELECT $1,id,'plugin.test',$3::jsonb FROM plugins WHERE id=$2 AND tenant_id=$1 RETURNING id,state,created_at",connection);
            Add(command,Tenant(user),id,PlatformStore.Json(new{message="ICPaaS connection test",sentAt=DateTimeOffset.UtcNow}));
            return await One(command,ct) is { } row?Results.Accepted("/api/v1/plugins/deliveries",row):Results.NotFound();
        });
        group.MapGet("/plugins/deliveries",async(ClaimsPrincipal user,PlatformStore store,CancellationToken ct)=>
        {
            await using var connection=await store.Open(ct);
            var platform=user.IsInRole("platform_admin");var sql=platform?"SELECT d.id,d.tenant_id,p.display_name,d.event_type,d.state,d.attempts,d.response_code,d.last_error,d.delivered_at,d.created_at FROM plugin_deliveries d JOIN plugins p ON p.id=d.plugin_id ORDER BY d.created_at DESC LIMIT 200":"SELECT d.id,d.tenant_id,p.display_name,d.event_type,d.state,d.attempts,d.response_code,d.last_error,d.delivered_at,d.created_at FROM plugin_deliveries d JOIN plugins p ON p.id=d.plugin_id WHERE d.tenant_id=$1 ORDER BY d.created_at DESC LIMIT 200";await using var command=new NpgsqlCommand(sql,connection);
            if(!platform)command.Parameters.AddWithValue(Tenant(user));return Results.Ok(await Rows(command,ct));
        });
        group.MapGet("/quality/evaluations",async(ClaimsPrincipal user,PlatformStore store,CancellationToken ct)=>
        {
            await using var connection=await store.Open(ct);
            var platform=user.IsInRole("platform_admin");var sql=platform?@"SELECT e.id,e.tenant_id,e.call_id,e.scorecard_id,s.name scorecard_name,s.version,e.reviewer_user_id,u.display_name reviewer_name,e.state,e.score,e.result,e.created_at,e.updated_at FROM quality_evaluations e JOIN quality_scorecards s ON s.id=e.scorecard_id JOIN users u ON u.id=e.reviewer_user_id ORDER BY e.created_at DESC LIMIT 300":@"SELECT e.id,e.tenant_id,e.call_id,e.scorecard_id,s.name scorecard_name,s.version,e.reviewer_user_id,u.display_name reviewer_name,e.state,e.score,e.result,e.created_at,e.updated_at FROM quality_evaluations e JOIN quality_scorecards s ON s.id=e.scorecard_id JOIN users u ON u.id=e.reviewer_user_id WHERE e.tenant_id=$1 ORDER BY e.created_at DESC LIMIT 300";await using var command=new NpgsqlCommand(sql,connection);
            if(!platform)command.Parameters.AddWithValue(Tenant(user));return Results.Ok(await Rows(command,ct));
        });
        group.MapGet("/quality/evaluations/{id:guid}/notes",async(Guid id,ClaimsPrincipal user,PlatformStore store,CancellationToken ct)=>
        {
            await using var connection=await store.Open(ct);
            await using var command=new NpgsqlCommand(@"SELECT n.id,n.note_type,n.body,u.display_name author_name,n.created_at FROM quality_evaluation_notes n JOIN users u ON u.id=n.author_user_id WHERE n.tenant_id=$1 AND n.evaluation_id=$2 ORDER BY n.created_at",connection);
            Add(command,Tenant(user),id);return Results.Ok(await Rows(command,ct));
        });
        group.MapPost("/quality/evaluations/{id:guid}/state",async(Guid id,QualityStateChange body,ClaimsPrincipal user,PlatformStore store,CancellationToken ct)=>
        {
            if(!Admin(user)||!Regex.IsMatch(body.State??"","^(draft|submitted|disputed|final)$"))return Results.BadRequest(new{error="Invalid evaluation state"});
            await using var connection=await store.Open(ct);await using var command=new NpgsqlCommand("UPDATE quality_evaluations SET state=$3,updated_at=now() WHERE id=$2 AND tenant_id=$1 RETURNING id,state,updated_at",connection);Add(command,Tenant(user),id,body.State);return await One(command,ct) is { } row?Results.Ok(row):Results.NotFound();
        });
        group.MapPost("/quality/evaluations/{id:guid}/notes",async(Guid id,QualityNoteCreate body,ClaimsPrincipal user,PlatformStore store,CancellationToken ct)=>
        {
            if(!Regex.IsMatch(body.NoteType??"","^(review|dispute|resolution|compliance)$")||string.IsNullOrWhiteSpace(body.Body)||body.Body.Length>5000)return Results.BadRequest(new{error="Invalid quality note"});
            await using var connection=await store.Open(ct);
            await using var command=new NpgsqlCommand("INSERT INTO quality_evaluation_notes(tenant_id,evaluation_id,author_user_id,note_type,body) SELECT $1,id,$3,$4,$5 FROM quality_evaluations WHERE id=$2 AND tenant_id=$1 RETURNING id,note_type,body,created_at",connection);
            Add(command,Tenant(user),id,User(user),body.NoteType,body.Body.Trim());return await One(command,ct) is { } row?Results.Ok(row):Results.NotFound();
        });
    }

    static bool Endpoint(string value)=>Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme is "https" or "http"&&string.IsNullOrEmpty(uri.UserInfo);
    static bool SecretReference(string? value)=>string.IsNullOrWhiteSpace(value)||Regex.IsMatch(value,"^(env|vault|file):[A-Za-z0-9_./-]{2,180}$");
    static string Category(string key)=>key.ToLowerInvariant() switch{"whatsapp"=>"messaging","email"=>"email","zoho" or "odoo"=>"crm",_=>"automation"};
    static void Add(NpgsqlCommand command,params object?[] values){foreach(var value in values)command.Parameters.AddWithValue(value??DBNull.Value);}
    static async Task<object?> One(NpgsqlCommand command,CancellationToken ct){await using var reader=await command.ExecuteReaderAsync(ct);return await reader.ReadAsync(ct)?Row(reader):null;}
    static async Task<List<object>> Rows(NpgsqlCommand command,CancellationToken ct){var rows=new List<object>();await using var reader=await command.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))rows.Add(Row(reader));return rows;}
    static Dictionary<string,object?> Row(NpgsqlDataReader reader){var row=new Dictionary<string,object?>();for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);return row;}
}
