using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RateLimiter.Function.Services;
using StackExchange.Redis;

var host = new HostBuilder()
    .ConfigureAppConfiguration((context, config) =>
    {
        // Load local.settings.json for local development (func CLI does this automatically,
        // but dotnet run does not). The "Values" section is flattened into config keys.
        config.AddJsonFile("local.settings.json", optional: true, reloadOnChange: false)
              .AddEnvironmentVariables();
    })
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        // Application Insights
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Redis — singleton ConnectionMultiplexer (thread-safe, reuse across requests)
        // Try "Values:RedisConnectionString" (local.settings.json nesting) first,
        // then fall back to "RedisConnectionString" (env var / App Settings)
        var redisConnectionString =
            context.Configuration["Values:RedisConnectionString"]
            ?? context.Configuration["RedisConnectionString"]
            ?? throw new InvalidOperationException(
                "RedisConnectionString is not configured. " +
                "Set it in local.settings.json or Azure App Settings.");

        var redisOptions = ConfigurationOptions.Parse(redisConnectionString);
        redisOptions.AbortOnConnectFail = false;       // Retry on transient failures
        redisOptions.ConnectRetry = 3;
        redisOptions.ConnectTimeout = 5000;            // 5s connect timeout
        redisOptions.SyncTimeout = 2000;               // 2s sync operation timeout
        redisOptions.AsyncTimeout = 2000;              // 2s async operation timeout

        services.AddSingleton<IConnectionMultiplexer>(
            ConnectionMultiplexer.Connect(redisOptions));

        // Services
        services.AddSingleton<ITokenBucketService, TokenBucketService>();
    })
    .ConfigureLogging(logging =>
    {
        logging.SetMinimumLevel(LogLevel.Information);
        logging.AddFilter("Microsoft", LogLevel.Warning);
        logging.AddFilter("System", LogLevel.Warning);
    })
    .Build();

host.Run();

