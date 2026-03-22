using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RateLimiter.Function.Services;
using StackExchange.Redis;

namespace RateLimiter.Tests;

/// <summary>
/// Unit tests for <see cref="TokenBucketService"/>.
/// 
/// These tests mock Redis to verify the service's behavior:
///   - Correct Lua script invocation
///   - Proper parsing of results
///   - Fail-open behavior on Redis failures
///   - Input validation
/// 
/// For integration tests with a real Redis instance, use Testcontainers.
/// </summary>
public class TokenBucketServiceTests
{
    private readonly Mock<IConnectionMultiplexer> _redisMock;
    private readonly Mock<IDatabase> _dbMock;
    private readonly Mock<IServer> _serverMock;
    private readonly Mock<ILogger<TokenBucketService>> _loggerMock;
    private readonly TokenBucketService _sut;

    public TokenBucketServiceTests()
    {
        _redisMock = new Mock<IConnectionMultiplexer>();
        _dbMock = new Mock<IDatabase>();
        _serverMock = new Mock<IServer>();
        _loggerMock = new Mock<ILogger<TokenBucketService>>();

        _redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_dbMock.Object);

        _serverMock.SetupGet(s => s.IsConnected).Returns(true);
        _redisMock.Setup(r => r.GetServers())
            .Returns([_serverMock.Object]);

        // Mock script loading
        _serverMock.Setup(s => s.ScriptLoadAsync(It.IsAny<string>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(new byte[] { 0x01, 0x02, 0x03 });

        _sut = new TokenBucketService(_redisMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task ConsumeTokenAsync_WhenAllowed_ReturnsAllowedWithRemaining()
    {
        // Arrange: Lua script returns [1 (allowed), 19 (remaining), 0 (no retry)]
        var luaResult = new RedisResult[]
        {
            RedisResult.Create((RedisValue)1),
            RedisResult.Create((RedisValue)19),
            RedisResult.Create((RedisValue)0)
        };

        _dbMock.Setup(db => db.ScriptEvaluateAsync(
                It.IsAny<byte[]>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create(luaResult));

        // Act
        var (allowed, remaining, retryAfterMs) = await _sut.ConsumeTokenAsync("test-oid", 20, 10);

        // Assert
        allowed.Should().BeTrue();
        remaining.Should().Be(19);
        retryAfterMs.Should().Be(0);
    }

    [Fact]
    public async Task ConsumeTokenAsync_WhenThrottled_ReturnsNotAllowedWithRetryAfter()
    {
        // Arrange: Lua script returns [0 (denied), 0 (no tokens), 100 (retry in 100ms)]
        var luaResult = new RedisResult[]
        {
            RedisResult.Create((RedisValue)0),
            RedisResult.Create((RedisValue)0),
            RedisResult.Create((RedisValue)100)
        };

        _dbMock.Setup(db => db.ScriptEvaluateAsync(
                It.IsAny<byte[]>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create(luaResult));

        // Act
        var (allowed, remaining, retryAfterMs) = await _sut.ConsumeTokenAsync("test-oid", 20, 10);

        // Assert
        allowed.Should().BeFalse();
        remaining.Should().Be(0);
        retryAfterMs.Should().Be(100);
    }

    [Fact]
    public async Task ConsumeTokenAsync_WhenRedisDown_FailsOpen()
    {
        // Arrange: Redis throws connection exception
        _dbMock.Setup(db => db.ScriptEvaluateAsync(
                It.IsAny<byte[]>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Connection refused"));

        // Act
        var (allowed, remaining, retryAfterMs) = await _sut.ConsumeTokenAsync("test-oid", 20, 10);

        // Assert: should fail open (allow the request)
        allowed.Should().BeTrue();
        remaining.Should().Be(20);  // Returns burst as remaining
        retryAfterMs.Should().Be(0);
    }

    [Fact]
    public async Task ConsumeTokenAsync_WhenRedisTimesOut_FailsOpen()
    {
        // Arrange
        _dbMock.Setup(db => db.ScriptEvaluateAsync(
                It.IsAny<byte[]>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisTimeoutException("Timeout performing EVALSHA", CommandStatus.Unknown));

        // Act
        var (allowed, remaining, retryAfterMs) = await _sut.ConsumeTokenAsync("test-oid", 20, 10);

        // Assert
        allowed.Should().BeTrue();
        remaining.Should().Be(20);
        retryAfterMs.Should().Be(0);
    }

    [Theory]
    [InlineData("", 20, 10)]    // Empty OID
    [InlineData("  ", 20, 10)]  // Whitespace OID
    public async Task ConsumeTokenAsync_WithInvalidOid_ThrowsArgumentException(
        string oid, int burst, int rps)
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.ConsumeTokenAsync(oid, burst, rps));
    }

    [Theory]
    [InlineData("valid-oid", 0, 10)]   // Zero burst
    [InlineData("valid-oid", -1, 10)]  // Negative burst
    [InlineData("valid-oid", 20, 0)]   // Zero rps
    [InlineData("valid-oid", 20, -5)]  // Negative rps
    public async Task ConsumeTokenAsync_WithInvalidLimits_ThrowsArgumentOutOfRangeException(
        string oid, int burst, int rps)
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _sut.ConsumeTokenAsync(oid, burst, rps));
    }

    [Fact]
    public async Task ConsumeTokenAsync_UsesCorrectRedisKey()
    {
        // Arrange
        var luaResult = new RedisResult[]
        {
            RedisResult.Create((RedisValue)1),
            RedisResult.Create((RedisValue)19),
            RedisResult.Create((RedisValue)0)
        };

        RedisKey[]? capturedKeys = null;
        _dbMock.Setup(db => db.ScriptEvaluateAsync(
                It.IsAny<byte[]>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .Callback<byte[], RedisKey[], RedisValue[], CommandFlags>(
                (sha, keys, values, flags) => capturedKeys = keys)
            .ReturnsAsync(RedisResult.Create(luaResult));

        // Act
        await _sut.ConsumeTokenAsync("my-test-oid-123", 20, 10);

        // Assert: Redis key should be "rl:{oid}"
        capturedKeys.Should().NotBeNull();
        capturedKeys![0].ToString().Should().Be("rl:my-test-oid-123");
    }
}
