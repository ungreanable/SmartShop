using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace SmartShop.Modules.Analytics.Services;

/// <summary>
/// "Who is looking at this shop right now", as a count only. Clients send a heartbeat every ~30 s while the shop page is
/// visible; a visitor drops out <see cref="Ttl"/> after the last one (two missed beats), or at once when they leave.
/// </summary>
public interface IPresenceTracker
{
    /// <summary>Marks the visitor as viewing and returns the number of distinct viewers, them included.</summary>
    Task<int> TouchAsync(Guid shopId, string visitor, CancellationToken ct = default);

    Task LeaveAsync(Guid shopId, string visitor, CancellationToken ct = default);

    Task<int> CountAsync(Guid shopId, CancellationToken ct = default);
}

public static class Presence
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(70);
}

/// <summary>Single process (no Valkey configured, tests). Entries are pruned whenever a shop is touched or counted.</summary>
public sealed class InMemoryPresence(TimeProvider clock) : IPresenceTracker
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, DateTimeOffset>> _shops = new();

    public Task<int> TouchAsync(Guid shopId, string visitor, CancellationToken ct = default)
    {
        var viewers = _shops.GetOrAdd(shopId, _ => new ConcurrentDictionary<string, DateTimeOffset>());
        viewers[visitor] = clock.GetUtcNow() + Presence.Ttl;
        return Task.FromResult(Prune(shopId, viewers));
    }

    public Task LeaveAsync(Guid shopId, string visitor, CancellationToken ct = default)
    {
        if (_shops.TryGetValue(shopId, out var viewers)) viewers.TryRemove(visitor, out _);
        return Task.CompletedTask;
    }

    public Task<int> CountAsync(Guid shopId, CancellationToken ct = default) =>
        Task.FromResult(_shops.TryGetValue(shopId, out var viewers) ? Prune(shopId, viewers) : 0);

    private int Prune(Guid shopId, ConcurrentDictionary<string, DateTimeOffset> viewers)
    {
        var now = clock.GetUtcNow();
        foreach (var (visitor, until) in viewers)
            if (until <= now) viewers.TryRemove(visitor, out _);
        // Forget shops nobody looks at so the map does not grow with every shop ever opened.
        if (viewers.IsEmpty) _shops.TryRemove(new KeyValuePair<Guid, ConcurrentDictionary<string, DateTimeOffset>>(shopId, viewers));
        return viewers.Count;
    }
}

/// <summary>
/// Several API replicas share the count through Valkey: one sorted set per shop, member = visitor key, score = expiry
/// (unix ms). Each heartbeat is one pipelined round trip. If Valkey is unreachable the count falls back to this
/// process's memory instead of failing the page.
/// </summary>
public sealed class ValkeyPresence(IConnectionMultiplexer redis, InMemoryPresence fallback, TimeProvider clock, ILogger<ValkeyPresence> logger)
    : IPresenceTracker
{
    private static RedisKey Key(Guid shopId) => $"smartshop:presence:{shopId:N}";

    public async Task<int> TouchAsync(Guid shopId, string visitor, CancellationToken ct = default)
    {
        try
        {
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var key = Key(shopId);
            var batch = redis.GetDatabase().CreateBatch();
            var add = batch.SortedSetAddAsync(key, visitor, now + Presence.Ttl.TotalMilliseconds);
            var trim = batch.SortedSetRemoveRangeByScoreAsync(key, double.NegativeInfinity, now);
            var count = batch.SortedSetLengthAsync(key);
            // The whole set goes away once nobody has sent a heartbeat for a while.
            var expire = batch.KeyExpireAsync(key, Presence.Ttl * 2);
            batch.Execute();
            await Task.WhenAll(add, trim, count, expire);
            return (int)count.Result;
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            logger.LogWarning(e, "Presence: Valkey unavailable, counting in memory");
            return await fallback.TouchAsync(shopId, visitor, ct);
        }
    }

    public async Task LeaveAsync(Guid shopId, string visitor, CancellationToken ct = default)
    {
        try { await redis.GetDatabase().SortedSetRemoveAsync(Key(shopId), visitor); }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            logger.LogWarning(e, "Presence: Valkey unavailable");
            await fallback.LeaveAsync(shopId, visitor, ct);
        }
    }

    public async Task<int> CountAsync(Guid shopId, CancellationToken ct = default)
    {
        try
        {
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            return (int)await redis.GetDatabase().SortedSetLengthAsync(Key(shopId), now, double.PositiveInfinity, Exclude.Start);
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            logger.LogWarning(e, "Presence: Valkey unavailable, counting in memory");
            return await fallback.CountAsync(shopId, ct);
        }
    }
}
