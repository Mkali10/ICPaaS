using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using IcpaaS.Core.Configuration;
using IcpaaS.Core.Telephony;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IcpaaS.Telephony;

public sealed class AsteriskAriEventConnection : BackgroundService
{
    private readonly EngineOptions options;
    private readonly ILogger<AsteriskAriEventConnection> logger;
    private readonly Channel<TelephonyEvent> events = Channel.CreateUnbounded<TelephonyEvent>();

    public AsteriskAriEventConnection(IOptions<PlatformOptions> configured, ILogger<AsteriskAriEventConnection> logger)
    {
        options = configured.Value.Telephony.Asterisk;
        this.logger = logger;
    }

    public async IAsyncEnumerable<TelephonyEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in events.Reader.ReadAllAsync(ct)) yield return item;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Enabled) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                if (!string.IsNullOrWhiteSpace(options.AriUsername))
                {
                    var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.AriUsername}:{options.AriPassword}"));
                    socket.Options.SetRequestHeader("Authorization", $"Basic {token}");
                }
                await socket.ConnectAsync(EventUri(), ct);
                logger.LogInformation("Asterisk ARI event stream connected");
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var json = await ReceiveAsync(socket, ct);
                    if (Map(json) is { } item) await events.Writer.WriteAsync(item, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Asterisk ARI event stream unavailable; retrying");
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private Uri EventUri()
    {
        var baseUrl = options.AriBaseUrl?.TrimEnd('/') ?? throw new InvalidOperationException("ARI URL missing");
        var builder = new UriBuilder(baseUrl) { Scheme = baseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws" };
        var app = Uri.EscapeDataString(options.AriApp ?? "icpaas");
        builder.Path = builder.Path.TrimEnd('/') + "/events";
        builder.Query = $"app={app}&subscribeAll=true";
        return builder.Uri;
    }

    private static async Task<string> ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("ARI WebSocket closed");
            if (output.Length + result.Count > 4 * 1024 * 1024) throw new IOException("ARI event too large");
            await output.WriteAsync(buffer.AsMemory(0, result.Count), ct);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static TelephonyEvent? Map(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
        if (type is null || !TryChannel(root, out var channel)) return null;
        var channelId = channel.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
        if (!Guid.TryParse(channelId, out var platformId)) return null;
        var attributes = new Dictionary<string, string> { ["engineCallId"] = channelId! };
        if (channel.TryGetProperty("name", out var name)) attributes["channel"] = name.GetString() ?? "";
        if (channel.TryGetProperty("state", out var state)) attributes["state"] = state.GetString() ?? "";
        var normalized=type=="ChannelStateChange"&&channel.TryGetProperty("state",out var channelState)&&channelState.GetString()=="Up"?"call.answered":Normalize(type);
        return new(platformId, "asterisk", normalized, DateTimeOffset.UtcNow, attributes);
    }

    private static bool TryChannel(JsonElement root, out JsonElement channel)
    {
        if (root.TryGetProperty("channel", out channel)) return true;
        if (root.TryGetProperty("peer", out channel)) return true;
        channel = default;
        return false;
    }

    private static string Normalize(string type) => type switch
    {
        "StasisStart" => "call.created",
        "ChannelStateChange" => "call.state",
        "ChannelDialplan" or "Dial" => "call.ringing",
        "ChannelEnteredBridge" => "call.bridged",
        "ChannelLeftBridge" => "call.unbridged",
        "ChannelHold" => "call.held",
        "ChannelUnhold" => "call.resumed",
        "ChannelDtmfReceived" => "call.dtmf",
        "ChannelDestroyed" or "StasisEnd" => "call.ended",
        _ => $"asterisk.{type.ToLowerInvariant()}"
    };
}
