using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Checkers.Api;
using Checkers.Api.Caching;
using Checkers.Api.Contracts;
using Checkers.Api.Engine;
using Checkers.Api.Logging;
using Checkers.Api.Moves;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// One JSON object per log line (compact log event format) to stdout and to rolling files.
var logFiles = builder.Configuration.GetSection(LogFileOptions.Section).Get<LogFileOptions>() ?? new LogFileOptions();
builder.Services.AddSerilog(logger =>
{
    logger.ReadFrom.Configuration(builder.Configuration).WriteTo.Console(new CompactJsonFormatter());
    if (!string.IsNullOrWhiteSpace(logFiles.Directory))
    {
        logger.WriteTo.File(
            new CompactJsonFormatter(),
            Path.Combine(Path.GetFullPath(logFiles.Directory, builder.Environment.ContentRootPath), "checkers-api-.json"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: logFiles.RetainedFiles,
            fileSizeLimitBytes: logFiles.FileSizeLimitMb * 1024L * 1024,
            rollOnFileSizeLimit: true);
    }
});

builder.Services.AddOptions<EngineOptions>()
    .Bind(builder.Configuration.GetSection(EngineOptions.Section))
    .ValidateDataAnnotations()
    .Validate(
        o => !o.Type.Equals("chinook", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(o.Path),
        "Engine:Path (the Kingsrow64.dll path) is required when Engine:Type is 'chinook'.")
    .ValidateOnStart();
builder.Services.AddOptions<CacheOptions>().Bind(builder.Configuration.GetSection(CacheOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<LimitsOptions>().Bind(builder.Configuration.GetSection(LimitsOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<RateLimitOptions>().Bind(builder.Configuration.GetSection(RateLimitOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.Configure<Dictionary<string, LevelOptions>>(builder.Configuration.GetSection("Levels"));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
    return new LruCache<string, SuggestMoveResponse>(
        options.Capacity, TimeSpan.FromMinutes(options.TtlMinutes), sp.GetRequiredService<TimeProvider>(), StringComparer.Ordinal);
});
builder.Services.AddSingleton<LevelPolicy>();
builder.Services.AddSingleton<MoveSuggestionService>();

// Long-lived worker processes, started and warmed up before the server accepts the first request.
builder.Services.AddSingleton<IEnginePool, EngineWorkerPool>();
builder.Services.AddHostedService<EnginePoolStartup>();
builder.Services.AddHostedService<StartupWarmUp>();

// A client may hold only a few engine requests at a time, so it cannot occupy every worker.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy(RateLimitPolicies.Engine, http =>
    {
        var limits = http.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
        return RateLimitPartition.GetConcurrencyLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = limits.PermitLimitPerClient,
                QueueLimit = limits.QueueLimitPerClient,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });
    });
    options.OnRejected = RateLimitPolicies.WriteRejectionAsync;
});

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.JsonSerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context => context.ProblemDetails.Extensions["requestId"] = context.HttpContext.TraceIdentifier);
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

var app = builder.Build();

app.UseMiddleware<RequestLogMiddleware>();
app.UseExceptionHandler();
app.UseDefaultFiles();   // the test board at /
app.UseStaticFiles();
app.UseRateLimiter();
app.MapControllers();

app.Run();

/// <summary>Entry point; public so integration tests can host the app.</summary>
public partial class Program;
