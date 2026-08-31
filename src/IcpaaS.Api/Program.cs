using System.Text.Json.Serialization;
using System.Text;
using IcpaaS.Api;
using IcpaaS.Core.Configuration;
using IcpaaS.Core.Telephony;
using IcpaaS.Telephony;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 52 * 1024 * 1024);
builder.Services.AddOptions<PlatformOptions>()
    .Bind(builder.Configuration.GetSection(PlatformOptions.SectionName))
    .Validate(options => new[] { "auto", "demo", "standalone", "application", "telephony-node", "distributed", "hybrid" }.Contains(options.Deployment.Profile), "Invalid deployment profile")
    .ValidateOnStart();
builder.Services.AddIcpaaSTelephony();
builder.Services.AddSingleton<CapabilityService>();
builder.Services.AddSingleton<PlatformStore>();builder.Services.AddSingleton<AuthService>();builder.Services.AddSingleton<WebRtcService>();
builder.Services.AddSingleton<CapacityService>();
builder.Services.AddSingleton<CallEventSink>();
builder.Services.AddSingleton<ManagedTelephonyService>();
builder.Services.AddHostedService<CampaignExecutionWorker>();
builder.Services.AddHostedService<AgentDeliveryWorker>();
builder.Services.AddHostedService<InboundQueueWorker>();
builder.Services.AddHostedService<CallLegLifecycleWorker>();
builder.Services.AddHostedService<RecordingWorker>();
builder.Services.AddHostedService<ProvisioningWorker>();
builder.Services.AddHttpClient("plugins",client=>client.Timeout=TimeSpan.FromSeconds(15));
builder.Services.AddHostedService<PluginDeliveryWorker>();
var jwtSecret=builder.Configuration["ICPaaS:Security:JwtSecret"]??throw new InvalidOperationException("ICPaaS:Security:JwtSecret is required");if(jwtSecret.Length<32)throw new InvalidOperationException("JWT secret must be at least 32 characters");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o=>o.TokenValidationParameters=new(){ValidateIssuer=true,ValidIssuer="icpaas",ValidateAudience=true,ValidAudience="icpaas-api",ValidateIssuerSigningKey=true,IssuerSigningKey=new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),ValidateLifetime=true,ClockSkew=TimeSpan.FromSeconds(30)});builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
app.UseExceptionHandler();
app.UseDefaultFiles();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), geolocation=(), payment=(), usb=(), microphone=(self)";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; media-src 'self' blob:; connect-src 'self' https: wss:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();app.UseAuthorization();app.Use(EntitlementAccess.Enforce);

app.MapGet("/health/live", () => Results.Ok(new { status = "live", time = DateTimeOffset.UtcNow }));
app.MapGet("/health/ready", async (CapabilityService service, CancellationToken ct) => Results.Ok(await service.ReadAsync(ct)));
app.MapGet("/api/v1/system/capabilities", async (CapabilityService service, CancellationToken ct) => Results.Ok(await service.ReadAsync(ct)));
app.MapGet("/api/v1/telephony/engines", async (TelephonyRouter router, CancellationToken ct) =>
    Results.Ok(await Task.WhenAll(router.Engines.Select(x => x.ProbeAsync(ct)))));
app.MapPost("/api/v1/telephony/test-call", async (TestCallRequest body, ClaimsPrincipal user, ManagedTelephonyService managed, CancellationToken ct) =>
{
    if(!user.IsInRole("platform_admin")&&user.FindFirstValue("tenant_id")!=body.TenantId.ToString())return Results.Forbid();
    var request = new CallRequest(Guid.NewGuid(), body.TenantId, body.Destination, body.CallerId, null, "quick-connect-test", body.EngineKey, body.TrunkKey, 1);
    return Results.Accepted(value: (await managed.Originate(request, ct)).Result);
}).RequireAuthorization();

app.MapPost("/api/v1/auth/bootstrap",async(BootstrapRequest b,HttpRequest request,AuthService auth,CancellationToken ct)=>Results.Ok(await auth.Bootstrap(b,request.Headers["X-ICPaaS-Bootstrap-Key"].ToString(),ct))).RequireRateLimiting("auth");
app.MapPost("/api/v1/auth/login",async(LoginRequest b,AuthService auth,CancellationToken ct)=>{
 try{return Results.Ok(await auth.Login(b,ct));}
 catch(UnauthorizedAccessException ex){return Results.Json(new{error=ex.Message},statusCode:StatusCodes.Status401Unauthorized);}
}).RequireRateLimiting("auth");
app.MapPost("/api/v1/auth/refresh",async(RefreshRequest b,AuthService auth,CancellationToken ct)=>Results.Ok(await auth.Refresh(b,ct)));
app.MapPost("/api/v1/auth/recover-platform-admin",async(AdminRecoveryRequest b,HttpRequest request,AuthService auth,CancellationToken ct)=>{await auth.ResetPlatformAdmin(b,request.Headers["X-ICPaaS-Bootstrap-Key"].ToString(),ct);return Results.NoContent();}).RequireRateLimiting("auth");
app.MapContactCenter();app.MapContactCenterLifecycle();app.MapInfrastructureAdmin();app.MapManagement();app.MapOperations();app.MapIntegrations();app.MapUsers();app.MapReseller();app.MapNodeEndpoints();app.MapProvisioning();
app.MapSupervisor();
app.MapRecordings();
app.MapReports();

app.MapFallbackToFile("index.html");
app.Run();

public sealed record TestCallRequest(Guid TenantId, string Destination, string? CallerId, string? EngineKey, string? TrunkKey);

public sealed class CapabilityService(IOptions<PlatformOptions> options, TelephonyRouter router)
{
    public async Task<PlatformCapabilities> ReadAsync(CancellationToken cancellationToken)
    {
        var value = options.Value;
        var engineHealth = await Task.WhenAll(router.Engines.Select(x => x.ProbeAsync(cancellationToken)));
        var capabilities = new List<CapabilityStatus>
        {
            new("database", string.IsNullOrWhiteSpace(value.Database.ConnectionString) ? (value.Database.AllowBundled ? "bundled-available" : "unconfigured") : "external-configured", "Storage selection is resolved at installation", true),
            new("turn", value.Media.AllowBundledTurn ? "bundled-available" : "external-or-disabled", "TURN is optional and capability-driven"),
            new("rtpengine", value.Media.AllowBundledRtpEngine ? "bundled-available" : "external-or-disabled", "Media anchoring is optional and route-driven"),
            new("public-endpoints", string.IsNullOrWhiteSpace(value.PublicEndpoints.ApiBaseUrl) ? "unconfigured" : "configured", "No platform domain or IP is hard-coded")
        };
        capabilities.AddRange(engineHealth.Select(x => new CapabilityStatus($"telephony:{x.EngineKey}", x.Availability.ToString().ToLowerInvariant(), x.Message)));
        return new PlatformCapabilities(value.Deployment.Profile, capabilities, DateTimeOffset.UtcNow);
    }
}
