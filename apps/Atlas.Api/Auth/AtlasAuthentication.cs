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
        // Public browser and issuer URLs always use HTTPS. A deployment may fetch
        // discovery over its private container network without weakening the issuer.
        var allowInsecurePreview = configuration.GetValue("Authentication:AllowInsecureLanPreview", false);
        var allowInternalMetadata = configuration.GetValue("Authentication:AllowInsecureInternalMetadata", false);
        var cookieSecurePolicy = allowInsecurePreview ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        var oidcAuthority = configuration["Authentication:Oidc:Authority"];
        var oidcMetadataAddress = configuration["Authentication:Oidc:MetadataAddress"];
        var bearerAuthority = configuration["Authentication:Bearer:Authority"];
        var bearerMetadataAddress = configuration["Authentication:Bearer:MetadataAddress"];

        services.AddAuthentication(options =>
        {
            options.DefaultScheme = SmartScheme;
            // API/session probes return 401; only /auth/login starts the interactive OIDC flow.
            options.DefaultChallengeScheme = SmartScheme;
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
            options.Cookie.SecurePolicy = cookieSecurePolicy;
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
            options.Authority = oidcAuthority;
            options.MetadataAddress = oidcMetadataAddress;
            if (allowInternalMetadata)
                options.BackchannelHttpHandler = PrivateIdentityBackchannelHandler.Create(oidcAuthority, oidcMetadataAddress);
            options.ClientId = configuration["Authentication:Oidc:ClientId"];
            options.ClientSecret = configuration["Authentication:Oidc:ClientSecret"];
            options.CallbackPath = "/signin-oidc";
            options.SignedOutCallbackPath = "/signout-callback-oidc";
            options.CorrelationCookie.SecurePolicy = cookieSecurePolicy;
            options.NonceCookie.SecurePolicy = cookieSecurePolicy;
            if (cookieSecurePolicy == CookieSecurePolicy.SameAsRequest)
            {
                // Browsers reject SameSite=None cookies without Secure on the explicit HTTP LAN preview.
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SameSite = SameSiteMode.Lax;
                options.ResponseMode = "query";
            }

            options.ResponseType = "code";
            options.UsePkce = true;
            options.SaveTokens = false;
            options.GetClaimsFromUserInfoEndpoint = configuration.GetValue("Authentication:Oidc:UseUserInfo", false);
            var callbackOrigin = configuration["Authentication:Oidc:CallbackOrigin"]?.TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(callbackOrigin))
            {
                // The callback is part of the public client contract, not whichever
                // loopback/host alias happened to reach the reverse proxy. In
                // particular, PAR validates it before returning the login redirect.
                options.Events.OnRedirectToIdentityProvider = context =>
                {
                    context.ProtocolMessage.RedirectUri = callbackOrigin + "/signin-oidc";
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToIdentityProviderForSignOut = context =>
                {
                    context.ProtocolMessage.PostLogoutRedirectUri = callbackOrigin + "/signout-callback-oidc";
                    return Task.CompletedTask;
                };
            }
            options.RequireHttpsMetadata = !allowInsecurePreview && !allowInternalMetadata && !environment.IsDevelopment();
            options.MapInboundClaims = false;
            options.TokenValidationParameters.AuthenticationType = CookieScheme;
            options.TokenValidationParameters.NameClaimType = "name";
            options.Scope.Clear();
            foreach (var scope in new[] { "openid", "profile", "atlas.read", "atlas.propose", "atlas.admin", "atlas.task", "atlas.evidence", "atlas.review", "atlas.ci", "atlas.agent" }) options.Scope.Add(scope);
            options.Events.OnTokenValidated = context =>
            {
                if (context.Principal?.Identity is ClaimsIdentity identity)
                {
                    foreach (var claim in identity.FindAll(CsrfClaim).ToArray()) identity.TryRemoveClaim(claim);
                    identity.AddClaim(new Claim(CsrfClaim, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
                }
                return Task.CompletedTask;
            };
        })
        .AddJwtBearer(BearerScheme, options =>
        {
            options.Authority = bearerAuthority;
            if (!string.IsNullOrWhiteSpace(bearerMetadataAddress))
            {
                options.MetadataAddress = bearerMetadataAddress;
                if (allowInternalMetadata)
                    options.BackchannelHttpHandler = PrivateIdentityBackchannelHandler.Create(bearerAuthority, bearerMetadataAddress);
            }
            options.Audience = configuration["Authentication:Bearer:Audience"];
            options.RequireHttpsMetadata = !allowInsecurePreview && !allowInternalMetadata && !environment.IsDevelopment();
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                AuthenticationType = BearerScheme,
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
            .AddPolicy("AtlasPropose", policy => policy.RequireAuthenticatedUser().RequireAssertion(context => HasScope(context.User, "atlas.propose", CookieScheme)))
            .AddPolicy("AtlasAdmin", policy => policy.RequireAuthenticatedUser().RequireAssertion(context => HasScope(context.User, "atlas.admin", CookieScheme)))
            .AddPolicy("AtlasTask", policy => policy.RequireAuthenticatedUser().RequireAssertion(context => HasScope(context.User, "atlas.task", CookieScheme)))
            .AddPolicy("AtlasEvidence", policy => policy.RequireAuthenticatedUser().RequireAssertion(context => HasScope(context.User, "atlas.evidence", CookieScheme)))
            .AddPolicy("AtlasReview", policy => policy.RequireAuthenticatedUser().RequireAssertion(context => HasScope(context.User, "atlas.review", CookieScheme)))
            .AddPolicy("AtlasCi", policy => policy.RequireAuthenticatedUser().RequireAssertion(context => HasScope(context.User, "atlas.ci", CookieScheme)))
            .AddPolicy("AtlasAgent", policy => policy.RequireAuthenticatedUser().RequireAssertion(context => HasScope(context.User, "atlas.agent", CookieScheme)))
            .AddPolicy("McpBearer", policy => policy.RequireAuthenticatedUser().RequireAssertion(context => context.User.Identity?.AuthenticationType == BearerScheme && HasScope(context.User, "atlas.read", BearerScheme)));
        return services;
    }

    public static Task<AtlasPrincipal> ResolveAtlasPrincipalAsync(this HttpContext context, CancellationToken cancellationToken) =>
        context.User.ResolveAtlasPrincipalAsync(context.RequestServices, cancellationToken);

    public static async Task<AtlasPrincipal> ResolveAtlasPrincipalAsync(this ClaimsPrincipal user, IServiceProvider services, CancellationToken cancellationToken)
    {
        var issuer = user.FindFirst("iss")?.Value;
        var subject = user.FindFirst("sub")?.Value;
        if (issuer is null && services.GetService<IConfiguration>() is { } configuration)
            issuer = user.Identity?.AuthenticationType == BearerScheme
                ? configuration["Authentication:Bearer:Authority"]
                : configuration["Authentication:Oidc:Authority"];
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
            throw new UnauthorizedAccessException("The authenticated subject is missing iss or sub.");
        var isBearer = user.Identity?.AuthenticationType == BearerScheme;
        var kind = PrincipalKind.Human;
        if (isBearer && string.Equals(user.FindFirst("atlas_principal_kind")?.Value, "agent", StringComparison.Ordinal))
            kind = PrincipalKind.Agent;
        else if (isBearer && (user.HasClaim("client_id", subject) || user.HasClaim("azp", subject)))
            kind = PrincipalKind.Service;
        return await services.GetRequiredService<PostgresProjectIdentityStore>()
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
            if (!string.IsNullOrEmpty(origin) && !IsSameOrigin(context, origin.ToString()))
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

    private static bool IsSameOrigin(HttpContext context, string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase))
            return false;
        var originPort = uri.IsDefaultPort ? (uri.Scheme == Uri.UriSchemeHttps ? 443 : 80) : uri.Port;
        var requestPort = context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80);
        return originPort == requestPort;
    }

    private static bool HasScope(ClaimsPrincipal user, string required, string browserScheme)
    {
        if (user.Identity?.AuthenticationType == browserScheme) return true;
        return user.FindAll("scope").SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Concat(user.FindAll("scp").SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Contains(required, StringComparer.Ordinal);
    }

    /// <summary>
    /// Keeps the public HTTPS issuer unchanged while sending server-side discovery,
    /// JWKS, token and user-info requests over the private identity network.
    /// Discovery alone is not sufficient: its jwks_uri still contains the public URL.
    /// </summary>
    private sealed class PrivateIdentityBackchannelHandler : HttpClientHandler
    {
        private readonly Uri _authority;
        private readonly Uri _metadata;
        private readonly string _authorityPath;
        private readonly string _privateRealmPath;

        private PrivateIdentityBackchannelHandler(Uri authority, Uri metadata)
        {
            _authority = authority;
            _metadata = metadata;
            _authorityPath = authority.AbsolutePath.TrimEnd('/');
            var realm = metadata.AbsolutePath.IndexOf("/realms/", StringComparison.Ordinal);
            _privateRealmPath = realm < 0 ? string.Empty : metadata.AbsolutePath[..realm].TrimEnd('/');
        }

        public static PrivateIdentityBackchannelHandler? Create(string? authority, string? metadataAddress)
        {
            if (!Uri.TryCreate(authority, UriKind.Absolute, out var publicAuthority) ||
                !Uri.TryCreate(metadataAddress, UriKind.Absolute, out var privateMetadata) ||
                publicAuthority.Scheme != Uri.UriSchemeHttps || privateMetadata.Scheme != Uri.UriSchemeHttp ||
                !privateMetadata.AbsolutePath.Contains("/realms/", StringComparison.Ordinal))
                return null;
            return new PrivateIdentityBackchannelHandler(publicAuthority, privateMetadata);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is { } uri && IsPublicIdentityUrl(uri))
            {
                var relativePath = uri.AbsolutePath[_authorityPath.Length..];
                var builder = new UriBuilder(uri)
                {
                    Scheme = _metadata.Scheme,
                    Host = _metadata.Host,
                    Port = _metadata.IsDefaultPort ? -1 : _metadata.Port,
                    Path = _privateRealmPath + relativePath
                };
                request.RequestUri = builder.Uri;
            }
            return base.SendAsync(request, cancellationToken);
        }

        private bool IsPublicIdentityUrl(Uri uri) =>
            string.Equals(uri.Scheme, _authority.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(uri.Host, _authority.Host, StringComparison.OrdinalIgnoreCase) &&
            uri.Port == _authority.Port &&
            (uri.AbsolutePath.Equals(_authorityPath, StringComparison.Ordinal) ||
             uri.AbsolutePath.StartsWith(_authorityPath + "/", StringComparison.Ordinal));
    }
}
