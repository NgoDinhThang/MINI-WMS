using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MiniWms.Application.Authentication;
using NUnit.Framework;

namespace MiniWms.Api.IntegrationTests;

public sealed partial class AuthApiIntegrationTests
{
    [Test]
    public async Task Login_persists_only_refresh_hashes_and_enforces_the_absolute_eight_hour_bound()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var tokens = await LoginAsync(" STAFF@INTEGRATION.TEST ");
        using var me = await MeAsync(Client, tokens.AccessToken);
        Assert.That(me.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await using var context = CreateContext();
        var session = await context.AuthSessions.SingleAsync();
        var refresh = await context.RefreshTokens.SingleAsync();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(tokens.AccessToken);
        Assert.Multiple(() =>
        {
            Assert.That(tokens.User.UserId, Is.EqualTo(staffId));
            Assert.That(session.UserId, Is.EqualTo(staffId));
            Assert.That(session.RevokedAt, Is.Null);
            Assert.That(refresh.ConsumedAt, Is.Null);
            Assert.That(refresh.SecretHash.Length, Is.EqualTo(64));
            Assert.That(refresh.SecretHash.All(Uri.IsHexDigit), Is.True);
            Assert.That(refresh.SecretHash.Contains(tokens.RefreshToken, StringComparison.Ordinal), Is.False);
            Assert.That(tokens.SessionExpiresAt, Is.EqualTo(session.ExpiresAt));
            Assert.That(tokens.SessionExpiresAt, Is.LessThanOrEqualTo(startedAt.AddHours(8).AddSeconds(2)));
            Assert.That(tokens.SessionExpiresAt, Is.GreaterThan(startedAt.AddHours(7.99)));
            Assert.That(tokens.AccessTokenExpiresAt, Is.LessThanOrEqualTo(startedAt.AddMinutes(15).AddSeconds(2)));
            Assert.That(tokens.AccessTokenExpiresAt, Is.LessThanOrEqualTo(session.ExpiresAt));
            Assert.That(jwt.ValidTo, Is.EqualTo(tokens.AccessTokenExpiresAt.UtcDateTime));
        });
        var ttl = await Redis.KeyTimeToLiveAsync(SessionKey(staffId));
        Assert.That(ttl, Is.Not.Null);
        Assert.That(ttl.GetValueOrDefault(), Is.LessThanOrEqualTo(TimeSpan.FromHours(8)));
    }

