# Sharkable.Cache.Redis

Redis-backed distributed stores for [Sharkable](https://github.com/SharkableIO/Sharkable).

## Install

```bash
dotnet add package Sharkable.Cache.Redis
```

## Usage

```csharp
// Before AddShark() — registers ConnectionMultiplexer and swaps stores
builder.Services.AddSharkableRedis("localhost:6379");

builder.Services.AddShark(opt =>
{
    opt.EnableIdempotency = true;
    opt.ConfigureRateLimiting(o => o.DefaultLimit = 100);
});
```

The `AddSharkableRedis()` call swaps the default in-memory stores for their Redis-backed counterparts. After that, `AddShark()` picks up the Redis implementations via `TryAddSingleton`.

### What it provides

| Feature | Interface | Redis impl |
|---------|-----------|------------|
| Idempotency | `IIdempotencyStore` | `RedisIdempotencyStore` |
| Rate limiting | `IDistributedRateLimitStore` | `RedisRateLimitStore` |
| Health check | `IHealthCheck` | `RedisHealthCheck` |

The `RedisHealthCheck` is automatically registered. When `EnableHealthChecks = true`, `/healthz` includes Redis connectivity status, latency, and endpoint count.

### Overloads

```csharp
// Connection string
services.AddSharkableRedis("localhost:6379,password=...");

// Existing IConnectionMultiplexer
services.AddSharkableRedis(existingMultiplexer);

// Full ConfigurationOptions
services.AddSharkableRedis(new ConfigurationOptions { ... });
```

## License

MIT
