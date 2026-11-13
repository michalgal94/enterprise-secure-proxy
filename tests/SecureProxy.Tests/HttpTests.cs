using System.Net;
using System.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecureProxy;
using Xunit;

namespace SecureProxy.Tests;
public sealed class HttpTests
{
    private sealed class RecordingHandler : HttpMessageHandler, IHttpClientFactory
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();
        public Func<CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json") });
        public HttpClient CreateClient(string name) => new(this, false) { Timeout = Timeout.InfiniteTimeSpan };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests.Enqueue(request.RequestUri!); return Respond(token); }
    }
    private sealed class Factory : WebApplicationFactory<Program>
    {
        public RecordingHandler Upstream { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            { ["ProxyApiKey"] = new('p', 40), ["AdminApiKey"] = new('a', 40) }));
            var name = Guid.NewGuid().ToString();
            builder.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<ProxyDb>>(); s.RemoveAll<ProxyDb>();
                s.AddDbContext<ProxyDb>(o => o.UseInMemoryDatabase(name));
                s.RemoveAll<IHttpClientFactory>();
                s.AddSingleton<IHttpClientFactory>(Upstream);
            });
        }
        public async Task Seed()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProxyDb>();
            db.Callers.Add(new Caller { Id = "local-demo", Grants = [new() { Operation = new() { Key = "echo", Path = "echo", Enabled = true, Destination = new() { Enabled = true, Name = "test", BaseUrl = "https://configured.example/" } } }] });
            await db.SaveChangesAsync();
        }
    }
    [Fact] public async Task Http_endpoint_rejects_unknown_operation_with_audit_and_correlation()
    {
        using var factory = new Factory(); using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Api-Key", new string('p', 40));
        var response = await client.PostAsync("/api/proxy/execute", new StringContent("{\"clientId\":\"absent\",\"json\":{\"urlId\":1}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("proxy", response.Headers.GetValues("X-Proxy-Source").Single());
        using var scope = factory.Services.CreateScope();
        var audit = Assert.Single(scope.ServiceProvider.GetRequiredService<ProxyDb>().ExecutionLogs);
        Assert.Equal(response.Headers.GetValues("X-Correlation-Id").Single(), audit.CorrelationId); Assert.False(audit.Forwarded);
        Assert.Empty(factory.Upstream.Requests);
    }
    [Theory] [InlineData("http://localhost", false, 400)] [InlineData("https://localhost", false, 401)] [InlineData("https://localhost", true, 403)]
    public async Task Transport_and_admin_authentication_are_enforced(string address, bool admin, int status)
    {
        using var factory = new Factory(); using var client = factory.CreateClient(new() { BaseAddress = new(address), AllowAutoRedirect = false });
        if (admin) client.DefaultRequestHeaders.Add("X-Api-Key", new string('p', 40));
        var response = admin ? await client.GetAsync("/api/admin/operations/customer.get") : await client.PostAsync("/api/proxy/execute", new StringContent("{}"));
        Assert.Equal(status, (int)response.StatusCode);
        using var scope = factory.Services.CreateScope(); Assert.Empty(scope.ServiceProvider.GetRequiredService<ProxyDb>().ExecutionLogs);
        Assert.Empty(factory.Upstream.Requests);
    }
    [Fact] public async Task Valid_HTTP_request_is_forwarded_and_response_is_identified()
    {
        using var factory = new Factory(); await factory.Seed();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Api-Key", new string('p', 40));
        var response = await client.PostAsync("/api/proxy/execute", new StringContent("{\"clientId\":\"echo\",\"json\":{\"urlId\":1}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("upstream", response.Headers.GetValues("X-Proxy-Source").Single());
        Assert.Equal("{\"ok\":true}", await response.Content.ReadAsStringAsync());
        Assert.Equal("https://configured.example/echo", Assert.Single(factory.Upstream.Requests).AbsoluteUri);
    }
    [Theory] [InlineData("{", "application/json")] [InlineData("{}", "text/plain")]
    public async Task Malformed_admin_requests_return_client_errors(string body, string type)
    {
        using var factory = new Factory();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Api-Key", new string('a', 40));
        var response = await client.PutAsync("/api/admin/operations/echo/enabled", new StringContent(body, Encoding.UTF8, type));
        Assert.Equal(type == "text/plain" ? 415 : 400, (int)response.StatusCode);
        Assert.Empty(factory.Upstream.Requests);
    }
    [Fact] public async Task Concurrency_limiter_rejects_the_33rd_request_without_dispatch()
    {
        using var factory = new Factory(); await factory.Seed();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        factory.Upstream.Respond = async token =>
        {
            if (Interlocked.Increment(ref count) == 32) arrived.TrySetResult();
            await release.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        };
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Api-Key", new string('p', 40));
        Task<HttpResponseMessage> Send() => client.PostAsync("/api/proxy/execute", new StringContent("{\"clientId\":\"echo\",\"json\":{\"urlId\":1}}", Encoding.UTF8, "application/json"));
        var pending = Enumerable.Range(0, 32).Select(_ => Send()).ToArray();
        try
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var rejected = await Send(); Assert.Equal(429, (int)rejected.StatusCode);
            Assert.Contains("rate_limited", await rejected.Content.ReadAsStringAsync());
            Assert.Equal(32, factory.Upstream.Requests.Count);
        }
        finally { release.TrySetResult(); await Task.WhenAll(pending); }
    }
    [Theory]
    [InlineData("route", 404)] [InlineData("method", 405)] [InlineData("admin_operation", 404)]
    [InlineData("version", 400)] [InlineData("conflict", 409)]
    public async Task Public_errors_have_the_same_safe_contract(string scenario, int expected)
    {
        using var factory = new Factory(); await factory.Seed();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Api-Key", new string(scenario.StartsWith("admin") || scenario is "version" or "conflict" ? 'a' : 'p', 40));
        var response = scenario switch
        {
            "route" => await client.GetAsync("/absent"),
            "method" => await client.GetAsync("/api/proxy/execute"),
            "admin_operation" => await client.GetAsync("/api/admin/operations/absent"),
            _ => await client.PutAsync("/api/admin/operations/echo/enabled", new StringContent(
                scenario == "version" ? "{\"enabled\":true,\"version\":\"invalid\"}" : "{\"enabled\":true,\"version\":\"AAAAAAAAAAA=\"}", Encoding.UTF8, "application/json"))
        };
        Assert.Equal(expected, (int)response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
        Assert.Equal(response.Headers.GetValues("X-Correlation-Id").Single(), body.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal("proxy", response.Headers.GetValues("X-Proxy-Source").Single());
        Assert.Empty(factory.Upstream.Requests);
    }
    [Fact] public async Task Execution_emits_metrics_and_links_its_trace_to_the_audit()
    {
        var measurements = new ConcurrentQueue<(string Name, KeyValuePair<string, object?>[] Tags)>();
        using var meters = new MeterListener();
        meters.InstrumentPublished = (instrument, listener) => { if (instrument.Meter.Name == ProxyTelemetry.Name) listener.EnableMeasurementEvents(instrument); };
        meters.SetMeasurementEventCallback<long>((instrument, _, tags, _) => measurements.Enqueue((instrument.Name, tags.ToArray())));
        meters.SetMeasurementEventCallback<double>((instrument, _, tags, _) => measurements.Enqueue((instrument.Name, tags.ToArray())));
        meters.Start();
        var spans = new ConcurrentQueue<Activity>();
        using var traces = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ProxyTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = spans.Enqueue
        };
        ActivitySource.AddActivityListener(traces);
        using var factory = new Factory(); await factory.Seed();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Api-Key", new string('p', 40));
        var response = await client.PostAsync("/api/proxy/execute", new StringContent("{\"clientId\":\"echo\",\"json\":{\"urlId\":1}}", Encoding.UTF8, "application/json"));
        Assert.Equal(200, (int)response.StatusCode);
        var id = response.Headers.GetValues("X-Correlation-Id").Single();
        var span = Assert.Single(spans, x => Equals(x.GetTagItem("proxy.correlation_id"), id));
        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<ProxyDb>().ExecutionLogs.Single(x => x.CorrelationId == id);
        Assert.Equal(span.TraceId.ToString(), audit.TraceId); Assert.Equal("local-demo", audit.CallerId);
        Assert.Contains(measurements, x => x.Name == "proxy.executions");
        Assert.Contains(measurements, x => x.Name == "proxy.execution.duration");
        Assert.All(measurements.SelectMany(x => x.Tags), tag => Assert.Contains(tag.Key, new[] { "outcome", "status", "reason", "state" }));
    }
}
