using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SecureProxy;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 1048576);
builder.Services.AddDbContext<ProxyDb>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("Proxy"), sql => sql.CommandTimeout(5)));
builder.Services.AddSingleton<IKeySource, EnvironmentKeySource>();
builder.Services.AddScoped<RequestPolicy>();
builder.Services.AddScoped<ProxyExecutor>();
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddHttpClient("upstream", c => c.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 32, MaxResponseHeadersLength = 32
    }).RemoveAllLoggers();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.OnRejected = async (context, token) => await context.HttpContext.Response.WriteAsJsonAsync(new { code = "concurrency_limit", correlationId = context.HttpContext.TraceIdentifier }, token);
    o.AddConcurrencyLimiter("proxy", l => { l.PermitLimit = 32; l.QueueLimit = 0; l.QueueProcessingOrder = QueueProcessingOrder.OldestFirst; });
});
var app = builder.Build();
if (args.Contains("--initialize"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ProxyDb>();
    await db.Database.MigrateAsync();
    if (!await db.Destinations.AnyAsync())
    {
        var dest = new Destination { Name = "Local mock", BaseUrl = "https://localhost:7443/", Enabled = true, PermittedPort = 7443 };
        db.Operations.Add(new Operation
        {
            Key = "customer.get", Destination = dest, Enabled = true, Method = "GET", Path = "customers/{id}",
            Parameters = [new() { Name = "id", Location = "path", Type = "integer", Required = true, Minimum = 1, Maximum = 1000000 }],
            Headers = [new() { Name = "Accept-Language" }]
        });
        await db.SaveChangesAsync();
    }
    return;
}
app.Use(async (context, next) =>
{
    context.TraceIdentifier = Guid.NewGuid().ToString("N");
    context.Response.Headers["X-Correlation-Id"] = context.TraceIdentifier;
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    if (!context.Request.IsHttps) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { code = "https_required", correlationId = context.TraceIdentifier }); return; }
    var expected = builder.Configuration[context.Request.Path.StartsWithSegments("/api/admin") ? "AdminApiKey" : "ProxyApiKey"];
    var supplied = context.Request.Headers["X-Api-Key"].ToString();
    if (string.IsNullOrEmpty(expected) || expected.Length < 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
    { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { code = "unauthorized", correlationId = context.TraceIdentifier }); return; }
    try { await next(context); }
    catch (BadHttpRequestException ex)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = ex.StatusCode;
            await context.Response.WriteAsJsonAsync(new { code = "invalid_http_request", correlationId = context.TraceIdentifier });
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    { if (!context.Response.HasStarted) context.Response.StatusCode = 499; }
    catch (Exception ex)
    {
        app.Logger.LogError("Unhandled request failure {CorrelationId} {FailureType}", context.TraceIdentifier, ex.GetType().Name);
        if (!context.Response.HasStarted) { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { code = "service_unavailable", correlationId = context.TraceIdentifier }); }
    }
});
app.UseRateLimiter();
app.MapPost("/api/proxy/execute", async (HttpContext ctx, ProxyExecutor executor) =>
{
    var reply = await executor.ExecuteAsync(ctx.Request.Body, ctx.Request.ContentType, ctx.Request.Headers, ctx.TraceIdentifier, ctx.RequestAborted);
    ctx.Response.StatusCode = reply.Status;
    ctx.Response.ContentType = reply.ContentType;
    ctx.Response.Headers["X-Proxy-Source"] = reply.Upstream ? "upstream" : "proxy";
    if (reply.Status is not (204 or 304)) await ctx.Response.Body.WriteAsync(reply.Body, ctx.RequestAborted);
}).RequireRateLimiting("proxy");
app.MapGet("/api/admin/operations/{key}", async (string key, ProxyDb db, CancellationToken token) =>
{
    var op = await db.Operations.AsNoTracking().SingleOrDefaultAsync(x => x.Key == key, token);
    return op is null ? Results.NotFound() : Results.Ok(new { op.Key, op.Enabled, version = Convert.ToBase64String(op.Version) });
});
app.MapPut("/api/admin/operations/{key}/enabled", async (string key, EnableRequest request, ProxyDb db, CancellationToken token) =>
{
    try { return await ConfigurationAdmin.SetEnabledAsync(db, key, request, token); }
    catch (FormatException) { return Results.BadRequest(new { code = "invalid_version" }); }
});
app.Run();
public partial class Program;
