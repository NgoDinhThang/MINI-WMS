using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniWms.Api.Authentication;
using MiniWms.Application.Authentication;
using MiniWms.Application.Business;
using MiniWms.Domain.Entities;
using MiniWms.Infrastructure.Authentication;
using Moq;
using NUnit.Framework;

namespace MiniWms.Api.IntegrationTests;

[TestFixture]
[Category("Unit")]
[NonParallelizable]
public sealed class ApiHostMoqTests
{
    [TestCase("/auth/login", "null")]
    [TestCase("/auth/login", "{}")]
    [TestCase("/auth/refresh", "null")]
    [TestCase("/auth/refresh", "{}")]
    [TestCase("/auth/logout", "null")]
    [TestCase("/auth/logout", "{}")]
    public async Task Nullable_authentication_bodies_are_forwarded_as_empty_credentials(string route, string json)
    {
        await using var factory = new UnitApiFactory();
        using var client = factory.CreateClient();
        var invalid = new AuthException(AuthErrorKind.Validation, "Input required");
        if (route == "/auth/login") { factory.Auth.Setup(a => a.LoginAsync("", "", It.IsAny<CancellationToken>())).ThrowsAsync(invalid); }
        if (route == "/auth/refresh") { factory.Auth.Setup(a => a.RefreshAsync("", It.IsAny<CancellationToken>())).ThrowsAsync(invalid); }
        if (route == "/auth/logout") { factory.Auth.Setup(a => a.LogoutAsync("", It.IsAny<CancellationToken>())).ThrowsAsync(invalid); }
        using var response = await client.PostAsync(route, new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        if (route == "/auth/login") { factory.Auth.Verify(a => a.LoginAsync("", "", It.IsAny<CancellationToken>()), Times.Once); }
        if (route == "/auth/refresh") { factory.Auth.Verify(a => a.RefreshAsync("", It.IsAny<CancellationToken>()), Times.Once); }
        if (route == "/auth/logout") { factory.Auth.Verify(a => a.LogoutAsync("", It.IsAny<CancellationToken>()), Times.Once); }
    }

    [Test]
    public async Task Missing_current_user_context_is_a_consistent_authentication_error()
    {
        await using var factory = new UnitApiFactory(eraseUserContext: true);
        using var client = factory.CreateClient();
        using var request = factory.Authorized(HttpMethod.Get, "/auth/me");
        using var response = await client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
    }

    [Test]
    public async Task Swagger_is_unavailable_in_Production()
    {
        await using var factory = new UnitApiFactory(environment: "Production");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public void Startup_requires_database_configuration()
    {
        using var factory = new UnitApiFactory(missingDatabase: true);
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.That(error?.Message, Does.Contain("WmsDatabase"));
    }

    [Test]
    public async Task Status_code_pages_include_the_custom_trace_identifier()
    {
        await using var factory = new UnitApiFactory();
        using var client = factory.CreateClient();
        using var request = factory.Authorized(HttpMethod.Get, "/does-not-exist");
        using var response = await client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("traceId"));
    }

    [Test]
    public async Task Unhealthy_liveness_check_returns_503()
    {
        await using var factory = new UnitApiFactory(unhealthy: true);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("Unhealthy"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Login_rate_limiting_covers_missing_and_present_remote_addresses(bool remoteAddress)
    {
        await using var factory = new UnitApiFactory(rateLimit: 1, remoteAddress: remoteAddress);
        using var client = factory.CreateClient();
        using var first = await client.PostAsJsonAsync("/auth/login", new { email = "admin@example.test", password = "test-password" });
        using var second = await client.PostAsJsonAsync("/auth/login", new { email = "admin@example.test", password = "test-password" });
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
    }

    [Test]
    public void Mock_seeding_is_rejected_in_Production_before_database_access()
    {
        var entry = typeof(Program).GetMethod("<Main>$", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new InvalidOperationException();
        var error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var task = entry.Invoke(null, [new[] { "--seed-mock-data", "--environment", "Production" }]) as Task
                ?? throw new InvalidOperationException("Missing entry task");
            await task;
        });
        Assert.That(error?.Message, Does.Contain("Development"));
    }

    private sealed class UnitApiFactory(
        string environment = "Development", bool missingDatabase = false, bool unhealthy = false,
        bool eraseUserContext = false, int rateLimit = 10000, bool remoteAddress = false) : WebApplicationFactory<Program>
    {
        public Mock<IAuthService> Auth { get; } = new(MockBehavior.Strict);
        public Mock<IWarehouseService> Business { get; } = new(MockBehavior.Strict);
        private readonly string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        private readonly Guid userId = Guid.NewGuid();
        private readonly Guid sid = Guid.NewGuid();

        public HttpRequestMessage Authorized(HttpMethod method, string path)
        {
            var now = DateTimeOffset.UtcNow;
            var issuer = new JwtAuthTokenIssuer(new JwtSettings { SigningKeyBase64 = key });
            var token = issuer.Create(new(userId, "admin@example.test", "Admin", "Admin"),
                new AuthSession { SessionId = sid, UserId = userId, AuthVersion = 1, ExpiresAt = now.AddHours(1) }, now, TimeSpan.FromMinutes(15));
            var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            return request;
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:WmsDatabase"] = missingDatabase ? "" : "Host=127.0.0.1;Port=1;Database=unused;Username=unit",
                ["ConnectionStrings:Redis"] = "127.0.0.1:1,connectTimeout=100",
                ["Jwt:SigningKeyBase64"] = key,
                ["Jwt:Issuer"] = "MiniWms.Api",
                ["Jwt:Audience"] = "MiniWms.Client",
                ["Authentication:LoginPermitLimit"] = rateLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureLogging(log => log.ClearProviders());
            builder.ConfigureServices(services =>
            {
                var user = new AuthUserDetails(userId, "admin@example.test", "Admin", "Admin");
                var tokens = new AuthTokens("unit-access", DateTimeOffset.UtcNow.AddMinutes(15), "unit-refresh", DateTimeOffset.UtcNow.AddHours(1), user);
                Auth.Setup(a => a.LoginAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(tokens);
                Auth.Setup(a => a.ValidateSessionAsync(It.IsAny<AuthSessionIdentity>(), It.IsAny<CancellationToken>())).ReturnsAsync(user);
                services.AddSingleton(Auth.Object);
                services.AddSingleton(Business.Object);
                if (unhealthy) { services.AddHealthChecks().AddCheck("forced-unhealthy", () => HealthCheckResult.Unhealthy("test")); }
                if (eraseUserContext) { services.AddSingleton<IAuthorizationHandler, EraseCurrentUser>(); }
                if (remoteAddress) { services.AddSingleton<IStartupFilter, SetRemoteAddress>(); }
            });
        }
    }

    private sealed class EraseCurrentUser : IAuthorizationHandler
    {
        public Task HandleAsync(AuthorizationHandlerContext context)
        {
            if (context.Resource is HttpContext http) { http.Items.Remove(ApiAuthenticationRegistration.CurrentUserItem); }
            return Task.CompletedTask;
        }
    }

    private sealed class SetRemoteAddress : IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) =>
            app =>
            {
                Microsoft.AspNetCore.Builder.UseExtensions.Use(app, async (context, continuation) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Loopback;
                    await continuation(context);
                });
                next(app);
            };
    }
}
