# Sharkable.Cache.Redis

![Sharkable](src/Sharkable.Cache.Redis/sharkable.jpg)

Redis-backed distributed stores for [Sharkable](https://github.com/SharkableIO/Sharkable).

## Install

```bash
dotnet add package Sharkable.Cache.Redis
```

## Usage

```csharp
// Register before AddShark()
builder.Services.AddSharkableRedis("localhost:6379");

builder.Services.AddShark(opt =>
{
    opt.EnableIdempotency = true;
    opt.ConfigureRateLimiting(o => o.DefaultLimit = 100);
});
```

## Stores

| Store | Interface | Redis Key Pattern |
|-------|-----------|-------------------|
| Idempotency | `IIdempotencyStore` | `sharkable:idempotency:{key}` |
| Rate limiting | `IDistributedRateLimitStore` | `sharkable:ratelimit:{key}` |

The `RateLimitStore` uses an atomic Lua script (`INCR` + conditional `EXPIRE`) for correct distributed counting.

## License

MIT — [CharleyPeng](https://github.com/charleypeng)
