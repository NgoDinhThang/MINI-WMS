using MiniWms.Application.Authentication;
using NUnit.Framework;

namespace MiniWms.Application.Tests;

[TestFixture]
public sealed class AuthServiceTests
{
    private AuthTestHarness harness = new();

    [SetUp]
    public void SetUp() => harness = new AuthTestHarness();

    [Test]
    public async Task Login_normalizes_email_and_creates_one_bounded_session()
    {
        var start = harness.Clock.Now;
        var result = await harness.Service.LoginAsync("  STAFF@MINI-WMS.TEST  ", AuthTestHarness.Password);
        var identity = harness.Identity(result);
        var session = harness.Store.Sessions[identity.SessionId];
        Assert.Multiple(() =>
        {
            Assert.That(harness.Store.LastEmailLookup, Is.EqualTo("staff@mini-wms.test"));
            Assert.That(result.User.Role, Is.EqualTo("Staff"));
            Assert.That(result.TokenType, Is.EqualTo("Bearer"));
            Assert.That(result.AccessTokenExpiresAt, Is.EqualTo(start.AddMinutes(15)));
            Assert.That(result.SessionExpiresAt, Is.EqualTo(start.AddHours(8)));
            Assert.That(session.AuthVersion, Is.EqualTo(1));
            Assert.That(harness.Store.Tokens.Count, Is.EqualTo(1));
            Assert.That(harness.Sessions.Current[harness.Staff.UserId].SessionId, Is.EqualTo(session.SessionId));
        });
    }

