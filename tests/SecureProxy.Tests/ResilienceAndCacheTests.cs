using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Polly.CircuitBreaker;
using SecureProxy;
using Xunit;

namespace SecureProxy.Tests;

public sealed class ResilienceAndCacheTests
{
    [Fact]
    public async Task Circuit_opens_blocks_dispatch_and_recovers_through_a_probe()
    {
        var clock = new FakeTimeProvider();
        var breaker = new DestinationCircuitBreaker(Options.Create(new ProxyResilienceOptions { MinimumThroughput = 2 }), clock, NullLogger<DestinationCircuitBreaker>.Instance);
        var calls = 0;
        ValueTask<ProxyReply> Fail(CancellationToken _) { calls++; return ValueTask.FromResult(ProxyExecutor.Error(503, "upstream_response", "test")); }
        ValueTask<ProxyReply> Succeed(CancellationToken _) { calls++; return ValueTask.FromResult(new ProxyReply(200, [], "application/json", true)); }
        await breaker.ExecuteAsync(1, Fail, default); await breaker.ExecuteAsync(1, Fail, default);
        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await breaker.ExecuteAsync(1, Fail, default));
        Assert.Equal(2, calls);
        Assert.Equal(200, (await breaker.ExecuteAsync(2, Succeed, default)).Status);
        clock.Advance(TimeSpan.FromSeconds(16));
        Assert.Equal(200, (await breaker.ExecuteAsync(1, Succeed, default)).Status);
        Assert.Equal(200, (await breaker.ExecuteAsync(1, Succeed, default)).Status);
        Assert.Equal(5, calls);
    }

    [Theory] [InlineData(400)] [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(422)] [InlineData(429)]
    public async Task Upstream_client_errors_do_not_trip_the_destination_circuit(int status)
    {
        var breaker = new DestinationCircuitBreaker(Options.Create(new ProxyResilienceOptions { MinimumThroughput = 2 }), new FakeTimeProvider(), NullLogger<DestinationCircuitBreaker>.Instance);
        var calls = 0;
        for (var i = 0; i < 5; i++)
            Assert.Equal(status, (await breaker.ExecuteAsync(1, _ => { calls++; return ValueTask.FromResult(ProxyExecutor.Error(status, "upstream_response", "test")); }, default)).Status);
        Assert.Equal(5, calls);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_trip_the_circuit()
    {
        var breaker = new DestinationCircuitBreaker(Options.Create(new ProxyResilienceOptions { MinimumThroughput = 2 }), new FakeTimeProvider(), NullLogger<DestinationCircuitBreaker>.Instance);
        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await breaker.ExecuteAsync(1, _ => throw new OperationCanceledException(), default));
        Assert.Equal(200, (await breaker.ExecuteAsync(1, _ => ValueTask.FromResult(new ProxyReply(200, [], "application/json", true)), default)).Status);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Cache_is_optional_and_expires_within_its_documented_window(bool enabled)
    {
        await using var db = new ProxyDb(new DbContextOptionsBuilder<ProxyDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var op = new Operation { Key = "cached", Enabled = true, Destination = new() { Name = "test", Enabled = true, BaseUrl = "https://test.example/" } };
        db.Add(op); await db.SaveChangesAsync();
        var clock = new FakeTimeProvider();
        using var cache = new ConfigurationCache(Options.Create(new ConfigurationCacheOptions { Enabled = enabled }), clock);
        var catalog = new OperationCatalog(db, cache);
        Assert.True((await catalog.FindAsync(op.Key, default))!.Enabled);
        op.Enabled = false; await db.SaveChangesAsync();
        Assert.Equal(enabled, (await catalog.FindAsync(op.Key, default))!.Enabled);
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.False((await catalog.FindAsync(op.Key, default))!.Enabled);
    }

    [Fact]
    public async Task Admin_update_invalidates_local_cache_and_records_the_actor()
    {
        await using var db = new ProxyDb(new DbContextOptionsBuilder<ProxyDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var op = new Operation { Key = "cached", Enabled = true, Version = new byte[8], Destination = new() { Name = "test", Enabled = true, BaseUrl = "https://test.example/" } };
        db.Add(op); await db.SaveChangesAsync();
        using var cache = new ConfigurationCache(Options.Create(new ConfigurationCacheOptions { Enabled = true }), new FakeTimeProvider());
        var catalog = new OperationCatalog(db, cache);
        Assert.True((await catalog.FindAsync(op.Key, default))!.Enabled);
        await ConfigurationAdmin.SetEnabledAsync(db, op.Key, new(false, Convert.ToBase64String(op.Version)), default, cache, "operator-1", "request-1");
        Assert.False((await catalog.FindAsync(op.Key, default))!.Enabled);
        Assert.Equal("operator-1", Assert.Single(db.ConfigurationChanges).ActorId);
    }

    [Fact]
    public void Concurrent_load_cannot_refill_a_cache_after_invalidation()
    {
        using var cache = new ConfigurationCache(Options.Create(new ConfigurationCacheOptions { Enabled = true }), new FakeTimeProvider());
        var generation = cache.Generation;
        cache.Invalidate(); cache.Store("cached", new Operation { Key = "cached", Enabled = true }, generation);
        Assert.False(cache.TryGet("cached", out _));
    }
}
