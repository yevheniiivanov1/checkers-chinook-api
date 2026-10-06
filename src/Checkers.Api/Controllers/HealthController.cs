using Checkers.Api.Caching;
using Checkers.Api.Contracts;
using Checkers.Api.Engine;
using Microsoft.AspNetCore.Mvc;

namespace Checkers.Api.Controllers;

[ApiController]
[Produces("application/json")]
public sealed class HealthController(IEnginePool pool, LruCache<string, SuggestMoveResponse> cache) : ControllerBase
{
    /// <summary>200 with ok=true while at least one engine worker is ready; 503 otherwise.</summary>
    [HttpGet("/healthz")]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult Get()
    {
        var status = pool.Status;
        var ok = status.ReadyWorkers > 0;
        var body = new HealthResponse(ok, status.ReadyWorkers)
        {
            ConfiguredWorkers = status.ConfiguredWorkers,
            Engine = status.EngineType,
            EngineName = status.EngineName,
            TablebasePieces = status.TablebasePieces,
            CacheEntries = cache.Count,
        };

        return ok ? Ok(body) : StatusCode(StatusCodes.Status503ServiceUnavailable, body);
    }
}
