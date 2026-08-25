using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using IcpaaS.Core.Telephony;

namespace IcpaaS.Telephony;

public sealed class SimulatorEngine : ITelephonyEngine
{
    private readonly ConcurrentDictionary<Guid, CallRef> _calls = new();
    private readonly Channel<TelephonyEvent> _events = Channel.CreateUnbounded<TelephonyEvent>();
    public string EngineKey => "simulator";
    public TelephonyEngineKind Kind => TelephonyEngineKind.Simulator;

    public Task<EngineHealth> ProbeAsync(CancellationToken cancellationToken)
        => Task.FromResult(new EngineHealth(EngineKey, Kind, EngineAvailability.Ready, "Simulator ready", _calls.Count));

    public Task<RegistrationResult> RegisterEndpointAsync(EndpointSpec endpoint, CancellationToken cancellationToken)
        => Task.FromResult(new RegistrationResult(true, "Simulated endpoint registered"));

    public async Task<OriginateResult> OriginateAsync(CallRequest request, CancellationToken cancellationToken)
    {
        var call = new CallRef(request.PlatformCallId, EngineKey, $"sim-{Guid.NewGuid():N}");
        _calls[request.PlatformCallId] = call;
        await Publish(call, "call.accepted", cancellationToken);
        return new OriginateResult(call, "accepted", DateTimeOffset.UtcNow);
    }

    public Task AnswerAsync(CallRef call, CancellationToken cancellationToken) => Publish(call, "call.answered", cancellationToken).AsTask();
    public Task BridgeAsync(CallRef first, CallRef second, CancellationToken cancellationToken) => Publish(first, "call.bridged", cancellationToken).AsTask();
    public Task TransferAsync(CallRef call, TransferRequest request, CancellationToken cancellationToken) => Publish(call, "call.transferred", cancellationToken, new Dictionary<string, string> { ["destination"] = request.Destination }).AsTask();
    public Task HoldAsync(CallRef call, bool enabled, CancellationToken cancellationToken) => Publish(call, enabled ? "call.held" : "call.resumed", cancellationToken).AsTask();
    public Task SendDtmfAsync(CallRef call, string digits, CancellationToken cancellationToken) => Publish(call, "call.dtmf", cancellationToken, new Dictionary<string, string> { ["digits"] = digits }).AsTask();
    public async Task HangupAsync(CallRef call, string reason, CancellationToken cancellationToken) { _calls.TryRemove(call.PlatformCallId, out _); await Publish(call, "call.hangup", cancellationToken, new Dictionary<string, string> { ["reason"] = reason }); }

    public async IAsyncEnumerable<TelephonyEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken)) yield return item;
    }

    private ValueTask Publish(CallRef call, string eventType, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? attributes = null)
        => _events.Writer.WriteAsync(new TelephonyEvent(call.PlatformCallId, EngineKey, eventType, DateTimeOffset.UtcNow, attributes ?? new Dictionary<string, string>()), cancellationToken);
}

