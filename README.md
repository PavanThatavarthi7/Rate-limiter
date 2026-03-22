# Azure APIM Token Bucket Rate Limiter

A production-grade, per-user rate limiting solution using the **Token Bucket algorithm**, powered by **Azure API Management**, **Azure Functions (.NET 8)**, and **Azure Cache for Redis**.

## Architecture

```
Client → Azure APIM (JWT validation + claim extraction)
           → Azure Function (token bucket logic)
               → Redis (atomic Lua script for state management)
```

## Quick Start

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Azure Functions Core Tools v4](https://learn.microsoft.com/en-us/azure/azure-functions/functions-run-local)
- Redis (local via Docker or Azure Cache for Redis)
- Azure subscription (for APIM + deployment)

### Local Development

1. **Start Redis locally:**
   ```bash
   docker run -d --name redis -p 6379:6379 redis:7-alpine
   ```

2. **Configure connection:**
   Edit `src/RateLimiter.Function/local.settings.json` — default is `localhost:6379`.

3. **Run the function:**
   ```bash
   cd src/RateLimiter.Function
   dotnet restore
   func start
   ```

4. **Test it:**
   ```bash
   curl -X POST http://localhost:7071/api/rate-limit/check \
     -H "Content-Type: application/json" \
     -d '{"oid":"test-user-123","burst":20,"rps":10}'
   ```

### Run Tests

```bash
dotnet test tests/RateLimiter.Tests/
```

### Test Lua Script Directly

```bash
redis-cli --eval src/RateLimiter.Function/Scripts/token_bucket.lua rl:test , 20 10 $(date +%s)000000
```

## Project Structure

```
├── RateLimiter.sln
├── src/RateLimiter.Function/
│   ├── Functions/TokenBucketFunction.cs   # HTTP trigger endpoint
│   ├── Services/
│   │   ├── ITokenBucketService.cs         # Service interface
│   │   └── TokenBucketService.cs          # Redis Lua-backed implementation
│   ├── Models/
│   │   ├── RateLimitRequest.cs            # Input DTO
│   │   └── RateLimitResponse.cs           # Output DTO
│   ├── Scripts/token_bucket.lua           # Standalone Lua script
│   ├── policies/apim-rate-limit-policy.xml# APIM XML policy
│   ├── Program.cs                         # Host setup + DI
│   ├── host.json                          # Function host config
│   └── local.settings.json                # Local dev settings
└── tests/RateLimiter.Tests/
    └── TokenBucketServiceTests.cs         # Unit tests
```

## APIM Setup

1. Import the XML policy from `policies/apim-rate-limit-policy.xml`
2. Replace placeholders: `{tenant-id}`, `{your-api-audience}`, `{your-function-app}`
3. Create an APIM Named Value `function-host-key` with your Function App host key

## JWT Claims

The policy expects these JWT claims (with defaults):

| Claim | Type | Default | Description |
|---|---|---|---|
| `oid` | string | required | Azure AD Object ID |
| `rate_limit_burst` | int | 20 | Max bucket capacity |
| `rate_limit_rps` | int | 10 | Tokens refilled per second |

## Algorithm

**Token Bucket**: Each user gets a bucket with `burst` capacity. Tokens refill at `rps` per second. Each request consumes 1 token. When empty → 429 with `Retry-After` header.

## Production Checklist

- [ ] Use **Azure Functions Premium plan** (no cold starts)
- [ ] Use **Azure Cache for Redis Standard/Premium** tier
- [ ] Store Redis connection string in **Azure Key Vault**
- [ ] Enable **Application Insights** for monitoring
- [ ] Load test with [k6](https://k6.io/) or Azure Load Testing
- [ ] Configure Redis connection pooling and timeouts
