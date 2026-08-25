using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using IcpaaS.Core.Configuration;
using IcpaaS.Core.Telephony;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IcpaaS.Telephony;

public sealed class FreeSwitchEslConnection : BackgroundService
{
    private static readonly string Events = "CHANNEL_CREATE CHANNEL_ORIGINATE CHANNEL_PROGRESS CHANNEL_PROGRESS_MEDIA CHANNEL_ANSWER CHANNEL_BRIDGE CHANNEL_UNBRIDGE CHANNEL_HOLD CHANNEL_UNHOLD DTMF CHANNEL_HANGUP CHANNEL_HANGUP_COMPLETE";
    private readonly EngineOptions options;
    private readonly ILogger<FreeSwitchEslConnection> logger;
    private readonly SemaphoreSlim commandLock = new(1, 1);
    private readonly Channel<TelephonyEvent> events = Channel.CreateUnbounded<TelephonyEvent>();
    private TcpClient? client;
    private NetworkStream? stream;
    private StreamReader? reader;

    public FreeSwitchEslConnection(IOptions<PlatformOptions> configured, ILogger<FreeSwitchEslConnection> logger)
    {
        options = configured.Value.Telephony.FreeSwitch;
        this.logger = logger;
    }

    public async Task<string> ExecuteCommandAsync(string command, CancellationToken ct)
    {
        if (!options.Enabled) throw new InvalidOperationException("FreeSWITCH is disabled");
        await commandLock.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await EnsureConnectedAsync(ct);
                    await WriteAsync(stream!, $"api {command}\n\n", ct);
                    var frame = await ReadFrameAsync(reader!, ct);
                    return frame.Body.Length > 0 ? frame.Body : frame.Headers;
                }
                catch (Exception ex) when (attempt == 0 && ex is IOException or SocketException)
                {
                    logger.LogWarning(ex, "FreeSWITCH ESL command connection dropped; reconnecting");
                    CloseCommand();
                }
            }
            throw new IOException("FreeSWITCH ESL command failed after reconnect");
        }
        finally { commandLock.Release(); }
    }

    public IAsyncEnumerable<TelephonyEvent> SubscribeAsync(CancellationToken ct) =>
        events.Reader.ReadAllAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Enabled) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var eventClient = new TcpClient();
                var endpoint = Endpoint();
                await eventClient.ConnectAsync(endpoint.Host, endpoint.Port, ct);
                await using var eventStream = eventClient.GetStream();
                using var eventReader = new StreamReader(eventStream, Encoding.UTF8, false, 4096, true);
                await AuthenticateAsync(eventStream, eventReader, ct);
                await WriteAsync(eventStream, $"event plain {Events}\n\n", ct);
                var accepted = await ReadFrameAsync(eventReader, ct);
                if (!accepted.Text.Contains("+OK", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("FreeSWITCH rejected event subscription");

                logger.LogInformation("FreeSWITCH ESL event stream connected");
                while (!ct.IsCancellationRequested)
                {
                    var frame = await ReadFrameAsync(eventReader, ct);
                    if (Map(frame.Body) is { } item) await events.Writer.WriteAsync(item, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "FreeSWITCH ESL events unavailable; retrying");
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (client?.Connected == true && stream is not null && reader is not null) return;
        CloseCommand();
        var endpoint = Endpoint();
        client = new TcpClient();
        await client.ConnectAsync(endpoint.Host, endpoint.Port, ct);
        stream = client.GetStream();
        reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
        await AuthenticateAsync(stream, reader, ct);
    }

    private async Task AuthenticateAsync(NetworkStream target, StreamReader source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.EslPassword))
            throw new InvalidOperationException("FreeSWITCH ESL password is missing");
        var greeting = await ReadFrameAsync(source, ct);
        if (!greeting.Text.Contains("auth/request", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Unexpected FreeSWITCH ESL greeting");
        await WriteAsync(target, $"auth {options.EslPassword}\n\n", ct);
        var response = await ReadFrameAsync(source, ct);
        if (!response.Text.Contains("+OK accepted", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("FreeSWITCH ESL authentication failed");
    }

    private (string Host, int Port) Endpoint()
    {
        if (string.IsNullOrWhiteSpace(options.EslEndpoint))
            throw new InvalidOperationException("FreeSWITCH ESL endpoint is missing");
        if (Uri.TryCreate($"tcp://{options.EslEndpoint.Trim()}", UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
            return (uri.Host, uri.IsDefaultPort ? 8021 : uri.Port);
        throw new InvalidOperationException("FreeSWITCH ESL endpoint must be host[:port]");
    }

    private static async Task WriteAsync(Stream target, string value, CancellationToken ct)
    {
        await target.WriteAsync(Encoding.UTF8.GetBytes(value), ct);
        await target.FlushAsync(ct);
    }

    private static async Task<Frame> ReadFrameAsync(StreamReader source, CancellationToken ct)
    {
        var raw = new List<string>();
        var length = 0;
        while (true)
        {
            var line = await source.ReadLineAsync(ct) ?? throw new IOException("FreeSWITCH ESL disconnected");
            if (line.Length == 0) break;
            raw.Add(line);
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line[(line.IndexOf(':') + 1)..].Trim(), out length);
        }
        if (length < 0 || length > 4 * 1024 * 1024) throw new IOException("Invalid ESL frame length");
        var body = "";
        if (length > 0)
        {
            var buffer = new char[length];
            var read = 0;
            while (read < length)
            {
                var count = await source.ReadAsync(buffer.AsMemory(read, length - read), ct);
                if (count == 0) throw new IOException("FreeSWITCH ESL disconnected");
                read += count;
            }
            body = new string(buffer);
        }
        return new(string.Join('\n', raw), body);
    }

    private static TelephonyEvent? Map(string body)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var source = new StringReader(body);
        while (source.ReadLine() is { } line && line.Length > 0)
        {
            var split = line.IndexOf(':');
            if (split > 0)
                values[line[..split].Trim()] = Uri.UnescapeDataString(line[(split + 1)..].Trim().Replace("+", "%20"));
        }
        var idText = Get(values, "variable_icpaas_call_id") ?? Get(values, "variable_platform_call_id");
        if (!Guid.TryParse(idText, out var id)) return null;
        var name = Get(values, "Event-Name") ?? "UNKNOWN";
        var attributes = new Dictionary<string, string>();
        Add(attributes, "engineCallId", Get(values, "Unique-ID"));
        Add(attributes, "direction", Get(values, "Call-Direction"));
        Add(attributes, "callerId", Get(values, "Caller-Caller-ID-Number"));
        Add(attributes, "destination", Get(values, "Caller-Destination-Number"));
        Add(attributes, "hangupCause", Get(values, "Hangup-Cause"));
        Add(attributes, "dtmfDigit", Get(values, "DTMF-Digit"));
        return new(id, "freeswitch", Normalize(name), DateTimeOffset.UtcNow, attributes);
    }

    private static string Normalize(string name) => name switch
    {
        "CHANNEL_CREATE" or "CHANNEL_ORIGINATE" => "call.created",
        "CHANNEL_PROGRESS" or "CHANNEL_PROGRESS_MEDIA" => "call.ringing",
        "CHANNEL_ANSWER" => "call.answered",
        "CHANNEL_BRIDGE" => "call.bridged",
        "CHANNEL_UNBRIDGE" => "call.unbridged",
        "CHANNEL_HOLD" => "call.held",
        "CHANNEL_UNHOLD" => "call.resumed",
        "DTMF" => "call.dtmf",
        "CHANNEL_HANGUP" or "CHANNEL_HANGUP_COMPLETE" => "call.ended",
        _ => $"freeswitch.{name.ToLowerInvariant()}"
    };
    private static string? Get(IReadOnlyDictionary<string, string> source, string key) =>
        source.TryGetValue(key, out var value) ? value : null;
    private static void Add(IDictionary<string, string> target, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) target[key] = value;
    }
    private void CloseCommand()
    {
        reader?.Dispose(); stream?.Dispose(); client?.Dispose();
        reader = null; stream = null; client = null;
    }
    public override void Dispose()
    {
        CloseCommand(); commandLock.Dispose(); base.Dispose();
    }
    private sealed record Frame(string Headers, string Body)
    {
        public string Text => Headers + "\n" + Body;
    }
}
