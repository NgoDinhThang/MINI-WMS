using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using MiniWms.Application.Authentication;
using MiniWms.Infrastructure.Authentication;
using Npgsql;
using NUnit.Framework;

namespace MiniWms.Api.IntegrationTests;

public sealed partial class AuthApiIntegrationTests
{
    [Test]
    public async Task Missing_Redis_session_denies_access_and_refresh_without_recreating_the_session()
    {
        var tokens = await LoginAsync();
        await Redis.KeyDeleteAsync(SessionKey(staffId));
        using var me = await MeAsync(Client, tokens.AccessToken);
        using var refresh = await RefreshAsync(Client, tokens.RefreshToken);
        Assert.That(me.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(refresh.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await Redis.KeyExistsAsync(SessionKey(staffId)), Is.False);
        await using var context = CreateContext();
        Assert.That((await context.RefreshTokens.SingleAsync()).ConsumedAt, Is.Null);
    }

    [Test]
    public async Task Short_absolute_session_deadline_clips_JWT_and_cannot_be_extended_by_refresh()
    {
        var shortSettings = new Dictionary<string, string?>(configuration) { ["Authentication:SessionLifetimeHours"] = "0.000555555556" };
        await using var shortFactory = new AuthApiFactory(shortSettings);
        using var shortClient = shortFactory.CreateClient();
        var tokens = await LoginAsync(target: shortClient);
        Assert.That(tokens.AccessTokenExpiresAt, Is.EqualTo(tokens.SessionExpiresAt));
        await Task.Delay(TimeSpan.FromSeconds(3));
        using var denied = await MeAsync(shortClient, tokens.AccessToken);
        using var deniedRefresh = await RefreshAsync(shortClient, tokens.RefreshToken);
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(deniedRefresh.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task JWT_signature_issuer_audience_algorithm_and_expiry_are_all_enforced()
    {
        var tokens = await LoginAsync();
        foreach (var defect in new[] { "signature", "issuer", "audience", "algorithm", "expiry" })
        {
            var invalid = ReissueJwt(tokens.AccessToken, defect);
            using var response = await MeAsync(Client, invalid);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), defect);
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
        }
        using var original = await MeAsync(Client, tokens.AccessToken);
        Assert.That(original.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Current_database_role_overrides_a_stale_privileged_role_claim()
    {
        var tokens = await LoginAsync();
        var staleRole = ReissueJwt(tokens.AccessToken, "role");
        using var response = await ChangeBanAsync(Client, staleRole, inactiveId, false);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Redis_unavailability_is_503_and_a_failed_login_rolls_back_database_changes()
    {
        var original = await LoginAsync();
        var unavailable = new Dictionary<string, string?>(configuration)
        {
            ["ConnectionStrings:Redis"] = "127.0.0.1:1,connectTimeout=200,syncTimeout=200,asyncTimeout=200"
        };
        await using var unavailableFactory = new AuthApiFactory(unavailable);
        using var unavailableClient = unavailableFactory.CreateClient();
        using var denied = await MeAsync(unavailableClient, original.AccessToken);
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        using var failedLogin = await unavailableClient.PostAsJsonAsync("/auth/login", new { email = "staff@integration.test", password });
        Assert.That(failedLogin.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        await using var context = CreateContext();
        Assert.That(await context.AuthSessions.CountAsync(), Is.EqualTo(1));
        Assert.That((await context.AuthSessions.SingleAsync()).RevokedAt, Is.Null);
        Assert.That((await context.Users.SingleAsync(user => user.UserId == staffId)).AuthVersion, Is.EqualTo(1));
        using var stillValid = await MeAsync(Client, original.AccessToken);
        Assert.That(stillValid.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task PostgreSQL_unavailability_is_503_even_with_a_valid_JWT_and_Redis_session()
    {
        var tokens = await LoginAsync();
        var connection = new NpgsqlConnectionStringBuilder(databaseConnection) { Port = 1, Timeout = 1, CommandTimeout = 1, Pooling = false };
        var unavailable = new Dictionary<string, string?>(configuration) { ["ConnectionStrings:WmsDatabase"] = connection.ConnectionString };
        await using var unavailableFactory = new AuthApiFactory(unavailable);
        using var unavailableClient = unavailableFactory.CreateClient();
        using var denied = await MeAsync(unavailableClient, tokens.AccessToken);
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    [Test]
    public async Task Failed_Redis_cleanup_during_ban_and_unban_cannot_resurrect_old_tokens()
    {
        var staff = await LoginAsync();
        var admin = await LoginAsync("admin@integration.test");
        await using var failingFactory = new AuthApiFactory(configuration, services =>
            services.AddSingleton<IAuthSessionStore>(provider => new FailingDeleteStore(
                ActivatorUtilities.CreateInstance<RedisAuthSessionStore>(provider))));
        using var failingClient = failingFactory.CreateClient();
        using var ban = await ChangeBanAsync(failingClient, admin.AccessToken, staffId, true);
        Assert.That(ban.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await Redis.KeyExistsAsync(SessionKey(staffId)), Is.True, "Fault injection must leave the old Redis entry behind.");
        using var inactive = await MeAsync(Client, staff.AccessToken);
        Assert.That(inactive.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using var unban = await ChangeBanAsync(failingClient, admin.AccessToken, staffId, false);
        Assert.That(unban.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        using var oldAccess = await MeAsync(Client, staff.AccessToken);
        using var oldRefresh = await RefreshAsync(Client, staff.RefreshToken);
        Assert.That(oldAccess.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(oldRefresh.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        var fresh = await LoginAsync();
        using var accepted = await MeAsync(Client, fresh.AccessToken);
        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Login_or_refresh_racing_with_ban_cannot_restore_access(bool refresh)
    {
        var staff = await LoginAsync();
        var admin = await LoginAsync("admin@integration.test");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> AuthenticateAtGate()
        {
            await gate.Task;
            return refresh
                ? await RefreshAsync(Client, staff.RefreshToken)
                : await Client.PostAsJsonAsync("/auth/login", new { email = "staff@integration.test", password });
        }
        async Task<HttpResponseMessage> BanAtGate()
        {
            await gate.Task;
            return await ChangeBanAsync(Client, admin.AccessToken, staffId, true);
        }
        var authentication = AuthenticateAtGate();
        var banning = BanAtGate();
        gate.SetResult();
        using var authenticationResponse = await authentication;
        using var banResponse = await banning;
        Assert.That(authenticationResponse.StatusCode, Is.AnyOf(HttpStatusCode.OK, HttpStatusCode.Unauthorized));
        Assert.That(banResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        if (authenticationResponse.StatusCode == HttpStatusCode.OK)
        {
            var issued = await authenticationResponse.Content.ReadFromJsonAsync<AuthTokens>() ?? throw new InvalidOperationException();
            using var denied = await MeAsync(Client, issued.AccessToken);
            Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }
        await using var context = CreateContext();
        Assert.That((await context.Users.SingleAsync(user => user.UserId == staffId)).IsActive, Is.False);
        Assert.That(await context.AuthSessions.CountAsync(session => session.UserId == staffId && session.RevokedAt == null), Is.Zero);
    }

    [Test]
    public async Task Delayed_Redis_cleanup_of_an_old_session_cannot_delete_a_newer_login()
    {
        var first = await LoginAsync();
        var original = new JwtSecurityTokenHandler().ReadJwtToken(first.AccessToken);
        var sid = Guid.Parse(original.Claims.Single(claim => claim.Type == "sid").Value);
        var version = long.Parse(original.Claims.Single(claim => claim.Type == "auth_version").Value, System.Globalization.CultureInfo.InvariantCulture);
        var second = await LoginAsync();
        await Factory.Services.GetRequiredService<IAuthSessionStore>().RemoveIfMatchesAsync(staffId, sid, version, CancellationToken.None);
        using var accepted = await MeAsync(Client, second.AccessToken);
        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Public_health_does_not_use_authentication_dependencies_for_an_optional_bearer_header()
    {
        var tokens = await LoginAsync();
        var connection = new NpgsqlConnectionStringBuilder(databaseConnection) { Port = 1, Timeout = 1, Pooling = false };
        var unavailable = new Dictionary<string, string?>(configuration)
        {
            ["ConnectionStrings:WmsDatabase"] = connection.ConnectionString,
            ["ConnectionStrings:Redis"] = "127.0.0.1:1,connectTimeout=200,syncTimeout=200,asyncTimeout=200"
        };
        await using var unavailableFactory = new AuthApiFactory(unavailable);
        using var unavailableClient = unavailableFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var response = await unavailableClient.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("Healthy"));
    }
    private string ReissueJwt(string original, string defect)
    {
        var handler = new JwtSecurityTokenHandler();
        var parsed = handler.ReadJwtToken(original);
        var excluded = new[] { "iss", "aud", "exp", "nbf", "iat" };
        var claims = parsed.Claims.Where(claim => !excluded.Contains(claim.Type)).ToList();
        if (defect == "role") { claims.RemoveAll(claim => claim.Type == "role"); claims.Add(new Claim("role", "Admin")); }
        var key = defect == "signature" ? RandomNumberGenerator.GetBytes(64) : Convert.FromBase64String(signingKey);
        var credentials = new SigningCredentials(new SymmetricSecurityKey(key),
            defect == "algorithm" ? SecurityAlgorithms.HmacSha384 : SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(defect == "issuer" ? "wrong-issuer" : parsed.Issuer,
            defect == "audience" ? "wrong-audience" : parsed.Audiences.Single(), claims,
            defect == "expiry" ? now.AddMinutes(-2) : now.AddSeconds(-1),
            defect == "expiry" ? now.AddMinutes(-1) : now.AddMinutes(5), credentials);
        return handler.WriteToken(token);
    }

    private sealed class FailingDeleteStore(IAuthSessionStore inner) : IAuthSessionStore
    {
        public Task ReplaceAsync(Guid userId, ActiveAuthSession session, CancellationToken cancellationToken) => inner.ReplaceAsync(userId, session, cancellationToken);
        public Task<ActiveAuthSession?> GetAsync(Guid userId, CancellationToken cancellationToken) => inner.GetAsync(userId, cancellationToken);
        public Task RemoveIfMatchesAsync(Guid userId, Guid sessionId, long authVersion, CancellationToken cancellationToken) =>
            Task.FromException(new AuthException(AuthErrorKind.Infrastructure, "Injected Redis cleanup outage."));
    }
}
