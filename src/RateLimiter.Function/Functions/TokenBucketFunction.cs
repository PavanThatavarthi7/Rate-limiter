using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using RateLimiter.Function.Models;
using RateLimiter.Function.Services;

namespace RateLimiter.Function.Functions;

/// <summary>
/// Azure Function HTTP endpoint called by APIM to perform token bucket rate limiting.
/// 
/// Flow:
///   1. APIM validates JWT, extracts OID/burst/rps from claims
///   2. APIM POSTs { oid, burst, rps } to this function
///   3. Function checks Redis via atomic Lua script
///   4. Returns 200 (allowed) or 429 (throttled) with rate-limit headers
/// </summary>
public sealed class TokenBucketFunction
{
    private readonly ITokenBucketService _tokenBucket;
    private readonly ILogger<TokenBucketFunction> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public TokenBucketFunction(
        ITokenBucketService tokenBucket,
        ILogger<TokenBucketFunction> logger)
    {
        _tokenBucket = tokenBucket;
        _logger = logger;
    }

    [Function("RateLimitCheck")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "rate-limit/check")]
        HttpRequestData req)
    {
        // --- 1. Parse & validate request ---
        RateLimitRequest? request;
        try
        {
            var body = await req.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body))
            {
                return await CreateErrorResponseAsync(req, HttpStatusCode.BadRequest,
                    "Request body is empty.");
            }

            request = JsonSerializer.Deserialize<RateLimitRequest>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON in rate limit request.");
            return await CreateErrorResponseAsync(req, HttpStatusCode.BadRequest,
                "Invalid JSON format.");
        }

        if (request is null
            || string.IsNullOrWhiteSpace(request.Oid)
            || request.Burst <= 0
            || request.Rps <= 0)
        {
            return await CreateErrorResponseAsync(req, HttpStatusCode.BadRequest,
                "Missing or invalid parameters. Required: oid (string), burst (>0), rps (>0).");
        }

        // --- 2. Execute token bucket check ---
        var (allowed, remaining, retryAfterMs) = await _tokenBucket.ConsumeTokenAsync(
            request.Oid, request.Burst, request.Rps);

        // --- 3. Build response ---
        var statusCode = allowed ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests;
        var response = req.CreateResponse(statusCode);

        // Standard rate-limit headers (RFC 6585 / draft-ietf-httpapi-ratelimit-headers)
        response.Headers.Add("X-RateLimit-Limit", request.Burst.ToString());
        response.Headers.Add("X-RateLimit-Remaining", remaining.ToString());

        if (!allowed)
        {
            var retryAfterSeconds = Math.Max(1, retryAfterMs / 1000);
            response.Headers.Add("Retry-After", retryAfterSeconds.ToString());
            response.Headers.Add("X-RateLimit-RetryAfter-Ms", retryAfterMs.ToString());
        }

        var responseBody = new RateLimitResponse
        {
            Allowed = allowed,
            Remaining = remaining,
            RetryAfterMs = retryAfterMs,
            Limit = request.Burst
        };

        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(responseBody, JsonOptions));
        return response;
    }

    private static async Task<HttpResponseData> CreateErrorResponseAsync(
        HttpRequestData req, HttpStatusCode status, string message)
    {
        var response = req.CreateResponse(status);
        await response.WriteAsJsonAsync(new { error = message });
        return response;
    }
}
