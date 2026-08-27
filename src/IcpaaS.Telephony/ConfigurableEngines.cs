using System.Net.Http.Headers;using System.Net.Sockets;using System.Runtime.CompilerServices;using System.Text;using System.Text.Json;using IcpaaS.Core.Configuration;using IcpaaS.Core.Telephony;using Microsoft.Extensions.Options;
namespace IcpaaS.Telephony;
public abstract class ConfigurableEngine(string key,TelephonyEngineKind kind,EngineOptions options):ITelephonyEngine{protected EngineOptions Options{get;}=options;public string EngineKey{get;}=key;public TelephonyEngineKind Kind{get;}=kind;public abstract Task<EngineHealth> ProbeAsync(CancellationToken ct);public virtual Task<RegistrationResult> RegisterEndpointAsync(EndpointSpec e,CancellationToken ct)=>No<RegistrationResult>("Endpoint provisioning is managed by provisioning jobs");public abstract Task<OriginateResult> OriginateAsync(CallRequest r,CancellationToken ct);public virtual Task AnswerAsync(CallRef c,CancellationToken ct)=>No("Answer");public virtual Task BridgeAsync(CallRef a,CallRef b,CancellationToken ct)=>No("Bridge");public virtual Task TransferAsync(CallRef c,TransferRequest r,CancellationToken ct)=>No("Transfer");public virtual Task HoldAsync(CallRef c,bool e,CancellationToken ct)=>No("Hold");public virtual Task SendDtmfAsync(CallRef c,string d,CancellationToken ct)=>No("DTMF");public virtual Task HangupAsync(CallRef c,string r,CancellationToken ct)=>No("Hangup");public virtual async IAsyncEnumerable<TelephonyEvent> SubscribeAsync([EnumeratorCancellation]CancellationToken ct){await Task.CompletedTask;yield break;}protected Task No(string op)=>Task.FromException(new NotSupportedException($"{op} unavailable for {EngineKey}"));protected Task<T>No<T>(string m)=>Task.FromException<T>(new NotSupportedException(m));protected static void Valid(CallRequest r){if(!System.Text.RegularExpressions.Regex.IsMatch(r.Destination,"^\\+[1-9][0-9]{6,14}$|^[A-Za-z0-9_.-]{2,64}$"))throw new ArgumentException("Invalid destination");}}
public sealed class FreeSwitchEngine : ConfigurableEngine
{
    private readonly FreeSwitchEslConnection connection;
    public FreeSwitchEngine(IOptions<PlatformOptions> options, FreeSwitchEslConnection connection)
        : base("freeswitch", TelephonyEngineKind.FreeSwitch, options.Value.Telephony.FreeSwitch) =>
        this.connection = connection;

    public override async Task<EngineHealth> ProbeAsync(CancellationToken ct)
    {
        if (!Options.Enabled) return new(EngineKey, Kind, EngineAvailability.Disabled, "Disabled");
        try
        {
            var reply = await connection.ExecuteCommandAsync("status", ct);
            return reply.Contains("-ERR", StringComparison.OrdinalIgnoreCase)
                ? new(EngineKey, Kind, EngineAvailability.Degraded, reply.Trim())
                : new(EngineKey, Kind, EngineAvailability.Ready, "Persistent ESL authenticated");
        }
        catch (Exception ex) { return new(EngineKey, Kind, EngineAvailability.Unavailable, ex.Message); }
    }

    public override async Task<OriginateResult> OriginateAsync(CallRequest request, CancellationToken ct)
    {
        Valid(request);
        var callId = Guid.NewGuid();
        var trunk = Safe(request.TrunkKey ?? "sofia/gateway/default", "trunk");
        var destination = Safe(request.Destination, "destination");
        var variables = $"origination_uuid={callId},icpaas_call_id={request.PlatformCallId}";
        if (!string.IsNullOrWhiteSpace(request.CallerId))
            variables += $",origination_caller_id_number={Safe(request.CallerId, "caller ID")}";
        var reply = await connection.ExecuteCommandAsync($"originate {{{variables}}}{trunk}/{destination} &park()", ct);
        Check(reply);
        return new(new(request.PlatformCallId, EngineKey, callId.ToString()), "accepted", DateTimeOffset.UtcNow);
    }

