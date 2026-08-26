using System.Security.Claims;
using Npgsql;

namespace IcpaaS.Api;

public sealed record ResourceStateChange(bool Enabled);
public static class InfrastructureAdminEndpoints
{
 static Guid Tenant(ClaimsPrincipal u)=>Guid.Parse(u.FindFirstValue("tenant_id")??throw new UnauthorizedAccessException("Tenant required"));
 static bool Platform(ClaimsPrincipal u)=>u.IsInRole("platform_admin");
 static bool Admin(ClaimsPrincipal u)=>Platform(u)||u.IsInRole("tenant_owner")||u.IsInRole("tenant_admin");
 public static void MapInfrastructureAdmin(this WebApplication app)
 {
  var api=app.MapGroup("/api/v1").RequireAuthorization();
  api.MapPatch("/infrastructure/{resource}/{id:guid}/state",async(string resource,Guid id,ResourceStateChange b,ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>{
   if(!Admin(u))return Results.Forbid();var table=resource switch{"nodes"=>"telephony_nodes","trunks"=>"trunks","dids"=>"dids","routes"=>"routes",_=>null};if(table is null)return Results.BadRequest(new{error="Unknown infrastructure resource"});
   await using var c=await s.Open(ct);var sql=$"UPDATE {table} SET enabled=$2,updated_at=now() WHERE id=$1"+(Platform(u)?"":" AND tenant_id=$3")+" RETURNING id,enabled,updated_at";await using var q=new NpgsqlCommand(sql,c);q.Parameters.AddWithValue(id);q.Parameters.AddWithValue(b.Enabled);if(!Platform(u))q.Parameters.AddWithValue(Tenant(u));var row=await One(q,ct);return row is null?Results.NotFound():Results.Ok(row);
  });
  api.MapPost("/infrastructure/trunks/{id:guid}/verify",async(Guid id,ClaimsPrincipal u,PlatformStore s,CancellationToken ct)=>{
   if(!Admin(u))return Results.Forbid();await using var c=await s.Open(ct);await using var tx=await c.BeginTransactionAsync(ct);
   var sql="UPDATE trunks SET configuration_revision=configuration_revision+1,status='provisioning',updated_at=now() WHERE id=$1"+(Platform(u)?"":" AND tenant_id=$2")+" RETURNING tenant_id,node_id,configuration_revision";await using var q=new NpgsqlCommand(sql,c,tx);q.Parameters.AddWithValue(id);if(!Platform(u))q.Parameters.AddWithValue(Tenant(u));await using var r=await q.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct)){await r.CloseAsync();await tx.RollbackAsync(ct);return Results.NotFound();}var tenant=r.GetGuid(0);var node=r.GetGuid(1);var revision=r.GetInt64(2);await r.CloseAsync();
   await using var job=new NpgsqlCommand("INSERT INTO provisioning_jobs(tenant_id,node_id,resource_type,resource_id,revision,state,available_at) VALUES($1,$2,'trunk',$3,$4,'queued',now())",c,tx);Add(job,tenant,node,id,revision);await job.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);return Results.Accepted(value:new{id,revision,state="queued"});
  });
 }
 static void Add(NpgsqlCommand c,params object?[] values){foreach(var value in values)c.Parameters.AddWithValue(value??DBNull.Value);}
 static async Task<object?> One(NpgsqlCommand c,CancellationToken ct){await using var r=await c.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return null;var d=new Dictionary<string,object?>();for(var i=0;i<r.FieldCount;i++)d[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i);return d;}
}