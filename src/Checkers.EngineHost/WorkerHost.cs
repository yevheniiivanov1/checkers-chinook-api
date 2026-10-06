using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Checkers.Core;
using Checkers.Core.Worker;
using Checkers.EngineHost.Builtin;
using Checkers.EngineHost.KingsRow;

namespace Checkers.EngineHost;

/// <summary>
/// The worker's protocol loop. The calling thread reads stdin so that a <c>stop</c> is seen while
/// a search is running; every other request is queued to one engine thread, because engine DLLs
/// written for CheckerBoard keep global state and are not re-entrant.
/// </summary>
internal sealed class WorkerHost(TextWriter output)
{
    // Deep alpha-beta recursion inside a native engine needs more than the default 1 MB.
    private const int EngineThreadStackBytes = 16 * 1024 * 1024;

    private readonly BlockingCollection<WorkerRequest> _queue = new();
    private readonly SearchControl _control = new();
    private readonly Lock _writeLock = new();
    private IEngine? _engine;
    private Position? _position;

    public void Run(Stream input)
    {
        var engineThread = new Thread(ProcessQueue, EngineThreadStackBytes) { Name = "engine", IsBackground = true };
        engineThread.Start();

        using (var reader = new StreamReader(input, Encoding.UTF8))
        {
            while (reader.ReadLine() is { } line)
            {
                if (TryRead(line) is not { } request)
                {
                    continue;
                }

                if (request.Type == WorkerCommands.Stop)
                {
                    _control.RequestStop(request.Id);
                    continue;
                }

                _queue.Add(request);
            }
        }

        // stdin closed: the API has shut down or dropped this worker. Abandon any search and exit.
        Log("stdin closed; exiting");
        _control.StopCurrent();
        _queue.CompleteAdding();
        if (engineThread.Join(TimeSpan.FromSeconds(2)))
        {
            _engine?.Dispose();
        }

        // Otherwise a search is still inside the DLL using the engine's buffers; the process exit
        // that follows releases everything anyway.
    }

    private WorkerRequest? TryRead(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(line, WorkerJsonContext.Default.WorkerRequest);
        }
        catch (JsonException ex)
        {
            // Id 0 is never used by the API, so it treats this as a broken channel, not a reply.
            Log($"malformed request: {ex.Message}");
            Write(new WorkerResponse(0, false) { Error = $"Malformed request: {ex.Message}" });
            return null;
        }
    }

    private void ProcessQueue()
    {
        foreach (var request in _queue.GetConsumingEnumerable())
        {
            WorkerResponse response;
            try
            {
                response = Handle(request);
            }
            catch (Exception ex)
            {
                Log($"{request.Type} #{request.Id} failed: {ex}");
                response = new WorkerResponse(request.Id, false) { Error = ex.Message };
            }

            Write(response);
        }
    }

    private WorkerResponse Handle(WorkerRequest request)
    {
        switch (request.Type)
        {
            case WorkerCommands.Init:
                var init = request.Init ?? throw new InvalidOperationException("init without parameters.");
                _engine?.Dispose();
                _engine = CreateEngine(init.Engine);
                return new WorkerResponse(request.Id, true) { Engine = _engine.Initialize(init) };

            case WorkerCommands.SetPosition:
                if (!Pdn.TryParse(request.Position, out var position, out var errors))
                {
                    return new WorkerResponse(request.Id, false) { Error = string.Join(" ", errors) };
                }

                _position = position;
                return new WorkerResponse(request.Id, true);

            case WorkerCommands.Search:
                var engine = _engine ?? throw new InvalidOperationException("search before init.");
                var root = _position ?? throw new InvalidOperationException("search before position.");
                var limits = request.Limits ?? throw new InvalidOperationException("search without limits.");
                _control.Begin(request.Id);
                try
                {
                    return new WorkerResponse(request.Id, true) { Result = engine.Search(root, limits, _control) };
                }
                finally
                {
                    _control.End();
                }

            case WorkerCommands.Ping:
                return new WorkerResponse(request.Id, true);

            default:
                return new WorkerResponse(request.Id, false) { Error = $"Unknown request type '{request.Type}'." };
        }
    }

    private static IEngine CreateEngine(string name) => name.ToLowerInvariant() switch
    {
        "chinook" or "kingsrow" => new KingsRowEngine(),
        "builtin" => new BuiltinEngine(),
        _ => throw new InvalidOperationException($"Unknown engine type '{name}'. Use 'chinook' or 'builtin'."),
    };

    private void Write(WorkerResponse response)
    {
        var json = JsonSerializer.Serialize(response, WorkerJsonContext.Default.WorkerResponse);
        lock (_writeLock)
        {
            output.WriteLine(json);
            output.Flush();
        }
    }

    public static void Log(string message) =>
        Console.Error.WriteLine($"[engine-host {Environment.ProcessId}] {message}");
}
