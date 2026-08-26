using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Npgsql;

namespace IcpaaS.Api;

public sealed record UserCreate(string Email, string DisplayName, string Password, string[] Roles);
public sealed record UserUpdate(string? DisplayName, string? Status, string[]? Roles, bool RevokeSessions = false);
public sealed record LogoutRequest(string RefreshToken);
public sealed record ProfileUpdate(string? DisplayName);
public sealed record PasswordChange(string CurrentPassword,string NewPassword);
public sealed record AdminPasswordReset(string NewPassword,bool RevokeSessions = true);

public static class UserEndpoints
{
    static Guid User(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub")!);
    static Guid Tenant(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("tenant_id") ?? throw new UnauthorizedAccessException("Tenant required"));
    static bool Admin(ClaimsPrincipal user) => user.IsInRole("platform_admin") || user.IsInRole("tenant_owner") || user.IsInRole("tenant_admin");
    static readonly HashSet<string> AllowedRoles = ["tenant_owner", "tenant_admin", "supervisor", "agent", "auditor", "billing_admin"];

    public static void MapUsers(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        api.MapGet("/profile", async (ClaimsPrincipal user, PlatformStore store, CancellationToken ct) =>
        {
            await using var connection=await store.Open(ct);await using var command=new NpgsqlCommand("SELECT id,tenant_id,email,display_name,roles,status,created_at,updated_at FROM users WHERE id=$1",connection);command.Parameters.AddWithValue(User(user));return await One(command,ct) is { } row?Results.Ok(row):Results.NotFound();
        });
        api.MapPatch("/profile", async (ProfileUpdate body,ClaimsPrincipal user,PlatformStore store,CancellationToken ct) =>
        {
            if(string.IsNullOrWhiteSpace(body.DisplayName))return Results.BadRequest(new{error="Display name required"});await using var connection=await store.Open(ct);await using var command=new NpgsqlCommand("UPDATE users SET display_name=$2,updated_at=now() WHERE id=$1 RETURNING id,email,display_name,roles,status,updated_at",connection);Add(command,User(user),body.DisplayName.Trim());return Results.Ok(await One(command,ct));
        });
        api.MapPost("/profile/password", async (PasswordChange body,ClaimsPrincipal user,PlatformStore store,CancellationToken ct) =>
        {
            if(body.NewPassword.Length<12)return Results.BadRequest(new{error="New password must be at least 12 characters"});await using var connection=await store.Open(ct);await using var find=new NpgsqlCommand("SELECT password_hash,password_salt FROM users WHERE id=$1",connection);find.Parameters.AddWithValue(User(user));await using var reader=await find.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct))return Results.NotFound();var hash=reader.GetString(0);var salt=reader.GetString(1);await reader.CloseAsync();if(!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash),Convert.FromHexString(PlatformStore.Password(body.CurrentPassword,salt))))return Results.BadRequest(new{error="Current password is incorrect"});var nextSalt=PlatformStore.Salt();await using var update=new NpgsqlCommand("UPDATE users SET password_hash=$2,password_salt=$3,token_version=token_version+1,updated_at=now() WHERE id=$1",connection);Add(update,User(user),PlatformStore.Password(body.NewPassword,nextSalt),nextSalt);await update.ExecuteNonQueryAsync(ct);return Results.NoContent();
        });
        api.MapGet("/users", async (ClaimsPrincipal user, PlatformStore store, CancellationToken ct) =>
        {
            if (!Admin(user)) return Results.Forbid();
            await using var connection = await store.Open(ct);
            await using var command = new NpgsqlCommand("SELECT id,email,display_name,roles,status,created_at,updated_at FROM users WHERE tenant_id=$1 ORDER BY created_at", connection);
            command.Parameters.AddWithValue(Tenant(user));
            return Results.Ok(await Rows(command, ct));
        });
        api.MapPost("/users", async (UserCreate body, ClaimsPrincipal user, PlatformStore store, CancellationToken ct) =>
        {
            if (!Admin(user)) return Results.Forbid();
            if (!Regex.IsMatch(body.Email, "^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$") || body.Password.Length < 12 || body.Roles.Length == 0 || body.Roles.Any(x => !AllowedRoles.Contains(x)))
                return Results.BadRequest(new { error = "Invalid email, password or roles" });
            var salt = PlatformStore.Salt();
            await using var connection = await store.Open(ct);
            await using var command = new NpgsqlCommand("INSERT INTO users(tenant_id,email,display_name,password_hash,password_salt,roles) VALUES($1,lower($2),$3,$4,$5,$6) RETURNING id,email,display_name,roles,status,created_at", connection);
            Add(command, Tenant(user), body.Email, body.DisplayName, PlatformStore.Password(body.Password, salt), salt, body.Roles.Distinct().ToArray());
            return Results.Created("/api/v1/users", await One(command, ct));
        });
        api.MapPatch("/users/{id:guid}", async (Guid id, UserUpdate body, ClaimsPrincipal user, PlatformStore store, CancellationToken ct) =>
        {
            if (!Admin(user)) return Results.Forbid();
            if (body.Status is not null && body.Status is not ("active" or "locked" or "disabled")) return Results.BadRequest(new { error = "Invalid status" });
            if (body.Roles is not null && (body.Roles.Length == 0 || body.Roles.Any(x => !AllowedRoles.Contains(x)))) return Results.BadRequest(new { error = "Invalid roles" });
            await using var connection = await store.Open(ct);
            await using var command = new NpgsqlCommand("UPDATE users SET display_name=COALESCE($3,display_name),status=COALESCE($4,status),roles=COALESCE($5,roles),token_version=token_version+CASE WHEN $6 THEN 1 ELSE 0 END,updated_at=now() WHERE id=$1 AND tenant_id=$2 RETURNING id,email,display_name,roles,status,token_version,updated_at", connection);
            Add(command, id, Tenant(user), body.DisplayName, body.Status, body.Roles, body.RevokeSessions || body.Status is "locked" or "disabled");
            var result = await One(command, ct);
            if (result is null) return Results.NotFound();
            if (body.RevokeSessions || body.Status is "locked" or "disabled")
            {
                await using var revoke = new NpgsqlCommand("UPDATE refresh_tokens SET revoked_at=COALESCE(revoked_at,now()) WHERE user_id=$1", connection);
                revoke.Parameters.AddWithValue(id);
                await revoke.ExecuteNonQueryAsync(ct);
            }
            return Results.Ok(result);
        });
        api.MapPost("/users/{id:guid}/reset-password", async (Guid id, AdminPasswordReset body, ClaimsPrincipal user, PlatformStore store, CancellationToken ct) =>
        {
            if (!Admin(user)) return Results.Forbid();
            if (body.NewPassword.Length < 12) return Results.BadRequest(new { error = "Password must be at least 12 characters" });
            var salt=PlatformStore.Salt();await using var connection=await store.Open(ct);
            await using var command=new NpgsqlCommand("UPDATE users SET password_hash=$3,password_salt=$4,token_version=token_version+1,updated_at=now() WHERE id=$1 AND tenant_id=$2 RETURNING id,email,display_name,status",connection);
            Add(command,id,Tenant(user),PlatformStore.Password(body.NewPassword,salt),salt);var result=await One(command,ct);if(result is null)return Results.NotFound();
            if(body.RevokeSessions){await using var revoke=new NpgsqlCommand("UPDATE refresh_tokens SET revoked_at=COALESCE(revoked_at,now()) WHERE user_id=$1",connection);revoke.Parameters.AddWithValue(id);await revoke.ExecuteNonQueryAsync(ct);}
            return Results.Ok(result);
        });
        api.MapPost("/auth/logout", async (LogoutRequest body, PlatformStore store, CancellationToken ct) =>
        {
            await using var connection = await store.Open(ct);
            await using var command = new NpgsqlCommand("UPDATE refresh_tokens SET revoked_at=COALESCE(revoked_at,now()) WHERE token_hash=$1", connection);
            command.Parameters.AddWithValue(PlatformStore.Hash(body.RefreshToken));
            await command.ExecuteNonQueryAsync(ct);
            return Results.NoContent();
        });
    }

    static void Add(NpgsqlCommand command, params object?[] values) { foreach (var value in values) command.Parameters.AddWithValue(value ?? DBNull.Value); }
    static async Task<object?> One(NpgsqlCommand command, CancellationToken ct) { await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) return null; var row = new Dictionary<string, object?>(); for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i); return row; }
    static async Task<List<Dictionary<string, object?>>> Rows(NpgsqlCommand command, CancellationToken ct) { await using var reader = await command.ExecuteReaderAsync(ct); var rows = new List<Dictionary<string, object?>>(); while (await reader.ReadAsync(ct)) { var row = new Dictionary<string, object?>(); for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i); rows.Add(row); } return rows; }
}
