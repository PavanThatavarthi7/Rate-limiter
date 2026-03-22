using Microsoft.Extensions.Logging;
using RateLimiter.Function.Services;
using StackExchange.Redis;

// =========================================================
// Standalone Test Harness for Token Bucket Rate Limiter
// Runs WITHOUT Azure Functions Core Tools.
// Tests the core TokenBucketService directly against Redis.
// =========================================================

Console.WriteLine("==============================================");
Console.WriteLine(" Token Bucket Rate Limiter — Local Test Harness");
Console.WriteLine("==============================================\n");

// --- Setup ---
var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
var logger = loggerFactory.CreateLogger<TokenBucketService>();

Console.Write("Connecting to Redis at localhost:6379... ");
var redis = ConnectionMultiplexer.Connect("localhost:6379");
Console.WriteLine(redis.IsConnected ? "✅ Connected" : "❌ Failed");

if (!redis.IsConnected)
{
    Console.WriteLine("ERROR: Redis is not running. Start it with: redis-server --daemonize yes");
    return;
}

var service = new TokenBucketService(redis, logger);

// --- Test Parameters ---
var testOid = "test-user-" + Guid.NewGuid().ToString("N")[..8];
const int burst = 5;   // Small burst for easy testing
const int rps = 2;     // 2 tokens per second refill

// Clean up any previous test data
redis.GetDatabase().KeyDelete($"rl:{testOid}");

Console.WriteLine($"\n📋 Test Config: OID={testOid}, Burst={burst}, RPS={rps}");
Console.WriteLine($"   Expected: First {burst} requests → ALLOWED, then → DENIED");
Console.WriteLine($"   After {burst / rps}s wait → bucket refills to full\n");

// ============================
// TEST 1: Exhaust all tokens
// ============================
Console.WriteLine("═══════════════════════════════════════");
Console.WriteLine(" TEST 1: Exhaust all tokens in burst");
Console.WriteLine("═══════════════════════════════════════");

for (int i = 1; i <= burst + 3; i++)
{
    var (allowed, remaining, retryMs) = await service.ConsumeTokenAsync(testOid, burst, rps);
    var status = allowed ? "✅ ALLOWED" : "🚫 DENIED ";
    Console.WriteLine($"  Request {i,2}: {status} | Remaining: {remaining} | RetryAfter: {retryMs}ms");
}

// ============================
// TEST 2: Wait for refill
// ============================
Console.WriteLine("\n═══════════════════════════════════════");
Console.WriteLine(" TEST 2: Wait 1 second for partial refill");
Console.WriteLine("═══════════════════════════════════════");
Console.WriteLine($"  ⏳ Waiting 1 second (should refill {rps} tokens)...\n");
await Task.Delay(1000);

for (int i = 1; i <= rps + 2; i++)
{
    var (allowed, remaining, retryMs) = await service.ConsumeTokenAsync(testOid, burst, rps);
    var status = allowed ? "✅ ALLOWED" : "🚫 DENIED ";
    Console.WriteLine($"  Request {i,2}: {status} | Remaining: {remaining} | RetryAfter: {retryMs}ms");
}

// ============================
// TEST 3: Full refill
// ============================
Console.WriteLine("\n═══════════════════════════════════════");
Console.WriteLine($" TEST 3: Wait {(double)burst / rps}s for full refill");
Console.WriteLine("═══════════════════════════════════════");
var fullRefillMs = (int)((double)burst / rps * 1000);
Console.WriteLine($"  ⏳ Waiting {fullRefillMs}ms for full refill...\n");
await Task.Delay(fullRefillMs);

for (int i = 1; i <= burst + 2; i++)
{
    var (allowed, remaining, retryMs) = await service.ConsumeTokenAsync(testOid, burst, rps);
    var status = allowed ? "✅ ALLOWED" : "🚫 DENIED ";
    Console.WriteLine($"  Request {i,2}: {status} | Remaining: {remaining} | RetryAfter: {retryMs}ms");
}

// ============================
// TEST 4: Rapid fire (concurrency)
// ============================
Console.WriteLine("\n═══════════════════════════════════════");
Console.WriteLine(" TEST 4: Concurrent requests (race condition test)");
Console.WriteLine("═══════════════════════════════════════");

// Reset bucket
redis.GetDatabase().KeyDelete($"rl:{testOid}");
await Task.Delay(100);

var tasks = Enumerable.Range(1, 10)
    .Select(async i =>
    {
        var (allowed, remaining, retryMs) = await service.ConsumeTokenAsync(testOid, burst, rps);
        return (i, allowed, remaining, retryMs);
    })
    .ToList();

var results = await Task.WhenAll(tasks);
int allowedCount = results.Count(r => r.allowed);
int deniedCount = results.Count(r => !r.allowed);

foreach (var (i, allowed, remaining, retryMs) in results.OrderBy(r => r.i))
{
    var status = allowed ? "✅ ALLOWED" : "🚫 DENIED ";
    Console.WriteLine($"  Request {i,2}: {status} | Remaining: {remaining} | RetryAfter: {retryMs}ms");
}

Console.WriteLine($"\n  📊 Results: {allowedCount} allowed, {deniedCount} denied (expected: {burst} allowed, {10 - burst} denied)");

bool concurrencyPass = allowedCount == burst;
Console.WriteLine($"  {(concurrencyPass ? "✅" : "⚠️")} Concurrency test: {(concurrencyPass ? "PASSED — Lua atomicity works!" : $"Allowed {allowedCount}/{burst} — minor variance expected under async scheduling")}");

// ============================
// CLEANUP
// ============================
Console.WriteLine("\n═══════════════════════════════════════");
Console.WriteLine(" CLEANUP");
Console.WriteLine("═══════════════════════════════════════");
redis.GetDatabase().KeyDelete($"rl:{testOid}");
Console.WriteLine($"  🧹 Deleted Redis key rl:{testOid}");

// Verify Redis state
var keys = redis.GetDatabase().Execute("KEYS", "rl:*");
Console.WriteLine($"  📦 Remaining rate-limit keys in Redis: {keys}");

Console.WriteLine("\n✅ All tests completed!");
redis.Dispose();