    [Test]
    public async Task Refresh_rotates_durably_and_replaying_an_earlier_token_revokes_the_family()
    {
        var original = await LoginAsync();
        using var first = await RefreshAsync(Client, original.RefreshToken);
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var rotated = await first.Content.ReadFromJsonAsync<AuthTokens>() ?? throw new InvalidOperationException();
        Assert.That(rotated.SessionExpiresAt, Is.EqualTo(original.SessionExpiresAt));
        using var second = await RefreshAsync(Client, rotated.RefreshToken);
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var latest = await second.Content.ReadFromJsonAsync<AuthTokens>() ?? throw new InvalidOperationException();
        await using (var context = CreateContext())
        {
            Assert.That(await context.RefreshTokens.CountAsync(), Is.EqualTo(3));
            Assert.That(await context.RefreshTokens.CountAsync(token => token.ConsumedAt != null), Is.EqualTo(2));
            Assert.That(await context.AuthSessions.CountAsync(), Is.EqualTo(1));
        }
        using var replay = await RefreshAsync(Client, original.RefreshToken);
        Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using var denied = await MeAsync(Client, latest.AccessToken);
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using var latestRefresh = await RefreshAsync(Client, latest.RefreshToken);
        Assert.That(latestRefresh.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        await using var persisted = CreateContext();
        Assert.That((await persisted.AuthSessions.SingleAsync()).RevokedAt, Is.Not.Null);
        Assert.That(await Redis.KeyExistsAsync(SessionKey(staffId)), Is.False);
        var fresh = await LoginAsync();
        using var oldReplay = await RefreshAsync(Client, original.RefreshToken);
        Assert.That(oldReplay.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using var freshMe = await MeAsync(Client, fresh.AccessToken);
        Assert.That(freshMe.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task A_new_login_replaces_the_previous_session_and_old_logout_cannot_remove_it()
    {
        var first = await LoginAsync();
        var second = await LoginAsync();
        using var denied = await MeAsync(Client, first.AccessToken);
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using var oldLogout = await Client.PostAsJsonAsync("/auth/logout", new { refreshToken = first.RefreshToken });
        Assert.That(oldLogout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        using var allowed = await MeAsync(Client, second.AccessToken);
        Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var logout = await Client.PostAsJsonAsync("/auth/logout", new { refreshToken = second.RefreshToken });
        Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        using var afterLogout = await MeAsync(Client, second.AccessToken);
        Assert.That(afterLogout.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        await using var context = CreateContext();
        Assert.That(await context.AuthSessions.CountAsync(session => session.RevokedAt == null), Is.Zero);
    }

    [Test]
    public async Task Invalid_credentials_inactive_accounts_and_bad_input_return_consistent_problems()
    {
        foreach (var email in new[] { "missing@integration.test", "inactive@integration.test", "staff@integration.test" })
        {
            using var response = await Client.PostAsJsonAsync("/auth/login", new { email, password = "incorrect-test-password" });
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
        }
        using var inactive = await Client.PostAsJsonAsync("/auth/login", new { email = "inactive@integration.test", password });
        Assert.That(inactive.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using var invalid = await Client.PostAsJsonAsync("/auth/login", new { email = "invalid", password = "" });
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        using var malformed = await Client.PostAsync("/auth/login", new StringContent("{bad", Encoding.UTF8, "application/json"));
        Assert.That(malformed.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        await using var context = CreateContext();
        Assert.That(await context.AuthSessions.CountAsync(), Is.Zero);
        Assert.That(await context.RefreshTokens.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task A_guessed_refresh_secret_cannot_revoke_a_valid_session()
    {
        var tokens = await LoginAsync();
        var parts = tokens.RefreshToken.Split('.');
        parts[2] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var wrongSecret = await RefreshAsync(Client, string.Join('.', parts));
        Assert.That(wrongSecret.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using var me = await MeAsync(Client, tokens.AccessToken);
        Assert.That(me.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var refresh = await RefreshAsync(Client, tokens.RefreshToken);
        Assert.That(refresh.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Staff_cannot_ban_and_Admin_ban_unban_requires_fresh_login()
    {
        var staff = await LoginAsync();
        var admin = await LoginAsync("admin@integration.test");
        using var forbidden = await ChangeBanAsync(Client, staff.AccessToken, inactiveId, false);
        Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        using var ban = await ChangeBanAsync(Client, admin.AccessToken, staffId, true);
        Assert.That(ban.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        using var denied = await MeAsync(Client, staff.AccessToken);
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using var deniedRefresh = await RefreshAsync(Client, staff.RefreshToken);
        Assert.That(deniedRefresh.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using var unban = await ChangeBanAsync(Client, admin.AccessToken, staffId, false);
        Assert.That(unban.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        using var oldAfterUnban = await MeAsync(Client, staff.AccessToken);
        Assert.That(oldAfterUnban.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        var fresh = await LoginAsync();
        using var accepted = await MeAsync(Client, fresh.AccessToken);
        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var cannotBanAdmin = await ChangeBanAsync(Client, admin.AccessToken, adminId, true);
        Assert.That(cannotBanAdmin.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Concurrent_logins_leave_exactly_one_current_persisted_and_Redis_session()
    {
        await using var otherFactory = new AuthApiFactory(configuration);
        using var otherClient = otherFactory.CreateClient();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(index => LoginAsync(target: index % 2 == 0 ? Client : otherClient)));
        var accepted = 0;
        AuthTokens? winner = null;
        foreach (var tokens in results)
        {
            using var response = await MeAsync(Client, tokens.AccessToken);
            if (response.StatusCode == HttpStatusCode.OK) { accepted++; winner = tokens; }
            else { Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized)); }
        }
        Assert.That(accepted, Is.EqualTo(1));
        await using var context = CreateContext();
        Assert.That(await context.AuthSessions.CountAsync(), Is.EqualTo(6));
        Assert.That(await context.AuthSessions.CountAsync(session => session.RevokedAt == null), Is.EqualTo(1));
        Assert.That(await context.RefreshTokens.CountAsync(), Is.EqualTo(6));
        Assert.That((await context.Users.SingleAsync(user => user.UserId == staffId)).AuthVersion, Is.EqualTo(6));
        Assert.That(winner, Is.Not.Null);
    }

    [Test]
    public async Task Concurrent_refresh_reuse_revokes_the_family_with_no_partial_rotation()
    {
        var original = await LoginAsync();
        var responses = await Task.WhenAll(RefreshAsync(Client, original.RefreshToken), RefreshAsync(Client, original.RefreshToken));
        try
        {
            Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.OK), Is.EqualTo(1));
            Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.Unauthorized), Is.EqualTo(1));
            using var me = await MeAsync(Client, original.AccessToken);
            Assert.That(me.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            await using var context = CreateContext();
            Assert.That(await context.RefreshTokens.CountAsync(), Is.EqualTo(2));
            Assert.That(await context.RefreshTokens.CountAsync(token => token.ConsumedAt != null), Is.EqualTo(1));
            Assert.That((await context.AuthSessions.SingleAsync()).RevokedAt, Is.Not.Null);
        }
        finally { foreach (var response in responses) { response.Dispose(); } }
    }

    [Test]
    public async Task Swagger_documents_actual_protected_and_refresh_token_operations()
    {
        using var response = await Client.GetAsync("/swagger/v1/swagger.json");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        Assert.That(paths.GetProperty("/auth/me").GetProperty("get").GetProperty("security").GetArrayLength(), Is.GreaterThan(0));
        Assert.That(paths.GetProperty("/auth/login").GetProperty("post").TryGetProperty("security", out var loginSecurity) && loginSecurity.GetArrayLength() > 0, Is.False);
        Assert.That(paths.GetProperty("/auth/refresh").GetProperty("post").TryGetProperty("security", out var refreshSecurity) && refreshSecurity.GetArrayLength() > 0, Is.False);
    }
}
