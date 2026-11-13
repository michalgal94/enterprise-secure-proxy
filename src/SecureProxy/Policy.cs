using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SecureProxy;

public interface IKeySource { byte[] GetKey(string reference); }
public sealed class EnvironmentKeySource : IKeySource
{
    public byte[] GetKey(string reference)
    {
        if (!Regex.IsMatch(reference, "\\A[A-Z0-9_]{1,80}\\z")) throw new ProxyFault(503, "encryption_unavailable");
        byte[] key;
        try { key = Convert.FromBase64String(Environment.GetEnvironmentVariable("PROXY_KEY_" + reference) ?? ""); }
        catch (FormatException) { throw new ProxyFault(503, "encryption_unavailable"); }
        if (key.Length != 32) throw new ProxyFault(503, "encryption_unavailable");
        return key;
    }
}
public sealed record PreparedRequest(Uri Uri, HttpMethod Method, byte[]? Body);
public sealed class RequestPolicy(IKeySource keys)
{
    private static readonly HashSet<string> Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"];
    private static readonly HashSet<string> SafeHeaders = new(StringComparer.OrdinalIgnoreCase) { "Accept-Language", "X-Request-Id", "Idempotency-Key" };
    public static bool HeaderAllowed(string name) => SafeHeaders.Contains(name);

    public PreparedRequest Prepare(Operation op, ExecuteRequest envelope)
    {
        if (!op.Enabled) throw new ProxyFault(403, "operation_disabled");
        if (op.Destination is not { Enabled: true } d) throw new ProxyFault(403, "destination_disabled");
        if (!Methods.Contains(op.Method)) throw new ProxyFault(503, "invalid_configuration");
        if (op.MaxRequestBytes is < 1 or > 1048576 || op.MaxResponseBytes is < 1 or > 8388608 || d.TimeoutSeconds is < 1 or > 120)
            throw new ProxyFault(503, "invalid_configuration");
        if (!Uri.TryCreate(d.BaseUrl, UriKind.Absolute, out var root) || root.Scheme != "https" || root.Port != d.PermittedPort ||
            root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0 || !root.AbsolutePath.EndsWith('/'))
            throw new ProxyFault(503, "invalid_configuration");
        JsonElement data = envelope.Json;
        if (op.EncryptionRequired)
        {
            if (envelope.Encrypted is null || data.ValueKind != JsonValueKind.Undefined || op.EncryptionProfile is not { Enabled: true } profile)
                throw new ProxyFault(400, "invalid_encrypted_payload");
            byte[]? key = null;
            try
            {
                key = keys.GetKey(profile.KeyReference);
                var e = envelope.Encrypted;
                var cipher = Convert.FromBase64String(e.Ciphertext);
                if (cipher.Length > op.MaxRequestBytes) throw new ProxyFault(413, "request_too_large");
                var plain = new byte[cipher.Length];
                try
                {
                    using var aes = new AesGcm(key, 16);
                    aes.Decrypt(Convert.FromBase64String(e.Nonce), cipher, Convert.FromBase64String(e.Tag), plain, Encoding.UTF8.GetBytes(op.Key));
                    using var document = JsonDocument.Parse(plain);
                    data = document.RootElement.Clone();
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or JsonException)
            { throw new ProxyFault(400, "invalid_encrypted_payload"); }
            finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
        }
        else if (envelope.Encrypted is not null) throw new ProxyFault(400, "unexpected_encryption");
        if (data.ValueKind != JsonValueKind.Object) throw new ProxyFault(400, "invalid_parameters");
        if (Encoding.UTF8.GetByteCount(data.GetRawText()) > op.MaxRequestBytes) throw new ProxyFault(413, "request_too_large");
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in data.EnumerateObject()) if (!values.TryAdd(p.Name, p.Value)) throw new ProxyFault(400, "duplicate_parameter");
        if (!values.Remove("urlId", out var urlId) || !urlId.TryGetInt32Safe(out var id) || id != d.Id)
            throw new ProxyFault(400, "invalid_url_id");
        var path = op.Path;
        if (path.Any(char.IsControl) || path.StartsWith('/') || path.Contains(':') || path.Contains('?') || path.Contains('#') || path.Contains('%') || path.Contains('\\') || path.Split('/').Any(x => x is "." or ".."))
            throw new ProxyFault(503, "invalid_configuration");
        var query = new List<string>();
        var body = new Dictionary<string, JsonElement>();
        foreach (var rule in op.Parameters)
        {
            if (!Regex.IsMatch(rule.Name, "\\A[A-Za-z][A-Za-z0-9_]{0,63}\\z") || rule.Name == "urlId" || rule.MaxLength is < 1 or > 65536)
                throw new ProxyFault(503, "invalid_configuration");
            if (!values.Remove(rule.Name, out var value))
            { if (rule.Required || rule.Location == "path") throw new ProxyFault(400, "missing_parameter"); continue; }
            Validate(rule, value);
            var s = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            switch (rule.Location)
            {
                case "path":
                    if (s.Any(char.IsControl) || !Regex.IsMatch(s, "^[A-Za-z0-9_-]+$")) throw new ProxyFault(400, "invalid_path_parameter");
                    path = path.Replace("{" + rule.Name + "}", Uri.EscapeDataString(s), StringComparison.Ordinal); break;
                case "query": query.Add(Uri.EscapeDataString(rule.Name) + "=" + Uri.EscapeDataString(s)); break;
                case "body": body.Add(rule.Name, value); break;
                default: throw new ProxyFault(503, "invalid_configuration");
            }
        }
        if (values.Count != 0 && !op.AllowUnknownParameters) throw new ProxyFault(400, "unknown_parameter");
        foreach (var pair in values) body.Add(pair.Key, pair.Value);
        if (path.Contains('{') || path.Contains('}') || ((op.Method is "GET" or "HEAD") && body.Count > 0)) throw new ProxyFault(503, "invalid_configuration");
        var uri = new Uri(root, path + (query.Count == 0 ? "" : "?" + string.Join('&', query)));
        if (uri.Authority != root.Authority || !uri.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal)) throw new ProxyFault(503, "invalid_configuration");
        return new(uri, new HttpMethod(op.Method), body.Count == 0 ? null : JsonSerializer.SerializeToUtf8Bytes(body));
    }
    private static void Validate(ParameterRule r, JsonElement v)
    {
        var valid = r.Type switch
        {
            "string" => v.ValueKind == JsonValueKind.String,
            "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
            "number" => v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out _),
            "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => throw new ProxyFault(503, "invalid_configuration")
        };
        if (!valid || (v.ValueKind == JsonValueKind.String ? v.GetString()!.Length : v.GetRawText().Length) > r.MaxLength) throw new ProxyFault(400, "invalid_parameter");
        if (v.ValueKind == JsonValueKind.Number && (!v.TryGetDecimal(out var n) || n < (r.Minimum ?? decimal.MinValue) || n > (r.Maximum ?? decimal.MaxValue)))
            throw new ProxyFault(400, "invalid_parameter");
    }
}
internal static class JsonExtensions
{
    public static bool TryGetInt32Safe(this JsonElement e, out int n) { n = 0; return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out n); }
}
