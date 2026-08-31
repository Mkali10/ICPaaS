using Microsoft.AspNetCore.Diagnostics;
using Npgsql;

namespace IcpaaS.Api;

public static class ApiExceptionHandler
{
    public static async Task Write(HttpContext context)
    {
        var error=context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var (status,message)=error switch
        {
            UnauthorizedAccessException ex=>(StatusCodes.Status403Forbidden,ex.Message),
            ArgumentException ex=>(StatusCodes.Status400BadRequest,ex.Message),
            PostgresException ex when ex.SqlState is "23503" or "23505"=>(StatusCodes.Status409Conflict,"The requested change conflicts with existing data."),
            InvalidOperationException ex=>(StatusCodes.Status409Conflict,ex.Message),
            _=>(StatusCodes.Status500InternalServerError,"The server could not complete this request.")
        };
        context.Response.StatusCode=status;
        context.Response.ContentType="application/problem+json";
        await context.Response.WriteAsJsonAsync(new{status,error=message,traceId=context.TraceIdentifier});
    }
}
