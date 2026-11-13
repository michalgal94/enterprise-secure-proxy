using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SecureProxy;
using Xunit;

namespace SecureProxy.Tests;

public sealed class CallerSecurityTests
{
    private sealed class Recorder : HttpMessageHandler, IHttpClientFactory
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Enqueue(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        }
    }
    private sealed class Factory(int rateLimit = 60) : WebApplicationFactory<Program>
    {
        public RSA SigningKey { get; } = RSA.Create(2048);
        public Recorder Upstream { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var name = Guid.NewGuid().ToString();
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:Mode"] = "Jwt", ["Security:Authority"] = "https://issuer.example", ["Security:Audience"] = "proxy-tests",
                ["ProxyApiKey"] = new('p', 40), ["AdminApiKey"] = new('a', 40),
                ["Resilience:RequestsPerMinute"] = rateLimit.ToString(), ["ConfigurationCache:Enabled"] = "true"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<ProxyDb>>(); services.RemoveAll<ProxyDb>();
                services.AddDbContext<ProxyDb>(o => o.UseInMemoryDatabase(name));
                services.RemoveAll<IHttpClientFactory>(); services.AddSingleton<IHttpClientFactory>(Upstream);
                services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Configuration = new OpenIdConnectConfiguration { Issuer = "https://issuer.example" };
                    options.Configuration.SigningKeys.Add(new RsaSecurityKey(SigningKey));
                    options.TokenValidationParameters.ValidIssuer = "https://issuer.example";
                    options.TokenValidationParameters.ValidAudience = "proxy-tests";
                });
            });
        }
        public async Task Seed(bool bobGrant = false)
        {
            using var scope = Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ProxyDb>();
            var op = new Operation { Key = "secure.get", Enabled = true, Path = "echo", Destination = new() { Name = "test", Enabled = true, BaseUrl = "https://trusted.example/" } };
            db.Callers.Add(new Caller { Id = "alice", Grants = [new() { Operation = op }] });
            db.Callers.Add(new Caller { Id = "bob", Grants = bobGrant ? [new() { Operation = op }] : [] });
            await db.SaveChangesAsync();
        }
        public string Token(string subject, string issuer = "https://issuer.example", string audience = "proxy-tests", bool expired = false, RSA? key = null, bool admin = false)
        {
            var claims = new List<Claim>();
            if (subject.Length > 0) claims.Add(new("sub", subject));
            if (admin) claims.Add(new("roles", "proxy.admin"));
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, audience, claims,
                DateTime.UtcNow.AddHours(-1), expired ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(10),
                new SigningCredentials(new RsaSecurityKey(key ?? SigningKey), SecurityAlgorithms.RsaSha256)));
        }
        public HttpClient Client(string subject, bool admin = false)
        {
            var client = CreateClient(new() { BaseAddress = new("https://localhost") });
            client.DefaultRequestHeaders.Authorization = new("Bearer", Token(subject, admin: admin));
            return client;
        }
    }
    private static Task<HttpResponseMessage> Execute(HttpClient client) => client.PostAsync("/api/proxy/execute", new StringContent("{\"clientId\":\"secure.get\",\"json\":{\"urlId\":1}}", Encoding.UTF8, "application/json"));

    [Fact]
    public async Task Valid_identity_still_needs_an_explicit_operation_grant()
    {
        using var factory = new Factory(); await factory.Seed();
        using var alice = factory.Client("alice"); using var bob = factory.Client("bob");
        Assert.Equal(HttpStatusCode.OK, (await Execute(alice)).StatusCode);
        var denied = await Execute(bob); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Single(factory.Upstream.Requests);
        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<ProxyDb>().ExecutionLogs.Single(x => x.CallerId == "bob");
        Assert.False(audit.Forwarded); Assert.Equal("operation_not_permitted", audit.Reason);
    }
    [Theory] [InlineData("signature")] [InlineData("expiry")] [InlineData("issuer")] [InlineData("audience")] [InlineData("subject")] [InlineData("api_key_fallback")]
    public async Task Invalid_JWT_is_rejected_without_forwarding(string scenario)
    {
        using var factory = new Factory(); await factory.Seed(); using var wrongKey = RSA.Create(2048);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Api-Key", new string('p', 40));
        if (scenario != "api_key_fallback") client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(
            scenario == "subject" ? "" : "alice", scenario == "issuer" ? "https://other.example" : "https://issuer.example",
            scenario == "audience" ? "other" : "proxy-tests", scenario == "expiry", scenario == "signature" ? wrongKey : null));
        var response = await Execute(client); Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.Upstream.Requests);
    }
    [Fact]
    public async Task Grant_revocation_applies_even_when_operation_configuration_is_cached()
    {
        using var factory = new Factory(); await factory.Seed(); using var client = factory.Client("alice");
        Assert.Equal(HttpStatusCode.OK, (await Execute(client)).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProxyDb>(); db.CallerOperationGrants.Remove(db.CallerOperationGrants.Single(x => x.CallerId == "alice")); await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Execute(client)).StatusCode); Assert.Single(factory.Upstream.Requests);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Disabled_or_unknown_callers_cannot_use_a_valid_token(bool unknown)
    {
        using var factory = new Factory(); await factory.Seed();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProxyDb>(); db.Callers.Single(x => x.Id == "alice").Enabled = false; await db.SaveChangesAsync();
        }
        using var client = factory.Client(unknown ? "unknown" : "alice");
        Assert.Equal(HttpStatusCode.Forbidden, (await Execute(client)).StatusCode); Assert.Empty(factory.Upstream.Requests);
    }
    [Fact]
    public async Task Rate_limits_are_separate_for_each_authenticated_caller()
    {
        using var factory = new Factory(2); await factory.Seed(bobGrant: true);
        using var alice = factory.Client("alice"); using var bob = factory.Client("bob");
        Assert.Equal(200, (int)(await Execute(alice)).StatusCode); Assert.Equal(200, (int)(await Execute(alice)).StatusCode);
        var rejected = await Execute(alice); Assert.Equal(429, (int)rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter); Assert.Equal(2, factory.Upstream.Requests.Count);
        Assert.Equal(200, (int)(await Execute(bob)).StatusCode); Assert.Equal(3, factory.Upstream.Requests.Count);
    }
    [Fact]
    public async Task Admin_endpoints_require_a_signed_admin_role()
    {
        using var factory = new Factory(); await factory.Seed();
        using var user = factory.Client("alice"); using var admin = factory.Client("operator-1", admin: true);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/operations/secure.get")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/operations/secure.get")).StatusCode);
        Assert.Empty(factory.Upstream.Requests);
    }
}
