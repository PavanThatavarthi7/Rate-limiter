namespace RateLimiter.Function.Services;

/// <summary>
/// Token bucket rate limiting service interface.
/// </summary>
public interface ITokenBucketService
{
    /// <summary>
    /// Attempts to consume one token from the bucket for the given OID.
    /// </summary>
    /// <param name="oid">Azure AD Object ID (unique user identifier).</param>
    /// <param name="burst">Maximum bucket capacity.</param>
    /// <param name="rps">Token refill rate per second.</param>
    /// <returns>A tuple of (Allowed, Remaining tokens, RetryAfter in ms).</returns>
    Task<(bool Allowed, int Remaining, int RetryAfterMs)> ConsumeTokenAsync(
        string oid, int burst, int rps);
}
