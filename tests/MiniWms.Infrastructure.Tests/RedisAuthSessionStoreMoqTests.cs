using System.Globalization;
using Microsoft.Extensions.Logging;
using MiniWms.Application.Authentication;
using MiniWms.Infrastructure.Authentication;
using Moq;
using NUnit.Framework;
using StackExchange.Redis;

namespace MiniWms.Infrastructure.Tests;

[TestFixture]
[Category("Unit")]
public sealed class RedisAuthSessionStoreMoqTests
{
    private readonly DateTimeOffset now = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
    private Mock<IConnectionMultiplexer> connection = new(MockBehavior.Strict);
    private Mock<IDatabase> database = new(MockBehavior.Strict);
    private Mock<ILogger<RedisAuthSessionStore>> logger = new();
    private RedisAuthSessionStore store = null!;
    private readonly Guid userId = Guid.NewGuid();
    private readonly Guid sid = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        connection = new(MockBehavior.Strict);
        database = new(MockBehavior.Strict);
        logger = new();
        connection.Setup(c => c.GetDatabase(-1, null)).Returns(database.Object);
        var clock = new Mock<TimeProvider>(MockBehavior.Strict);
        clock.Setup(c => c.GetUtcNow()).Returns(now);
        store = new(connection.Object, new RedisAuthSettings { KeyPrefix = "test:" }, clock.Object, logger.Object);
    }

    [Test]
    public async Task Replacement_writes_the_exact_key_generation_and_absolute_deadline_atomically()
    {
        RedisKey[]? keys = null;
        RedisValue[]? values = null;
        string? script = null;
        database.Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), CommandFlags.None))
            .Callback<string, RedisKey[], RedisValue[], CommandFlags>((s, k, v, _) => { script = s; keys = k; values = v; })
            .ReturnsAsync(RedisResult.Create((RedisValue)1));
        var deadline = now.AddHours(8);
        await store.ReplaceAsync(userId, new(sid, 12, deadline), default);
        Assert.Multiple(() =>
        {
            Assert.That(keys?.Single().ToString(), Is.EqualTo("test:session:{" + userId.ToString("N") + "}"));
            Assert.That(values?[0].ToString(), Is.EqualTo($"{sid:N}:00000000000000000012:{deadline.ToUnixTimeMilliseconds()}"));
            Assert.That(values?[1].ToString(), Is.EqualTo("00000000000000000012"));
            Assert.That(values?[2].ToString(), Is.EqualTo(deadline.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
            Assert.That(script, Does.Contain("'PXAT'"));
        });
    }

    [TestCase("sid")]
    [TestCase("generation")]
    [TestCase("expired")]
    public void Invalid_replacements_never_write_to_Redis(string defect)
    {
        var session = new ActiveAuthSession(defect == "sid" ? Guid.Empty : sid, defect == "generation" ? -1 : 1,
            defect == "expired" ? now : now.AddHours(1));
        Assert.That(Assert.ThrowsAsync<AuthException>(() => store.ReplaceAsync(userId, session, default))?.Kind, Is.EqualTo(AuthErrorKind.InvalidSession));
        connection.VerifyNoOtherCalls();
        database.VerifyNoOtherCalls();
    }

    [Test]
    public void A_newer_generation_fence_prevents_replacing_the_current_session()
    {
        ScriptResult(0);
        Assert.That(Assert.ThrowsAsync<AuthException>(() => store.ReplaceAsync(userId, new(sid, 1, now.AddHours(1)), default))?.Kind,
            Is.EqualTo(AuthErrorKind.InvalidSession));
    }

    [Test]
    public async Task Missing_key_and_expired_deadline_both_return_no_session()
    {
        database.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), CommandFlags.None)).ReturnsAsync(RedisValue.Null);
        Assert.That(await store.GetAsync(userId, default), Is.Null);
        database.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), CommandFlags.None)).ReturnsAsync(Wire(now));
        Assert.That(await store.GetAsync(userId, default), Is.Null);
    }

    [Test]
    public async Task A_valid_stored_session_is_parsed_without_extending_its_deadline()
    {
        var deadline = now.AddHours(1);
        database.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), CommandFlags.None)).ReturnsAsync(Wire(deadline));
        var session = await store.GetAsync(userId, default);
        Assert.That(session, Is.EqualTo(new ActiveAuthSession(sid, 1, deadline)));
        database.Verify(d => d.StringGetAsync("test:session:{" + userId.ToString("N") + "}", CommandFlags.None), Times.Once);
        database.VerifyNoOtherCalls();
    }

    [TestCase("parts")]
    [TestCase("sid-format")]
    [TestCase("sid-empty")]
    [TestCase("version-length")]
    [TestCase("version-format")]
    [TestCase("deadline-format")]
    [TestCase("deadline-range")]
    public void Corrupt_wire_values_fail_closed_as_infrastructure_errors(string defect)
    {
        var id = defect == "sid-format" ? "invalid" : defect == "sid-empty" ? Guid.Empty.ToString("N") : sid.ToString("N");
        var version = defect == "version-length" ? "1" : defect == "version-format" ? new string('x', 20) : "00000000000000000001";
        var expiry = defect == "deadline-format" ? "invalid" : defect == "deadline-range" ? long.MaxValue.ToString(CultureInfo.InvariantCulture) : now.AddHours(1).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var value = defect == "parts" ? "bad:value" : $"{id}:{version}:{expiry}";
        database.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), CommandFlags.None)).ReturnsAsync(value);
        Assert.That(Assert.ThrowsAsync<AuthException>(() => store.GetAsync(userId, default))?.Kind, Is.EqualTo(AuthErrorKind.Infrastructure));
    }

    [Test]
    public async Task Cleanup_compares_the_exact_session_and_generation_before_deleting()
    {
        ScriptResult(1);
        await store.RemoveIfMatchesAsync(userId, sid, 9, default);
        database.Verify(d => d.ScriptEvaluateAsync(
            It.Is<string>(s => s.Contains("'GET'") && s.Contains("'DEL'")),
            It.Is<RedisKey[]>(k => k.Length == 1 && k[0] == "test:session:{" + userId.ToString("N") + "}"),
            It.Is<RedisValue[]>(v => v.Length == 1 && v[0] == sid.ToString("N") + ":00000000000000000009:"),
            CommandFlags.None), Times.Once);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Read_failures_are_wrapped_and_logged_without_secret_values(bool redisException)
    {
        Exception failure = redisException
            ? new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "test", null, CommandStatus.Unknown)
            : new TimeoutException("test");
        database.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), CommandFlags.None)).ThrowsAsync(failure);
        var error = Assert.ThrowsAsync<AuthException>(() => store.GetAsync(userId, default));
        Assert.That(error?.Kind, Is.EqualTo(AuthErrorKind.Infrastructure));
        Assert.That(error?.InnerException, Is.SameAs(failure));
        logger.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("read session")),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Test]
    public void Read_cancellation_propagates_instead_of_becoming_a_dependency_error()
    {
        using var source = new CancellationTokenSource();
        var pending = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        database.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), CommandFlags.None)).Returns(pending.Task);
        source.Cancel();
        Assert.ThrowsAsync<TaskCanceledException>(() => store.GetAsync(userId, source.Token));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("with space:")]
    [TestCase("with{brace:")]
    [TestCase("with}brace:")]
    [TestCase("with\tcontrol:")]
    public void Invalid_key_prefixes_are_rejected(string? prefix) =>
        Assert.Throws<InvalidOperationException>(() => new RedisAuthSettings { KeyPrefix = prefix! }.Validate());

    [Test]
    public void Prefix_length_is_bounded_and_a_valid_prefix_is_accepted()
    {
        Assert.Throws<InvalidOperationException>(() => new RedisAuthSettings { KeyPrefix = new('a', 129) }.Validate());
        Assert.DoesNotThrow(() => new RedisAuthSettings().Validate());
    }

    private string Wire(DateTimeOffset deadline) => $"{sid:N}:00000000000000000001:{deadline.ToUnixTimeMilliseconds()}";
    private void ScriptResult(int value) => database.Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(),
        It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), CommandFlags.None)).ReturnsAsync(RedisResult.Create((RedisValue)value));
}
