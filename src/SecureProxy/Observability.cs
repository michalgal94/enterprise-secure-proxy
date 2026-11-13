using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SecureProxy;

public static class ProxyTelemetry
{
    public const string Name = "SecureProxy";
    public static readonly ActivitySource Activities = new(Name);
    private static readonly Meter Meter = new(Name);
    private static readonly Counter<long> Executions = Meter.CreateCounter<long>("proxy.executions");
    private static readonly Counter<long> Ingress = Meter.CreateCounter<long>("proxy.ingress_rejections");
    private static readonly Counter<long> CircuitChanges = Meter.CreateCounter<long>("proxy.circuit_transitions");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("proxy.execution.duration", "ms");
    public static void Record(ExecutionLog log)
    {
        var tags = new TagList { { "outcome", log.Outcome }, { "status", log.ProxyStatus } };
        Executions.Add(1, tags);
        Duration.Record(log.DurationMs, tags);
    }
    public static void Rejected(int status, string reason) => Ingress.Add(1, new("status", status), new("reason", reason));
    public static void Circuit(string state) => CircuitChanges.Add(1, new KeyValuePair<string, object?>("state", state));
}

public static class PublicErrors
{
    public static IResult Result(int status, string code, string correlationId) => Results.Json(new { code, correlationId }, statusCode: status);
    public static async Task WriteAsync(HttpContext context, int status, string code, CancellationToken token = default)
    {
        context.Response.StatusCode = status;
        context.Response.Headers["X-Proxy-Source"] = "proxy";
        await context.Response.WriteAsJsonAsync(new { code, correlationId = context.TraceIdentifier }, token);
    }
}
