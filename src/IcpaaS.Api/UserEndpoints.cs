using System.Security.Claims;
using System.Text.RegularExpressions;
using Npgsql;

namespace IcpaaS.Api;

public sealed record UserCreate(string Email, string DisplayName, string Password, string[] Roles);
public sealed record UserUpdate(string? DisplayName, string? Status, string[]? Roles, bool RevokeSessions = false);
public sealed record LogoutRequest(string RefreshToken);

public static class UserEndpoints
{
    static Guid Tenant(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("tenant_id") ?? throw new UnauthorizedAccessException("Tenant required"));
    static bool Admin(ClaimsPrincipal user) => user.IsInRole("platform_admin") || user.IsInRole("tenant_owner") || user.IsInRole("tenant_admin");
    static readonly HashSet<string> AllowedRoles = ["tenant_owner", "tenant_admin", "supervisor", "agent", "auditor", "billing_admin"];

    public static void MapUsers(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
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
