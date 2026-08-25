namespace IcpaaS.Core.Configuration;

public sealed class PlatformOptions
{
    public const string SectionName = "ICPaaS";
    public DeploymentOptions Deployment { get; init; } = new();
    public DatabaseOptions Database { get; init; } = new();
    public TelephonyOptions Telephony { get; init; } = new();
    public MediaOptions Media { get; init; } = new();
    public PublicEndpointOptions PublicEndpoints { get; init; } = new();
}

public sealed class DeploymentOptions { public string Profile { get; init; } = "auto"; }

public sealed class DatabaseOptions
{
    public string Mode { get; init; } = "auto";
    public string Provider { get; init; } = "postgresql";
    public string? ConnectionString { get; init; }
    public bool AllowBundled { get; init; } = true;
}

public sealed class TelephonyOptions
{
    public string Mode { get; init; } = "auto";
    public string DefaultEngine { get; init; } = "simulator";
    public EngineOptions FreeSwitch { get; init; } = new();
    public EngineOptions Asterisk { get; init; } = new();
    public EngineOptions GenericSip { get; init; } = new();
}

public sealed class EngineOptions
{
    public bool Enabled { get; init; }
    public string? SipEndpoint { get; init; }
    public string? EslEndpoint { get; init; }
    public string? EslPassword { get; init; }
    public string? AriBaseUrl { get; init; }
    public string? AriUsername { get; init; }
    public string? AriPassword { get; init; }
    public string? AriApp { get; init; } = "icpaas";
    public string? ControlWebhook { get; init; }
    public string? ApiToken { get; init; }
}

public sealed class MediaOptions
{
    public string Mode { get; init; } = "auto";
    public bool AllowBundledTurn { get; init; } = true;
    public bool AllowBundledRtpEngine { get; init; } = true;
    public string? TurnRealm { get; init; }
    public string? TurnSharedSecret { get; init; }
}

public sealed class PublicEndpointOptions
{
    public string? ApiBaseUrl { get; init; }
    public string? WebSocketUrl { get; init; }
}

public sealed record CapabilityStatus(string Key, string State, string Message, bool Required = false);
public sealed record PlatformCapabilities(
    string DeploymentProfile,
    IReadOnlyList<CapabilityStatus> Capabilities,
    DateTimeOffset CheckedAt);