    public async Task<OriginateResult> OriginateEndpointAsync(CallRequest request,string endpoint,CancellationToken ct)
    {
        Valid(request);var callId=Guid.NewGuid();var target=Safe(endpoint,"agent endpoint");
        var variables=$"origination_uuid={callId},icpaas_call_id={request.PlatformCallId}";
        var reply=await connection.ExecuteCommandAsync($"originate {{{variables}}}{target} &park()",ct);Check(reply);
        return new(new(request.PlatformCallId,EngineKey,callId.ToString()),"accepted",DateTimeOffset.UtcNow);
    }

    public async Task<OriginateResult> OriginateSupervisionAsync(CallRequest request,string endpoint,CallRef target,string mode,CancellationToken ct)
    {
        var callId=Guid.NewGuid();var destination=Safe(endpoint,"supervisor endpoint");var targetId=Id(target);
        var flags=mode switch{"listen"=>"","whisper"=>",eavesdrop_whisper_aleg=true","barge"=>",eavesdrop_whisper_aleg=true,eavesdrop_whisper_bleg=true",_=>throw new ArgumentException("Invalid supervision mode")};
        var variables=$"origination_uuid={callId},icpaas_call_id={request.PlatformCallId}{flags}";
        var reply=await connection.ExecuteCommandAsync($"originate {{{variables}}}{destination} &eavesdrop({targetId})",ct);Check(reply);
        return new(new(request.PlatformCallId,EngineKey,callId.ToString()),"accepted",DateTimeOffset.UtcNow);
    }

    public override Task AnswerAsync(CallRef call, CancellationToken ct) => Act($"uuid_answer {Id(call)}", ct);
    public override Task HangupAsync(CallRef call, string reason, CancellationToken ct) =>
        Act($"uuid_kill {Id(call)} {Safe(reason, "hangup reason")}", ct);
    public override Task HoldAsync(CallRef call, bool enabled, CancellationToken ct) =>
        Act(enabled ? $"uuid_hold {Id(call)}" : $"uuid_hold off {Id(call)}", ct);
    public override Task SendDtmfAsync(CallRef call, string digits, CancellationToken ct) =>
        Act($"uuid_send_dtmf {Id(call)} {Safe(digits, "DTMF")}", ct);
    public override Task TransferAsync(CallRef call, TransferRequest request, CancellationToken ct) =>
        Act($"uuid_transfer {Id(call)} {Safe(request.Destination, "transfer destination")}", ct);
    public override Task BridgeAsync(CallRef first,CallRef second,CancellationToken ct)=>
        Act($"uuid_bridge {Id(first)} {Id(second)}",ct);
    public Task StartRecordingAsync(CallRef call,string storageKey,CancellationToken ct)=>Act($"uuid_record {Id(call)} start /var/lib/icpaas/recordings/{Safe(storageKey,"recording key")}.wav",ct);
    public override IAsyncEnumerable<TelephonyEvent> SubscribeAsync(CancellationToken ct) =>
        connection.SubscribeAsync(ct);

