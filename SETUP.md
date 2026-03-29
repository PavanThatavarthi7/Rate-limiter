# Local Setup Guide

> **Prerequisites for testing on another laptop with Azure Cache for Redis.**
> You do NOT need a local Redis server — you will use the live Azure one.

---

## 1. Install Prerequisites

```bash
# .NET 8 SDK
# Download from: https://dotnet.microsoft.com/download/dotnet/8

# Azure Functions Core Tools v4
npm install -g azure-functions-core-tools@4 --unsafe-perm

# Azurite (local Azure Storage emulator — required by the Function host)
npm install -g azurite
```

> **Note:** Azurite is still required locally even when using a live Redis.
> Azure Functions Core Tools needs `AzureWebJobsStorage` to be running.
> Redis itself does NOT need to be installed.

---

## 2. Clone and Configure

```bash
git clone https://github.com/PavanThatavarthi7/Rate-limiter.git
cd Rate-limiter
```

Edit `src/RateLimiter.Function/local.settings.json`:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "RedisConnectionString": "<YOUR AZURE CACHE FOR REDIS CONNECTION STRING>",
    "APPLICATIONINSIGHTS_CONNECTION_STRING": ""
  }
}
```

> **Where to get the Redis connection string:**
> Azure Portal → Your Redis Cache → **Access Keys** → Copy the **Primary connection string**
> It looks like: `yourcache.redis.cache.windows.net:6380,password=...,ssl=True,abortConnect=False`

---

## 3. Start Azurite

```bash
# Run in background (needed for Azure Functions host)
azurite --silent --location /tmp/azurite &
```

> **Getting `EADDRINUSE: address already in use 127.0.0.1:10000`?**
> Azurite is already running in the background — you can skip this step entirely.
> To verify: `lsof -i :10000` — if you see a process, it's already up ✅
> To force a clean restart: `lsof -ti:10000 | xargs kill -9 && azurite --silent --location /tmp/azurite &`

---

## 4. Build and Run

```bash
cd src/RateLimiter.Function
dotnet build
func start
```

You should see:
```
Functions:
  RateLimitCheck: [POST] http://localhost:7071/api/rate-limit/check
  RateLimitStats: [GET]  http://localhost:7071/api/rate-limit/stats
```

---

## 5. Test It

```bash
# Quick smoke test
curl -X POST http://localhost:7071/api/rate-limit/check \
  -H "Content-Type: application/json" \
  -d '{"oid": "my-test-user", "burst": 3, "rps": 1}'

# Run the full production scenario
bash production-scenario.sh
```

---

## 6. Open the Dashboard

Open `src/RateLimiter.Function/dashboard.html` in your browser.
The stats endpoint URL should be: `http://localhost:7071/api/rate-limit/stats`

> **Tip:** If the dashboard shows an error fetching stats, serve it via a local HTTP server to avoid CORS:
> ```bash
> npx serve src/RateLimiter.Function --port 8080
> # Then open http://localhost:8080/dashboard.html
> ```

---

## Summary: What you need vs. what you don't

| Requirement | Needed? | Notes |
|---|---|---|
| .NET 8 SDK | ✅ Yes | |
| Azure Functions Core Tools v4 | ✅ Yes | `npm i -g azure-functions-core-tools@4` |
| Azurite | ✅ Yes | For `AzureWebJobsStorage` |
| Node.js | ✅ Yes | To install azurite + func tools |
| Redis (local) | ❌ No | Using Azure Cache for Redis |
| Docker | ❌ No | Not needed |
| Azure CLI | ❌ No | Only needed for deployment |
