-- token_bucket.lua
-- Standalone version of the token bucket script for testing with redis-cli.
--
-- Usage:
--   redis-cli --eval token_bucket.lua rl:test-user , 20 10 1711100000000000
--
-- KEYS[1] = rate limit key (e.g., "rl:{oid}")
-- ARGV[1] = burst (max tokens / bucket capacity)
-- ARGV[2] = rps   (refill rate: tokens per second)
-- ARGV[3] = now   (current timestamp in microseconds)
--
-- Returns: { allowed (0|1), remaining_tokens, retry_after_ms }

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

-- Set TTL for auto-cleanup of idle keys (2x full refill time, minimum 10s)
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
