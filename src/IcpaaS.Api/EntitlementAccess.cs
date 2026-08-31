using System.Security.Claims;

namespace IcpaaS.Api;

public static class EntitlementAccess
{
    static readonly (string Prefix,string Entitlement)[] Routes =
    {
        ("/api/v1/contact-center/agent", "agent_desk"),
        ("/api/v1/agents", "agent_desk"),
        ("/api/v1/contact-center", "campaigns"),
        ("/api/v1/processes", "campaigns"),
        ("/api/v1/campaigns", "campaigns"),
        ("/api/v1/quick-connect", "infrastructure"),
        ("/api/v1/nodes", "infrastructure"),
        ("/api/v1/trunks", "infrastructure"),
        ("/api/v1/infrastructure", "infrastructure"),
        ("/api/v1/numbers", "numbers"),
        ("/api/v1/dids", "numbers"),
        ("/api/v1/routes", "routing"),
        ("/api/v1/telephony/test-call", "routing"),
        ("/api/v1/supervisor", "supervision"),
        ("/api/v1/calls", "recordings"),
        ("/api/v1/recordings", "recordings"),
        ("/api/v1/users", "team"),
        ("/api/v1/plugins", "integrations"),
        ("/api/v1/integrations", "integrations"),
        ("/api/v1/quality", "quality"),
        ("/api/v1/reports", "reports"),
        ("/api/v1/operations", "operations"),
        ("/api/v1/audit-log", "audit")
    };

    public static async Task Enforce(HttpContext context,RequestDelegate next)
    {
        var user=context.User;
        if(user.Identity?.IsAuthenticated!=true||user.IsInRole("platform_admin")){await next(context);return;}
        var path=context.Request.Path;
        var required=Routes.FirstOrDefault(x=>path.StartsWithSegments(x.Prefix,StringComparison.OrdinalIgnoreCase));
        if(required.Prefix is null||user.HasClaim("entitlement",required.Entitlement)){await next(context);return;}
        context.Response.StatusCode=StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new{error=$"Service '{required.Entitlement}' is not enabled for this workspace."});
    }
}