    private async Task Act(string command, CancellationToken ct) => Check(await connection.ExecuteCommandAsync(command, ct));
    private static void Check(string reply)
    {
        if (reply.Contains("-ERR", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(reply.Trim());
    }
    private static string Id(CallRef call) =>
        Guid.TryParse(call.EngineCallId, out var id) ? id.ToString() : throw new ArgumentException("Invalid call ID");
    private static string Safe(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(c => !(char.IsLetterOrDigit(c) || c is '+' or '-' or '_' or '.' or '@' or '/' or ':' or '#')))
            throw new ArgumentException($"Invalid {label}");
        return value;
    }
}

public sealed class AsteriskEngine:IcpaaS.Telephony.ConfigurableEngine{readonly HttpClient http=new();readonly AsteriskAriEventConnection events;public AsteriskEngine(IOptions<PlatformOptions> o,AsteriskAriEventConnection events):base("asterisk",TelephonyEngineKind.Asterisk,o.Value.Telephony.Asterisk){this.events=events;if(!string.IsNullOrWhiteSpace(Options.AriUsername))http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Options.AriUsername}:{Options.AriPassword}")));}string Url(string p)=>$"{Options.AriBaseUrl?.TrimEnd('/')??throw new InvalidOperationException("ARI URL missing")}/{p}";async Task Send(HttpMethod m,string p,CancellationToken ct){var x=await http.SendAsync(new(m,Url(p)),ct);if(!x.IsSuccessStatusCode)throw new InvalidOperationException($"ARI returned {(int)x.StatusCode}: {await x.Content.ReadAsStringAsync(ct)}");}public override async Task<EngineHealth> ProbeAsync(CancellationToken ct){if(!Options.Enabled)return new(EngineKey,Kind,EngineAvailability.Disabled,"Disabled");try{await Send(HttpMethod.Get,"asterisk/info",ct);return new(EngineKey,Kind,EngineAvailability.Ready,"ARI authenticated");}catch(Exception e){return new(EngineKey,Kind,EngineAvailability.Unavailable,e.Message);}}async Task<OriginateResult> Originate(CallRequest r,string endpoint,CancellationToken ct){Valid(r);var id=r.PlatformCallId.ToString("N");await Send(HttpMethod.Post,$"channels/{id}?endpoint={Uri.EscapeDataString(endpoint)}&app={Uri.EscapeDataString(Options.AriApp??"icpaas")}&callerId={Uri.EscapeDataString(r.CallerId??"")}",ct);return new(new(r.PlatformCallId,EngineKey,id),"accepted",DateTimeOffset.UtcNow);}public override Task<OriginateResult> OriginateAsync(CallRequest r,CancellationToken ct)=>Originate(r,$"PJSIP/{r.Destination}@{r.TrunkKey??"default"}",ct);public Task<OriginateResult> OriginateEndpointAsync(CallRequest r,string endpoint,CancellationToken ct)=>Originate(r,endpoint,ct);public async Task<OriginateResult> OriginateSupervisionAsync(CallRequest r,string endpoint,CallRef target,string mode,CancellationToken ct){var supervisor=await Originate(r,endpoint,ct);var snoop=Guid.NewGuid().ToString("N");var bridge=$"supervise-{r.PlatformCallId:N}";var spy=mode=="whisper"?"none":"both";var whisper=mode=="listen"?"none":"both";if(mode is not ("listen" or "whisper" or "barge"))throw new ArgumentException("Invalid supervision mode");await Send(HttpMethod.Post,$"channels/{target.EngineCallId}/snoop/{snoop}?app={Uri.EscapeDataString(Options.AriApp??"icpaas")}&spy={spy}&whisper={whisper}",ct);await Send(HttpMethod.Post,$"bridges/{bridge}?type=mixing",ct);await Send(HttpMethod.Post,$"bridges/{bridge}/addChannel?channel={Uri.EscapeDataString($"{supervisor.Call.EngineCallId},{snoop}")}",ct);return supervisor;}public Task StartRecordingAsync(CallRef call,string storageKey,CancellationToken ct)=>Send(HttpMethod.Post,$"channels/{call.EngineCallId}/record?name={Uri.EscapeDataString(storageKey)}&format=wav&ifExists=overwrite&beep=false",ct);Task Act(CallRef c,string p,HttpMethod m,CancellationToken ct)=>Send(m,$"channels/{c.EngineCallId}/{p}",ct);public override Task AnswerAsync(CallRef c,CancellationToken ct)=>Act(c,"answer",HttpMethod.Post,ct);public override Task HangupAsync(CallRef c,string r,CancellationToken ct)=>Act(c,"",HttpMethod.Delete,ct);public override Task HoldAsync(CallRef c,bool e,CancellationToken ct)=>Act(c,e?"hold":"unhold",HttpMethod.Post,ct);public override Task SendDtmfAsync(CallRef c,string d,CancellationToken ct)=>Act(c,$"dtmf?dtmf={Uri.EscapeDataString(d)}",HttpMethod.Post,ct);public override Task TransferAsync(CallRef c,TransferRequest r,CancellationToken ct)=>Act(c,$"redirect?endpoint={Uri.EscapeDataString(r.Destination)}",HttpMethod.Post,ct);public override async Task BridgeAsync(CallRef a,CallRef b,CancellationToken ct){var bridge=$"icpaas-{a.PlatformCallId:N}-{b.PlatformCallId:N}";await Send(HttpMethod.Post,$"bridges/{bridge}?type=mixing",ct);await Send(HttpMethod.Post,$"bridges/{bridge}/addChannel?channel={Uri.EscapeDataString($"{a.EngineCallId},{b.EngineCallId}")}",ct);}public override IAsyncEnumerable<TelephonyEvent> SubscribeAsync(CancellationToken ct)=>events.SubscribeAsync(ct);}
public sealed class GenericSipEngine:IcpaaS.Telephony.ConfigurableEngine{readonly HttpClient http=new();public GenericSipEngine(IOptions<PlatformOptions> o):base("generic-sip",TelephonyEngineKind.GenericSip,o.Value.Telephony.GenericSip){if(!string.IsNullOrWhiteSpace(Options.ApiToken))http.DefaultRequestHeaders.Authorization=new("Bearer",Options.ApiToken);}async Task<JsonDocument> Post(string p,object b,CancellationToken ct){var url=Options.ControlWebhook?.TrimEnd('/')??throw new InvalidOperationException("Control webhook missing");var x=await http.PostAsync($"{url}/{p}",new StringContent(JsonSerializer.Serialize(b),Encoding.UTF8,"application/json"),ct);if(!x.IsSuccessStatusCode)throw new InvalidOperationException($"Gateway returned {(int)x.StatusCode}");return JsonDocument.Parse(await x.Content.ReadAsStringAsync(ct));}public override async Task<EngineHealth> ProbeAsync(CancellationToken ct){if(!Options.Enabled)return new(EngineKey,Kind,EngineAvailability.Disabled,"Disabled");try{var x=await http.GetAsync($"{Options.ControlWebhook?.TrimEnd('/')}/health",ct);return new(EngineKey,Kind,x.IsSuccessStatusCode?EngineAvailability.Ready:EngineAvailability.Degraded,$"Gateway {(int)x.StatusCode}");}catch(Exception e){return new(EngineKey,Kind,EngineAvailability.Unavailable,e.Message);}}public override async Task<OriginateResult> OriginateAsync(CallRequest r,CancellationToken ct){Valid(r);using var d=await Post("calls",r,ct);var id=d.RootElement.TryGetProperty("callId",out var v)?v.GetString():null;return new(new(r.PlatformCallId,EngineKey,id),"accepted",DateTimeOffset.UtcNow);}async Task Act(CallRef c,string p,object b,CancellationToken ct){using var _=await Post($"calls/{c.EngineCallId}/{p}",b,ct);}public override Task AnswerAsync(CallRef c,CancellationToken ct)=>Act(c,"answer",new{},ct);public override Task HangupAsync(CallRef c,string r,CancellationToken ct)=>Act(c,"hangup",new{reason=r},ct);public override Task HoldAsync(CallRef c,bool e,CancellationToken ct)=>Act(c,"hold",new{enabled=e},ct);public override Task SendDtmfAsync(CallRef c,string d,CancellationToken ct)=>Act(c,"dtmf",new{digits=d},ct);public override Task TransferAsync(CallRef c,TransferRequest r,CancellationToken ct)=>Act(c,"transfer",r,ct);}
