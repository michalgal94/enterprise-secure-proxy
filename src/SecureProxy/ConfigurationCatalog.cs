using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace SecureProxy;

public sealed class ConfigurationCacheOptions
{
    public bool Enabled { get; set; }
    public int TtlSeconds { get; set; } = 10;
    public int Capacity { get; set; } = 256;
}

public sealed class ConfigurationCache(IOptions<ConfigurationCacheOptions> options, TimeProvider time) : IDisposable
{
    private sealed record Entry(Operation Operation, DateTimeOffset ExpiresAt);
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = options.Value.Capacity });
    private readonly object gate = new();
    private long generation;
    public bool Enabled => options.Value.Enabled;
    public long Generation => Interlocked.Read(ref generation);
    public bool TryGet(string key, out Operation? operation)
    {
        operation = null;
        if (!Enabled || !cache.TryGetValue(key, out Entry? entry) || entry is null) return false;
        if (time.GetUtcNow() >= entry.ExpiresAt) { cache.Remove(key); return false; }
        operation = entry.Operation;
        return true;
    }
    public void Store(string key, Operation operation, long observedGeneration)
    {
        if (!Enabled) return;
        lock (gate)
        {
            // A load started before invalidation must not refill the cache with stale data.
            if (generation == observedGeneration)
                cache.Set(key, new Entry(operation, time.GetUtcNow().AddSeconds(options.Value.TtlSeconds)),
                    new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(options.Value.TtlSeconds) });
        }
    }
    public void Invalidate()
    {
        lock (gate) { generation++; cache.Clear(); }
    }
    public void Dispose() => cache.Dispose();
}

public sealed class OperationCatalog(ProxyDb db, ConfigurationCache cache)
{
    public async Task<Operation?> FindAsync(string key, CancellationToken token)
    {
        var generation = cache.Generation;
        if (cache.TryGet(key, out var cached)) return cached;
        var operation = await db.Operations.AsNoTracking().AsSingleQuery().Include(x => x.Destination)
            .Include(x => x.Parameters).Include(x => x.Headers).Include(x => x.EncryptionProfile)
            .SingleOrDefaultAsync(x => x.Key == key, token);
        if (operation is not null) cache.Store(key, operation, generation);
        return operation;
    }
}
