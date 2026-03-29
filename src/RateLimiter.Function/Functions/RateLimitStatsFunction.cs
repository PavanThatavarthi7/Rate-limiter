using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using RateLimiter.Function.Services;

namespace RateLimiter.Function.Functions;

/// <summary>
/// GET /api/rate-limit/stats — Returns a live snapshot of all active
/// user buckets from Redis. Powers the dashboard.
/// </summary>
public sealed class RateLimitStatsFunction
{
    private readonly ITokenBucketService _tokenBucket;
    private readonly ILogger<RateLimitStatsFunction> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RateLimitStatsFunction(
        ITokenBucketService tokenBucket,
        ILogger<RateLimitStatsFunction> logger)
    {
        _tokenBucket = tokenBucket;
        _logger = logger;
    }

    [Function("RateLimitStats")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "rate-limit/stats")]
        HttpRequestData req)
    {
        var stats = await _tokenBucket.GetAllStatsAsync();
        var recent = await _tokenBucket.GetRecentTransactionsAsync();

        var payload = new
        {
            users = stats.Select(s => new
            {
                oid         = s.Oid,
                tokens      = Math.Round(s.Tokens, 3),
                ttlSeconds  = s.TtlSeconds,
                lastSeen    = DateTimeOffset.FromUnixTimeMilliseconds(s.LastTimestampUs / 1000)
                                .ToString("HH:mm:ss")
            }).ToList(),

            recent = recent.Select(r => new
            {
                oid       = r.Oid,
                allowed   = r.Allowed,
                remaining = r.Remaining,
                limit     = r.Limit,
                timestamp = r.Timestamp
            }).ToList()
        };

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        // Allow browser to load the dashboard from any origin  
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, JsonOptions));
        return response;
    }
}
