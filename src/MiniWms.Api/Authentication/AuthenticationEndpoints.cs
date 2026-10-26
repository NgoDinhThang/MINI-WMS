using Microsoft.AspNetCore.Mvc;
using MiniWms.Application.Authentication;

namespace MiniWms.Api.Authentication;

public static class AuthenticationEndpoints
{
    public static void MapAuthenticationEndpoints(this WebApplication app)
    {
        var authentication = app.MapGroup("/auth").WithTags("Authentication");

        authentication.MapPost("/login", async (
            LoginRequest? request, IAuthService auth, HttpContext context, CancellationToken cancellationToken) =>
        {
            PreventCaching(context);
            var tokens = await auth.LoginAsync(request?.Email ?? string.Empty,
                request?.Password ?? string.Empty, cancellationToken);
            return TypedResults.Ok(tokens);
        })
            .WithName("Login")
            .WithSummary("Sign in and replace any previous login session")
            .WithDescription("Returns a short-lived access token and a rotating refresh token. The entire session has an absolute maximum lifetime of eight hours. Tokens are returned as JSON for API clients; no browser cookie flow is implemented.")
            .AllowAnonymous()
            .RequireRateLimiting(ApiAuthenticationRegistration.LoginRateLimitPolicy)
            .Produces<AuthTokens>()
            .Produces<HttpValidationProblemDetails>(400, "application/problem+json")
            .Produces<ProblemDetails>(401, "application/problem+json")
            .Produces<ProblemDetails>(429, "application/problem+json")
            .Produces<ProblemDetails>(503, "application/problem+json");

        authentication.MapPost("/refresh", async (
            RefreshTokenRequest? request, IAuthService auth, HttpContext context, CancellationToken cancellationToken) =>
        {
            PreventCaching(context);
            return TypedResults.Ok(await auth.RefreshAsync(request?.RefreshToken ?? string.Empty, cancellationToken));
        })
            .WithName("RefreshTokens")
            .WithSummary("Exchange the current refresh token for a new token pair")
            .WithDescription("Authenticates using the refresh token, so an unexpired access token is not required. Each refresh token is single-use. Replaying a consumed token revokes its current session. Refresh does not extend the session's original deadline; coordinate concurrent refresh requests in the client.")
            .AllowAnonymous()
            .Produces<AuthTokens>()
            .Produces<HttpValidationProblemDetails>(400, "application/problem+json")
            .Produces<ProblemDetails>(401, "application/problem+json")
            .Produces<ProblemDetails>(503, "application/problem+json");

        authentication.MapPost("/logout", async (
            RefreshTokenRequest? request, IAuthService auth, HttpContext context, CancellationToken cancellationToken) =>
        {
            PreventCaching(context);
            await auth.LogoutAsync(request?.RefreshToken ?? string.Empty, cancellationToken);
            return TypedResults.NoContent();
        })
            .WithName("Logout")
            .WithSummary("Revoke the session identified by its refresh token")
            .WithDescription("Does not require an unexpired access token. A logout from an older session cannot revoke a newer login.")
            .AllowAnonymous()
            .Produces(204)
            .Produces<HttpValidationProblemDetails>(400, "application/problem+json")
            .Produces<ProblemDetails>(401, "application/problem+json")
            .Produces<ProblemDetails>(503, "application/problem+json");

        authentication.MapGet("/me", (HttpContext context) =>
        {
            PreventCaching(context);
            if (!context.Items.TryGetValue(ApiAuthenticationRegistration.CurrentUserItem, out var value) ||
                value is not AuthUserDetails user)
            {
                throw new AuthException(AuthErrorKind.InvalidSession, "The session is invalid.");
            }
            return TypedResults.Ok(user);
        })
            .WithName("GetCurrentUser")
            .WithSummary("Get the currently authenticated account")
            .RequireAuthorization(ApiAuthenticationRegistration.StaffPolicy)
            .Produces<AuthUserDetails>()
            .Produces<ProblemDetails>(503, "application/problem+json");

        var staff = app.MapGroup("/staff").WithTags("Staff accounts")
            .RequireAuthorization(ApiAuthenticationRegistration.AdminPolicy);

        staff.MapPost("/{userId}/ban", async (
            Guid userId, IAuthService auth, HttpContext context, CancellationToken cancellationToken) =>
        {
            await auth.BanStaffAsync(ApiAuthenticationRegistration.ReadSessionIdentity(context.User),
                userId, true, cancellationToken);
            return TypedResults.NoContent();
        })
            .WithName("BanStaff")
            .WithSummary("Ban a Staff account and revoke its login session")
            .WithDescription("Admin only. The committed inactive state and authentication version deny future requests even if Redis cleanup is unavailable.")
            .Produces(204)
            .Produces<ProblemDetails>(400, "application/problem+json")
            .Produces<ProblemDetails>(404, "application/problem+json")
            .Produces<ProblemDetails>(503, "application/problem+json");

        staff.MapPost("/{userId}/unban", async (
            Guid userId, IAuthService auth, HttpContext context, CancellationToken cancellationToken) =>
        {
            await auth.BanStaffAsync(ApiAuthenticationRegistration.ReadSessionIdentity(context.User),
                userId, false, cancellationToken);
            return TypedResults.NoContent();
        })
            .WithName("UnbanStaff")
            .WithSummary("Unban a Staff account; a fresh login is required")
            .Produces(204)
            .Produces<ProblemDetails>(400, "application/problem+json")
            .Produces<ProblemDetails>(404, "application/problem+json")
            .Produces<ProblemDetails>(503, "application/problem+json");
    }

    private static void PreventCaching(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
}

public sealed record LoginRequest(string? Email, string? Password);
public sealed record RefreshTokenRequest(string? RefreshToken);
