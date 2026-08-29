using System.Globalization;
using System.Security.Claims;
using System.Text;
using Npgsql;

namespace IcpaaS.Api;

public static class ReportingEndpoints
{
    static Guid Tenant(ClaimsPrincipal user)=>Guid.Parse(user.FindFirstValue("tenant_id")??throw new UnauthorizedAccessException("Tenant required"));
    static bool Allowed(ClaimsPrincipal user)=>user.IsInRole("tenant_owner")||user.IsInRole("tenant_admin")||user.IsInRole("supervisor")||user.IsInRole("auditor")||user.IsInRole("billing_admin");
    public static void MapReports(this WebApplication app)
    {
        var reports=app.MapGroup("/api/v1/reports").RequireAuthorization();
        reports.MapGet("/summary",Summary);
        reports.MapGet("/calls.csv",CallsCsv);
        reports.MapGet("/outcomes.csv",OutcomesCsv);
    }

    static async Task<IResult> Summary(DateTimeOffset? from,DateTimeOffset? to,ClaimsPrincipal user,PlatformStore store,CancellationToken ct)
    {
        if(!Allowed(user))return Results.Forbid();var range=Range(from,to);if(range is null)return Results.BadRequest(new{error="Report range must be valid and no longer than 366 days"});var (start,end)=range.Value;
        await using var connection=await store.Open(ct);var tenant=Tenant(user);
        var totals=await One(new NpgsqlCommand(@"SELECT count(*) total_calls,count(*) FILTER(WHERE answered_at IS NOT NULL) answered,count(*) FILTER(WHERE answered_at IS NULL AND ended_at IS NOT NULL) unanswered,count(*) FILTER(WHERE direction='inbound' AND answered_at IS NULL AND ended_at IS NOT NULL) abandoned,count(*) FILTER(WHERE direction='inbound') inbound,count(*) FILTER(WHERE direction='outbound') outbound,round(coalesce(avg(extract(epoch FROM ended_at-answered_at)) FILTER(WHERE answered_at IS NOT NULL AND ended_at IS NOT NULL),0)) average_talk_seconds,round(coalesce(100.0*count(*) FILTER(WHERE answered_at IS NOT NULL)/nullif(count(*),0),0),2) answer_rate FROM calls WHERE tenant_id=$1 AND created_at>=$2 AND created_at<$3",connection).With(tenant,start,end),ct);
        var daily=await Rows(new NpgsqlCommand(@"SELECT created_at::date AS "day",count(*) total,count(*) FILTER(WHERE answered_at IS NOT NULL) answered,count(*) FILTER(WHERE direction='inbound') inbound,count(*) FILTER(WHERE direction='outbound') outbound FROM calls WHERE tenant_id=$1 AND created_at>=$2 AND created_at<$3 GROUP BY created_at::date ORDER BY "day"",connection).With(tenant,start,end),ct);
        var campaigns=await Rows(new NpgsqlCommand(@"SELECT coalesce(c.name,'Unassigned') campaign,count(cu.id) calls,count(cu.id) FILTER(WHERE cu.answered_at IS NOT NULL) answered,count(cu.id) FILTER(WHERE cu.ended_at IS NOT NULL) completed,round(coalesce(avg(extract(epoch FROM cu.ended_at-cu.answered_at)) FILTER(WHERE cu.answered_at IS NOT NULL AND cu.ended_at IS NOT NULL),0)) average_talk_seconds FROM calls cu LEFT JOIN campaigns c ON c.id=cu.campaign_id WHERE cu.tenant_id=$1 AND cu.created_at>=$2 AND cu.created_at<$3 GROUP BY c.id,c.name ORDER BY calls DESC",connection).With(tenant,start,end),ct);
        var agents=await Rows(new NpgsqlCommand(@"SELECT u.display_name agent,count(DISTINCT o.id) outcomes,count(DISTINCT o.call_id) FILTER(WHERE o.call_id IS NOT NULL) handled_calls,count(*) FILTER(WHERE d.category='callback') callbacks FROM call_outcomes o JOIN users u ON u.id=o.agent_user_id JOIN dispositions d ON d.id=o.disposition_id WHERE o.tenant_id=$1 AND o.created_at>=$2 AND o.created_at<$3 GROUP BY u.id,u.display_name ORDER BY outcomes DESC",connection).With(tenant,start,end),ct);
        var dispositions=await Rows(new NpgsqlCommand(@"SELECT d.name disposition,d.category,count(*) total FROM call_outcomes o JOIN dispositions d ON d.id=o.disposition_id WHERE o.tenant_id=$1 AND o.created_at>=$2 AND o.created_at<$3 GROUP BY d.id,d.name,d.category ORDER BY total DESC",connection).With(tenant,start,end),ct);
        return Results.Ok(new{from=start,to=end,totals,daily,campaigns,agents,dispositions});
    }

