# Authentication design and review guide

This implementation adds the requested access/refresh login flow to MINI WMS. PostgreSQL stores durable account/session/refresh state; Redis stores the current session accepted by all API instances. The original warehouse fixture is preserved. The user requested access tokens and refresh tokens, rather than OpenID Connect ID tokens.

## Contracts and configuration

- Access token: HS256 JWT; default lifetime 15 minutes, clipped to the session's original deadline.
- Session: one current session per user; an absolute maximum of eight hours. Requests and refresh do not extend it.
- Refresh token: two GUID lookup identifiers plus a cryptographically random 32-byte Base64Url verifier. Clients treat the whole value as opaque. Only a SHA-256 verifier hash is persisted; neither identifier alone authenticates a request.
- JWT claims: `sub`, `sid`, `auth_version`, `role`, `name`, `email`, `jti`, `iat`, plus issuer/audience/not-before/expiry. Signatures, HS256 algorithm, issuer, audience, and lifetime are validated with zero clock skew.
- JSON transport is implemented for API clients and Swagger/Postman. Refresh/logout bodies contain `refreshToken`; they do not require an unexpired access token. A browser HttpOnly-cookie/CSRF flow is not implemented.

`Jwt:SigningKeyBase64` must decode to at least 32 random bytes and is supplied through private configuration. The local key is already generated in `MiniWms.Api` User Secrets. Issuer/audience, token/session durations, login rate limit, and Redis key prefix have explicit non-secret defaults in `appsettings.json`.

## Login transaction

1. Normalize the submitted email with trim/invariant lowercase and validate email/password lengths. Verify the existing ASP.NET Core Identity password hash. Missing/incorrect/inactive accounts receive the same public authentication failure.
2. Open a PostgreSQL read-committed transaction and lock that user's row with `FOR UPDATE`. The role is loaded separately, avoiding a locking join on nullable navigation data. Login, refresh, logout, and account-state changes use the same per-user lock protocol across API instances.
3. Revoke every earlier unrevoked session, including expired ones, and flush those revocations inside the transaction before inserting a replacement. This ordering respects the filtered unique active-session index.
4. Increment `Users.AuthVersion`, create the session and first refresh-token hash, and prepare the signed token response.
5. Atomically replace the Redis current-session value with the same absolute expiry, commit PostgreSQL, and only then return credentials.

PostgreSQL and Redis have separate commit points. A timeout can have an ambiguous outcome; credentials are not returned after an observed failure. Compensation conditionally removes only the attempted session, never a newer login. A failed/uncertain operation can require a fresh login; there is no JWT-only fallback and no automatic retry of an ambiguous credential issue.

## Protected requests

`OnTokenValidated` resolves the centralized Application validation service after JWT cryptographic validation. PostgreSQL must report an active Admin/Staff account with a matching `AuthVersion`; Redis must report the same `sid` and generation with an unexpired absolute deadline. Role policies use the current database role. A signed JWT alone is insufficient.

Redis values use `sessionIdN:generationD20:expiresAtUnixMilliseconds`. The fixed-width generation lets Lua compare the full signed-64-bit range without floating-point precision loss. Keys use the configured prefix followed by `session:{userIdN}`. Atomic replacement fences off lower generations; compare-and-delete targets an exact session/generation prefix.

Public endpoints, including status/health and refresh-token-authenticated routes, ignore an optional access-token header. Thus public liveness does not depend on database/Redis availability. Business endpoints inherit a fallback authentication policy and explicit role policies where appropriate.

## Refresh rotation and replay

The refresh service locks the user, loads the submitted token record within that user's scope, and verifies its secret hash before considering replay or revocation. Unknown IDs and incorrect secrets are rejected without revoking another session.

For a current account/session/token, Redis must still recognize the current session. A database transaction marks the old token consumed and inserts the next token hash. `sid`, `AuthVersion`, and the session deadline remain unchanged. Consumed hashes stay in PostgreSQL so replay of any earlier token can be recognized even if Redis loses recent state.

A correctly authenticated consumed token from the current family revokes that family and advances `AuthVersion`. Replaying an older replaced/revoked family cannot revoke a newer login. Missing Redis state is never reconstructed from a refresh token.

Strict rotation cannot distinguish an attacker from two legitimate concurrent refreshes using the same token: one may rotate, while the second detects reuse and revokes the family. Clients must coordinate refresh attempts, including across tabs. A lost successful rotation response can require password login again; consumed credentials are not restored.

## Logout, ban, and unban

Logout authenticates a known refresh credential and revokes only its matching current family, advancing the account generation. Logout from a previously revoked/replaced family is idempotent and cannot remove a newer Redis session.

Only a current Admin may ban or unban a Staff account. The target's active state, generation, and session revocations commit in PostgreSQL before best-effort conditional Redis cleanup. Cleanup failure does not undo a committed ban. Unban advances the generation as well and requires a new login; retained old Redis entries or refresh credentials cannot regain access. Admin creation is not exposed. POST /staff lets Admin create active Staff accounts with a configured initial password, stored as an Identity hash; see BUSINESS_API.md.

Revocation takes effect at a fresh authorization check. A request authorized before a concurrent ban/replacement may finish. A stronger rule preventing any later business commit would require account-state locking/checks inside that business transaction.

## Errors, logging, and operational limits

Errors use ProblemDetails and trace IDs: validation400, invalid credentials/session401, permission403, missing target404, throttling429, and dependency unavailable503. Npgsql's transient InvalidOperationException wrapper is classified as a database dependency failure. Production responses omit internal details. Request bodies, passwords, raw access/refresh tokens, and signing keys are not logged.

Redis command timeouts and PostgreSQL's five-second authentication lock timeout bound contention/failure handling. The rate limiter is per-IP and per API instance; distributed lockout, trusted reverse-proxy configuration, TLS deployment, key rotation, and cleanup/retention of expired authentication history require further deployment work.

## Files to review

| Layer | Files |
| --- | --- |
| Domain | `Entities/User.cs`, `Entities/AuthSession.cs`, `Entities/RefreshToken.cs` |
| Application | `Authentication/AuthService.cs`, `AuthContracts.cs`, `AuthException.cs` |
| Infrastructure | `Authentication/PostgresAuthStore.cs`, `RedisAuthSessionStore.cs`, `OpaqueRefreshTokenCodec.cs`, `JwtAuthTokenIssuer.cs`, `AuthenticationRegistration.cs` |
| API | `Authentication/AuthenticationEndpoints.cs`, `ApiAuthenticationRegistration.cs`, `ApiProblems.cs`, composition in `Program.cs` |
| Persistence | Three auth-related EF configurations and migration `20261026170941_AddAuthenticationSessions` |
| Tests | Application doubles/unit tests, Infrastructure adapter/model tests, and real-provider API integration tests |

The API integration suite creates a guarded disposable PostgreSQL database and uses a unique Redis prefix. It verifies actual persisted sessions/consumed hashes and outcomes under concurrency, replay, dependency outages, and failed revocation. The fixture checks its configured database before sending requests and deletes only its own resources. Warehouse business-flow integration tests are now implemented in the same isolated fixture; see BUSINESS_API.md for stock and account-creation coverage.
