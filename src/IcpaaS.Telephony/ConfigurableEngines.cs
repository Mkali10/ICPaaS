using System.Runtime.CompilerServices;
using IcpaaS.Core.Configuration;
using IcpaaS.Core.Telephony;
using Microsoft.Extensions.Options;

namespace IcpaaS.Telephony;

public abstract class ConfigurableEngine : ITelephonyEngine
{
    private readonly EngineOptions _options;
    protected ConfigurableEngine(string engineKey, TelephonyEngineKind kind, EngineOptions options)
        => (EngineKey, Kind, _options) = (engineKey, kind, options);

    public string EngineKey { get; }
    public TelephonyEngineKind Kind { get; }

    public virtual Task<EngineHealth> ProbeAsync(CancellationToken cancellationToken)
    {
        var state = !_options.Enabled ? EngineAvailability.Disabled
            : string.IsNullOrWhiteSpace(_options.SipEndpoint) ? EngineAvailability.Degraded
            : EngineAvailability.Configured;
        var message = state switch
        {
            EngineAvailability.Disabled => "Adapter disabled",
            EngineAvailability.Degraded => "Enabled but SIP endpoint is missing",
            _ => "Configured; live connectivity probe will be supplied by the engine adapter"
        };
        return Task.FromResult(new EngineHealth(EngineKey, Kind, state, message));
    }

    public virtual Task<RegistrationResult> RegisterEndpointAsync(EndpointSpec endpoint, CancellationToken cancellationToken)
        => Unsupported<RegistrationResult>("Dynamic endpoint registration is not available for this adapter");
    public virtual Task<OriginateResult> OriginateAsync(CallRequest request, CancellationToken cancellationToken)
        => Unsupported<OriginateResult>("Live originate is not implemented for this adapter");
    public virtual Task AnswerAsync(CallRef call, CancellationToken cancellationToken) => Unsupported("Answer");
    public virtual Task BridgeAsync(CallRef first, CallRef second, CancellationToken cancellationToken) => Unsupported("Bridge");
    public virtual Task TransferAsync(CallRef call, TransferRequest request, CancellationToken cancellationToken) => Unsupported("Transfer");
    public virtual Task HoldAsync(CallRef call, bool enabled, CancellationToken cancellationToken) => Unsupported("Hold");
    public virtual Task SendDtmfAsync(CallRef call, string digits, CancellationToken cancellationToken) => Unsupported("DTMF");
    public virtual Task HangupAsync(CallRef call, string reason, CancellationToken cancellationToken) => Unsupported("Hangup");

    public virtual async IAsyncEnumerable<TelephonyEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    private Task Unsupported(string operation) => Task.FromException(new NotSupportedException($"{operation} is not available for {EngineKey}"));
    private Task<T> Unsupported<T>(string message) => Task.FromException<T>(new NotSupportedException(message));
}

public sealed class FreeSwitchEngine(IOptions<PlatformOptions> options)
    : ConfigurableEngine("freeswitch", TelephonyEngineKind.FreeSwitch, options.Value.Telephony.FreeSwitch);

public sealed class AsteriskEngine(IOptions<PlatformOptions> options)
    : ConfigurableEngine("asterisk", TelephonyEngineKind.Asterisk, options.Value.Telephony.Asterisk);

public sealed class GenericSipEngine(IOptions<PlatformOptions> options)
    : ConfigurableEngine("generic-sip", TelephonyEngineKind.GenericSip, options.Value.Telephony.GenericSip);
