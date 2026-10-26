using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using MiniWms.Application.Authentication;
using MiniWms.Infrastructure.Authentication;

namespace MiniWms.Api.Authentication;

public static class ApiAuthenticationRegistration
{
    public const string StaffPolicy = "WarehouseStaff";
    public const string AdminPolicy = "Administrator";
    public const string LoginRateLimitPolicy = "LoginAttempts";
    public const string CurrentUserItem = "MiniWms.AuthenticatedUser";

    public static IServiceCollection AddApiAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var jwt = JwtSettings.FromConfiguration(configuration);
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.IncludeErrorDetails = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(jwt.GetSigningKeyBytes()),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                    RoleClaimType = "role",
                    NameClaimType = "name"
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        // Public status and refresh-token endpoints do not authenticate
                        // an optional access-token header or contact its dependencies.
                        if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
                        {
                            context.NoResult();
                        }
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async context =>
                    {
                        try
                        {
                            if (context.Principal is null)
                            {
                                context.Fail("The session is invalid.");
                                return;
                            }

                            var identity = ReadSessionIdentity(context.Principal);
                            var auth = context.HttpContext.RequestServices.GetRequiredService<IAuthService>();
                            var user = await auth.ValidateSessionAsync(identity, context.HttpContext.RequestAborted);
                            context.HttpContext.Items[CurrentUserItem] = user;

                            // Authorization uses the account's current role, read from PostgreSQL.
                            if (context.Principal.Identity is ClaimsIdentity claimsIdentity)
                            {
                                foreach (var roleClaim in claimsIdentity.FindAll("role").ToArray())
                                {
                                    claimsIdentity.RemoveClaim(roleClaim);
                                }
                                claimsIdentity.AddClaim(new Claim("role", user.Role));
                            }
                        }
                        catch (AuthException exception) when (exception.Kind is AuthErrorKind.InvalidSession or
                            AuthErrorKind.InvalidCredentials or AuthErrorKind.Validation)
                        {
                            context.Fail("The session is invalid.");
                        }
                    },
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        if (context.Response.HasStarted)
                        {
                            return;
                        }
                        context.Response.Headers["WWW-Authenticate"] = "Bearer";
                        await ApiProblems.WriteAsync(context.HttpContext, 401, "Authentication required",
                            "A valid bearer token and current login session are required.");
                    },
                    OnForbidden = context => ApiProblems.WriteAsync(context.HttpContext, 403, "Forbidden",
                        "You do not have permission to perform this operation.")
                };
            });

        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().Build();
            options.AddPolicy(StaffPolicy, policy => policy.RequireAuthenticatedUser().RequireRole("Admin", "Staff"));
            options.AddPolicy(AdminPolicy, policy => policy.RequireAuthenticatedUser().RequireRole("Admin"));
        });

        var loginPermitLimit = configuration.GetValue<int?>("Authentication:LoginPermitLimit") ?? 60;
        if (loginPermitLimit is < 1 or > 10000)
        {
            throw new InvalidOperationException("Authentication:LoginPermitLimit must be between 1 and 10000.");
        }
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) => new ValueTask(ApiProblems.WriteAsync(context.HttpContext,
                429, "Too many login attempts", "Too many login attempts. Wait before trying again."));
            options.AddPolicy(LoginRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = loginPermitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
        });
        return services;
    }

    public static AuthSessionIdentity ReadSessionIdentity(ClaimsPrincipal principal)
    {
        if (!Guid.TryParse(principal.FindFirst("sub")?.Value, out var userId) || userId == Guid.Empty ||
            !Guid.TryParse(principal.FindFirst("sid")?.Value, out var sessionId) || sessionId == Guid.Empty ||
            !long.TryParse(principal.FindFirst("auth_version")?.Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var version) || version < 1)
        {
            throw new AuthException(AuthErrorKind.InvalidSession, "The session is invalid.");
        }
        return new AuthSessionIdentity(userId, sessionId, version);
    }
}
