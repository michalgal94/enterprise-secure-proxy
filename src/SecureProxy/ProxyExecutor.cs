using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SecureProxy;

public sealed record ProxyReply(int Status, byte[] Body, string ContentType, bool Upstream);
public sealed class ProxyExecutor(ProxyDb db, RequestPolicy policy, IHttpClientFactory clients, ILogger<ProxyExecutor> logger)
{
    public async Task<ProxyReply> ExecuteAsync(Stream input, string? contentType, IHeaderDictionary headers, string correlationId, CancellationToken cancellation)
    {
        var audit = new ExecutionLog { CorrelationId = correlationId };
        var watch = Stopwatch.StartNew();
        // A durable intent is mandatory before any network side effect.
        db.ExecutionLogs.Add(audit);
        using var admissionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await db.SaveChangesAsync(admissionDeadline.Token); }
        catch (Exception ex)
        { logger.LogError("Audit admission failed {CorrelationId} {FailureType}", correlationId, ex.GetType().Name); return Error(503, "audit_unavailable", correlationId); }
        ProxyReply result;
        try
        {
            if (contentType?.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase) != true) throw new ProxyFault(415, "unsupported_content_type");
            var raw = await ReadBoundedAsync(input, 1048576, cancellation, 413);
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ProxyFault(400, "invalid_envelope");
            var names = new HashSet<string>();
            foreach (var p in doc.RootElement.EnumerateObject())
                if (!names.Add(p.Name) || p.Name is not ("clientId" or "json" or "encrypted")) throw new ProxyFault(400, "invalid_envelope");
            var request = JsonSerializer.Deserialize<ExecuteRequest>(raw, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) ?? throw new ProxyFault(400, "invalid_envelope");
            if (string.IsNullOrWhiteSpace(request.ClientId) || request.ClientId.Length > 128 || !System.Text.RegularExpressions.Regex.IsMatch(request.ClientId, "^[a-zA-Z0-9._-]+$")) throw new ProxyFault(400, "invalid_envelope");
            // Record only a known, trusted operation key, never arbitrary caller text.
            var op = await db.Operations.AsNoTracking().AsSingleQuery().Include(x => x.Destination).Include(x => x.Parameters).Include(x => x.Headers).Include(x => x.EncryptionProfile)
                .SingleOrDefaultAsync(x => x.Key == request.ClientId, cancellation) ?? throw new ProxyFault(404, "unknown_operation");
            audit.OperationKey = op.Key;
            audit.DestinationId = op.DestinationId;
            var prepared = policy.Prepare(op, request);
            if (raw.Length > op.MaxRequestBytes) throw new ProxyFault(413, "request_too_large");
            using var message = new HttpRequestMessage(prepared.Method, prepared.Uri);
            if (prepared.Body is not null)
            {
                message.Content = new ByteArrayContent(prepared.Body);
                message.Content.Headers.ContentType = new("application/json");
            }
            var connectionHeaders = headers["Connection"].ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in op.Headers)
                if (RequestPolicy.HeaderAllowed(rule.Name) && !connectionHeaders.Contains(rule.Name) && headers.TryGetValue(rule.Name, out var value) && value.ToString().Length <= 256 && !value.ToString().Any(char.IsControl))
                    message.Headers.TryAddWithoutValidation(rule.Name, value.ToArray());
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(op.Destination.TimeoutSeconds));
            audit.Outcome = "Dispatching";
            await db.SaveChangesAsync(cancellation);
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                audit.Forwarded = true; // Send invoked/attempted, not proof of upstream receipt.
                using var response = await clients.CreateClient("upstream").SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                audit.UpstreamStatus = (int)response.StatusCode;
                if (response.Content.Headers.ContentLength > op.MaxResponseBytes) throw new ProxyFault(502, "response_too_large");
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                var bytes = await ReadBoundedAsync(stream, op.MaxResponseBytes, deadline.Token, 502);
                result = response.IsSuccessStatusCode
                    ? new((int)response.StatusCode, bytes, SafeContentType(response.Content.Headers.ContentType?.MediaType), true)
                    : new((int)response.StatusCode, JsonSerializer.SerializeToUtf8Bytes(new { code = "upstream_response", correlationId }), "application/json", true);
                audit.Outcome = response.IsSuccessStatusCode ? "Succeeded" : "UpstreamError";
                if (!response.IsSuccessStatusCode) audit.Reason = "upstream_http_error";
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new ProxyFault(504, "upstream_timeout"); }
        }
        catch (ProxyFault f) { audit.Reason = f.Code; audit.Outcome = audit.Forwarded ? "Failed" : "Rejected"; result = Error(f.Status, f.Code, correlationId); }
        catch (JsonException) { audit.Reason = "invalid_json"; audit.Outcome = "Rejected"; result = Error(400, "invalid_json", correlationId); }
        catch (BadHttpRequestException ex) { audit.Reason = "invalid_http_request"; audit.Outcome = "Rejected"; result = Error(ex.StatusCode, "invalid_http_request", correlationId); }
        catch (OperationCanceledException) { audit.Reason = "caller_cancelled"; audit.Outcome = "Cancelled"; result = Error(499, "caller_cancelled", correlationId); }
        catch (HttpRequestException) { audit.Reason = "upstream_connection_failed"; audit.Outcome = "Failed"; result = Error(502, "upstream_connection_failed", correlationId); }
        catch (IOException)
        {
            audit.Reason = audit.Forwarded ? "upstream_connection_failed" : "request_read_failed";
            audit.Outcome = audit.Forwarded ? "Failed" : "Rejected";
            result = Error(audit.Forwarded ? 502 : 400, audit.Reason, correlationId);
        }
        catch (Exception ex)
        {
            logger.LogError("Execution failed {CorrelationId} {FailureType}", correlationId, ex.GetType().Name);
            audit.Reason = "service_unavailable"; audit.Outcome = "Failed"; result = Error(503, "service_unavailable", correlationId);
        }
        audit.ProxyStatus = result.Status;
        audit.CompletedAt = DateTimeOffset.UtcNow;
        audit.DurationMs = watch.ElapsedMilliseconds;
        using var auditDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await db.SaveChangesAsync(auditDeadline.Token); }
        catch (Exception ex)
        {
            logger.LogCritical("Audit completion failed {CorrelationId} {FailureType}", correlationId, ex.GetType().Name);
            return Error(503, "audit_completion_failed", correlationId);
        }
        logger.LogInformation("Proxy execution {CorrelationId} {Outcome} {Status} {DurationMs}", correlationId, audit.Outcome, result.Status, audit.DurationMs);
        return result;
    }
    private static string SafeContentType(string? type) => type == "application/json" ? type : "application/octet-stream";
    public static ProxyReply Error(int status, string code, string id) => new(status, JsonSerializer.SerializeToUtf8Bytes(new { code, correlationId = id }), "application/json", false);
    public static async Task<byte[]> ReadBoundedAsync(Stream source, int limit, CancellationToken token, int status)
    {
        using var target = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)target.Length)), token);
            if (count == 0) return target.ToArray();
            if (target.Length + count > limit) throw new ProxyFault(status, status == 413 ? "request_too_large" : "response_too_large");
            await target.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }
}
