using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Checkers.Api;

internal static class RateLimitPolicies
{
    /// <summary>Per-client concurrency limit for requests that occupy an engine worker.</summary>
    public const string Engine = "engine";

    /// <summary>429 problem response with Retry-After, in the same shape as every other error.</summary>
    public static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        var limits = http.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        http.Response.Headers.RetryAfter = "1";
        await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails =
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many engine requests from this client.",
                Detail = $"At most {limits.PermitLimitPerClient} move suggestions per client may run at once.",
            },
        });
    }
}
