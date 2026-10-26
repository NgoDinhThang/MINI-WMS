# MINI WMS

ASP.NET Core 8 backend for a Mini Warehouse Management System, using Clean Architecture, PostgreSQL, EF Core, Redis, and NUnit.

Authentication is implemented: login, rotating access/refresh credentials, a single current session per user, logout, current-account lookup, and Admin ban/unban of Staff. Access tokens default to 15 minutes inside an absolute session lifetime of eight hours. All requested core business endpoints are implemented: catalog/warehouse/inventory lookups, transactional inbound/outbound receipts, physical-count adjustments, product/warehouse creation, and Admin Staff creation/ban/unban. See [business API examples](docs/BUSINESS_API.md). Deployment hardening remains outside this implementation.

The original nine ERD tables and their development fixture remain intact. Authentication adds `Users.AuthVersion`, `AuthSessions`, and `RefreshTokens`. See [requirements](docs/PROJECT_REQUIREMENTS.md), [database design](docs/DATABASE_DESIGN.md), and [authentication design](docs/AUTHENTICATION.md). The original [draw.io diagram](mini_wms_erd.drawio) is preserved.

## Run locally

Prerequisites: .NET 8 SDK, the configured local PostgreSQL database, and Docker Desktop using Linux containers. This machine has SDK 8.0.426 installed system-wide and in `.tools/dotnet`; a runtime alone cannot build the solution. `global.json` permits stable .NET 8 feature-band roll-forward from 8.0.100.

From the repository root:

```powershell
docker compose up -d --wait redis
dotnet restore MiniWms.sln
dotnet tool restore
dotnet build MiniWms.sln -c Release --no-restore
dotnet run --project src/MiniWms.Api -c Release --no-build --launch-profile http
```