    [TestCase("staff@mini-wms.test", "wrong-password")]
    [TestCase("unknown@mini-wms.test", AuthTestHarness.Password)]
    [TestCase("inactive@mini-wms.test", AuthTestHarness.Password)]
    public void Failed_login_uses_one_generic_credentials_error(string email, string password)
    {
        AssertAuthError(() => harness.Service.LoginAsync(email, password), AuthErrorKind.InvalidCredentials);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Store.Sessions, Is.Empty);
            Assert.That(harness.Store.CommitCount, Is.Zero);
            Assert.That(harness.Sessions.ReplaceCount, Is.Zero);
        });
    }

    [Test]
    public void Invalid_login_input_is_reported_before_the_data_layer()
    {
        var error = Assert.ThrowsAsync<AuthException>(() => harness.Service.LoginAsync("not-an-email", ""));
        Assert.Multiple(() =>
        {
            Assert.That(error?.Kind, Is.EqualTo(AuthErrorKind.Validation));
            Assert.That(error?.Errors.Keys, Is.EquivalentTo(new[] { "email", "password" }));
            Assert.That(harness.Store.LastEmailLookup, Is.Null);
        });
    }

    [Test]
    public async Task Replacement_login_revokes_old_access_and_refresh_credentials()
    {
        var first = await LoginStaff();
        var second = await LoginStaff();
        AssertAuthError(() => harness.Service.ValidateSessionAsync(harness.Identity(first)), AuthErrorKind.InvalidSession);
        AssertAuthError(() => harness.Service.RefreshAsync(first.RefreshToken), AuthErrorKind.InvalidSession);
        var current = await harness.Service.ValidateSessionAsync(harness.Identity(second));
        Assert.Multiple(() =>
        {
            Assert.That(current.UserId, Is.EqualTo(harness.Staff.UserId));
            Assert.That(harness.Store.Sessions.Values.Count(session => session.RevokedAt is null), Is.EqualTo(1));
            Assert.That(harness.Store.Users[harness.Staff.UserId].AuthVersion, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Logout_from_old_device_does_not_revoke_newer_login()
    {
        var oldDevice = await LoginStaff();
        var newDevice = await LoginStaff();
        var commits = harness.Store.CommitCount;
        await harness.Service.LogoutAsync(oldDevice.RefreshToken);
        var current = await harness.Service.ValidateSessionAsync(harness.Identity(newDevice));
        Assert.Multiple(() =>
        {
            Assert.That(current.UserId, Is.EqualTo(harness.Staff.UserId));
            Assert.That(harness.Store.CommitCount, Is.EqualTo(commits));
            Assert.That(harness.Sessions.Current[harness.Staff.UserId].SessionId, Is.EqualTo(harness.Identity(newDevice).SessionId));
        });
    }

    [Test]
    public async Task Refresh_rotates_only_the_secret_and_preserves_original_deadline()
    {
        var original = await LoginStaff();
        var originalIdentity = harness.Identity(original);
        var originalToken = harness.RefreshTokens.Parse(original.RefreshToken);
        Assert.That(originalToken, Is.Not.Null);
        harness.Clock.Advance(TimeSpan.FromMinutes(10));
        var refreshed = await harness.Service.RefreshAsync(original.RefreshToken);
        Assert.Multiple(() =>
        {
            Assert.That(refreshed.RefreshToken, Is.Not.EqualTo(original.RefreshToken));
            Assert.That(harness.Identity(refreshed), Is.EqualTo(originalIdentity));
            Assert.That(refreshed.SessionExpiresAt, Is.EqualTo(original.SessionExpiresAt));
            Assert.That(refreshed.AccessTokenExpiresAt, Is.EqualTo(harness.Clock.Now.AddMinutes(15)));
            Assert.That(harness.Store.Tokens.Count, Is.EqualTo(2));
            Assert.That(harness.Store.Tokens[originalToken?.TokenId ?? Guid.Empty].ConsumedAt, Is.EqualTo(harness.Clock.Now));
            Assert.That(harness.Sessions.ReplaceCount, Is.EqualTo(1));
            Assert.That(harness.Sessions.Current[harness.Staff.UserId].ExpiresAt, Is.EqualTo(original.SessionExpiresAt));
        });
    }

    [Test]
    public async Task Refresh_access_expiry_is_clipped_to_the_eight_hour_deadline()
    {
        var original = await LoginStaff();
        harness.Clock.Advance(TimeSpan.FromHours(7) + TimeSpan.FromMinutes(55));
        var refreshed = await harness.Service.RefreshAsync(original.RefreshToken);
        Assert.Multiple(() =>
        {
            Assert.That(refreshed.AccessTokenExpiresAt, Is.EqualTo(original.SessionExpiresAt));
            Assert.That(refreshed.AccessTokenExpiresAt - harness.Clock.Now, Is.EqualTo(TimeSpan.FromMinutes(5)));
            Assert.That(refreshed.SessionExpiresAt, Is.EqualTo(original.SessionExpiresAt));
        });
    }

    [Test]
    public async Task Exact_absolute_deadline_rejects_access_and_refresh()
    {
        var original = await LoginStaff();
        harness.Clock.Advance(TimeSpan.FromHours(8));
        AssertAuthError(() => harness.Service.RefreshAsync(original.RefreshToken), AuthErrorKind.InvalidSession);
        AssertAuthError(() => harness.Service.ValidateSessionAsync(harness.Identity(original)), AuthErrorKind.InvalidSession);
        Assert.That(harness.Store.Tokens.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task Replay_of_an_earlier_consumed_token_revokes_the_entire_current_family()
    {
        var first = await LoginStaff();
        var second = await harness.Service.RefreshAsync(first.RefreshToken);
        var third = await harness.Service.RefreshAsync(second.RefreshToken);
        AssertAuthError(() => harness.Service.RefreshAsync(first.RefreshToken), AuthErrorKind.InvalidSession);
        AssertAuthError(() => harness.Service.RefreshAsync(third.RefreshToken), AuthErrorKind.InvalidSession);
        AssertAuthError(() => harness.Service.ValidateSessionAsync(harness.Identity(third)), AuthErrorKind.InvalidSession);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Store.Users[harness.Staff.UserId].AuthVersion, Is.EqualTo(2));
            Assert.That(harness.Store.Sessions[harness.Identity(first).SessionId].RevokedAt, Is.Not.Null);
            Assert.That(harness.Store.Tokens.Count, Is.EqualTo(3));
            Assert.That(harness.Sessions.Current.ContainsKey(harness.Staff.UserId), Is.False);
        });
    }

    [Test]
    public async Task Wrong_secret_for_a_real_token_does_not_log_out_the_user()
    {
        var original = await LoginStaff();
        var tampered = harness.RefreshTokens.WithWrongSecret(original.RefreshToken);
        AssertAuthError(() => harness.Service.RefreshAsync(tampered), AuthErrorKind.InvalidSession);
        var refreshed = await harness.Service.RefreshAsync(original.RefreshToken);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Identity(refreshed).AuthVersion, Is.EqualTo(1));
            Assert.That(harness.Store.Sessions[harness.Identity(original).SessionId].RevokedAt, Is.Null);
        });
    }

    [Test]
    public async Task Logout_accepts_refresh_credentials_after_access_token_has_expired()
    {
        var original = await LoginStaff();
        harness.Clock.Advance(TimeSpan.FromMinutes(16));
        await harness.Service.LogoutAsync(original.RefreshToken);
        AssertAuthError(() => harness.Service.ValidateSessionAsync(harness.Identity(original)), AuthErrorKind.InvalidSession);
        AssertAuthError(() => harness.Service.RefreshAsync(original.RefreshToken), AuthErrorKind.InvalidSession);
        Assert.That(harness.Store.Sessions[harness.Identity(original).SessionId].RevokedAt, Is.EqualTo(harness.Clock.Now));
    }

    [Test]
    public async Task Missing_redis_session_is_not_recreated_by_refresh()
    {
        var original = await LoginStaff();
        harness.Sessions.Current.Clear();
        AssertAuthError(() => harness.Service.RefreshAsync(original.RefreshToken), AuthErrorKind.InvalidSession);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Sessions.ReplaceCount, Is.EqualTo(1));
            Assert.That(harness.Store.Tokens.Count, Is.EqualTo(1));
            Assert.That(harness.Store.Tokens.Values.Single().ConsumedAt, Is.Null);
        });
    }

    [Test]
    public async Task Redis_outage_is_an_infrastructure_error_not_invalid_credentials()
    {
        var original = await LoginStaff();
        harness.Sessions.FailRead = true;
        AssertAuthError(() => harness.Service.ValidateSessionAsync(harness.Identity(original)), AuthErrorKind.Infrastructure);
        AssertAuthError(() => harness.Service.RefreshAsync(original.RefreshToken), AuthErrorKind.Infrastructure);
        Assert.That(harness.Store.Tokens.Values.Single().ConsumedAt, Is.Null);
    }

    [Test]
    public void Failed_redis_replacement_does_not_commit_a_login()
    {
        harness.Sessions.FailReplace = true;
        AssertAuthError(() => LoginStaff(), AuthErrorKind.Infrastructure);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Store.Users[harness.Staff.UserId].AuthVersion, Is.Zero);
            Assert.That(harness.Store.Sessions, Is.Empty);
            Assert.That(harness.Store.Tokens, Is.Empty);
            Assert.That(harness.Store.CommitCount, Is.Zero);
        });
    }

    [Test]
    public void Failed_database_commit_cleans_only_the_attempted_redis_session()
    {
        harness.Store.FailCommit = true;
        AssertAuthError(() => LoginStaff(), AuthErrorKind.Infrastructure);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Store.Users[harness.Staff.UserId].AuthVersion, Is.Zero);
            Assert.That(harness.Store.Sessions, Is.Empty);
            Assert.That(harness.Sessions.Current, Is.Empty);
        });
    }

    [Test]
    public async Task Staff_cannot_ban_accounts()
    {
        var staff = await LoginStaff();
        AssertAuthError(
            () => harness.Service.BanStaffAsync(harness.Identity(staff), harness.InactiveStaff.UserId, true),
            AuthErrorKind.Forbidden);
        Assert.That(harness.Store.Users[harness.InactiveStaff.UserId].AuthVersion, Is.Zero);
    }

    [Test]
    public async Task Admin_cannot_ban_the_Admin_account()
    {
        var admin = await harness.Service.LoginAsync(harness.Admin.Email, AuthTestHarness.Password);
        AssertAuthError(
            () => harness.Service.BanStaffAsync(harness.Identity(admin), harness.Admin.UserId, true),
            AuthErrorKind.Forbidden);
        await harness.Service.ValidateSessionAsync(harness.Identity(admin));
    }

    [Test]
    public async Task Ban_commits_even_if_redis_cleanup_fails_and_unban_requires_fresh_login()
    {
        var staff = await LoginStaff();
        var admin = await harness.Service.LoginAsync(harness.Admin.Email, AuthTestHarness.Password);
        harness.Sessions.FailRemove = true;
        await harness.Service.BanStaffAsync(harness.Identity(admin), harness.Staff.UserId, true);
        Assert.That(harness.Store.Users[harness.Staff.UserId].IsActive, Is.False);
        Assert.That(harness.Sessions.Current.ContainsKey(harness.Staff.UserId), Is.True);
        AssertAuthError(() => harness.Service.ValidateSessionAsync(harness.Identity(staff)), AuthErrorKind.InvalidSession);
        AssertAuthError(() => LoginStaff(), AuthErrorKind.InvalidCredentials);
        await harness.Service.BanStaffAsync(harness.Identity(admin), harness.Staff.UserId, false);
        Assert.That(harness.Store.Users[harness.Staff.UserId].IsActive, Is.True);
        AssertAuthError(() => harness.Service.ValidateSessionAsync(harness.Identity(staff)), AuthErrorKind.InvalidSession);
        AssertAuthError(() => harness.Service.RefreshAsync(staff.RefreshToken), AuthErrorKind.InvalidSession);
        harness.Sessions.FailRemove = false;
        var fresh = await LoginStaff();
        await harness.Service.ValidateSessionAsync(harness.Identity(fresh));
        Assert.That(harness.Identity(fresh).AuthVersion, Is.GreaterThan(harness.Identity(staff).AuthVersion));
    }

    [Test]
    public async Task Inactive_account_is_denied_before_an_unavailable_redis_check()
    {
        var staff = await LoginStaff();
        harness.Store.Users[harness.Staff.UserId].IsActive = false;
        var reads = harness.Sessions.ReadCount;
        harness.Sessions.FailRead = true;
        AssertAuthError(() => harness.Service.ValidateSessionAsync(harness.Identity(staff)), AuthErrorKind.InvalidSession);
        Assert.That(harness.Sessions.ReadCount, Is.EqualTo(reads));
    }

    [Test]
    public void Session_policy_cannot_exceed_eight_hours()
    {
        Assert.That(
            () => harness.CreateService(new AuthPolicy(TimeSpan.FromMinutes(15), TimeSpan.FromHours(9))),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [TestCase("")]
    [TestCase(" ")]
    public void Empty_refresh_credentials_are_input_validation_failures(string token) =>
        AssertAuthError(() => harness.Service.RefreshAsync(token), AuthErrorKind.Validation);

    [Test]
    public void Malformed_refresh_credentials_are_authentication_failures() =>
        AssertAuthError(() => harness.Service.RefreshAsync("not-an-issued-token"), AuthErrorKind.InvalidSession);

    private Task<AuthTokens> LoginStaff() => harness.Service.LoginAsync(harness.Staff.Email, AuthTestHarness.Password);

    private static void AssertAuthError(Func<Task> action, AuthErrorKind kind)
    {
        var exception = Assert.ThrowsAsync<AuthException>(async () => await action());
        Assert.That(exception?.Kind, Is.EqualTo(kind));
    }
}
