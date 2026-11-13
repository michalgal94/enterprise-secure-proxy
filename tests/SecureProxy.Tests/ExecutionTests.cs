using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SecureProxy;
using Xunit;

namespace SecureProxy.Tests;

public sealed class ExecutionTests
{
    [Theory] [InlineData(null)] [InlineData("invalid-base64")] [InlineData("AQID")]
    public void Missing_or_invalid_secret_is_a_service_failure(string? value)
    {
        var reference = "TEST_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var variable = "PROXY_KEY_" + reference;
        try
        {
            Environment.SetEnvironmentVariable(variable, value);
            var fault = Assert.Throws<ProxyFault>(() => new EnvironmentKeySource().GetKey(reference));
            Assert.Equal(503, fault.Status); Assert.Equal("encryption_unavailable", fault.Code);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }
    private sealed class Recorder : HttpMessageHandler, IHttpClientFactory
    {
        public List<(string Method, Uri Uri, Dictionary<string, string> Headers, string? Body)> Received { get; } = [];
        public Func<CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json") });
        public HttpClient CreateClient(string name) => new(this, false) { Timeout = Timeout.InfiniteTimeSpan };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Received.Add((request.Method.Method, request.RequestUri!, request.Headers.ToDictionary(x => x.Key, x => string.Join(',', x.Value)), request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            return await Respond(token);
        }
    }
    private sealed class Keys : IKeySource { public byte[] GetKey(string reference) => Enumerable.Repeat((byte)7, 32).ToArray(); }
    private sealed class UnknownLengthStream(bool stall = false, bool fail = false) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (fail) throw new IOException("private upstream details");
            if (stall) await Task.Delay(Timeout.Infinite, token);
            buffer.Span.Fill(42); return buffer.Length;
        }
    }
    [Fact] public async Task Broken_upstream_body_is_a_gateway_failure()
    {
        using var f = new Fixture();
        f.Handler.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnknownLengthStream(fail: true)) });
        var result = await f.Send(); Assert.Equal(502, result.Status); Assert.Single(f.Handler.Received);
        Assert.DoesNotContain("private", Encoding.UTF8.GetString(result.Body));
        Assert.Equal("upstream_connection_failed", Assert.Single(f.Db.ExecutionLogs).Reason);
    }
    [Fact] public async Task Connection_nominated_headers_are_not_forwarded()
    {
        using var f = new Fixture(); f.Op.Headers = [new() { Name = "Accept-Language" }];
        await f.Send(headers: new HeaderDictionary { ["Accept-Language"] = "he", ["Connection"] = "Accept-Language" });
        Assert.Empty(Assert.Single(f.Handler.Received).Headers);
    }
    [Fact] public async Task Destination_update_preserves_creation_time_and_refreshes_update_time()
    {
        using var f = new Fixture(); var created = f.Op.Destination.CreatedAt;
        f.Op.Destination.UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1); f.Op.Destination.Name = "changed";
        var before = DateTimeOffset.UtcNow; await f.Db.SaveChangesAsync();
        Assert.Equal(created, f.Op.Destination.CreatedAt); Assert.True(f.Op.Destination.UpdatedAt >= before);
    }
    private sealed class Fixture : IDisposable
    {
        public ProxyDb Db { get; }
        public Recorder Handler { get; } = new();
        public Operation Op { get; }
        public Fixture(SaveChangesInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<ProxyDb>().UseInMemoryDatabase(Guid.NewGuid().ToString());
            if (interceptor is not null) options.AddInterceptors(interceptor);
            Db = new(options.Options);
            Op = new() { Key = "customer.get", Enabled = true, Method = "GET", Path = "customers/{id}", Destination = new() { Name = "mock", BaseUrl = "https://trusted.example/api/", Enabled = true }, Parameters = [new() { Name = "id", Type = "integer", Required = true, Location = "path", Minimum = 1 }] };
            Db.Add(Op); Db.SaveChanges();
        }
        public async Task<ProxyReply> Send(string? json = null, string contentType = "application/json", IHeaderDictionary? headers = null, CancellationToken cancellation = default)
        {
            await Db.SaveChangesAsync();
            var input = json ?? $$$"""{"clientId":"customer.get","json":{"urlId":{{{Op.DestinationId}}},"id":12}}""";
            return await new ProxyExecutor(Db, new RequestPolicy(new Keys()), Handler, NullLogger<ProxyExecutor>.Instance)
                .ExecuteAsync(new MemoryStream(Encoding.UTF8.GetBytes(input)), contentType, headers ?? new HeaderDictionary(), Guid.NewGuid().ToString("N"), cancellation);
        }
        public string Envelope(string parameters) => $$$"""{"clientId":"customer.get","json":{"urlId":{{{Op.DestinationId}}},{{{parameters}}}}}""";
        public void Dispose() => Db.Dispose();
    }
    private sealed class AuditFailure(bool admission) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<ExecutionLog>().Any(e => admission ? e.State == EntityState.Added : e.Entity.CompletedAt is not null))
                throw new DbUpdateException("Simulated audit outage");
            return ValueTask.FromResult(result);
        }
    }
    [Fact] public async Task Audit_admission_failure_prevents_forwarding()
    {
        using var f = new Fixture(new AuditFailure(true));
        Assert.Equal(503, (await f.Send()).Status); Assert.Empty(f.Handler.Received);
        Assert.Empty(await f.Db.ExecutionLogs.AsNoTracking().ToListAsync());
    }
    [Fact] public async Task Audit_completion_failure_leaves_durable_dispatch_intent()
    {
        using var f = new Fixture(new AuditFailure(false));
        Assert.Equal(503, (await f.Send()).Status); Assert.Single(f.Handler.Received);
        var audit = Assert.Single(await f.Db.ExecutionLogs.AsNoTracking().ToListAsync());
        Assert.Equal("Dispatching", audit.Outcome); Assert.False(audit.Forwarded); Assert.Null(audit.CompletedAt);
    }
    private sealed class DispatchFailure : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<ExecutionLog>().Any(e => e.Entity.Outcome == "Dispatching" && e.Entity.CompletedAt is null))
                throw new DbUpdateException("Simulated dispatch-intent write failure");
            return ValueTask.FromResult(result);
        }
    }
    [Fact] public async Task Dispatch_intent_failure_does_not_claim_forwarding_or_send()
    {
        using var f = new Fixture(new DispatchFailure());
        Assert.Equal(503, (await f.Send()).Status); Assert.Empty(f.Handler.Received);
        Assert.False(Assert.Single(await f.Db.ExecutionLogs.AsNoTracking().ToListAsync()).Forwarded);
    }
    [Fact] public async Task Valid_operation_is_forwarded_and_audited()
    {
        using var f = new Fixture(); var result = await f.Send();
        Assert.Equal(200, result.Status); Assert.True(result.Upstream);
        Assert.Equal("https://trusted.example/api/customers/12", Assert.Single(f.Handler.Received).Uri.AbsoluteUri);
        var audit = Assert.Single(f.Db.ExecutionLogs); Assert.Equal("Succeeded", audit.Outcome); Assert.True(audit.Forwarded); Assert.NotNull(audit.CompletedAt);
    }
    [Theory]
    [InlineData("unknown", 404)] [InlineData("disabled", 403)] [InlineData("destination", 403)]
    [InlineData("url", 400)] [InlineData("method", 503)] [InlineData("required", 400)]
    [InlineData("unknown_parameter", 400)] [InlineData("traversal", 400)] [InlineData("content_type", 415)]
    [InlineData("missing_url_id", 400)] [InlineData("wrong_url_id", 400)] [InlineData("duplicate", 400)]
    [InlineData("malformed", 400)] [InlineData("size", 413)] [InlineData("template", 503)]
    [InlineData("caller_method", 400)] [InlineData("caller_path", 400)] [InlineData("duplicate_envelope", 400)]
    [InlineData("invalid_type", 400)] [InlineData("out_of_range", 400)] [InlineData("plaintext_required_encryption", 400)]
    [InlineData("http_destination", 503)] [InlineData("wrong_port", 503)]
    public async Task Rejections_never_reach_destination(string scenario, int status)
    {
        using var f = new Fixture(); string? input = null; var content = "application/json";
        switch (scenario)
        {
            case "unknown": input = "{\"clientId\":\"unknown\",\"json\":{\"urlId\":1}}"; break;
            case "disabled": f.Op.Enabled = false; break;
            case "destination": f.Op.Destination.Enabled = false; break;
            case "url": input = $$$"""{"clientId":"customer.get","destinationUrl":"https://evil.example","json":{"urlId":{{{f.Op.DestinationId}}},"id":12}}"""; break;
            case "method": f.Op.Method = "CONNECT"; break;
            case "required": input = $$$"""{"clientId":"customer.get","json":{"urlId":{{{f.Op.DestinationId}}}}}"""; break;
            case "unknown_parameter": input = f.Envelope("\"id\":12,\"url\":\"https://evil.example\""); break;
            case "traversal": f.Op.Parameters[0].Type = "string"; input = f.Envelope("\"id\":\"%252e%252e%252fadmin\""); break;
            case "content_type": content = "text/plain"; break;
            case "missing_url_id": input = "{\"clientId\":\"customer.get\",\"json\":{\"id\":12}}"; break;
            case "wrong_url_id": input = "{\"clientId\":\"customer.get\",\"json\":{\"urlId\":999,\"id\":12}}"; break;
            case "duplicate": input = f.Envelope("\"id\":12,\"id\":13"); break;
            case "malformed": input = "{"; break;
            case "size": f.Op.MaxRequestBytes = 10; break;
            case "template": f.Op.Path = "../admin"; break;
            case "caller_method": input = "{\"clientId\":\"customer.get\",\"method\":\"DELETE\",\"json\":{\"urlId\":1,\"id\":12}}"; break;
            case "caller_path": input = "{\"clientId\":\"customer.get\",\"path\":\"/admin\",\"json\":{\"urlId\":1,\"id\":12}}"; break;
            case "duplicate_envelope": input = "{\"clientId\":\"customer.get\",\"clientId\":\"other\",\"json\":{\"urlId\":1,\"id\":12}}"; break;
            case "invalid_type": input = f.Envelope("\"id\":\"12\""); break;
            case "out_of_range": input = f.Envelope("\"id\":0"); break;
            case "plaintext_required_encryption": f.Op.EncryptionRequired = true; break;
            case "http_destination": f.Op.Destination.BaseUrl = "http://trusted.example/api/"; break;
            case "wrong_port": f.Op.Destination.PermittedPort = 8443; break;
        }
        var result = await f.Send(input, content);
        Assert.Equal(status, result.Status); Assert.Empty(f.Handler.Received);
        var audit = Assert.Single(f.Db.ExecutionLogs); Assert.Equal("Rejected", audit.Outcome); Assert.False(audit.Forwarded); Assert.Equal(status, audit.ProxyStatus);
    }
    [Theory] [InlineData("..")] [InlineData("../admin")] [InlineData("%2e%2e%2fadmin")] [InlineData("%252e%252e%252fadmin")] [InlineData("a\\b")] [InlineData("a/b")] [InlineData("a\n")]
    public async Task All_path_traversal_encodings_are_blocked(string value)
    {
        using var f = new Fixture(); f.Op.Parameters[0].Type = "string";
        var input = JsonSerializer.Serialize(new { clientId = f.Op.Key, json = new { urlId = f.Op.DestinationId, id = value } });
        Assert.Equal(400, (await f.Send(input)).Status); Assert.Empty(f.Handler.Received);
    }
    [Fact] public async Task Query_values_are_encoded_and_body_values_keep_their_types()
    {
        using var f = new Fixture(); f.Op.Method = "POST";
        f.Op.Parameters.Add(new() { Name = "filter", Type = "string", Location = "query" });
        f.Op.Parameters.Add(new() { Name = "active", Type = "boolean", Location = "body", Required = true });
        var result = await f.Send(f.Envelope("\"id\":12,\"filter\":\"a&host=evil.example\",\"active\":true"));
        Assert.Equal(200, result.Status); var sent = Assert.Single(f.Handler.Received);
        Assert.Equal("?filter=a%26host%3Devil.example", sent.Uri.Query);
        using var body = JsonDocument.Parse(sent.Body!); Assert.True(body.RootElement.GetProperty("active").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("urlId", out _));
        Assert.False(body.RootElement.TryGetProperty("id", out _));
    }
    [Fact] public async Task Optional_query_parameter_can_be_omitted()
    {
        using var f = new Fixture(); f.Op.Parameters.Add(new() { Name = "filter", Location = "query" });
        Assert.Equal(200, (await f.Send()).Status); Assert.Equal("", Assert.Single(f.Handler.Received).Uri.Query);
    }
    [Fact] public async Task Unknown_allowed_parameters_go_only_to_body()
    {
        using var f = new Fixture(); f.Op.Method = "POST"; f.Op.AllowUnknownParameters = true;
        var result = await f.Send(f.Envelope("\"id\":12,\"destinationUrl\":\"https://evil.example\""));
        Assert.Equal(200, result.Status); var sent = Assert.Single(f.Handler.Received);
        Assert.Equal("trusted.example", sent.Uri.Host); Assert.Contains("destinationUrl", sent.Body);
    }
    [Fact] public async Task Headers_require_both_configured_and_hard_allowlists()
    {
        using var f = new Fixture(); f.Op.Headers = [new() { Name = "Authorization" }, new() { Name = "Accept-Language" }, new() { Name = "Cookie" }];
        await f.Send(headers: new HeaderDictionary { ["Authorization"] = "secret", ["Cookie"] = "secret", ["Host"] = "evil.example", ["Connection"] = "keep-alive", ["Accept-Language"] = "he" });
        var sent = Assert.Single(f.Handler.Received); Assert.Single(sent.Headers); Assert.Equal("he", sent.Headers["Accept-Language"]);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Invalid_encryption_is_never_forwarded(bool corruptTag)
    {
        using var f = new Fixture(); f.Op.EncryptionRequired = true; f.Op.EncryptionProfile = new() { Enabled = true, KeyReference = "TEST" };
        string envelope;
        if (corruptTag)
        {
            var plaintext = Encoding.UTF8.GetBytes($$$"""{"urlId":{{{f.Op.DestinationId}}},"id":12}""");
            var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var ciphertext = new byte[plaintext.Length];
            using var aes = new AesGcm(new Keys().GetKey("TEST"), 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(f.Op.Key)); tag[0] ^= 1;
            envelope = JsonSerializer.Serialize(new { clientId = f.Op.Key, encrypted = new { nonce = Convert.ToBase64String(nonce), ciphertext = Convert.ToBase64String(ciphertext), tag = Convert.ToBase64String(tag) } });
        }
        else envelope = "{\"clientId\":\"customer.get\",\"encrypted\":{\"nonce\":\"invalid\",\"ciphertext\":\"?\",\"tag\":\"?\"}}";
        Assert.Equal(400, (await f.Send(envelope)).Status); Assert.Empty(f.Handler.Received);
    }
    [Fact] public async Task Valid_encryption_is_authenticated_and_forwarded()
    {
        using var f = new Fixture(); f.Op.EncryptionRequired = true; f.Op.EncryptionProfile = new() { Enabled = true, KeyReference = "TEST" };
        var plain = Encoding.UTF8.GetBytes($$$"""{"urlId":{{{f.Op.DestinationId}}},"id":12}""");
        var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[plain.Length];
        using var aes = new AesGcm(new Keys().GetKey("TEST"), 16); aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(f.Op.Key));
        var envelope = JsonSerializer.Serialize(new { clientId = f.Op.Key, encrypted = new { nonce = Convert.ToBase64String(nonce), ciphertext = Convert.ToBase64String(cipher), tag = Convert.ToBase64String(tag) } });
        Assert.Equal(200, (await f.Send(envelope)).Status); Assert.Single(f.Handler.Received);
    }
    [Fact] public async Task Upstream_timeout_returns_504()
    {
        using var f = new Fixture(); f.Op.Destination.TimeoutSeconds = 1;
        f.Handler.Respond = async token => { await Task.Delay(Timeout.Infinite, token); return new(); };
        Assert.Equal(504, (await f.Send()).Status); Assert.Single(f.Handler.Received); Assert.Equal("upstream_timeout", Assert.Single(f.Db.ExecutionLogs).Reason);
    }
    [Fact] public async Task Caller_cancellation_is_propagated()
    {
        using var f = new Fixture(); using var cts = new CancellationTokenSource();
        f.Handler.Respond = async token => { cts.Cancel(); await Task.Delay(Timeout.Infinite, token); return new(); };
        Assert.Equal(499, (await f.Send(cancellation: cts.Token)).Status); Assert.Single(f.Handler.Received); Assert.Equal("Cancelled", Assert.Single(f.Db.ExecutionLogs).Outcome);
    }
    [Fact] public async Task Upstream_error_preserves_status_and_hides_sensitive_body()
    {
        using var f = new Fixture(); f.Handler.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("internal-host password") });
        var result = await f.Send(); Assert.Equal(422, result.Status); Assert.True(result.Upstream); Assert.DoesNotContain("password", Encoding.UTF8.GetString(result.Body)); Assert.Single(f.Handler.Received);
        Assert.Equal("upstream_http_error", Assert.Single(f.Db.ExecutionLogs).Reason);
    }
    [Fact] public void Missing_destination_is_rejected_defensively()
    {
        using var f = new Fixture(); f.Op.Destination = null!;
        using var json = JsonDocument.Parse("{\"urlId\":1,\"id\":12}");
        var fault = Assert.Throws<ProxyFault>(() => new RequestPolicy(new Keys()).Prepare(f.Op, new(f.Op.Key, json.RootElement)));
        Assert.Equal(403, fault.Status); Assert.Empty(f.Handler.Received);
    }
    [Fact] public async Task Connection_failure_returns_502()
    {
        using var f = new Fixture(); f.Handler.Respond = _ => throw new HttpRequestException("private host");
        Assert.Equal(502, (await f.Send()).Status); Assert.Single(f.Handler.Received);
    }
    [Fact] public async Task Oversized_response_is_rejected()
    {
        using var f = new Fixture(); f.Op.MaxResponseBytes = 3;
        Assert.Equal(502, (await f.Send()).Status); Assert.Single(f.Handler.Received);
    }
    [Theory] [InlineData(false, 502)] [InlineData(true, 504)]
    public async Task Response_body_without_content_length_is_bounded_and_has_deadline(bool stall, int expected)
    {
        using var f = new Fixture(); f.Op.MaxResponseBytes = 64; f.Op.Destination.TimeoutSeconds = 1;
        f.Handler.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnknownLengthStream(stall)) });
        Assert.Equal(expected, (await f.Send()).Status); Assert.Single(f.Handler.Received);
    }
    [Fact] public async Task Stale_configuration_returns_conflict_without_forwarding()
    {
        using var f = new Fixture(); f.Op.Version = new byte[8]; await f.Db.SaveChangesAsync();
        var result = await ConfigurationAdmin.SetEnabledAsync(f.Db, f.Op.Key, new(false, Convert.ToBase64String(Enumerable.Repeat((byte)1, 8).ToArray())), default);
        Assert.Equal(409, ((IStatusCodeHttpResult)result).StatusCode); Assert.True(f.Op.Enabled); Assert.Empty(f.Handler.Received); Assert.Empty(f.Db.ConfigurationChanges);
    }
}
