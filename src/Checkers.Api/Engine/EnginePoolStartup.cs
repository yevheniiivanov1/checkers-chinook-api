namespace Checkers.Api.Engine;

/// <summary>
/// Starts and stops the registered <see cref="IEnginePool"/> with the host when it needs it (the
/// process pool does; a test double need not). Hosted services start before the server listens,
/// so the workers are up and warm before the first request.
/// </summary>
internal sealed class EnginePoolStartup(IEnginePool pool) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        pool is IHostedService hosted ? hosted.StartAsync(cancellationToken) : Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) =>
        pool is IHostedService hosted ? hosted.StopAsync(cancellationToken) : Task.CompletedTask;
}
