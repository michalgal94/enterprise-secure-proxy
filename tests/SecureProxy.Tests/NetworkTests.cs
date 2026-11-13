using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using SecureProxy;
using Xunit;

namespace SecureProxy.Tests;

public sealed class NetworkTests
{
    private sealed class Diagnostics(Action<Exception> report) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            try { return await base.SendAsync(request, token); }
            catch (Exception ex) { report(ex); throw; }
        }
    }
    private sealed class Factory(X509Certificate2 trustRoot) : WebApplicationFactory<Program>
    {
        public Exception? LastFailure { get; private set; }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var name = Guid.NewGuid().ToString();
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["ProxyApiKey"] = new('p', 40) }));
            builder.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<ProxyDb>>(); s.RemoveAll<ProxyDb>();
                s.AddDbContext<ProxyDb>(o => o.UseInMemoryDatabase(name));
                // Trust the test certificate without changing the system trust store.
                s.Configure<HttpClientFactoryOptions>("upstream", o => o.HttpMessageHandlerBuilderActions.Add(h =>
                {
                    var sockets = Assert.IsType<SocketsHttpHandler>(h.PrimaryHandler);
                    sockets.SslOptions.CertificateChainPolicy = new X509ChainPolicy
                    {
                        TrustMode = X509ChainTrustMode.CustomRootTrust,
                        CustomTrustStore = { trustRoot }, RevocationMode = X509RevocationMode.NoCheck
                    };
                    h.AdditionalHandlers.Add(new Diagnostics(ex => LastFailure = ex));
                }));
            });
        }
    }

    [Fact]
    public async Task Real_HTTPS_destination_receives_only_valid_calls_and_redirects_are_not_followed()
    {
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
        certificateRequest.CertificateExtensions.Add(san.Build());
        certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var generated = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        // Windows Schannel requires a key imported into its user key provider.
        using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx));
        var received = new ConcurrentQueue<string>();
        var upstreamBuilder = WebApplication.CreateBuilder();
        upstreamBuilder.Configuration["AllowedHosts"] = "127.0.0.1";
        upstreamBuilder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var upstream = upstreamBuilder.Build();
        upstream.Use(async (ctx, next) => { received.Enqueue(ctx.Request.Path); await next(ctx); });
        upstream.MapGet("/customers/{id}", (string id) => new { id });
        upstream.MapGet("/redirect", () => Microsoft.AspNetCore.Http.Results.Redirect("/forbidden"));
        upstream.MapGet("/forbidden", () => new { forbidden = true });
        await upstream.StartAsync();
        using var factory = new Factory(certificate);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProxyDb>();
            var destination = new Destination { Name = "TLS mock", Enabled = true, BaseUrl = upstream.Urls.Single() + "/", PermittedPort = new Uri(upstream.Urls.Single()).Port };
            db.Operations.Add(new() { Key = "network", Enabled = true, Path = "customers/{id}", Destination = destination, Parameters = [new() { Name = "id", Type = "string", Required = true, Location = "path" }] });
            db.Operations.Add(new() { Key = "redirect", Enabled = true, Path = "redirect", Destination = destination });
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", new string('p', 40));
        Task<HttpResponseMessage> Send(string json) => client.PostAsync("/api/proxy/execute", new StringContent(json, Encoding.UTF8, "application/json"));
        var success = await Send("{\"clientId\":\"network\",\"json\":{\"urlId\":1,\"id\":\"12\"}}");
        Assert.True(success.StatusCode == HttpStatusCode.OK, factory.LastFailure?.ToString() ?? await success.Content.ReadAsStringAsync()); Assert.Equal("/customers/12", Assert.Single(received));
        var rejected = await Send("{\"clientId\":\"network\",\"json\":{\"urlId\":1,\"id\":\"../admin\"}}");
        Assert.Equal(400, (int)rejected.StatusCode); Assert.Single(received);
        var redirect = await Send("{\"clientId\":\"redirect\",\"json\":{\"urlId\":1}}");
        Assert.Equal(302, (int)redirect.StatusCode); Assert.Null(redirect.Headers.Location);
        Assert.Equal(new[] { "/customers/12", "/redirect" }, received.ToArray());
    }
}
