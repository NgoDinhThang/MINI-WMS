using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using MiniWms.Api.Authentication;
using MiniWms.Api.OpenApi;
using MiniWms.Application.Authentication;
using MiniWms.Application.Business;
using Moq;
using NUnit.Framework;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MiniWms.Api.IntegrationTests;

[TestFixture]
[Category("Unit")]
public sealed class ApiBoundaryMoqTests
{
    private static readonly string SigningKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
    private static readonly AuthenticationScheme Scheme = new("Bearer", null, typeof(JwtBearerHandler));
    private readonly AuthSessionIdentity identity = new(Guid.NewGuid(), Guid.NewGuid(), 3);

    [TestCase(null)]
    [TestCase("not-guid")]
    [TestCase("00000000-0000-0000-0000-000000000000")]
    public void Invalid_subject_claim_is_rejected(string? value) =>
        AssertInvalidIdentity(Principal(value, identity.SessionId.ToString(), "3"));

    [TestCase(null)]
    [TestCase("not-guid")]
    [TestCase("00000000-0000-0000-0000-000000000000")]
    public void Invalid_session_claim_is_rejected(string? value) =>
        AssertInvalidIdentity(Principal(identity.UserId.ToString(), value, "3"));

    [TestCase(null)]
    [TestCase("NaN")]
    [TestCase("0")]
    [TestCase("-1")]
    [TestCase("9223372036854775808")]
    public void Invalid_generation_claim_is_rejected(string? value) =>
        AssertInvalidIdentity(Principal(identity.UserId.ToString(), identity.SessionId.ToString(), value));

    [Test]
    public void A_valid_identity_is_read_from_claims()
    {
        Assert.That(ApiAuthenticationRegistration.ReadSessionIdentity(CurrentPrincipal()), Is.EqualTo(identity));
    }

