using CatMeme.Api.Api;
using CatMeme.Api.Data;
using CatMeme.Api.Jobs;
using CatMeme.Api.Services;
using FluentValidation;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Serilog;

// .env sits next to the app or at the repo root; both are checked.
foreach (var candidate in new[]
         {
             Path.Combine(AppContext.BaseDirectory, ".env"),
             Path.Combine(Directory.GetCurrentDirectory(), ".env"),
             Path.Combine(Directory.GetCurrentDirectory(), "..", "..", ".env"),
         })
{
    if (File.Exists(candidate)) { DotNetEnv.Env.Load(candidate); break; }
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

var paths = new PipelinePaths(builder.Configuration);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Hangfire", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(paths.Logs, "catmeme-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

builder.Host.UseSerilog();

var connectionString = builder.Configuration["DB_CONNECTION_STRING"]
    ?? builder.Configuration.GetConnectionString("Default")
    ?? @"Server=(localdb)\MSSQLLocalDB;Database=CatMemePipeline;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True";

builder.Services.AddSingleton(paths);
builder.Services.AddHttpClient();

builder.Services.AddDbContext<CatMemeDbContext>(o => o.UseSqlServer(connectionString));

builder.Services.AddScoped<ProcessRunner>();
builder.Services.AddScoped<FFmpegService>();
builder.Services.AddScoped<RenderService>();
builder.Services.AddScoped<RenderPreflight>();
builder.Services.AddScoped<VideoVerifier>();
builder.Services.AddScoped<ClaudeCliService>();
builder.Services.AddScoped<BackgroundAssetService>();
builder.Services.AddScoped<ClipLibraryService>();
builder.Services.AddPairedInteractionAssets();
builder.Services.AddScoped<StoryboardCastabilityService>();
builder.Services.AddScoped<IVisualFrameSampler, FfmpegVisualFrameSampler>();
builder.Services.AddScoped<IVisualQualitySecondaryReviewer, LocalCliVisualQualitySecondaryReviewer>();
builder.Services.AddScoped<VisualQualityReviewService>();
builder.Services.AddScoped<VisualQualityBenchmarkRunner>();
builder.Services.AddScoped<GraphApiService>();

builder.Services.AddScoped<TopicGenerationJob>();
builder.Services.AddScoped<AssetGenerationJob>();
builder.Services.AddScoped<VideoEditJob>();
builder.Services.AddScoped<PostSchedulerJob>();
builder.Services.AddScoped<VisualQualityBenchmarkJob>();

builder.Services.AddValidatorsFromAssemblyContaining<BatchRequestValidator>();

builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
    {
        CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
        SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
        QueuePollInterval = TimeSpan.FromSeconds(5),
        UseRecommendedIsolationLevel = true,
        DisableGlobalLocks = true,
    }));

// General jobs keep their worker pool. CPU-heavy FFmpeg jobs are routed by
// VideoRenderQueueFilter to a dedicated single-worker queue below.
builder.Services.AddHangfireServer(o =>
{
    o.ServerName = $"{Environment.MachineName}:general";
    o.Queues = ["default"];
    o.WorkerCount = Math.Max(2, Environment.ProcessorCount / 4);
    o.ServerTimeout = TimeSpan.FromMinutes(30);
});

builder.Services.AddHangfireServer(o =>
{
    o.ServerName = $"{Environment.MachineName}:video-render";
    o.Queues = [VideoRenderQueueFilter.RenderQueue];
    o.WorkerCount = 1;
    o.ServerTimeout = TimeSpan.FromMinutes(30);
});

builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 512L * 1024 * 1024);

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

// Queue routing is a state-election filter so it also covers AutomaticRetry transitions.
// The server filter holds a SQL-backed per-video lease for the entire edit job, protecting
// the output even if another app instance or a duplicate Hangfire delivery is involved.
GlobalJobFilters.Filters.Add(new VideoRenderQueueFilter());
GlobalJobFilters.Filters.Add(new VideoRenderGuard(
    app.Services.GetRequiredService<IServiceScopeFactory>(),
    app.Services.GetRequiredService<ILogger<VideoRenderGuard>>()));

// Create/upgrade the database before Hangfire's server starts polling it.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CatMemeDbContext>();
    await db.Database.MigrateAsync();
    await SettingsAccessor.GetAsync(db);
    Log.Information("Database ready ({Server})", connectionString.Split(';')[0]);
}

app.UseSerilogRequestLogging();
app.UseCors();

app.UseDefaultFiles();
app.UseStaticFiles();

// Finished reels + thumbnails, served to the UI's <video> players.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(paths.Output),
    RequestPath = "/media",
    ServeUnknownFileTypes = true,
});

app.MapCatMemeApi();
app.MapHangfireDashboard("/hangfire");

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    pipeline = paths.Base,
    time = DateTime.UtcNow,
}));

// SPA fallback: any non-API route renders the React app.
app.MapFallbackToFile("index.html");

RecurringJob.AddOrUpdate<PostSchedulerJob>(
    "post-scheduler",
    j => j.RunAsync(CancellationToken.None),
    "* * * * *");

Log.Information("CatMeme pipeline ready. Pipeline root: {Base}", paths.Base);

app.Run();
