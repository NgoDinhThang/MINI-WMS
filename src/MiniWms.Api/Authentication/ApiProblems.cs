using MiniWms.Application.Business;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using MiniWms.Application.Authentication;

namespace MiniWms.Api.Authentication;

public static class ApiProblems
{
    public static Task WriteAsync(
        HttpContext context,
        int statusCode,
        string title,
        string detail,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
        ProblemDetails problem = errors is { Count: > 0 }
            ? new HttpValidationProblemDetails(errors.ToDictionary(pair => pair.Key, pair => pair.Value))
            : new ProblemDetails();
        problem.Status = statusCode;
        problem.Title = title;
        problem.Detail = detail;
        problem.Instance = context.Request.Path;
        problem.Extensions["traceId"] = context.TraceIdentifier;
        context.Response.StatusCode = statusCode;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync((object)problem,
            options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
}

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        if (exception is BusinessException business)
        {
            var (status, title) = business.Kind switch
            {
                BusinessErrorKind.Validation => (400, "Invalid request"),
                BusinessErrorKind.NotFound => (404, "Not found"),
                BusinessErrorKind.Conflict => (409, "Business conflict"),
                BusinessErrorKind.Infrastructure => (503, "Warehouse services unavailable"),
                _ => (500, "Unexpected error")
            };
            logger.LogWarning("Business request rejected. Reason: {BusinessErrorKind}. Path: {Path}. TraceId: {TraceId}",
                business.Kind, httpContext.Request.Path, httpContext.TraceIdentifier);
            await ApiProblems.WriteAsync(httpContext, status, title,
                status == 503 ? "Warehouse services are temporarily unavailable. Try again later." : business.Message,
                business.Errors);
            return true;
        }
        if (exception is AuthException authException)
        {
            var (status, title, detail) = authException.Kind switch
            {
                AuthErrorKind.Validation => (400, "Invalid request", authException.Message),
                AuthErrorKind.InvalidCredentials => (401, "Authentication failed", "The email or password is incorrect, or the account is unavailable."),
                AuthErrorKind.InvalidSession => (401, "Invalid session", "The session is invalid, expired, or revoked. Sign in again."),
                AuthErrorKind.Forbidden => (403, "Forbidden", "You do not have permission to perform this operation."),
                AuthErrorKind.NotFound => (404, "Not found", authException.Message),
                AuthErrorKind.Infrastructure => (503, "Authentication unavailable", "Authentication services are temporarily unavailable. Try again later."),
                _ => (500, "Unexpected error", "The request could not be completed.")
            };

            if (status == 401)
            {
                httpContext.Response.Headers["WWW-Authenticate"] = "Bearer";
            }
            if (status == 503)
            {
                logger.LogError("Authentication dependency failed. FailureType: {FailureType}. TraceId: {TraceId}",
                    authException.InnerException?.GetType().Name ?? authException.GetType().Name,
                    httpContext.TraceIdentifier);
            }
            else
            {
                logger.LogInformation("Authentication request rejected. Reason: {AuthErrorKind}. TraceId: {TraceId}",
                    authException.Kind, httpContext.TraceIdentifier);
            }

            await ApiProblems.WriteAsync(httpContext, status, title, detail, authException.Errors);
            return true;
        }

        if (exception is BadHttpRequestException badRequest)
        {
            await ApiProblems.WriteAsync(httpContext, badRequest.StatusCode, "Invalid request",
                "The request body, route values, or content type is invalid.");
            return true;
        }

        // Log error categories and correlation IDs without serializing request bodies or credentials.
        logger.LogError("Unhandled request failure. FailureType: {FailureType}. TraceId: {TraceId}",
            exception.GetType().FullName, httpContext.TraceIdentifier);
        await ApiProblems.WriteAsync(httpContext, 500, "Unexpected error", "The request could not be completed.");
        return true;
    }
}
