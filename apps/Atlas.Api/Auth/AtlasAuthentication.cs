using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using ProjectIdentity.Contracts;
using ProjectIdentity.Infrastructure;

namespace Atlas.Api.Auth;

public static class AtlasAuthentication
{
    public const string SmartScheme = "AtlasSmart";
    public const string CookieScheme = "AtlasCookie";
    public const string BearerScheme = "AtlasBearer";
    public const string OidcScheme = "AtlasOidc";
    public const string CsrfClaim = "atlas.csrf";

    public static IServiceCollection AddAtlasAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var dataProtection = services.AddDataProtection().SetApplicationName("BusinessAtlas");
        var keyPath = configuration["Authentication:DataProtectionKeyPath"];
        if (!string.IsNullOrWhiteSpace(keyPath)) dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));

        services.AddAuthentication(options =>
        {
            options.DefaultScheme = SmartScheme;
            options.DefaultChallengeScheme = OidcScheme;
            options.DefaultSignOutScheme = CookieScheme;
        })
        .AddPolicyScheme(SmartScheme, "Atlas browser or API credential", options =>
        {
            options.ForwardDefaultSelector = context => context.Request.Headers.Authorization.Any(value =>
                value?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true) ? BearerScheme : CookieScheme;
        })
        .AddCookie(CookieScheme, options =>
        {
            options.Cookie.Name = "atlas.session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        })
        .AddOpenIdConnect(OidcScheme, options =>
        {
            options.Authority = configuration["Authentication:Oidc:Authority"];
            options.ClientId = configuration["Authentication:Oidc:ClientId"];
            options.ClientSecret = configuration["Authentication:Oidc:ClientSecret"];
            options.CallbackPath = "/signin-oidc";
            options.ResponseType = "code";
            options.UsePkce = true;
            options.SaveTokens = false;
            options.GetClaimsFromUserInfoEndpoint = configuration.GetValue("Authentication:Oidc:UseUserInfo", false);
            options.RequireHttpsMetadata = !environment.IsDevelopment();
            options.MapInboundClaims = false;
            options.TokenValidationParameters.NameClaimType = "name";
            options.Scope.Clear();
            foreach (var scope in new[] { "openid", "profile", "atlas.read", "atlas.admin" }) options.Scope.Add(scope);
            options.Events.OnTokenValidated = context =>
            {
                if (context.Principal?.Identity is ClaimsIdentity identity)
                {
                    identity.RemoveClaim(identity.FindFirst(CsrfClaim));
                    identity.AddClaim(new Claim(CsrfClaim, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
                }
                return Task.CompletedTask;
            };
        })
        .AddJwtBearer(BearerScheme, options =>
        {
            options.Authority = configuration["Authentication:Bearer:Authority"];
            options.Audience = configuration["Authentication:Bearer:Audience"];
            options.RequireHttpsMetadata = !environment.IsDevelopment();
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidAudience = configuration["Authentication:Bearer:Audience"],
                ClockSkew = TimeSpan.FromMinutes(1),
                NameClaimType = "name"
            };
        });

        services.AddAuthorizationBuilder()
            .AddPolicy("AtlasRead", policy => policy.RequireAuthenticatedUser().RequireAssertion(context => HasScope(context.User, "atlas.read", CookieScheme)))
            .AddPolicy("AtlasAdmin", policy => policy.RequireAuthenticatedUser().RequireAssertion(context => HasScope(context.User, "atlas.admin", CookieScheme)));
        return services;
    }

    public static async Task<AtlasPrincipal> ResolveAtlasPrincipalAsync(this HttpContext context, CancellationToken cancellationToken)
    {
        var user = context.User;
        var issuer = user.FindFirst("iss")?.Value;
        var subject = user.FindFirst("sub")?.Value;
        if (issuer is null && context.RequestServices.GetService<IConfiguration>() is { } configuration)
            issuer = user.Identity?.AuthenticationType == BearerScheme
                ? configuration["Authentication:Bearer:Authority"]
                : configuration["Authentication:Oidc:Authority"];
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
            throw new UnauthorizedAccessException("The authenticated subject is missing iss or sub.");
        var isBearer = user.Identity?.AuthenticationType == BearerScheme;
        var kind = isBearer && user.HasClaim("client_id", user.FindFirst("sub")?.Value ?? string.Empty)
            ? PrincipalKind.Service
            : PrincipalKind.Human;
        return await context.RequestServices.GetRequiredService<PostgresProjectIdentityStore>()
            .ResolvePrincipalAsync(issuer, subject, kind, cancellationToken);
    }

    public static IApplicationBuilder UseAtlasCsrfProtection(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var unsafeRequest = HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) ||
                            HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method);
        var browserSession = context.User.Identity?.AuthenticationType == CookieScheme;
        if (unsafeRequest && browserSession)
        {
            var origin = context.Request.Headers.Origin;
            if (!string.IsNullOrEmpty(origin) && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Host, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase) ||
                (uri.IsDefaultPort ? -1 : uri.Port) != (context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80))))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var expected = context.User.FindFirst(CsrfClaim)?.Value;
            var actual = context.Request.Headers["X-CSRF-Token"].ToString();
            if (expected is null || actual.Length == 0 ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
        await next();
    });

    private static bool HasScope(ClaimsPrincipal user, string required, string browserScheme)
    {
        if (user.Identity?.AuthenticationType == browserScheme) return true;
        return user.FindAll("scope").SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Concat(user.FindAll("scp").SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Contains(required, StringComparer.Ordinal);
    }
}
