namespace RateLimiter.Function.Models;

/// <summary>
/// Rate limit check result returned to APIM.
/// </summary>
public sealed record RateLimitResponse
{
    /// <summary>Whether the request is allowed (true) or throttled (false).</summary>
    public bool Allowed { get; init; }

    /// <summary>Remaining tokens in the bucket after this request.</summary>
    public int Remaining { get; init; }

    /// <summary>
    /// Milliseconds to wait before retrying (only meaningful when Allowed=false).
    /// Maps to Retry-After header.
    /// </summary>
    public int RetryAfterMs { get; init; }

    /// <summary>Maximum bucket capacity (burst value).</summary>
    public int Limit { get; init; }
}
