using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace RateLimiter.Function.Services;

/// <summary>
/// Redis-backed token bucket rate limiter.
/// Uses an atomic Lua script to prevent race conditions under high concurrency.
/// 
/// Algorithm:
///   - Each user (OID) has a bucket with capacity = burst.
///   - Tokens refill at a rate of rps (rate per second).
///   - Each request consumes 1 token.
///   - If tokens < 1, the request is rejected with a retry-after hint.
///   - State is stored in Redis as a hash: { tokens, last_ts }.
/// </summary>
public sealed class TokenBucketService : ITokenBucketService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<TokenBucketService> _logger;

    // Cached SHA1 hash of the Lua script for fast EVALSHA calls.
    private static byte[]? _scriptSha;
    private static readonly SemaphoreSlim _scriptLock = new(1, 1);

    /// <summary>
    /// Atomic Lua script implementing the token bucket algorithm.
    /// 
    /// KEYS[1] = Redis key for this user's bucket (e.g., "rl:{oid}")
    /// ARGV[1] = burst (max tokens / bucket capacity)
    /// ARGV[2] = rps   (refill rate: tokens per second)
    /// ARGV[3] = now   (current timestamp in microseconds for precision)
    /// 
    /// Returns: { allowed (0|1), remaining_tokens, retry_after_ms }
    /// </summary>
    private const string TokenBucketLuaScript = """
        local key = KEYS[1]
        local burst = tonumber(ARGV[1])
        local rps = tonumber(ARGV[2])
        local now = tonumber(ARGV[3])

        -- Fetch current bucket state
        local data = redis.call('HMGET', key, 'tokens', 'last_ts')
        local tokens = tonumber(data[1])
        local last_ts = tonumber(data[2])

        -- First request: initialize bucket to full capacity
        if tokens == nil then
            tokens = burst
            last_ts = now
        end

        -- Calculate elapsed time and refill tokens
        local elapsed = (now - last_ts) / 1000000  -- microseconds → seconds
        local refill = elapsed * rps
        tokens = math.min(burst, tokens + refill)

        -- Attempt to consume 1 token
        local allowed = 0
        if tokens >= 1 then
            tokens = tokens - 1
            allowed = 1
        end

        -- Set TTL for auto-cleanup of idle keys (2x full refill time, min 10s)
        local ttl = math.ceil((burst / rps) * 2)
        if ttl < 10 then ttl = 10 end

        -- Persist updated state
        redis.call('HMSET', key, 'tokens', tostring(tokens), 'last_ts', tostring(now))
        redis.call('EXPIRE', key, ttl)

        -- Calculate retry-after hint (ms until at least 1 token is available)
        local retry_after = 0
        if allowed == 0 then
            retry_after = math.ceil((1 - tokens) / rps * 1000)
        end

        return {allowed, math.floor(tokens), retry_after}
        """;

    public TokenBucketService(
        IConnectionMultiplexer redis,
        ILogger<TokenBucketService> logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<(bool Allowed, int Remaining, int RetryAfterMs)> ConsumeTokenAsync(
        string oid, int burst, int rps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oid, nameof(oid));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(burst, 0, nameof(burst));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(rps, 0, nameof(rps));

        var db = _redis.GetDatabase();
        var key = $"rl:{oid}";

        // Microsecond-precision timestamp for accurate refill calculation
        var nowMicroseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

        try
        {
            await EnsureScriptLoadedAsync();

            var result = (RedisResult[]?)await db.ScriptEvaluateAsync(
                _scriptSha!,
                keys: [new RedisKey(key)],
                values: [burst, rps, nowMicroseconds]);

            if (result is null || result.Length < 3)
            {
                _logger.LogError(
                    "Unexpected Lua script response for OID={Oid}. Failing open.",
                    oid);
                return (true, burst, 0); // Fail open
            }

            var allowed = (int)result[0] == 1;
            var remaining = (int)result[1];
            var retryAfterMs = (int)result[2];

            if (!allowed)
            {
                _logger.LogInformation(
                    "Rate limit exceeded: OID={Oid}, Remaining={Remaining}, RetryAfterMs={RetryAfterMs}",
                    oid, remaining, retryAfterMs);
            }
            else
            {
                _logger.LogDebug(
                    "Rate limit OK: OID={Oid}, Remaining={Remaining}/{Burst}",
                    oid, remaining, burst);
            }

            return (allowed, remaining, retryAfterMs);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogCritical(ex,
                "REDIS CONNECTION FAILURE for OID={Oid}. Check if Redis is running at the configured endpoint. " +
                "Failing open - request will be ALLOWED.",
                oid);
            return (true, burst, 0);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex,
                "Redis timeout for OID={Oid}. Failing open.",
                oid);
            return (true, burst, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "UNEXPECTED ERROR during rate limit check for OID={Oid}. Type: {Type}",
                oid, ex.GetType().Name);
            return (true, burst, 0); // Still fail open to prevent API outage
        }
    }

    /// <summary>
    /// Loads the Lua script into Redis and caches the SHA1 hash.
    /// EVALSHA is faster than EVAL because Redis skips parsing the script body.
    /// Uses double-check locking for thread safety.
    /// </summary>
    private async Task EnsureScriptLoadedAsync()
    {
        if (_scriptSha is not null) return;

        await _scriptLock.WaitAsync();
        try
        {
            if (_scriptSha is not null) return;

            var server = _redis.GetServers().First(s => s.IsConnected);
            _scriptSha = await server.ScriptLoadAsync(TokenBucketLuaScript);

            _logger.LogInformation(
                "Token bucket Lua script loaded into Redis. SHA={Sha}",
                Convert.ToHexString(_scriptSha));
        }
        finally
        {
            _scriptLock.Release();
        }
    }
}
