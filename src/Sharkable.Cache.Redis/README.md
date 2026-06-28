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

| Store | Interface | Redis impl |
|-------|-----------|------------|
| Idempotency | `IIdempotencyStore` | `RedisIdempotencyStore` |
| Rate limiting | `IDistributedRateLimitStore` | `RedisRateLimitStore` |

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
