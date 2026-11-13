using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace SecureProxy;

public sealed class ProxyRateLimiterSetup(IOptions<ProxyResilienceOptions> settings, ILogger<ProxyRateLimiterSetup> logger) : IConfigureOptions<RateLimiterOptions>
{
    public void Configure(RateLimiterOptions options)
    {
        var limits = settings.Value;
        options.RejectionStatusCode = 429;
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == "proxy"
                ? RateLimitPartition.GetConcurrencyLimiter("proxy", _ => new ConcurrencyLimiterOptions { PermitLimit = limits.MaxConcurrency, QueueLimit = 0 })
                : RateLimitPartition.GetNoLimiter("other"));
        options.AddPolicy("proxy", context => RateLimitPartition.GetFixedWindowLimiter(CallerIdentity.Subject(context.User) ?? "anonymous", _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = limits.RequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
        options.OnRejected = async (rejected, token) =>
        {
            var context = rejected.HttpContext;
            if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after))
                context.Response.Headers.RetryAfter = Math.Ceiling(after.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            ProxyTelemetry.Rejected(429, "rate_limited");
            logger.LogInformation("Ingress rejection {CorrelationId} {Status} {Reason}", context.TraceIdentifier, 429, "rate_limited");
            await PublicErrors.WriteAsync(context, 429, "rate_limited", token);
        };
    }
}