The `http` profile selects Development and listens at `http://localhost:5080`. Open [Swagger UI](http://localhost:5080/swagger). Stop an existing API instance using that port before starting another. Swagger UI and `/swagger/v1/swagger.json` are Development-only; the input accepts an access token without the `Bearer ` prefix and does not persist it across reloads.

## Configuration

The local Development User Secrets store is `MiniWms.Api`. Database credentials, the JWT signing key, and fixture passwords are stored outside the repository. In Visual Studio, right-click **MiniWms.Api > Manage User Secrets** to inspect your local settings.

| Setting | Purpose |
| --- | --- |
| `ConnectionStrings:WmsDatabase` | Required PostgreSQL connection; local database is `mini_wms` on `127.0.0.1:5432`. |
| `ConnectionStrings:Redis` | Required Redis connection; local value `127.0.0.1:6379`. |
| `Jwt:SigningKeyBase64` | Required private Base64 signing key, decoding to at least 32 cryptographically random bytes. The local key is already configured. |
| `Jwt:Issuer` / `Jwt:Audience` | Defaults `MiniWms.Api` / `MiniWms.Client`. |
| `Authentication:AccessTokenMinutes` | Default 15; token expiry is clipped to the session deadline. |
| `Authentication:SessionLifetimeHours` | Default 8; positive values above eight hours are rejected. |
| `Authentication:LoginPermitLimit` | Default 60 attempts per minute per remote IP in each API instance. |
| `Redis:KeyPrefix` | Default `miniwms:auth:`; integration tests use their own prefix. |
| `MockData:AdminPassword` / `MockData:StaffPassword` | Initial fixture credentials, already configured locally. |

Environment equivalents use double underscores, for example `ConnectionStrings__Redis` and `Jwt__SigningKeyBase64`. Production configuration must provide the required secrets separately; Development User Secrets are not a deployment secret store.

## Try the seeded accounts

| Email | Role | State | Helper account |
| --- | --- | --- | --- |
| `admin@mini-wms.test` | Admin | Active | `Admin` |
| `staff1@mini-wms.test` | Staff | Active | `Staff` |
| `staff2@mini-wms.test` | Staff | Active | `Staff2` |
| `staff.banned@mini-wms.test` | Staff | Inactive; login denied | `Banned` |

Existing password hashes were preserved. Helpers read the initial password from your User Secrets without embedding it in source or command history. With the API running:

```powershell
$login = .\scripts\Invoke-DemoLogin.ps1 -Account Staff
$headers = @{ Authorization = 'Bearer ' + $login.accessToken }
Invoke-RestMethod http://localhost:5080/auth/me -Headers $headers

$refreshed = Invoke-RestMethod -Method Post -Uri http://localhost:5080/auth/refresh `
    -ContentType 'application/json' `
    -Body (@{ refreshToken = $login.refreshToken } | ConvertTo-Json)

Invoke-RestMethod -Method Post -Uri http://localhost:5080/auth/logout `
    -ContentType 'application/json' `
    -Body (@{ refreshToken = $refreshed.refreshToken } | ConvertTo-Json)
```

Assign login/refresh results to variables: they contain credentials. The consumed refresh token cannot be reused; replay revokes its current family. Coordinate refresh attempts across client tabs, and sign in again if a rotation response is lost.

For manual Swagger/Postman entry, retrieve only the selected demo credential:

```powershell
$credential = .\scripts\Get-DemoCredential.ps1 -Account Admin
$credential.UserName
$credential.GetNetworkCredential().Password
```

The final command intentionally displays that initial password in your own terminal. A seed rerun never resets passwords, so changing secret configuration after the first seed does not change existing account hashes.

## Implemented HTTP endpoints

| Method and route | Authentication | Result |
| --- | --- | --- |
| `GET /` | Public | Minimal API name/status. |
| `GET /health` | Public | Process liveness; no PostgreSQL or Redis connectivity claim. |
| `POST /auth/login` | Email/password | Access token, rotating refresh token, expiry times, and account details. |
| `POST /auth/refresh` | Refresh token in JSON body | New access/refresh pair within the original session deadline. |
| `POST /auth/logout` | Refresh token in JSON body | Revoke its session; no unexpired access token required. |
| `GET /auth/me` | Current Admin or Staff bearer session | Current database-backed account details. |
| `GET /products` | Current Admin or Staff bearer session | Paginated products, optional search/isActive filters. |
| `POST /products` | Current Admin bearer session | Create a product. |
| `GET /warehouses` | Current Admin or Staff bearer session | Paginated warehouses, optional search/isActive filters. |
| `POST /warehouses` | Current Admin bearer session | Create a warehouse. |
| `GET /inventory` | Current Admin or Staff bearer session | Stock filtered by productId and/or warehouseId. |
| `POST /inbound-receipts` | Current Admin or Staff bearer session | Receive all document lines in one transaction. |
| `POST /outbound-receipts` | Current Admin or Staff bearer session | Issue all lines atomically, rejecting insufficient stock. |
| `POST /inventory/adjustments` | Current Admin bearer session | Set the absolute physical count. |
| `POST /staff` | Current Admin bearer session | Create an active Staff account with a hashed initial password. |
| `POST /staff/{userId}/ban` | Current Admin bearer session | Ban Staff and revoke access. |
| `POST /staff/{userId}/unban` | Current Admin bearer session | Unban Staff; fresh login is required. |

Refresh/logout authenticate their refresh credential themselves. Every protected request validates JWT signature, issuer, audience, lifetime, current Redis session, and current PostgreSQL account state/version. Admin inherits Staff permissions; Staff cannot manage account state, and Admin accounts cannot be banned through these endpoints.

Errors use ProblemDetails with a trace identifier: 400 for invalid input, 401 for invalid credentials/session, 403 for insufficient permissions, 404 for missing resources, 409 for business/concurrency conflicts, 429 for login throttling, and 503 for unavailable dependencies. Raw passwords and tokens are not logged. Public liveness remains independent of optional bearer headers.

Import [the Postman collection](docs/postman/MiniWms.postman_collection.json). Set its local password variables using your selected demo credentials; committed token/password values are empty. The collection captures tokens and includes deliberate session replacement/replay and authorization checks. Its **Business flow - run in order** folder creates its own product, warehouse, and Staff account, then tests receipt/issue/count/ban/unban behavior. Start that folder at its Admin login request. See [request contracts and transaction behavior](docs/BUSINESS_API.md). Its ban/unban requests change Staff state. [MiniWms.Api.http](src/MiniWms.Api/MiniWms.Api.http) provides manual requests as well.

## Database migrations and mock fixtures

Applied local migrations are `20261021164214_InitialCreate`, `20261022164820_AddAuditTimestamps`, and `20261026170941_AddAuthenticationSessions`. The API never applies migrations or calls `EnsureCreated` at startup. The design-time factory reads `ConnectionStrings__WmsDatabase` directly; its password-free fallback supports model inspection, not authenticated database updates.

For explicit migration commands, load the configured connection into this terminal without displaying it:

```powershell
$settings = Get-Content (Join-Path $env:APPDATA 'Microsoft\UserSecrets\MiniWms.Api\secrets.json') -Raw | ConvertFrom-Json
$env:ConnectionStrings__WmsDatabase = $settings.'ConnectionStrings:WmsDatabase'
dotnet ef database update --project src/MiniWms.Infrastructure --startup-project src/MiniWms.Api -c Release
Remove-Item Env:ConnectionStrings__WmsDatabase
```

The original fixture contains two roles, four users, ten products, three warehouses, three inbound documents/twelve lines, two outbound documents/six lines, and ten ledger-derived stock balances: 52 original rows. Accounts include exactly one Admin and one inactive Staff. Quantities remain `numeric(18,3)`; physical units are still open.

After migrations, the explicit Development seed command is:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src/MiniWms.Api -c Release --no-launch-profile -- --seed-mock-data
Remove-Item Env:ASPNETCORE_ENVIRONMENT
```

The seeder serializes seed operations with an advisory lock and locks the nine fixture tables. It accepts empty data with optional canonical Admin/Staff roles or skips the complete matching fixture. It refuses partial/conflicting/unrelated data, never clears tables, and never resets existing passwords, timestamps, or edits. Migration plus seed-rerun checksum verification preserved all original fixture values and password hashes. Authentication history is created by login/refresh operations, separately from the warehouse fixture.

All eleven tables have required UTC audit timestamps. Context saves stamp inserts/updates; direct SQL and `ExecuteUpdate` must maintain `UpdatedAt` explicitly.

## Redis operations

`compose.yaml` runs Redis 8.2.10, publishes only `127.0.0.1:6379`, mounts `miniwms-redis-data`, enables AOF, and uses a 128 MB Redis data-memory limit with `noeviction`. This password-free setup is local Development configuration. Docker Desktop must be running.

```powershell
docker compose ps
docker compose exec -T redis redis-cli -e PING
docker compose stop redis
docker compose start redis
```

Windows-host ping, read/write, TTL expiration, and persistence through a normal restart were checked. AOF/replicas do not guarantee zero data loss after crashes/failover. Durable refresh rotation/revocation remains in PostgreSQL, and a missing Redis session is never recreated from a refresh credential. Redis outages fail closed instead of accepting JWT-only authentication.

## Tests

```powershell
dotnet restore MiniWms.sln
dotnet build MiniWms.sln -c Release --no-restore
dotnet test MiniWms.sln -c Release --no-build
.\scripts\Test-AuthIntegration.ps1 -NoBuild
```

`MiniWms.Infrastructure.Tests` inspects mappings/generated PostgreSQL SQL, deterministic auditing, fixture integrity/password hashing, and real JWT/refresh-token adapters without a database connection. `MiniWms.Application.Tests` isolates orchestration with store/clock doubles.

`MiniWms.Api.IntegrationTests` uses real PostgreSQL and Redis. Its helper loads local connection settings, creates a disposable `miniwms_auth_tests_<guid>` database, applies migrations, uses a unique Redis prefix, and removes only its own database/keys afterward. The PostgreSQL account therefore needs database-creation permission. CI can supply `MINIWMS_TEST_POSTGRES` and `MINIWMS_TEST_REDIS` directly. Without PostgreSQL test configuration, normal solution testing explicitly skips this integration fixture; the helper runs it with real services.

Coverage includes persisted refresh rotation, older-token replay, login replacement, cross-instance concurrent logins, concurrent refresh, login/refresh versus ban, old-session logout and delayed Redis cleanup, failed Redis revocation followed by unban, JWT validation, permission boundaries, fixed session expiry, and dependency outages. Business coverage includes complete goods-issue persistence, all-or-nothing multiline rollback, inactive/missing references, first-balance creation across hosts, oversell protection, concurrent count/movement ordering, duplicate codes across warehouses, quantity overflow, physical counts, Staff creation and role boundaries, filters, and Swagger.

Verification completed: Release and an isolated-output Debug build had zero warnings/errors (the normal Debug output was locked by the running API; stop that instance before rebuilding there); all 122 tests passed with no skipped tests when real services were configured (33 Infrastructure, 49 Application, 40 API integration). EF reported no pending model changes. A separate temporary API process verified the actual seeded Admin/Staff credentials, inactive-account denial, refresh rotation, logout, and Swagger; that process was stopped afterward.

## Open in Visual Studio

Use Visual Studio 2022 17.11 or later with ASP.NET and web development and a .NET 8 SDK. Select **File > Open > Project/Solution > MiniWms.sln**, set **MiniWms.Api** as Startup Project, and select the **http** profile. VS Code C# Dev Kit also uses the system SDK; reload its window after installing an SDK.

## Structure and remaining scope

`Domain` contains entities; `Application` references Domain and orchestrates use cases; `Infrastructure` implements Application abstractions; `Api` composes services and handles HTTP. The three NUnit projects live under `tests/`. No source project references a test project.

Stock writes use a PostgreSQL warehouse row lock through the complete transaction; same-warehouse writes serialize across instances. Lock conflicts are bounded and returned as 409 after rollback; no internal automatic retry is performed. See [business API design](docs/BUSINESS_API.md). No schema migration was required for the business endpoints. Physical-count adjustments update current stock and emit structured logs, but have no durable adjustment-history table; movement documents alone cannot reconstruct balances after counts. Browser cookie/CSRF flows, an external OIDC provider, durable-auth-history cleanup, and deployment hardening are not implemented. JSON access/refresh transport is intended for API clients and Swagger/Postman; deployment requires HTTPS and appropriate secret/service protection. Login throttling is per API instance rather than a distributed account lockout policy. Requests already authorized before revocation may finish; later authorization checks reject revoked sessions.
