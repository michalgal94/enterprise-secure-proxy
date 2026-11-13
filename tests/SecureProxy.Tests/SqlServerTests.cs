using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using SecureProxy;
using Xunit;

namespace SecureProxy.Tests;
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROXY_TEST_SQL"))) Skip = "Set PROXY_TEST_SQL to a dedicated disposable SQL Server database."; }
}
public sealed class SqlServerTests
{
    private sealed class Recorder : HttpMessageHandler, IHttpClientFactory
    {
        public int Count { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
    private sealed class NoKeys : IKeySource { public byte[] GetKey(string reference) => throw new InvalidOperationException(); }
    [SqlServerFact]
    public async Task Real_SQL_persists_success_and_rejection_audits()
    {
        var options = new DbContextOptionsBuilder<ProxyDb>().UseSqlServer(Environment.GetEnvironmentVariable("PROXY_TEST_SQL")).Options;
        await using var db = new ProxyDb(options); await db.Database.MigrateAsync();
        var key = "sql-execute-" + Guid.NewGuid().ToString("N");
        var op = new Operation { Key = key, Enabled = true, Path = "echo", Destination = new() { Name = key, Enabled = true, BaseUrl = "https://configured.example/" } };
        db.Add(op); await db.SaveChangesAsync();
        var recorder = new Recorder();
        var ids = new[] { Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") };
        try
        {
            var executor = new ProxyExecutor(db, new RequestPolicy(new NoKeys()), recorder, NullLogger<ProxyExecutor>.Instance);
            var valid = System.Text.Json.JsonSerializer.Serialize(new { clientId = key, json = new { urlId = op.DestinationId } });
            Assert.Equal(200, (await executor.ExecuteAsync(new MemoryStream(Encoding.UTF8.GetBytes(valid)), "application/json", new HeaderDictionary(), ids[0], default)).Status);
            Assert.Equal(1, recorder.Count);
            var invalid = System.Text.Json.JsonSerializer.Serialize(new { clientId = key, json = new { urlId = op.DestinationId, extra = "rejected" } });
            Assert.Equal(400, (await executor.ExecuteAsync(new MemoryStream(Encoding.UTF8.GetBytes(invalid)), "application/json", new HeaderDictionary(), ids[1], default)).Status);
            Assert.Equal(1, recorder.Count);
            await using var verify = new ProxyDb(options);
            var logs = await verify.ExecutionLogs.Where(x => ids.Contains(x.CorrelationId)).ToListAsync();
            Assert.Equal(2, logs.Count);
            Assert.True(logs.Single(x => x.CorrelationId == ids[0]).Forwarded);
            Assert.False(logs.Single(x => x.CorrelationId == ids[1]).Forwarded);
            Assert.All(logs, x => Assert.NotNull(x.CompletedAt));
        }
        finally
        {
            await db.ExecutionLogs.Where(x => ids.Contains(x.CorrelationId)).ExecuteDeleteAsync();
            await db.Operations.Where(x => x.Id == op.Id).ExecuteDeleteAsync();
            await db.Destinations.Where(x => x.Id == op.DestinationId).ExecuteDeleteAsync();
        }
    }
    [SqlServerFact]
    public async Task Real_SQL_rowversion_rejects_stale_writer_and_records_history_atomically()
    {
        var options = new DbContextOptionsBuilder<ProxyDb>().UseSqlServer(Environment.GetEnvironmentVariable("PROXY_TEST_SQL")).Options;
        await using var first = new ProxyDb(options);
        await first.Database.MigrateAsync();
        var key = "sql-test-" + Guid.NewGuid().ToString("N");
        var op = new Operation { Key = key, Enabled = true, Destination = new() { Name = key, BaseUrl = "https://test.example/", Enabled = true } };
        first.Add(op); await first.SaveChangesAsync();
        try
        {
            await using var second = new ProxyDb(options);
            var stale = await second.Operations.SingleAsync(x => x.Key == key);
            var result = await ConfigurationAdmin.SetEnabledAsync(first, key, new(false, Convert.ToBase64String(op.Version)), default);
            Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
            // second still tracks the old version: pre-check passes, SQL detects the race.
            result = await ConfigurationAdmin.SetEnabledAsync(second, key, new(true, Convert.ToBase64String(stale.Version)), default);
            Assert.Equal(409, ((IStatusCodeHttpResult)result).StatusCode);
            await using var verify = new ProxyDb(options);
            Assert.False((await verify.Operations.SingleAsync(x => x.Key == key)).Enabled);
            Assert.Single(await verify.ConfigurationChanges.Where(x => x.OperationId == op.Id).ToListAsync());
        }
        finally
        {
            await first.ConfigurationChanges.Where(x => x.OperationId == op.Id).ExecuteDeleteAsync();
            await first.Operations.Where(x => x.Id == op.Id).ExecuteDeleteAsync();
            await first.Destinations.Where(x => x.Id == op.DestinationId).ExecuteDeleteAsync();
        }
    }
}
