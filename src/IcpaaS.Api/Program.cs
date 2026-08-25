using IcpaaS.Core.Configuration;
using IcpaaS.Core.Telephony;
using IcpaaS.Telephony;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOptions<PlatformOptions>()
    .Bind(builder.Configuration.GetSection(PlatformOptions.SectionName))
    .Validate(options => new[] { "auto", "demo", "standalone", "application", "telephony-node", "distributed", "hybrid" }.Contains(options.Deployment.Profile), "Invalid deployment profile")
    .ValidateOnStart();
builder.Services.AddIcpaaSTelephony();
builder.Services.AddSingleton<CapabilityService>();
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health/live", () => Results.Ok(new { status = "live", time = DateTimeOffset.UtcNow }));
app.MapGet("/health/ready", async (CapabilityService service, CancellationToken ct) => Results.Ok(await service.ReadAsync(ct)));
app.MapGet("/api/v1/system/capabilities", async (CapabilityService service, CancellationToken ct) => Results.Ok(await service.ReadAsync(ct)));
app.MapGet("/api/v1/telephony/engines", async (TelephonyRouter router, CancellationToken ct) =>
    Results.Ok(await Task.WhenAll(router.Engines.Select(x => x.ProbeAsync(ct)))));
app.MapPost("/api/v1/telephony/test-call", async (TestCallRequest body, TelephonyRouter router, CancellationToken ct) =>
{
    var request = new CallRequest(Guid.NewGuid(), body.TenantId, body.Destination, body.CallerId, null, "quick-connect-test", body.EngineKey, body.TrunkKey, 1);
    return Results.Accepted(value: await router.OriginateAsync(request, ct));
});

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