    static async Task<IResult> CallsCsv(DateTimeOffset? from,DateTimeOffset? to,ClaimsPrincipal user,PlatformStore store,CancellationToken ct)
    {
        if(!Allowed(user))return Results.Forbid();var range=Range(from,to);if(range is null)return Results.BadRequest();var (start,end)=range.Value;await using var connection=await store.Open(ct);
        await using var command=new NpgsqlCommand(@"SELECT cu.id,cu.created_at,cu.direction,cu.from_number,cu.to_number,cu.state,cu.engine_type,p.name process,c.name campaign,cu.answered_at,cu.ended_at,CASE WHEN cu.answered_at IS NOT NULL AND cu.ended_at IS NOT NULL THEN extract(epoch FROM cu.ended_at-cu.answered_at)::integer END talk_seconds,cu.hangup_cause FROM calls cu LEFT JOIN processes p ON p.id=cu.process_id LEFT JOIN campaigns c ON c.id=cu.campaign_id WHERE cu.tenant_id=$1 AND cu.created_at>=$2 AND cu.created_at<$3 ORDER BY cu.created_at",connection);command.With(Tenant(user),start,end);
        return Results.File(Encoding.UTF8.GetBytes(await Csv(command,ct)),"text/csv; charset=utf-8",$"calls-{start:yyyyMMdd}-{end:yyyyMMdd}.csv");
    }

    static async Task<IResult> OutcomesCsv(DateTimeOffset? from,DateTimeOffset? to,ClaimsPrincipal user,PlatformStore store,CancellationToken ct)
    {
        if(!Allowed(user))return Results.Forbid();var range=Range(from,to);if(range is null)return Results.BadRequest();var (start,end)=range.Value;await using var connection=await store.Open(ct);
        await using var command=new NpgsqlCommand(@"SELECT o.id,o.created_at,o.call_id,c.name campaign,ct.external_id,ct.phone_number,u.display_name agent,d.name disposition,sd.name sub_disposition,o.remark,o.callback_at FROM call_outcomes o LEFT JOIN campaigns c ON c.id=o.campaign_id LEFT JOIN contacts ct ON ct.id=o.contact_id LEFT JOIN users u ON u.id=o.agent_user_id JOIN dispositions d ON d.id=o.disposition_id LEFT JOIN dispositions sd ON sd.id=o.sub_disposition_id WHERE o.tenant_id=$1 AND o.created_at>=$2 AND o.created_at<$3 ORDER BY o.created_at",connection);command.With(Tenant(user),start,end);
        return Results.File(Encoding.UTF8.GetBytes(await Csv(command,ct)),"text/csv; charset=utf-8",$"outcomes-{start:yyyyMMdd}-{end:yyyyMMdd}.csv");
    }

    static (DateTimeOffset Start,DateTimeOffset End)? Range(DateTimeOffset? from,DateTimeOffset? to){var end=to??DateTimeOffset.UtcNow.AddDays(1);var start=from??end.AddDays(-30);return end>start&&end-start<=TimeSpan.FromDays(366)?(start,end):null;}
    static async Task<string> Csv(NpgsqlCommand command,CancellationToken ct){var output=new StringBuilder();await using var reader=await command.ExecuteReaderAsync(ct);for(var i=0;i<reader.FieldCount;i++){if(i>0)output.Append(',');output.Append(Escape(reader.GetName(i)));}output.AppendLine();while(await reader.ReadAsync(ct)){for(var i=0;i<reader.FieldCount;i++){if(i>0)output.Append(',');var value=reader.IsDBNull(i)?"":Convert.ToString(reader.GetValue(i),CultureInfo.InvariantCulture)??"";output.Append(Escape(value));}output.AppendLine();}return output.ToString();}
    static string Escape(string value)=>value.IndexOfAny([',','"','\r','\n'])>=0?$"\"{value.Replace("\"","\"\"")}\"":value;
    static async Task<Dictionary<string,object?>> One(NpgsqlCommand command,CancellationToken ct){await using var reader=await command.ExecuteReaderAsync(ct);await reader.ReadAsync(ct);return Row(reader);}
    static async Task<List<Dictionary<string,object?>>> Rows(NpgsqlCommand command,CancellationToken ct){await using var reader=await command.ExecuteReaderAsync(ct);var rows=new List<Dictionary<string,object?>>();while(await reader.ReadAsync(ct))rows.Add(Row(reader));return rows;}
    static Dictionary<string,object?> Row(NpgsqlDataReader reader){var row=new Dictionary<string,object?>();for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);return row;}
    static NpgsqlCommand With(this NpgsqlCommand command,params object[] values){foreach(var value in values)command.Parameters.AddWithValue(value);return command;}
}
