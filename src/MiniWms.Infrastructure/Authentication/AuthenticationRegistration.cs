using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MiniWms.Application.Authentication;
using MiniWms.Domain.Entities;
using StackExchange.Redis;

namespace MiniWms.Infrastructure.Authentication;

public static class AuthenticationRegistration
{
    public static IServiceCollection AddAuthenticationInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var jwtSettings = JwtSettings.FromConfiguration(configuration);
        var redisSettings = new RedisAuthSettings { KeyPrefix = configuration["Redis:KeyPrefix"] ?? "miniwms:auth:" };
        redisSettings.Validate();
        var policy = new AuthPolicy(
            TimeSpan.FromMinutes(ReadPositiveNumber(configuration, "Authentication:AccessTokenMinutes", 15)),
            TimeSpan.FromHours(ReadPositiveNumber(configuration, "Authentication:SessionLifetimeHours", 8)));
        policy.Validate();
        var redisConnectionString = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(redisConnectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:Redis is required for authentication.");
        }

        var redisOptions = ConfigurationOptions.Parse(redisConnectionString);
        redisOptions.AbortOnConnectFail = false;
        redisOptions.ConnectRetry = 1;
        redisOptions.ClientName = string.IsNullOrWhiteSpace(redisOptions.ClientName) ? "MiniWms.Api" : redisOptions.ClientName;

        services.AddSingleton(jwtSettings);
        services.AddSingleton(redisSettings);
        services.AddSingleton(policy);
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddSingleton<IConnectionMultiplexer>(provider =>
        {
            try { return ConnectionMultiplexer.Connect(redisOptions); }
            catch (Exception exception) when (exception is RedisException or TimeoutException)
            {
                provider.GetRequiredService<ILogger<RedisAuthSessionStore>>()
                    .LogWarning("Authentication Redis connection failed ({ExceptionType}).", exception.GetType().Name);
                throw new AuthException(AuthErrorKind.Infrastructure, "Authentication session storage is unavailable.", exception);
            }
        });
        services.AddScoped<IAuthStore, PostgresAuthStore>();
        services.AddScoped<IAuthPasswordVerifier, IdentityAuthPasswordVerifier>();
        services.AddSingleton<IAuthTokenIssuer, JwtAuthTokenIssuer>();
        services.AddSingleton<IRefreshTokenCodec, OpaqueRefreshTokenCodec>();
        services.AddSingleton<IAuthSessionStore, RedisAuthSessionStore>();
        services.AddScoped<IAuthService, AuthService>();
        return services;
    }

    private static double ReadPositiveNumber(IConfiguration configuration, string key, double fallback)
    {
        var configured = configuration[key];
        if (configured is null) { return fallback; }
        if (!double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value <= 0)
        {
            throw new InvalidOperationException($"{key} must be a positive number.");
        }
        return value;
    }
}
