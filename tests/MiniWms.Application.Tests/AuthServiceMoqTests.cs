using MiniWms.Application.Authentication;
using MiniWms.Domain.Entities;
using Moq;
using NUnit.Framework;

namespace MiniWms.Application.Tests;

[TestFixture]
[Category("Unit")]
public sealed class AuthServiceMoqTests
{
    private Fixture f = new();

    [SetUp]
    public void Setup() => f = new Fixture();

    [TestCase(null, "valid-password")]
    [TestCase("", "valid-password")]
    [TestCase("bad-email", "valid-password")]
    [TestCase("Display <staff@example.test>", "valid-password")]
    [TestCase("staff@example.test", null)]
    [TestCase("staff@example.test", "")]
    [TestCase("staff@example.test", "   ")]
    public void Invalid_login_input_never_reaches_persistence(string? email, string? password)
    {
        Error(() => f.Service.LoginAsync(email!, password!), AuthErrorKind.Validation);
        f.Store.VerifyNoOtherCalls();
        f.Passwords.VerifyNoOtherCalls();
    }

    [Test]
    public void Excessive_email_and_password_lengths_are_rejected()
    {
        Error(() => f.Service.LoginAsync(new string('a', 255), new string('p', 1025)), AuthErrorKind.Validation);
        f.Store.VerifyNoOtherCalls();
    }

