namespace RateLimiter.Function.Models;

/// <summary>
/// Inbound rate limit check request from APIM.
/// Contains the user identity and their rate limit configuration.
/// </summary>
public sealed record RateLimitRequest
{
    /// <summary>
    /// Azure AD Object ID extracted from JWT token.
    /// Used as the unique key for per-user rate limiting.
    /// </summary>
    public required string Oid { get; init; }

    /// <summary>
    /// Maximum token bucket capacity (burst allowance).
    /// Example: 20 means the user can make up to 20 rapid requests before throttling.
    /// </summary>
    public required int Burst { get; init; }

    /// <summary>
    /// Token refill rate per second.
    /// Example: 10 means 10 tokens are replenished per second.
    /// </summary>
    public required int Rps { get; init; }
}
