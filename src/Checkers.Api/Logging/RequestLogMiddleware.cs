using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Checkers.Api.Logging;

/// <summary>Engine figures a controller attaches to the request for the request log.</summary>
public sealed record EngineRequestMetrics(int? Depth, long? Nodes, bool? TablebaseHit, bool Cached, string? Level, int? Worker, string? BestMove);

/// <summary>
/// Writes exactly one structured log event per API request — with the JSON console formatter that
/// is one JSON line carrying requestId, timeMs, depth, nodes and tablebaseHit. It sits outside the
/// exception handler so the logged status is the one the client received.
/// </summary>
/// <remarks>
/// The request id is taken from an <c>X-Request-Id</c> header when the caller sends one of 1–64
/// characters <c>[A-Za-z0-9._-]</c>, otherwise generated; it is echoed back in the response header
/// and returned as <c>requestId</c> in problem responses.
/// </remarks>
public sealed partial class RequestLogMiddleware(RequestDelegate next, ILogger<RequestLogMiddleware> logger)
{
    private const string RequestIdHeader = "X-Request-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/v1") && !context.Request.Path.StartsWithSegments("/healthz"))
        {
            await next(context);
            return;
        }

        // A caller's id goes into logs and back into a response header, so only a safe shape is taken.
        var requestId = context.Request.Headers[RequestIdHeader].ToString();
        if (!SafeRequestId().IsMatch(requestId))
        {
            requestId = Guid.NewGuid().ToString("N");
        }

        context.TraceIdentifier = requestId;
        // Set just before sending: the exception handler clears headers when it writes a problem.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[RequestIdHeader] = requestId;
            return Task.CompletedTask;
        });

        var clock = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            var metrics = context.Features.Get<EngineRequestMetrics>();
            Log.Request(
                logger,
                requestId,
                context.Request.Method,
                context.Request.Path.Value,
                context.Response.StatusCode,
                clock.ElapsedMilliseconds,
                metrics?.Depth,
                metrics?.Nodes,
                metrics?.TablebaseHit,
                metrics?.Cached,
                metrics?.Level,
                metrics?.Worker,
                metrics?.BestMove);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex SafeRequestId();

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1,
            EventName = "Request",
            Level = LogLevel.Information,
            Message = "{Method} {Path} -> {StatusCode} in {TimeMs} ms; requestId={RequestId} depth={Depth} nodes={Nodes} " +
                      "tablebaseHit={TablebaseHit} cached={Cached} level={Level} worker={Worker} bestMove={BestMove}")]
        public static partial void Request(
            ILogger logger,
            string requestId,
            string method,
            string? path,
            int statusCode,
            long timeMs,
            int? depth,
            long? nodes,
            bool? tablebaseHit,
            bool? cached,
            string? level,
            int? worker,
            string? bestMove);
    }
}
