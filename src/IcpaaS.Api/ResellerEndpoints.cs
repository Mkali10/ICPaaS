using System.Security.Claims;
using System.Text.Json;
using Npgsql;

namespace IcpaaS.Api;

public sealed record TenantControlUpdate(string? Status,string? PlanKey,int? ChannelLimit,decimal? CpsLimit,int? AgentLimit,int? StorageLimitGb,int? RecordingRetentionDays,string[]? ServiceEntitlements,JsonElement? Branding);
public sealed record CreditAdjustment(Guid TenantId,decimal Amount,string EntryType,string? Reference,string? Note);
public sealed record ProcessCreate(string Name,string ProcessType,decimal? MaxCps);
public sealed record CampaignCreate(Guid ProcessId,string Name,string DialerMode,decimal? MaxCps,int? MaxChannels);

public static class ResellerEndpoints
{
    static bool Platform(ClaimsPrincipal u)=>u.IsInRole("platform_admin");
    static bool Admin(ClaimsPrincipal u)=>Platform(u)||u.IsInRole("tenant_owner")||u.IsInRole("tenant_admin");
    static Guid Tenant(ClaimsPrincipal u)=>Guid.Parse(u.FindFirstValue("tenant_id")??throw new UnauthorizedAccessException("Tenant context required"));
    static Guid User(ClaimsPrincipal u)=>Guid.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)??u.FindFirstValue("sub")!);
    public static void MapReseller(this WebApplication app)
    {
        var api=app.MapGroup("/api/v1").RequireAuthorization();
        api.MapGet("/platform/tenants/{id:guid}",async(Guid id,ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>{
            if(!Platform(u))return Results.Forbid();await using var c=await s.Open(ct);
            await using var q=new NpgsqlCommand(@"SELECT t.id,t.slug,t.name,t.status,t.branding,t.created_at,
              ts.plan_key,ts.channel_limit,ts.cps_limit,ts.agent_limit,ts.storage_limit_gb,ts.recording_retention_days,ts.service_entitlements,
              b.currency,b.billing_mode,b.credit_balance,b.credit_limit,b.status billing_status,
              (SELECT count(*) FROM users WHERE tenant_id=t.id) users,
              (SELECT count(*) FROM agent_endpoints WHERE tenant_id=t.id) agents,
              (SELECT count(*) FROM trunks WHERE tenant_id=t.id) trunks,
              (SELECT count(*) FROM dids WHERE tenant_id=t.id) dids,
              (SELECT count(*) FROM calls WHERE tenant_id=t.id AND ended_at IS NULL) active_calls
              FROM tenants t JOIN tenant_settings ts ON ts.tenant_id=t.id
              LEFT JOIN billing_accounts b ON b.tenant_id=t.id WHERE t.id=$1",c);q.Parameters.AddWithValue(id);
            return await One(q,ct) is { } row?Results.Ok(row):Results.NotFound();
        });
        api.MapPatch("/platform/tenants/{id:guid}",async(Guid id,TenantControlUpdate b,ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>{
            if(!Platform(u))return Results.Forbid();await using var c=await s.Open(ct);await using var tx=await c.BeginTransactionAsync(ct);
            if(b.Status is not null){await using var t=new NpgsqlCommand("UPDATE tenants SET status=$2,branding=COALESCE($3::jsonb,branding),updated_at=now() WHERE id=$1",c,tx);Add(t,id,b.Status,b.Branding is null?null:b.Branding.Value.GetRawText());await t.ExecuteNonQueryAsync(ct);}
            else if(b.Branding is not null){await using var t=new NpgsqlCommand("UPDATE tenants SET branding=$2::jsonb,updated_at=now() WHERE id=$1",c,tx);Add(t,id,b.Branding.Value.GetRawText());await t.ExecuteNonQueryAsync(ct);}
            var allowed=new HashSet<string>{"infrastructure","numbers","routing","campaigns","agent_desk","supervision","recordings","team","integrations","quality","reports","operations","audit"};if(b.ServiceEntitlements is not null&&b.ServiceEntitlements.Any(x=>!allowed.Contains(x)))return Results.BadRequest(new{error="Invalid service entitlement"});
            await using var q=new NpgsqlCommand(@"UPDATE tenant_settings SET plan_key=COALESCE($2,plan_key),channel_limit=COALESCE($3,channel_limit),cps_limit=COALESCE($4,cps_limit),agent_limit=COALESCE($5,agent_limit),storage_limit_gb=COALESCE($6,storage_limit_gb),recording_retention_days=COALESCE($7,recording_retention_days),service_entitlements=COALESCE($8,service_entitlements),updated_at=now() WHERE tenant_id=$1",c,tx);Add(q,id,b.PlanKey,b.ChannelLimit,b.CpsLimit,b.AgentLimit,b.StorageLimitGb,b.RecordingRetentionDays,b.ServiceEntitlements?.Distinct().ToArray());await q.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);return Results.NoContent();
        });
        api.MapGet("/numbers",async(ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>{
            await using var c=await s.Open(ct);var sql=Platform(u)?"SELECT d.id,d.tenant_id,d.trunk_id,d.number_e164,d.use_for_inbound,d.use_for_outbound_cli,d.enabled,d.ownership_verified_at,t.display_name trunk_name FROM dids d JOIN trunks t ON t.id=d.trunk_id ORDER BY d.created_at DESC":"SELECT d.id,d.tenant_id,d.trunk_id,d.number_e164,d.use_for_inbound,d.use_for_outbound_cli,d.enabled,d.ownership_verified_at,t.display_name trunk_name FROM dids d JOIN trunks t ON t.id=d.trunk_id WHERE d.tenant_id=$1 ORDER BY d.created_at DESC";await using var q=new NpgsqlCommand(sql,c);if(!Platform(u))q.Parameters.AddWithValue(Tenant(u));return Results.Ok(await Rows(q,ct));
        });
        api.MapGet("/routes",async(ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>{
            await using var c=await s.Open(ct);var sql=Platform(u)?"SELECT id,tenant_id,route_type,name,did_id,process_id,campaign_id,primary_trunk_id,failover_trunk_id,preferred_engine,destination_pattern,priority,enabled,configuration_revision FROM routes ORDER BY priority,created_at":"SELECT id,tenant_id,route_type,name,did_id,process_id,campaign_id,primary_trunk_id,failover_trunk_id,preferred_engine,destination_pattern,priority,enabled,configuration_revision FROM routes WHERE tenant_id=$1 ORDER BY priority,created_at";await using var q=new NpgsqlCommand(sql,c);if(!Platform(u))q.Parameters.AddWithValue(Tenant(u));return Results.Ok(await Rows(q,ct));
        });
        api.MapGet("/processes",async(ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>{if(Platform(u))return Results.Forbid();await using var c=await s.Open(ct);await using var q=new NpgsqlCommand("SELECT id,name,process_type,max_cps,enabled,created_at FROM processes WHERE tenant_id=$1 ORDER BY created_at DESC",c);q.Parameters.AddWithValue(Tenant(u));return Results.Ok(await Rows(q,ct));});
        api.MapPost("/processes",async(ProcessCreate b,ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>{if(!Admin(u)||Platform(u))return Results.Forbid();await using var c=await s.Open(ct);await using var q=new NpgsqlCommand("INSERT INTO processes(tenant_id,name,process_type,max_cps) VALUES($1,$2,$3,$4) RETURNING id,name,process_type,max_cps,enabled",c);Add(q,Tenant(u),b.Name,b.ProcessType,b.MaxCps);return Results.Created("/api/v1/processes",await One(q,ct));});
        api.MapGet("/campaigns",async(ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>{if(Platform(u))return Results.Forbid();await using var c=await s.Open(ct);await using var q=new NpgsqlCommand("SELECT c.id,c.name,c.dialer_mode,c.state,c.max_cps,c.max_channels,p.name process_name,c.created_at FROM campaigns c JOIN processes p ON p.id=c.process_id WHERE c.tenant_id=$1 ORDER BY c.created_at DESC",c);q.Parameters.AddWithValue(Tenant(u));return Results.Ok(await Rows(q,ct));});
        api.MapPost("/campaigns",async(CampaignCreate b,ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>{if(!Admin(u)||Platform(u))return Results.Forbid();await using var c=await s.Open(ct);await using var q=new NpgsqlCommand("INSERT INTO campaigns(tenant_id,process_id,name,dialer_mode,max_cps,max_channels) SELECT $1,$2,$3,$4,$5,$6 WHERE EXISTS(SELECT 1 FROM processes WHERE id=$2 AND tenant_id=$1) RETURNING id,name,dialer_mode,state,max_cps,max_channels",c);Add(q,Tenant(u),b.ProcessId,b.Name,b.DialerMode,b.MaxCps,b.MaxChannels);return Results.Created("/api/v1/campaigns",await One(q,ct));});
        api.MapGet("/billing",async(ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>{
            await using var c=await s.Open(ct);var sql=Platform(u)?"SELECT b.tenant_id,t.name,b.currency,b.billing_mode,b.credit_balance,b.credit_limit,b.low_balance_threshold,b.status,b.updated_at FROM billing_accounts b JOIN tenants t ON t.id=b.tenant_id ORDER BY t.name":"SELECT b.tenant_id,t.name,b.currency,b.billing_mode,b.credit_balance,b.credit_limit,b.low_balance_threshold,b.status,b.updated_at FROM billing_accounts b JOIN tenants t ON t.id=b.tenant_id WHERE b.tenant_id=$1";await using var q=new NpgsqlCommand(sql,c);if(!Platform(u))q.Parameters.AddWithValue(Tenant(u));return Results.Ok(await Rows(q,ct));
        });
        api.MapPost("/billing/adjust",async(CreditAdjustment b,ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>{
            if(!Platform(u)||b.Amount==0)return Results.Forbid();await using var c=await s.Open(ct);await using var tx=await c.BeginTransactionAsync(ct);await using var l=new NpgsqlCommand("INSERT INTO credit_ledger(tenant_id,amount,entry_type,reference,note,actor_user_id) VALUES($1,$2,$3,$4,$5,$6)",c,tx);Add(l,b.TenantId,b.Amount,b.EntryType,b.Reference,b.Note,User(u));await l.ExecuteNonQueryAsync(ct);await using var q=new NpgsqlCommand("UPDATE billing_accounts SET credit_balance=credit_balance+$2,updated_at=now() WHERE tenant_id=$1",c,tx);Add(q,b.TenantId,b.Amount);await q.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);return Results.NoContent();
        });
        api.MapGet("/platform/tenants/{id:guid}/users",async(Guid id,ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>{
            if(!Platform(u))return Results.Forbid();await using var c=await s.Open(ct);await using var q=new NpgsqlCommand("SELECT id,email,display_name,roles,status,created_at,updated_at FROM users WHERE tenant_id=$1 ORDER BY created_at",c);q.Parameters.AddWithValue(id);return Results.Ok(await Rows(q,ct));
        });
        api.MapPost("/platform/tenants/{id:guid}/users",async(Guid id,UserCreate b,ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>{
            if(!Platform(u))return Results.Forbid();var allowed=new HashSet<string>{"tenant_owner","tenant_admin","supervisor","agent","auditor","billing_admin"};if(!System.Text.RegularExpressions.Regex.IsMatch(b.Email,"^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$")||b.Password.Length<12||b.Roles.Length==0||b.Roles.Any(x=>!allowed.Contains(x)))return Results.BadRequest(new{error="Invalid user details"});var salt=PlatformStore.Salt();await using var c=await s.Open(ct);await using var q=new NpgsqlCommand("INSERT INTO users(tenant_id,email,display_name,password_hash,password_salt,roles) SELECT $1,lower($2),$3,$4,$5,$6 WHERE EXISTS(SELECT 1 FROM tenants WHERE id=$1) RETURNING id,email,display_name,roles,status,created_at",c);Add(q,id,b.Email,b.DisplayName,PlatformStore.Password(b.Password,salt),salt,b.Roles.Distinct().ToArray());return Results.Created($"/api/v1/platform/tenants/{id}/users",await One(q,ct));
        });
        api.MapPatch("/platform/tenants/{tenantId:guid}/users/{userId:guid}",async(Guid tenantId,Guid userId,UserUpdate b,ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>{
            if(!Platform(u))return Results.Forbid();var allowed=new HashSet<string>{"tenant_owner","tenant_admin","supervisor","agent","auditor","billing_admin"};if(b.Status is not null&&b.Status is not ("active" or "locked" or "disabled"))return Results.BadRequest(new{error="Invalid status"});if(b.Roles is not null&&(b.Roles.Length==0||b.Roles.Any(x=>!allowed.Contains(x))))return Results.BadRequest(new{error="Invalid roles"});await using var c=await s.Open(ct);await using var q=new NpgsqlCommand("UPDATE users SET display_name=COALESCE($3,display_name),status=COALESCE($4,status),roles=COALESCE($5,roles),token_version=CASE WHEN $6 THEN token_version+1 ELSE token_version END,updated_at=now() WHERE id=$1 AND tenant_id=$2 RETURNING id,email,display_name,roles,status,updated_at",c);Add(q,userId,tenantId,b.DisplayName,b.Status,b.Roles?.Distinct().ToArray(),b.RevokeSessions);return await One(q,ct) is { } row?Results.Ok(row):Results.NotFound();
        });
        api.MapPost("/platform/tenants/{tenantId:guid}/users/{userId:guid}/reset-password",async(Guid tenantId,Guid userId,AdminPasswordReset b,ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>{
            if(!Platform(u))return Results.Forbid();if(b.NewPassword.Length<12)return Results.BadRequest(new{error="Password must be at least 12 characters"});var salt=PlatformStore.Salt();await using var c=await s.Open(ct);await using var q=new NpgsqlCommand("UPDATE users SET password_hash=$3,password_salt=$4,token_version=token_version+1,updated_at=now() WHERE id=$1 AND tenant_id=$2 RETURNING id,email,display_name,status",c);Add(q,userId,tenantId,PlatformStore.Password(b.NewPassword,salt),salt);var row=await One(q,ct);if(row is null)return Results.NotFound();if(b.RevokeSessions){await using var revoke=new NpgsqlCommand("UPDATE refresh_tokens SET revoked_at=COALESCE(revoked_at,now()) WHERE user_id=$1",c);revoke.Parameters.AddWithValue(userId);await revoke.ExecuteNonQueryAsync(ct);}return Results.Ok(row);
        });
        api.MapGet("/audit-log",async(ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>{
            if(!Admin(u))return Results.Forbid();await using var c=await s.Open(ct);var sql=Platform(u)?"SELECT id,tenant_id,event_type,resource_type,resource_id,correlation_id,occurred_at FROM audit_events ORDER BY occurred_at DESC LIMIT 500":"SELECT id,tenant_id,event_type,resource_type,resource_id,correlation_id,occurred_at FROM audit_events WHERE tenant_id=$1 ORDER BY occurred_at DESC LIMIT 500";await using var q=new NpgsqlCommand(sql,c);if(!Platform(u))q.Parameters.AddWithValue(Tenant(u));return Results.Ok(await Rows(q,ct));
        });
    }
    static void Add(NpgsqlCommand c,params object?[] values){for(var i=0;i<values.Length;i++)c.Parameters.AddWithValue(values[i]??DBNull.Value);}
    static async Task<object?> One(NpgsqlCommand c,CancellationToken ct){await using var r=await c.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return null;var d=new Dictionary<string,object?>();for(var i=0;i<r.FieldCount;i++)d[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i);return d;}
    static async Task<List<Dictionary<string,object?>>> Rows(NpgsqlCommand c,CancellationToken ct){await using var r=await c.ExecuteReaderAsync(ct);var rows=new List<Dictionary<string,object?>>();while(await r.ReadAsync(ct)){var d=new Dictionary<string,object?>();for(var i=0;i<r.FieldCount;i++)d[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i);rows.Add(d);}return rows;}
}
