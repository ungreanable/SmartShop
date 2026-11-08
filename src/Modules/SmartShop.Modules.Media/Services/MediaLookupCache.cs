using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SmartShop.Modules.Media.Data;

namespace SmartShop.Modules.Media.Services;

/// <summary>
/// Remembers which media objects exist and whether they have resized variants, so serving an image (every thumbnail on
/// every list) does not hit the database each time. Media is immutable after upload, so entries never go stale; the
/// cache is bounded by entry count and expiry because it lives in every API process.
/// </summary>
internal sealed class MediaLookupCache : IDisposable
{
    private const int MaxEntries = 20_000;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = MaxEntries });

    /// <summary>Null when the media does not exist (not cached, so a just-uploaded id is found on the next request).</summary>
    public async Task<bool?> HasVariantsAsync(Guid id, MediaDbContext db, CancellationToken ct)
    {
        if (_cache.TryGetValue(id, out bool cached)) return cached;
        var row = await db.Objects.AsNoTracking().Where(m => m.Id == id).Select(m => new { m.HasVariants }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        _cache.Set(id, row.HasVariants, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = Lifetime });
        return row.HasVariants;
    }

    public void Dispose() => _cache.Dispose();
}

/// <summary>
/// Keeps the bytes of small images (thumbnails and list pictures) in memory, so a picture many people look at is fetched
/// from object storage once instead of on every phone's first view. Media is never changed or deleted after upload, so
/// nothing has to be invalidated; the cache is bounded by total bytes and forgets pictures nobody asked for in a while.
/// </summary>
internal sealed class MediaContentCache : IDisposable
{
    /// <summary>Thumbnails are 10-30 KB and list pictures 50-150 KB; bigger files (originals, PDFs) are streamed as before.</summary>
    public const long MaxItemBytes = 512 * 1024;
    private const long MaxTotalBytes = 48L * 1024 * 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = MaxTotalBytes, CompactionPercentage = 0.25 });

    public sealed record Entry(byte[] Content, string ContentType);

    public bool TryGet(string key, out Entry? entry) => _cache.TryGetValue(key, out entry);

    public void Add(string key, Entry entry) =>
        _cache.Set(key, entry, new MemoryCacheEntryOptions { Size = entry.Content.Length, SlidingExpiration = Lifetime });

    public void Dispose() => _cache.Dispose();
}
