using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;

namespace SecureProxy;

public sealed class ProxyResilienceOptions
{
    public int RequestsPerMinute { get; set; } = 60;
    public int MaxConcurrency { get; set; } = 32;
    public bool CircuitBreakerEnabled { get; set; } = true;
    public int MinimumThroughput { get; set; } = 5;
    public double FailureRatio { get; set; } = 0.5;
    public int SamplingSeconds { get; set; } = 30;
    public int BreakSeconds { get; set; } = 15;
}

public sealed class DestinationCircuitBreaker(IOptions<ProxyResilienceOptions> options, TimeProvider time, ILogger<DestinationCircuitBreaker> logger)
{
    private readonly ConcurrentDictionary<int, ResiliencePipeline<ProxyReply>> pipelines = new();
    public async ValueTask<ProxyReply> ExecuteAsync(int destinationId, Func<CancellationToken, ValueTask<ProxyReply>> send, CancellationToken token)
    {
        if (!options.Value.CircuitBreakerEnabled) return await send(token);
        var pipeline = pipelines.GetOrAdd(destinationId, Build);
        return await pipeline.ExecuteAsync(send, token);
    }
    private ResiliencePipeline<ProxyReply> Build(int destinationId)
    {
        var settings = options.Value;
        return new ResiliencePipelineBuilder<ProxyReply> { TimeProvider = time }
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<ProxyReply>
            {
                FailureRatio = settings.FailureRatio,
                MinimumThroughput = settings.MinimumThroughput,
                SamplingDuration = TimeSpan.FromSeconds(settings.SamplingSeconds),
                BreakDuration = TimeSpan.FromSeconds(settings.BreakSeconds),
                ShouldHandle = new PredicateBuilder<ProxyReply>()
                    .Handle<HttpRequestException>().Handle<IOException>()
                    .Handle<ProxyFault>(fault => fault.Status is 502 or 504)
                    .HandleResult(reply => reply.Status >= 500 || reply.Status == 408),
                OnOpened = _ => { Changed("open", destinationId); return default; },
                OnClosed = _ => { Changed("closed", destinationId); return default; },
                OnHalfOpened = _ => { Changed("half_open", destinationId); return default; }
            }).Build();
    }
    private void Changed(string state, int destinationId)
    {
        ProxyTelemetry.Circuit(state);
        logger.LogInformation("Destination circuit {DestinationId} {State}", destinationId, state);
    }
}
