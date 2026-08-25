using System.Collections.Concurrent;
using IcpaaS.Core.Configuration;
using IcpaaS.Core.Telephony;
using Microsoft.Extensions.Options;

namespace IcpaaS.Telephony;

public sealed class TelephonyRouter
{
    private readonly IReadOnlyDictionary<string, ITelephonyEngine> _engines;
    private readonly string _defaultEngine;
    private readonly ConcurrentDictionary<Guid, string> _callBindings = new();

    public TelephonyRouter(IEnumerable<ITelephonyEngine> engines, IOptions<PlatformOptions> options)
    {
        _engines = engines.ToDictionary(x => x.EngineKey, StringComparer.OrdinalIgnoreCase);
        _defaultEngine = options.Value.Telephony.DefaultEngine;
    }

    public IReadOnlyCollection<ITelephonyEngine> Engines => _engines.Values;

    public async Task<OriginateResult> OriginateAsync(CallRequest request, CancellationToken cancellationToken)
    {
        var requested = request.PreferredEngine ?? _defaultEngine;
        if (!_engines.TryGetValue(requested, out var engine)) throw new InvalidOperationException($"Unknown telephony engine '{requested}'");
        var health = await engine.ProbeAsync(cancellationToken);
        if (health.Availability is EngineAvailability.Disabled or EngineAvailability.Unavailable)
            throw new InvalidOperationException($"Telephony engine '{requested}' is not available: {health.Message}");
        var result = await engine.OriginateAsync(request, cancellationToken);
        _callBindings[request.PlatformCallId] = engine.EngineKey;
        return result;
    }

    public ITelephonyEngine Resolve(CallRef call)
    {
        var key = _callBindings.TryGetValue(call.PlatformCallId, out var bound) ? bound : call.EngineKey;
        return _engines.TryGetValue(key, out var engine) ? engine : throw new InvalidOperationException($"Engine binding '{key}' is unavailable");
    }
}