    [TestCase("none")]
    [TestCase("private")]
    [TestCase("anonymous")]
    public async Task Optional_bearer_headers_are_ignored_only_for_anonymous_endpoints(string endpoint)
    {
        using var provider = Provider();
        var options = Options(provider);
        var http = Context(provider);
        if (endpoint != "none")
        {
            http.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                new EndpointMetadataCollection(endpoint == "anonymous" ? new AllowAnonymousAttribute() : new AuthorizeAttribute()), "unit"));
        }
        var received = new MessageReceivedContext(http, Scheme, options);
        await options.Events.MessageReceived(received);
        Assert.That(received.Result?.None ?? false, Is.EqualTo(endpoint == "anonymous"));
    }

    [Test]
    public async Task Missing_principal_fails_validation_without_calling_the_auth_service()
    {
        var auth = new Mock<IAuthService>(MockBehavior.Strict);
        using var provider = Provider(auth.Object);
        var options = Options(provider);
        var context = new TokenValidatedContext(Context(provider), Scheme, options);
        await options.Events.TokenValidated(context);
        Assert.That(context.Result?.Failure, Is.Not.Null);
        auth.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Validation_replaces_a_stale_role_with_the_current_database_role()
    {
        var auth = new Mock<IAuthService>(MockBehavior.Strict);
        var user = new AuthUserDetails(identity.UserId, "staff@example.test", "Staff", "Staff");
        auth.Setup(a => a.ValidateSessionAsync(identity, default)).ReturnsAsync(user);
        using var provider = Provider(auth.Object);
        var options = Options(provider);
        var http = Context(provider);
        var principal = CurrentPrincipal("Admin");
        var context = new TokenValidatedContext(http, Scheme, options) { Principal = principal };
        await options.Events.TokenValidated(context);
        Assert.That(principal.FindAll("role").Select(c => c.Value), Is.EqualTo(new[] { "Staff" }));
        Assert.That(http.Items[ApiAuthenticationRegistration.CurrentUserItem], Is.SameAs(user));
        auth.VerifyAll();
    }

    [Test]
    public async Task A_custom_principal_identity_does_not_bypass_account_validation()
    {
        var auth = new Mock<IAuthService>(MockBehavior.Strict);
        auth.Setup(a => a.ValidateSessionAsync(identity, default)).ReturnsAsync(new AuthUserDetails(identity.UserId, "staff@example.test", "Staff", "Staff"));
        using var provider = Provider(auth.Object);
        var options = Options(provider);
        var principal = new Mock<ClaimsPrincipal>();
        var expected = CurrentPrincipal();
        principal.Setup(p => p.FindFirst(It.IsAny<string>())).Returns((string claim) => expected.FindFirst(claim));
        principal.SetupGet(p => p.Identity).Returns(new Mock<System.Security.Principal.IIdentity>().Object);
        var context = new TokenValidatedContext(Context(provider), Scheme, options) { Principal = principal.Object };
        await options.Events.TokenValidated(context);
        auth.Verify(a => a.ValidateSessionAsync(identity, default), Times.Once);
        Assert.That(context.Result?.Failure, Is.Null);
    }

    [TestCase(AuthErrorKind.Validation)]
    [TestCase(AuthErrorKind.InvalidCredentials)]
    [TestCase(AuthErrorKind.InvalidSession)]
    public async Task Invalid_session_errors_become_authentication_failures(AuthErrorKind kind)
    {
        var auth = new Mock<IAuthService>(MockBehavior.Strict);
        auth.Setup(a => a.ValidateSessionAsync(identity, default)).ThrowsAsync(new AuthException(kind, "private detail"));
        using var provider = Provider(auth.Object);
        var options = Options(provider);
        var context = new TokenValidatedContext(Context(provider), Scheme, options) { Principal = CurrentPrincipal() };
        await options.Events.TokenValidated(context);
        Assert.That(context.Result?.Failure?.Message, Is.EqualTo("The session is invalid."));
    }

    [Test]
    public void Infrastructure_failures_propagate_to_the_central_error_handler()
    {
        var auth = new Mock<IAuthService>(MockBehavior.Strict);
        var failure = new AuthException(AuthErrorKind.Infrastructure, "database offline");
        auth.Setup(a => a.ValidateSessionAsync(identity, default)).ThrowsAsync(failure);
        using var provider = Provider(auth.Object);
        var options = Options(provider);
        var context = new TokenValidatedContext(Context(provider), Scheme, options) { Principal = CurrentPrincipal() };
        Assert.That(Assert.ThrowsAsync<AuthException>(() => options.Events.TokenValidated(context)), Is.SameAs(failure));
    }

    [Test]
    public async Task Challenge_and_forbidden_responses_have_consistent_problem_details()
    {
        using var provider = Provider();
        var options = Options(provider);
        var unauthorized = Context(provider);
        var challenge = new JwtBearerChallengeContext(unauthorized, Scheme, options, new AuthenticationProperties());
        await options.Events.Challenge(challenge);
        Assert.That(challenge.Handled, Is.True);
        Assert.That(unauthorized.Response.StatusCode, Is.EqualTo(401));
        Assert.That(unauthorized.Response.Headers.WWWAuthenticate.ToString(), Is.EqualTo("Bearer"));
        Assert.That((await Problem(unauthorized)).GetProperty("traceId").GetString(), Is.EqualTo("unit-trace"));
        var forbidden = Context(provider);
        await options.Events.Forbidden(new ForbiddenContext(forbidden, Scheme, options));
        Assert.That(forbidden.Response.StatusCode, Is.EqualTo(403));
    }

    [Test]
    public async Task Challenge_does_not_write_after_response_has_started()
    {
        using var provider = Provider();
        var options = Options(provider);
        var context = Context(provider);
        var feature = new Mock<IHttpResponseFeature>();
        feature.SetupGet(f => f.HasStarted).Returns(true);
        context.Features.Set(feature.Object);
        var challenge = new JwtBearerChallengeContext(context, Scheme, options, new AuthenticationProperties());
        await options.Events.Challenge(challenge);
        feature.VerifySet(f => f.StatusCode = It.IsAny<int>(), Times.Never);
    }

    [TestCase(0)]
    [TestCase(10001)]
    public void Invalid_login_rate_limits_are_rejected(int limit)
    {
        using var configuration = (ConfigurationRoot)Configuration(new() { ["Authentication:LoginPermitLimit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddApiAuthentication(configuration));
    }

    [Test]
    public async Task Rate_limit_rejection_is_429_problem_details()
    {
        using var provider = Provider();
        var options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        var context = Context(provider);
        var lease = new Mock<RateLimitLease>();
        var callback = options.OnRejected ?? throw new InvalidOperationException();
        await callback(new OnRejectedContext { HttpContext = context, Lease = lease.Object }, default);
        Assert.That(context.Response.StatusCode, Is.EqualTo(429));
    }

    [TestCase("anonymous")]
    [TestCase("public")]
    [TestCase("authorize")]
    [TestCase("policy")]
    public void OpenAPI_security_tracks_authorization_metadata(string kind)
    {
        var metadata = new List<object>();
        if (kind == "anonymous") { metadata.Add(new AllowAnonymousAttribute()); metadata.Add(new AuthorizeAttribute()); }
        if (kind == "authorize") { metadata.Add(new AuthorizeAttribute()); }
        if (kind == "policy") { metadata.Add(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()); }
        var description = new ApiDescription { ActionDescriptor = new ActionDescriptor { EndpointMetadata = metadata } };
        var filterContext = new OperationFilterContext(description, new Mock<ISchemaGenerator>().Object, new SchemaRepository(), new OpenApiDocument(),
            typeof(ApiBoundaryMoqTests).GetMethod(nameof(A_valid_identity_is_read_from_claims))!);
        var operation = new OpenApiOperation { Responses = kind == "policy" ? null! : new OpenApiResponses() };
        new BearerSecurityOperationFilter().Apply(operation, filterContext);
        Assert.That(operation.Security?.Count ?? 0, Is.EqualTo(kind is "authorize" or "policy" ? 1 : 0));
    }

    [TestCase(BusinessErrorKind.Validation, 400)]
    [TestCase(BusinessErrorKind.NotFound, 404)]
    [TestCase(BusinessErrorKind.Conflict, 409)]
    [TestCase(BusinessErrorKind.Infrastructure, 503)]
    [TestCase((BusinessErrorKind)999, 500)]
    public async Task Business_errors_are_mapped_centrally(BusinessErrorKind kind, int status)
    {
        using var provider = Provider();
        var context = Context(provider);
        var handler = new ApiExceptionHandler(new Mock<ILogger<ApiExceptionHandler>>().Object);
        Assert.That(await handler.TryHandleAsync(context, new BusinessException(kind, "business error"), default), Is.True);
        Assert.That(context.Response.StatusCode, Is.EqualTo(status));
        Assert.That(context.Response.ContentType, Does.StartWith("application/problem+json"));
    }

    [TestCase(AuthErrorKind.Validation, 400)]
    [TestCase(AuthErrorKind.InvalidCredentials, 401)]
    [TestCase(AuthErrorKind.InvalidSession, 401)]
    [TestCase(AuthErrorKind.Forbidden, 403)]
    [TestCase(AuthErrorKind.NotFound, 404)]
    [TestCase(AuthErrorKind.Infrastructure, 503)]
    [TestCase((AuthErrorKind)999, 500)]
    public async Task Authentication_errors_are_mapped_centrally(AuthErrorKind kind, int status)
    {
        using var provider = Provider();
        var context = Context(provider);
        var handler = new ApiExceptionHandler(new Mock<ILogger<ApiExceptionHandler>>().Object);
        Assert.That(await handler.TryHandleAsync(context, new AuthException(kind, "private error", new TimeoutException()), default), Is.True);
        Assert.That(context.Response.StatusCode, Is.EqualTo(status));
    }

    [Test]
    public async Task An_infrastructure_error_without_an_inner_exception_is_also_handled()
    {
        using var provider = Provider();
        var context = Context(provider);
        await new ApiExceptionHandler(new Mock<ILogger<ApiExceptionHandler>>().Object)
            .TryHandleAsync(context, new AuthException(AuthErrorKind.Infrastructure, "unavailable"), default);
        Assert.That(context.Response.StatusCode, Is.EqualTo(503));
    }

    [Test]
    public async Task Body_binding_and_unexpected_errors_do_not_expose_internal_details()
    {
        using var provider = Provider();
        var handler = new ApiExceptionHandler(new Mock<ILogger<ApiExceptionHandler>>().Object);
        var body = Context(provider);
        await handler.TryHandleAsync(body, new BadHttpRequestException("private", 413), default);
        Assert.That(body.Response.StatusCode, Is.EqualTo(413));
        var unexpected = Context(provider);
        await handler.TryHandleAsync(unexpected, new InvalidOperationException("do-not-expose"), default);
        Assert.That(unexpected.Response.StatusCode, Is.EqualTo(500));
        Assert.That((await Problem(unexpected)).GetProperty("detail").GetString(), Does.Not.Contain("do-not-expose"));
        var notAborted = Context(provider);
        await handler.TryHandleAsync(notAborted, new OperationCanceledException(), default);
        Assert.That(notAborted.Response.StatusCode, Is.EqualTo(500));
    }

    [Test]
    public async Task Aborted_requests_are_left_to_the_server_cancellation_flow()
    {
        using var provider = Provider();
        using var source = new CancellationTokenSource();
        source.Cancel();
        var context = Context(provider);
        context.RequestAborted = source.Token;
        var handled = await new ApiExceptionHandler(new Mock<ILogger<ApiExceptionHandler>>().Object)
            .TryHandleAsync(context, new OperationCanceledException(), source.Token);
        Assert.That(handled, Is.False);
        Assert.That(context.Response.Body.Length, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Validation_details_are_included_only_when_errors_are_present(bool populated)
    {
        using var provider = Provider();
        var context = Context(provider);
        await ApiProblems.WriteAsync(context, 400, "Invalid request", "validation",
            populated ? new Dictionary<string, string[]> { ["field"] = ["required"] } : new Dictionary<string, string[]>());
        Assert.That((await Problem(context)).TryGetProperty("errors", out _), Is.EqualTo(populated));
    }

    private ClaimsPrincipal CurrentPrincipal(string role = "Staff") => Principal(identity.UserId.ToString(), identity.SessionId.ToString(), "3", role);
    private static ClaimsPrincipal Principal(string? user, string? session, string? version, string role = "Staff") =>
        new(new ClaimsIdentity(new[] { ("sub", user), ("sid", session), ("auth_version", version), ("role", (string?)role) }
            .Where(p => p.Item2 is not null).Select(p => new Claim(p.Item1, p.Item2!)), "unit"));
    private static void AssertInvalidIdentity(ClaimsPrincipal principal) =>
        Assert.That(Assert.Throws<AuthException>(() => ApiAuthenticationRegistration.ReadSessionIdentity(principal))?.Kind, Is.EqualTo(AuthErrorKind.InvalidSession));
    private static ServiceProvider Provider(IAuthService? auth = null)
    {
        var services = new ServiceCollection().AddLogging().AddOptions();
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(_ => { });
        services.AddApiAuthentication(Configuration([]));
        if (auth is not null) { services.AddSingleton(auth); }
        return services.BuildServiceProvider();
    }
    private static IConfigurationRoot Configuration(Dictionary<string, string?> extra)
    {
        extra["Jwt:SigningKeyBase64"] = SigningKey;
        return new ConfigurationBuilder().AddInMemoryCollection(extra).Build();
    }
    private static JwtBearerOptions Options(IServiceProvider provider) => provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("Bearer");
    private static DefaultHttpContext Context(IServiceProvider provider)
    {
        var context = new DefaultHttpContext { RequestServices = provider, TraceIdentifier = "unit-trace" };
        context.Response.Body = new MemoryStream();
        return context;
    }
    private static async Task<JsonElement> Problem(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }
}
