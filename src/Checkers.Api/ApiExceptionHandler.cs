using Checkers.Api.Engine;
using Checkers.Api.Moves;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Checkers.Api;

/// <summary>
/// Maps exceptions to problem+json responses: invalid content 422, no engine worker 503,
/// engine errors 500. (Timeouts are turned into 504 by the controller, which owns the deadline.)
/// </summary>
internal sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails problem = exception switch
        {
            RequestValidationException invalid => new ValidationProblemDetails(invalid.Errors)
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "The request is not valid.",
            },
            EngineUnavailableException => new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "No engine worker is available.",
                Detail = exception.Message,
            },
            EngineFailureException => new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "The engine failed.",
                Detail = exception.Message,
            },
            _ => new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Unexpected error.",
            },
        };

        if (problem.Status >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Request {RequestId} failed with {StatusCode}", context.TraceIdentifier, problem.Status);
        }

        context.Response.StatusCode = problem.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
