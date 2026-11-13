using System.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using SecureProxy;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.UseUtcTimestamp = true; o.TimestampFormat = "O"; });
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 1048576);
builder.Services.AddDbContext<ProxyDb>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("Proxy"), sql => sql.CommandTimeout(5)));
builder.Services.AddSingleton<IKeySource, EnvironmentKeySource>();
builder.Services.AddScoped<RequestPolicy>();
builder.Services.AddScoped<ProxyExecutor>();
builder.Services.AddProxySecurity(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ConfigurationCache>();
builder.Services.AddScoped<OperationCatalog>();
builder.Services.AddSingleton<DestinationCircuitBreaker>();
builder.Services.AddOptions<ConfigurationCacheOptions>().BindConfiguration("ConfigurationCache")
    .Validate(o => o.TtlSeconds is >= 1 and <= 60 && o.Capacity is >= 1 and <= 4096, "Invalid configuration cache bounds.").ValidateOnStart();
builder.Services.AddOptions<ProxyResilienceOptions>().BindConfiguration("Resilience")
    .Validate(o => o.RequestsPerMinute is >= 1 and <= 100000 && o.MaxConcurrency is >= 1 and <= 1024 &&
        o.MinimumThroughput >= 2 && o.FailureRatio is > 0 and <= 1 && o.SamplingSeconds is >= 1 and <= 300 &&
        o.BreakSeconds is >= 1 and <= 300, "Invalid resilience bounds.").ValidateOnStart();
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddHttpClient("upstream", c => c.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 32, MaxResponseHeadersLength = 32
    }).RemoveAllLoggers();
builder.Services.AddRateLimiter(_ => { });
builder.Services.AddSingleton<IConfigureOptions<RateLimiterOptions>, ProxyRateLimiterSetup>();
var app = builder.Build();
if (args.Contains("--initialize"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ProxyDb>();
    await db.Database.MigrateAsync();
    if (!await db.Destinations.AnyAsync())
    {
        var dest = new Destination { Name = "Local mock", BaseUrl = "https://localhost:7443/", Enabled = true, PermittedPort = 7443 };
        var demoOperation = new Operation
        {
            Key = "customer.get", Destination = dest, Enabled = true, Method = "GET", Path = "customers/{id}",
            Parameters = [new() { Name = "id", Location = "path", Type = "integer", Required = true, Minimum = 1, Maximum = 1000000 }],
            Headers = [new() { Name = "Accept-Language" }]
        };
        db.Operations.Add(demoOperation);
        db.Callers.Add(new Caller { Id = "local-demo", Grants = [new() { Operation = demoOperation }] });
        await db.SaveChangesAsync();
    }
    if (!await db.Callers.AnyAsync(x => x.Id == "local-demo"))
    {
        var demoOperation = await db.Operations.SingleOrDefaultAsync(x => x.Key == "customer.get");
        if (demoOperation is not null)
        {
            db.Callers.Add(new Caller { Id = "local-demo", Grants = [new() { Operation = demoOperation }] });
            await db.SaveChangesAsync();
        }
    }
    return;
}
app.Use(async (context, next) =>
{
    context.TraceIdentifier = Guid.NewGuid().ToString("N");
    context.Response.Headers["X-Correlation-Id"] = context.TraceIdentifier;
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Proxy-Source"] = "proxy";
    if (!context.Request.IsHttps)
    {
        ProxyTelemetry.Rejected(400, "https_required");
        app.Logger.LogInformation("Ingress rejection {CorrelationId} {Status} {Reason}", context.TraceIdentifier, 400, "https_required");
        await PublicErrors.WriteAsync(context, 400, "https_required"); return;
    }
    try { await next(context); }
    catch (BadHttpRequestException ex)
    {
        if (!context.Response.HasStarted)
        {
            await PublicErrors.WriteAsync(context, ex.StatusCode, "invalid_http_request");
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    { if (!context.Response.HasStarted) context.Response.StatusCode = 499; }
    catch (Exception ex)
    {
        app.Logger.LogError("Unhandled request failure {CorrelationId} {FailureType}", context.TraceIdentifier, ex.GetType().Name);
        if (!context.Response.HasStarted) await PublicErrors.WriteAsync(context, 503, "service_unavailable");
    }
});
app.UseAuthentication();
app.Use(async (context, next) =>
{
    var subject = CallerIdentity.Subject(context.User);
    var status = context.User.Identity?.IsAuthenticated == true && subject is { Length: > 0 and <= 128 } ? 0 : 401;
    if (status == 0 && context.Request.Path.StartsWithSegments("/api/admin") && !context.User.IsInRole("proxy.admin")) status = 403;
    if (status != 0)
    {
        var code = status == 401 ? "unauthorized" : "forbidden";
        ProxyTelemetry.Rejected(status, code);
        app.Logger.LogInformation("Ingress rejection {CorrelationId} {Status} {Reason}", context.TraceIdentifier, status, code);
        await PublicErrors.WriteAsync(context, status, code); return;
    }
    await next(context);
});
app.UseRateLimiter();
app.UseStatusCodePages(async status =>
{
    var context = status.HttpContext;
    var code = context.Response.StatusCode switch { 404 => "not_found", 405 => "method_not_allowed", _ => "request_failed" };
    await PublicErrors.WriteAsync(context, context.Response.StatusCode, code);
});
app.MapPost("/api/proxy/execute", async (HttpContext ctx, ProxyExecutor executor) =>
{
    var reply = await executor.ExecuteAsync(ctx.Request.Body, ctx.Request.ContentType, ctx.Request.Headers, ctx.TraceIdentifier, ctx.RequestAborted, CallerIdentity.Subject(ctx.User)!);
    ctx.Response.StatusCode = reply.Status;
    ctx.Response.ContentType = reply.ContentType;
    ctx.Response.Headers["X-Proxy-Source"] = reply.Upstream ? "upstream" : "proxy";
    if (reply.Status is not (204 or 304)) await ctx.Response.Body.WriteAsync(reply.Body, ctx.RequestAborted);
}).RequireRateLimiting("proxy");
app.MapGet("/api/admin/operations/{key}", async (string key, ProxyDb db, HttpContext ctx, CancellationToken token) =>
{
    var op = await db.Operations.AsNoTracking().SingleOrDefaultAsync(x => x.Key == key, token);
    return op is null ? PublicErrors.Result(404, "unknown_operation", ctx.TraceIdentifier) : Results.Ok(new { op.Key, op.Enabled, version = Convert.ToBase64String(op.Version) });
});
app.MapPut("/api/admin/operations/{key}/enabled", async (string key, EnableRequest request, ProxyDb db, ConfigurationCache cache, HttpContext ctx, CancellationToken token) =>
{
    return await ConfigurationAdmin.SetEnabledAsync(db, key, request, token, cache, CallerIdentity.Subject(ctx.User)!, ctx.TraceIdentifier);
});
app.MapGet("/api/admin/health", async (ProxyDb db, HttpContext ctx, CancellationToken token) =>
    await db.Database.CanConnectAsync(token) ? Results.Ok(new { status = "ready" }) : PublicErrors.Result(503, "database_unavailable", ctx.TraceIdentifier));
app.MapPost("/api/admin/configuration-cache/invalidate", (ConfigurationCache cache, HttpContext ctx) =>
{
    cache.Invalidate();
    app.Logger.LogInformation("Configuration cache invalidated {CorrelationId} {ActorId}", ctx.TraceIdentifier, CallerIdentity.Subject(ctx.User));
    return Results.NoContent();
});
app.Run();
public partial class Program;
