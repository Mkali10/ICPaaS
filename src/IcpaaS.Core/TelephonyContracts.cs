namespace IcpaaS.Core.Telephony;

public enum TelephonyEngineKind { Simulator, GenericSip, FreeSwitch, Asterisk, ExternalProvider }
public enum EngineAvailability { Disabled, Configured, Ready, Degraded, Unavailable }

public sealed record EngineHealth(
    string EngineKey,
    TelephonyEngineKind Kind,
    EngineAvailability Availability,
    string Message,
    int? ActiveChannels = null,
    int? ChannelLimit = null,
    decimal? CpsLimit = null);

public sealed record CallRef(Guid PlatformCallId, string EngineKey, string? EngineCallId = null);

public sealed record CallRequest(
    Guid PlatformCallId,
    Guid TenantId,
    string Destination,
    string? CallerId,
    string? CampaignId,
    string? ProcessId,
    string? PreferredEngine,
    string? TrunkKey,
    decimal? RequestedCps);

public sealed record OriginateResult(CallRef Call, string State, DateTimeOffset AcceptedAt);
public sealed record TransferRequest(string Destination, bool Attended = false);
public sealed record RegistrationResult(bool Accepted, string Message);

public sealed record EndpointSpec(
    Guid TenantId,
    string AddressOfRecord,
    string? AuthenticationUsername,
    string? SecretReference);

public sealed record TelephonyEvent(
    Guid PlatformCallId,
    string EngineKey,
    string EventType,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string> Attributes);

public interface ITelephonyEngine
{
    string EngineKey { get; }
    TelephonyEngineKind Kind { get; }
    Task<EngineHealth> ProbeAsync(CancellationToken cancellationToken);
    Task<RegistrationResult> RegisterEndpointAsync(EndpointSpec endpoint, CancellationToken cancellationToken);
    Task<OriginateResult> OriginateAsync(CallRequest request, CancellationToken cancellationToken);
    Task AnswerAsync(CallRef call, CancellationToken cancellationToken);
    Task BridgeAsync(CallRef first, CallRef second, CancellationToken cancellationToken);
    Task TransferAsync(CallRef call, TransferRequest request, CancellationToken cancellationToken);
    Task HoldAsync(CallRef call, bool enabled, CancellationToken cancellationToken);
    Task SendDtmfAsync(CallRef call, string digits, CancellationToken cancellationToken);
    Task HangupAsync(CallRef call, string reason, CancellationToken cancellationToken);
    IAsyncEnumerable<TelephonyEvent> SubscribeAsync(CancellationToken cancellationToken);
}
