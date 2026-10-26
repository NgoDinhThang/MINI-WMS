using MiniWms.Api.Authentication;
using MiniWms.Api.Business;
using MiniWms.Api.OpenApi;
using MiniWms.Infrastructure;
using MiniWms.Infrastructure.Authentication;
using MiniWms.Infrastructure.Persistence.Seeding;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi;

var seedMockData = args.Contains("--seed-mock-data", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(
    args.Where(argument => argument != "--seed-mock-data").ToArray());

if (seedMockData && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("Mock data seeding is only allowed in the Development environment.");
}

var connectionString = builder.Configuration.GetConnectionString("WmsDatabase");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Connection string 'WmsDatabase' is required. Set ConnectionStrings__WmsDatabase " +
        "or configure ConnectionStrings:WmsDatabase in User Secrets.");
}

builder.Services.AddInfrastructure(connectionString);
if (!seedMockData)
{
    builder.Services.AddAuthenticationInfrastructure(builder.Configuration);
    builder.Services.AddApiAuthentication(builder.Configuration);
}

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    };
});
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Mini WMS API",
        Version = "v1",
        Description = "Mini Warehouse Management System. Access/refresh token authentication, inventory lookups, transactional goods receipts and issues, stock adjustments, catalog creation, and Staff account management."
    });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Enter the access token only, without the Bearer prefix. Obtain a token with POST /auth/login. Refresh tokens are sent only to refresh/logout endpoints."
    });
    options.OperationFilter<BearerSecurityOperationFilter>();
});

var app = builder.Build();

if (seedMockData)
{
    await using (app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var seeder = scope.ServiceProvider.GetRequiredService<MockDataSeeder>();
        var result = await seeder.SeedAsync(new MockDataOptions
        {
            AdminPassword = builder.Configuration["MockData:AdminPassword"] ?? string.Empty,
            StaffPassword = builder.Configuration["MockData:StaffPassword"] ?? string.Empty
        });
        app.Logger.LogInformation("Development mock data {SeedStatus}. Table counts: {Counts}",
            result.IsAlreadySeeded ? "already present; no changes applied" : "created", result.Counts);
    }

    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("v1/swagger.json", "Mini WMS API v1");
        options.DocumentTitle = "Mini WMS API - Swagger";
        options.ConfigObject.PersistAuthorization = false;
    });
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapAuthenticationEndpoints();
app.MapBusinessEndpoints();

// Swagger is intentionally unavailable outside Development, including for anonymous callers.
if (!app.Environment.IsDevelopment())
{
    app.MapGet("/swagger/{**path}", () => TypedResults.NotFound())
        .AllowAnonymous()
        .ExcludeFromDescription();
}

app.MapGet("/", () => TypedResults.Ok(new
{
    name = "MiniWms.Api",
    status = "running"
}))
    .WithName("GetApiStatus")
    .WithTags("System")
    .WithSummary("Get API status")
    .WithDescription("Returns the API name and process status.")
    .AllowAnonymous();

app.MapGet("/health", async (
        HealthCheckService healthChecks,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
    {
        var report = await healthChecks.CheckHealthAsync(cancellationToken);
        httpContext.Response.Headers.CacheControl = "no-store, no-cache";
        httpContext.Response.Headers.Pragma = "no-cache";
        httpContext.Response.Headers.Expires = "Thu, 01 Jan 1970 00:00:00 GMT";

        return TypedResults.Text(report.Status.ToString(), "text/plain",
            statusCode: report.Status == HealthStatus.Unhealthy
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status200OK);
    })
    .WithName("GetHealth")
    .WithTags("System")
    .WithSummary("Check process liveness")
    .WithDescription("Checks process liveness only. Database and Redis readiness are not checked.")
    .Produces<string>(StatusCodes.Status200OK, "text/plain")
    .Produces<string>(StatusCodes.Status503ServiceUnavailable, "text/plain")
    .AllowAnonymous();

app.Run();

public partial class Program { }
