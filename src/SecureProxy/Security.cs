using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace SecureProxy;

public sealed class SecurityOptions
{
    public string Mode { get; set; } = "DevelopmentKey";
    public string? Authority { get; set; }
    public string? Audience { get; set; }
}
public static class CallerIdentity
{
    public static string? Subject(ClaimsPrincipal user) => user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
}
public sealed class DevelopmentKeyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IConfiguration config)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var supplied = Request.Headers["X-Api-Key"].ToString();
        var admin = Matches(config["AdminApiKey"], supplied);
        if (!admin && !Matches(config["ProxyApiKey"], supplied)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, admin ? "local-admin" : "local-demo") };
        if (admin) claims.Add(new(ClaimTypes.Role, "proxy.admin"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
    private static bool Matches(string? expected, string supplied) => expected is { Length: >= 32 } &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
}
public static class ProxySecurity
{
    public static IServiceCollection AddProxySecurity(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<SecurityOptions>().BindConfiguration("Security")
            .Validate(o => o.Mode is "DevelopmentKey" or "Jwt", "Security:Mode must be DevelopmentKey or Jwt.")
            .Validate(o => o.Mode != "Jwt" || (Uri.TryCreate(o.Authority, UriKind.Absolute, out var uri) && uri.Scheme == "https" && !string.IsNullOrWhiteSpace(o.Audience)),
                "JWT mode requires an HTTPS authority and an audience.").ValidateOnStart();
        services.AddAuthentication("Proxy")
            .AddPolicyScheme("Proxy", null, o => o.ForwardDefaultSelector = context =>
                context.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value.Mode == "Jwt" ? JwtBearerDefaults.AuthenticationScheme : "DevelopmentKey")
            .AddScheme<AuthenticationSchemeOptions, DevelopmentKeyHandler>("DevelopmentKey", _ => { })
            .AddJwtBearer(o =>
            {
                o.Authority = config["Security:Authority"];
                o.Audience = config["Security:Audience"];
                o.RequireHttpsMetadata = true;
                o.IncludeErrorDetails = false;
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
                    ValidateIssuerSigningKey = true, RequireSignedTokens = true, RequireExpirationTime = true,
                    NameClaimType = "sub", RoleClaimType = "roles", ClockSkew = TimeSpan.FromSeconds(30)
                };
            });
        return services;
    }
}