    [TestCase("missing")]
    [TestCase("inactive")]
    [TestCase("role")]
    [TestCase("null-role")]
    [TestCase("password")]
    public void Login_denies_unavailable_accounts_and_always_performs_password_work(string reason)
    {
        if (reason == "missing") { f.User = null; }
        if (reason == "inactive") { f.RequiredUser.IsActive = false; }
        if (reason == "role") { f.RequiredUser.Role = new Role { Name = "Reader" }; }
        if (reason == "null-role") { f.RequiredUser.Role = null; }
        if (reason == "password") { f.Passwords.Setup(p => p.Verify(It.IsAny<User?>(), It.IsAny<string>())).Returns(false); }
        Error(() => f.Login(), AuthErrorKind.InvalidCredentials);
        f.Passwords.Verify(p => p.Verify(f.User, "password"), Times.Once);
        f.Transaction.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        f.Transaction.Verify(t => t.DisposeAsync(), Times.Once);
        f.Sessions.Verify(s => s.ReplaceAsync(It.IsAny<Guid>(), It.IsAny<ActiveAuthSession>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Login_propagates_cancellation_token_and_commits_only_after_Redis_accepts_the_session()
    {
        using var source = new CancellationTokenSource();
        var redisAccepted = false;
        f.Sessions.Setup(s => s.ReplaceAsync(It.IsAny<Guid>(), It.IsAny<ActiveAuthSession>(), source.Token))
            .Callback(() => redisAccepted = true).Returns(Task.CompletedTask);
        f.Transaction.Setup(t => t.CommitAsync(source.Token))
            .Callback(() => Assert.That(redisAccepted, Is.True, "Database commit must follow the Redis acceptance."))
            .Returns(Task.CompletedTask);
        var result = await f.Service.LoginAsync(" STAFF@EXAMPLE.TEST ", "password", source.Token);
        Assert.Multiple(() =>
        {
            Assert.That(result.User.UserId, Is.EqualTo(f.RequiredUser.UserId));
            Assert.That(result.AccessToken, Is.EqualTo("access"));
            Assert.That(result.RefreshToken, Is.EqualTo("new-refresh"));
            Assert.That(result.TokenType, Is.EqualTo("Bearer"));
            Assert.That(f.CreatedSession?.AuthVersion, Is.EqualTo(5));
            Assert.That(f.CreatedToken?.SessionId, Is.EqualTo(f.CreatedSession?.SessionId));
            Assert.That(f.CreatedToken?.SecretHash, Is.EqualTo("new-hash"));
        });
        f.Store.Verify(s => s.BeginForEmailAsync("staff@example.test", source.Token), Times.Once);
        f.Sessions.Verify(s => s.ReplaceAsync(f.RequiredUser.UserId, It.Is<ActiveAuthSession>(s => s.SessionId == f.CreatedSession!.SessionId), source.Token), Times.Once);
        f.Transaction.Verify(t => t.CommitAsync(source.Token), Times.Once);
    }

    [TestCase("expired")]
    [TestCase("beyond-session")]
    [TestCase("beyond-access")]
    public void Misconfigured_issuer_cannot_return_credentials_outside_the_allowed_lifetime(string defect)
    {
        f.Issuer.Setup(i => i.Create(It.IsAny<AuthUserDetails>(), It.IsAny<AuthSession>(), It.IsAny<DateTimeOffset>(), It.IsAny<TimeSpan>()))
            .Returns((AuthUserDetails _, AuthSession s, DateTimeOffset now, TimeSpan lifetime) =>
                new IssuedAccessToken("invalid", defect switch
                {
                    "expired" => now,
                    "beyond-session" => s.ExpiresAt.AddSeconds(1),
                    _ => now.Add(lifetime).AddSeconds(1)
                }));
        Error(() => f.Login(), AuthErrorKind.Infrastructure);
        f.Sessions.Verify(s => s.ReplaceAsync(It.IsAny<Guid>(), It.IsAny<ActiveAuthSession>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public void An_inconsistent_role_from_a_collaborator_is_not_issued_as_a_token()
    {
        f.Codec.Setup(c => c.Create(It.IsAny<Guid>())).Callback(() => f.RequiredUser.Role = null).Returns(f.Material);
        Error(() => f.Login(), AuthErrorKind.Infrastructure);
        f.Issuer.VerifyNoOtherCalls();
    }

    [Test]
    public void Login_compensation_swallows_only_an_infrastructure_cleanup_failure()
    {
        var original = new AuthException(AuthErrorKind.Infrastructure, "commit failed");
        f.Transaction.Setup(t => t.CommitAsync(It.IsAny<CancellationToken>())).ThrowsAsync(original);
        f.Sessions.Setup(s => s.RemoveIfMatchesAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<long>(), CancellationToken.None))
            .ThrowsAsync(new AuthException(AuthErrorKind.Infrastructure, "cleanup failed"));
        Assert.That(Assert.ThrowsAsync<AuthException>(() => f.Login()), Is.SameAs(original));
    }

    [Test]
    public void Unexpected_cleanup_failures_are_propagated()
    {
        f.Sessions.Setup(s => s.ReplaceAsync(It.IsAny<Guid>(), It.IsAny<ActiveAuthSession>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("write failed"));
        var cleanup = new InvalidOperationException("cleanup failed");
        f.Sessions.Setup(s => s.RemoveIfMatchesAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<long>(), CancellationToken.None)).ThrowsAsync(cleanup);
        Assert.That(Assert.ThrowsAsync<InvalidOperationException>(() => f.Login()), Is.SameAs(cleanup));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    public void Empty_refresh_inputs_are_validation_errors(string? value) =>
        Error(() => f.Service.RefreshAsync(value!), AuthErrorKind.Validation);

    [Test]
    public void Overlong_refresh_input_is_rejected_before_parsing()
    {
        Error(() => f.Service.RefreshAsync(new string('t', 513)), AuthErrorKind.Validation);
        f.Codec.Verify(c => c.Parse(It.IsAny<string>()), Times.Never);
    }

    [TestCase("null")]
    [TestCase("user")]
    [TestCase("token")]
    public void Invalid_parsed_refresh_identity_is_rejected(string defect)
    {
        f.Codec.Setup(c => c.Parse("refresh")).Returns(defect switch
        {
            "null" => null,
            "user" => f.Presented with { UserId = Guid.Empty },
            _ => f.Presented with { TokenId = Guid.Empty }
        });
        Error(() => f.Service.RefreshAsync("refresh"), AuthErrorKind.InvalidSession);
        f.Store.Verify(s => s.BeginForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase("user")]
    [TestCase("inactive")]
    [TestCase("role")]
    [TestCase("null-role")]
    [TestCase("token")]
    [TestCase("secret")]
    [TestCase("session")]
    [TestCase("owner")]
    [TestCase("version")]
    [TestCase("revoked")]
    [TestCase("session-expired")]
    [TestCase("token-expired")]
    public void Refresh_rejects_each_invalid_persisted_state_without_rotating(string defect)
    {
        switch (defect)
        {
            case "user": f.User = null; break;
            case "inactive": f.RequiredUser.IsActive = false; break;
            case "role": f.RequiredUser.Role = new Role { Name = "Reader" }; break;
            case "null-role": f.RequiredUser.Role = null; break;
            case "token": f.Token = null; break;
            case "secret": f.Codec.Setup(c => c.Matches(It.IsAny<string>(), It.IsAny<string>())).Returns(false); break;
            case "session": f.Session = null; break;
            case "owner": f.RequiredSession.UserId = Guid.NewGuid(); break;
            case "version": f.RequiredSession.AuthVersion++; break;
            case "revoked": f.RequiredSession.RevokedAt = f.Now; break;
            case "session-expired": f.RequiredSession.ExpiresAt = f.Now; break;
            case "token-expired": f.RequiredToken.ExpiresAt = f.Now; break;
        }
        Error(() => f.Service.RefreshAsync("refresh"), AuthErrorKind.InvalidSession);
        f.Transaction.Verify(t => t.AddRefreshToken(It.IsAny<RefreshToken>()), Times.Never);
        f.Transaction.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Refresh_rotates_hashes_and_preserves_session_identity_and_deadline()
    {
        var oldSession = f.RequiredSession;
        var oldToken = f.RequiredToken;
        var result = await f.Service.RefreshAsync("refresh");
        Assert.Multiple(() =>
        {
            Assert.That(result.SessionExpiresAt, Is.EqualTo(oldSession.ExpiresAt));
            Assert.That(oldToken.ConsumedAt, Is.EqualTo(f.Now));
            Assert.That(f.CreatedToken?.RefreshTokenId, Is.EqualTo(f.Material.TokenId));
            Assert.That(f.CreatedToken?.SessionId, Is.EqualTo(oldSession.SessionId));
        });
        f.Sessions.Verify(s => s.ReplaceAsync(It.IsAny<Guid>(), It.IsAny<ActiveAuthSession>(), It.IsAny<CancellationToken>()), Times.Never);
        f.Transaction.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void Replay_commits_revocation_and_does_not_issue_new_credentials()
    {
        f.RequiredToken.ConsumedAt = f.Now.AddMinutes(-1);
        Error(() => f.Service.RefreshAsync("refresh"), AuthErrorKind.InvalidSession);
        Assert.That(f.RequiredSession.RevokedAt, Is.EqualTo(f.Now));
        Assert.That(f.RequiredUser.AuthVersion, Is.EqualTo(5));
        f.Transaction.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        f.Codec.Verify(c => c.Create(It.IsAny<Guid>()), Times.Never);
        f.Sessions.Verify(s => s.RemoveIfMatchesAsync(f.RequiredUser.UserId, f.RequiredSession.SessionId, 4, CancellationToken.None), Times.Once);
    }

    [TestCase("missing")]
    [TestCase("sid")]
    [TestCase("version")]
    [TestCase("expiry")]
    public void Redis_session_must_match_in_every_dimension(string defect)
    {
        f.Active = defect switch
        {
            "missing" => null,
            "sid" => f.Active! with { SessionId = Guid.NewGuid() },
            "version" => f.Active! with { AuthVersion = 99 },
            _ => f.Active! with { ExpiresAt = f.Now }
        };
        Error(() => f.Service.RefreshAsync("refresh"), AuthErrorKind.InvalidSession);
        f.Transaction.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase("user")]
    [TestCase("token")]
    [TestCase("secret")]
    [TestCase("session")]
    [TestCase("owner")]
    public void Logout_requires_proven_ownership(string defect)
    {
        switch (defect)
        {
            case "user": f.User = null; break;
            case "token": f.Token = null; break;
            case "secret": f.Codec.Setup(c => c.Matches(It.IsAny<string>(), It.IsAny<string>())).Returns(false); break;
            case "session": f.Session = null; break;
            case "owner": f.RequiredSession.UserId = Guid.NewGuid(); break;
        }
        Error(() => f.Service.LogoutAsync("refresh"), AuthErrorKind.InvalidSession);
        f.Transaction.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase("revoked")]
    [TestCase("version")]
    public async Task Logout_of_an_old_session_is_idempotent(string defect)
    {
        if (defect == "revoked") { f.RequiredSession.RevokedAt = f.Now; }
        else { f.RequiredSession.AuthVersion++; }
        await f.Service.LogoutAsync("refresh");
        f.Transaction.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        f.Sessions.VerifyNoOtherCalls();
    }

    [TestCase("user")]
    [TestCase("sid")]
    [TestCase("version")]
    public void Malformed_access_identity_is_rejected_before_storage(string defect)
    {
        var identity = defect switch
        {
            "user" => f.Identity with { UserId = Guid.Empty },
            "sid" => f.Identity with { SessionId = Guid.Empty },
            _ => f.Identity with { AuthVersion = 0 }
        };
        Error(() => f.Service.ValidateSessionAsync(identity), AuthErrorKind.InvalidSession);
        f.Store.VerifyNoOtherCalls();
    }

    [TestCase("missing")]
    [TestCase("inactive")]
    [TestCase("version")]
    [TestCase("role")]
    [TestCase("null-role")]
    public void Current_account_state_is_required_for_access(string defect)
    {
        switch (defect)
        {
            case "missing": f.User = null; break;
            case "inactive": f.RequiredUser.IsActive = false; break;
            case "version": f.RequiredUser.AuthVersion++; break;
            case "role": f.RequiredUser.Role = new Role { Name = "Reader" }; break;
            case "null-role": f.RequiredUser.Role = null; break;
        }
        Error(() => f.Service.ValidateSessionAsync(f.Identity), AuthErrorKind.InvalidSession);
        f.Sessions.VerifyNoOtherCalls();
    }

    [TestCase("Admin")]
    [TestCase("Staff")]
    public async Task Permitted_roles_are_returned_from_current_account_state(string role)
    {
        f.RequiredUser.Role = new Role { Name = role };
        var result = await f.Service.ValidateSessionAsync(f.Identity);
        Assert.That(result.Role, Is.EqualTo(role));
    }

    [TestCase("empty")]
    [TestCase("missing")]
    [TestCase("admin")]
    [TestCase("null-role")]
    public void Ban_rejects_invalid_targets(string defect)
    {
        f.MakeActorAdmin();
        var targetId = Guid.NewGuid();
        if (defect == "empty") { targetId = Guid.Empty; }
        if (defect == "missing") { f.User = null; }
        if (defect == "admin") { f.RequiredUser.Role = new Role { Name = "Admin" }; }
        if (defect == "null-role") { f.RequiredUser.Role = null; }
        Error(() => f.Service.BanStaffAsync(f.Identity, targetId, true),
            defect == "empty" ? AuthErrorKind.Validation : defect == "missing" ? AuthErrorKind.NotFound : AuthErrorKind.Forbidden);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Ban_and_unban_commit_account_generation_before_session_cleanup(bool banned)
    {
        f.MakeActorAdmin();
        f.Revoked.Add(f.RequiredSession);
        var committed = false;
        f.Transaction.Setup(t => t.CommitAsync(It.IsAny<CancellationToken>()))
            .Callback(() => committed = true).Returns(Task.CompletedTask);
        f.Sessions.Setup(s => s.RemoveIfMatchesAsync(f.RequiredUser.UserId, f.RequiredSession.SessionId, 4, CancellationToken.None))
            .Callback(() => Assert.That(committed, Is.True, "Durable revocation must commit before Redis cleanup."))
            .Returns(Task.CompletedTask);
        await f.Service.BanStaffAsync(f.Identity, f.RequiredUser.UserId, banned);
        Assert.That(f.RequiredUser.IsActive, Is.EqualTo(!banned));
        Assert.That(f.RequiredUser.AuthVersion, Is.EqualTo(5));
        f.Transaction.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCase(0, 8)]
    [TestCase(15, 0)]
    [TestCase(15, 9)]
    [TestCase(481, 8)]
    public void Invalid_token_or_session_durations_are_rejected(int accessMinutes, int sessionHours)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => f.CreateService(new(TimeSpan.FromMinutes(accessMinutes), TimeSpan.FromHours(sessionHours))));
    }

    private static void Error(Func<Task> action, AuthErrorKind kind) =>
        Assert.That(Assert.ThrowsAsync<AuthException>(async () => await action())?.Kind, Is.EqualTo(kind));

    private sealed class Fixture
    {
        public DateTimeOffset Now { get; } = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
        public Mock<IAuthStore> Store { get; } = new(MockBehavior.Strict);
        public Mock<IAuthTransaction> Transaction { get; } = new(MockBehavior.Strict);
        public Mock<IAuthSessionStore> Sessions { get; } = new(MockBehavior.Strict);
        public Mock<IAuthTokenIssuer> Issuer { get; } = new(MockBehavior.Strict);
        public Mock<IRefreshTokenCodec> Codec { get; } = new(MockBehavior.Strict);
        public Mock<IAuthPasswordVerifier> Passwords { get; } = new(MockBehavior.Strict);
        public Mock<TimeProvider> Clock { get; } = new(MockBehavior.Strict);
        public User? User { get; set; }
        public AuthSession? Session { get; set; }
        public RefreshToken? Token { get; set; }
        public ActiveAuthSession? Active { get; set; }
        public User RequiredUser => User ?? throw new InvalidOperationException();
        public AuthSession RequiredSession => Session ?? throw new InvalidOperationException();
        public RefreshToken RequiredToken => Token ?? throw new InvalidOperationException();
        public AuthSessionIdentity Identity { get; }
        public PresentedRefreshToken Presented { get; }
        public RefreshTokenMaterial Material { get; } = new("new-refresh", Guid.NewGuid(), "new-hash");
        public List<AuthSession> Revoked { get; } = [];
        public AuthSession? CreatedSession { get; private set; }
        public RefreshToken? CreatedToken { get; private set; }
        public AuthService Service { get; }

        public Fixture()
        {
            User = new User { UserId = Guid.NewGuid(), FullName = "Staff", Email = "staff@example.test", Role = new Role { Name = "Staff" }, AuthVersion = 4 };
            Session = new AuthSession { UserId = User.UserId, AuthVersion = 4, ExpiresAt = Now.AddHours(8) };
            Token = new RefreshToken { SessionId = Session.SessionId, SecretHash = "old-hash", ExpiresAt = Session.ExpiresAt };
            Active = new(Session.SessionId, 4, Session.ExpiresAt);
            Identity = new(User.UserId, Session.SessionId, 4);
            Presented = new(User.UserId, Token.RefreshTokenId, "old-hash");
            Clock.Setup(c => c.GetUtcNow()).Returns(Now);
            Store.Setup(s => s.BeginForEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Transaction.Object);
            Store.Setup(s => s.BeginForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(Transaction.Object);
            Store.Setup(s => s.FindAccountAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => User);
            Transaction.SetupGet(t => t.User).Returns(() => User);
            Transaction.Setup(t => t.FindSessionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Session);
            Transaction.Setup(t => t.FindRefreshTokenAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Token);
            Transaction.Setup(t => t.RevokeSessionsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(Revoked);
            Transaction.Setup(t => t.AddSession(It.IsAny<AuthSession>())).Callback<AuthSession>(s => CreatedSession = s);
            Transaction.Setup(t => t.AddRefreshToken(It.IsAny<RefreshToken>())).Callback<RefreshToken>(t => CreatedToken = t);
            Transaction.Setup(t => t.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Transaction.Setup(t => t.DisposeAsync()).Returns(ValueTask.CompletedTask);
            Sessions.Setup(s => s.ReplaceAsync(It.IsAny<Guid>(), It.IsAny<ActiveAuthSession>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Sessions.Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Active);
            Sessions.Setup(s => s.RemoveIfMatchesAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Issuer.Setup(i => i.Create(It.IsAny<AuthUserDetails>(), It.IsAny<AuthSession>(), It.IsAny<DateTimeOffset>(), It.IsAny<TimeSpan>()))
                .Returns((AuthUserDetails _, AuthSession s, DateTimeOffset now, TimeSpan lifetime) => new IssuedAccessToken("access", now.Add(lifetime) < s.ExpiresAt ? now.Add(lifetime) : s.ExpiresAt));
            Codec.Setup(c => c.Create(It.IsAny<Guid>())).Returns(Material);
            Codec.Setup(c => c.Parse(It.IsAny<string>())).Returns(() => Presented);
            Codec.Setup(c => c.Matches(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            Passwords.Setup(p => p.Verify(It.IsAny<User?>(), It.IsAny<string>())).Returns(true);
            Service = CreateService(AuthPolicy.Default);
        }

        public Task<AuthTokens> Login() => Service.LoginAsync("staff@example.test", "password");

        public AuthService CreateService(AuthPolicy policy) => new(Store.Object, Sessions.Object, Issuer.Object, Codec.Object, Passwords.Object, policy, Clock.Object);

        public void MakeActorAdmin()
        {
            var admin = new User { UserId = Identity.UserId, FullName = "Admin", Email = "admin@example.test", AuthVersion = 4, Role = new Role { Name = "Admin" } };
            Store.Setup(s => s.FindAccountAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(admin);
        }
    }
}
