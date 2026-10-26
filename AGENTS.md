# Mini WMS Development Instructions

This file applies to the entire repository. Before editing files in a subdirectory, read any instructions there; instructions closer to a file supplement or override the rules here.

## Current context

- Build a RESTful Mini Warehouse Management System (Mini WMS) for goods receipts, goods issues, inventory lookups, and physical-count adjustments.
- [Project requirements](docs/PROJECT_REQUIREMENTS.md) record the supplied assignment and its acceptance criteria. Read that document before designing or implementing a feature. Later explicit user requirements take precedence over repository instructions.
- The project is named **MINI WMS**. The workspace folder is `MINI WMS`; use `MiniWms` for solution, project, and namespace names.
- The repository contains nine ERD-based Domain entities with `CreatedAt`/`UpdatedAt`, EF Core configurations, `WmsDbContext`, model/audit/fixture tests, PostgreSQL DI registration, migration tooling, an explicit Development mock-data seeder, and Development-only Swagger UI/OpenAPI documentation. PostgreSQL 18.6 is running locally with `20261021164214_InitialCreate` and `20261022164820_AddAuditTimestamps` applied to `mini_wms`. Restore and the Release build passed with zero warnings/errors; all 25 NUnit tests passed after Swagger integration. The database contains 52 development-fixture rows. Live checks confirmed 61 user-defined columns, all 18 required audit fields/defaults, valid timestamps, exactly one Admin, hashed passwords, and ledger-consistent nonnegative stock. A seed rerun preserved every persisted value. Business use cases, authentication, Redis integration, the Postman Collection, and runtime business-flow integration tests remain pending.
- Communicate and write documentation in English. Use English for project names, namespaces, types, members, and routes.
- Do not describe this scaffold as production-ready. Document its limitations and any unimplemented steps in the relevant documentation.

## Required stack and implementation status

- Target ASP.NET Core on **.NET 8** with **Entity Framework Core 8**. **PostgreSQL** is the selected database, as requested by the user; use the Npgsql EF Core provider.
- Use **Redis** for active login sessions and **JWT bearer authentication** for API access.
- **NUnit** is the selected automated test framework. Provide Swagger with authenticated requests and a Postman Collection as API features are implemented.
- `Directory.Build.props` targets `net8.0`; `global.json` selects a stable .NET 8 SDK from baseline `8.0.100` with `latestFeature` roll-forward. Keep EF Core and Npgsql on compatible 8.x versions.
- Use [mini_wms_erd.drawio](mini_wms_erd.drawio) as the structural source for the nine tables. The user's model-first workflow is implemented with C# entities and Fluent API mappings, followed by EF Core migrations when database setup is requested. Do not introduce an EF6/EDMX designer or scaffold from a live database.
- The user has authorized PostgreSQL setup and database creation. The local development database is `mini_wms` on `127.0.0.1:5432`, owned by `postgres` with UTF8 encoding. Apply reviewed EF Core migrations explicitly during setup; do not call `EnsureCreated`, apply migrations at API startup, or require a database connection merely to construct or inspect the model.
- The local .NET 8.0.426 SDK is installed in `.tools/dotnet`; see the README for workspace-local CLI home/cache settings. `.config/dotnet-tools.json` pins `dotnet-ef` 8.0.32. The API requires `ConnectionStrings:WmsDatabase` from configuration. EF's design-time factory reads `ConnectionStrings__WmsDatabase` directly and uses a password-free local fallback for model inspection/migration generation; database updates require credentials. Never commit those credentials.
- The API uses `UserSecretsId` `MiniWms.Api`. Development connection and mock-account settings are stored outside the repository in that User Secrets store; never print or commit their values.
- The assignment's final integration-test sentence is incomplete. Preserve the known requirement and the missing-text note in the requirements document; do not invent the omitted criteria.

## Structure and dependency direction

```text
src/
  MiniWms.Domain/
  MiniWms.Application/
  MiniWms.Infrastructure/
  MiniWms.Api/
```

The API's implemented application routes remain the root endpoint and `/health`. In Development, Swagger UI is available at `/swagger` and its generated OpenAPI document at `/swagger/v1/swagger.json`. Domain entities live under `Entities/`; Infrastructure mappings live under `Persistence/Configurations/`. Model tests live in `tests/MiniWms.Infrastructure.Tests`. Keep the following dependency direction:

- `Domain`: references no other projects; contains inventory business rules, entities, and value objects.
- `Application` -> `Domain`: contains use cases and the abstractions needed to execute them.
- `Infrastructure` -> `Application`; it may reference `Domain` when an implementation needs Domain types directly. Contains EF Core configuration/migrations, database access, Redis session storage, and implementations of Application abstractions.
- `Api` -> `Application` and `Infrastructure`: receives requests, maps data, and returns responses. Use Infrastructure only to configure and register dependencies at the composition root (`Program.cs` and service registration extensions).
- Test projects may reference the projects they test. Projects under `src` must not reference test projects.

Do not reverse dependency direction or introduce circular references to fix a local issue. Place an abstraction in the layer that needs to consume it.

## Implementation principles

- Keep Domain independent of ASP.NET Core, EF Core, HTTP, application configuration, and external services. Keep HTTP DTOs and persistence-specific attributes out of Domain.
- Place use case orchestration in Application and business rules in Domain. Endpoints and controllers handle HTTP responsibilities only; keep business logic out of them.
- Use constructor injection. Register implementations at the composition root, choose appropriate lifetimes, and avoid service locator calls in application logic.
- Use asynchronous APIs for I/O and propagate `CancellationToken` through to dependencies that support it. Avoid `.Result`, `.Wait()`, and wrapping asynchronous I/O in `Task.Run`.
- Keep nullable reference types enabled. Handle missing values explicitly; do not use `!` simply to suppress warnings.
- EF Core, a relational provider, Redis, JWT authentication, Swagger, and a test framework are required by this assignment. Add abstractions and optional libraries only when they serve an actual use case; MediatR, a CQRS framework, generic repositories, and mapping frameworks are not prescribed.
- Keep features traceable to the Mini WMS assignment or later explicit user requirements. Implement complete use cases with their validation, authorization, persistence, and verification.
- Do not hard-code secrets or connection strings containing sensitive information. Use environment configuration or appropriate secret storage; do not log tokens, passwords, or sensitive data.
- Keep changes within the requested scope. Do not revert existing user changes. When changing contracts or architectural decisions, update the relevant documentation and tests.

## Database design

- [Database design](docs/DATABASE_DESIGN.md) records the ERD, data dictionary, keys, constraints, indexes, and mapping decisions. Keep it consistent with the C# entities and Fluent API configurations.
- Preserve all nine tables and 43 business fields from the supplied drawing. Its stray inbound-to-outbound connector and duplicate user-to-inbound connector are documented corrections: both receipt headers relate to `Users` through `UserId`; no receipt-to-receipt relationship exists. Preserve the user's original drawing unless asked to edit it.
- The user additionally requires `CreatedAt` and `UpdatedAt` on every business table: 18 audit columns supplement the original 43 fields, for 61 user-defined columns. Every entity implements `IAuditableEntity` without a common EF-mapped base hierarchy. Keep the original drawing and initial migration unchanged; use a new migration for audit columns.
- Audit timestamps use required UTC `DateTimeOffset` values mapped to PostgreSQL `timestamp with time zone`. `WmsDbContext.SaveChanges`/`SaveChangesAsync` sets both timestamps to the same current UTC instant for added rows; updates preserve the original `CreatedAt` and refresh `UpdatedAt`. `CURRENT_TIMESTAMP` defaults support migration backfill and inserts that omit the fields. Direct SQL and `ExecuteUpdate` bypass this auditing and must manage `UpdatedAt` explicitly.
- Cover products, warehouses, receipt headers/lines, issue headers/lines, users, roles, and current stock. Preserve every required field listed in the requirements document, including the issue recipient, document dates, creator, notes, and active/discontinued status.
- Enforce unique product codes, warehouse codes, and user emails in the database. Define email/code normalization and document-number uniqueness rules explicitly.
- Design a fast current-stock lookup for each warehouse/product pair. For a stock-balance table, enforce a unique warehouse/product pair and a database constraint preventing negative quantities. The relational database is the stock source of truth; Redis session storage must not become the authoritative inventory store.
- Quantities use `decimal` mapped to `numeric(18,3)`; movement details require positive quantities and stock balances require nonnegative quantities. Duplicate product lines in one receipt are rejected by unique `(receiptId, ProductId)` indexes. Future input validation must reject excess precision rather than rely on PostgreSQL rounding. Physical units remain a business decision.
- IDs are client-generated GUIDs. Receipt timestamps use `DateTimeOffset` and must have UTC offset zero before persistence. Descriptions and notes are optional; all other ERD scalar fields are required.
- Reference navigations may be nullable when not loaded; configure their relationships as required through the nonnullable foreign keys. Keep collection navigations initialized. Do not introduce nullability suppressions to hide an unloaded navigation.
- PostgreSQL unique indexes currently enforce exact stored values. Future use cases must trim/canonicalize email, SKU, warehouse code, and receipt code consistently before persistence; do not claim case-insensitive email uniqueness is already implemented.
- Stock uses a shadow `uint Version` mapped to PostgreSQL's implicit `xmin` concurrency token. This model configuration is not a complete concurrency strategy: implement transaction boundaries, conflict handling, and full-operation retries with stock use cases.
- Use EF Core migrations and reproducible seed data. Preserve historical document relationships. Derive the creator from the authenticated user rather than trusting an actor supplied in the request body.
- Development mock data are created only by explicitly running Api with `--seed-mock-data` in `Development`, after migrations are applied. Use `MockData:AdminPassword` and `MockData:StaffPassword` from User Secrets/environment configuration, and ASP.NET Core Identity's password hasher; never commit plaintext credentials. The fixture has two roles, exactly one Admin and three Staff (one inactive), ten products (two inactive), three warehouses (one inactive), three inbound documents with twelve lines, two outbound documents with six lines, and ten stock pairs derived from the document ledger.
- Preserve the seeder's safety contract: serialize seeders with a transaction-scoped advisory lock, exclude concurrent table writes during validation, accept an empty database or canonical Admin/Staff roles only, and skip a complete matching fixture on rerun without resetting passwords, timestamps, or existing edits. Refuse partial, conflicting, or unrelated data; never clear existing data to make the seed succeed. A complete fixture is recognized using counts, keys, and identifying values rather than by overwriting rows. Mock seeding does not implement application business workflows or login.

## Stock integrity and concurrency

- A receipt or issue may contain multiple lines. Validate all lines and commit its header, details, and every stock change as one database transaction. Any invalid line or failed write must roll back the complete document and all associated stock changes.
- Never permit negative stock. Reject an issue if any requested quantity exceeds the available stock for its warehouse/product pair.
- An adjustment sets the resulting stock to the physically counted quantity. It is an absolute count, not an increment or decrement.
- Reject receipts and issues involving a discontinued product or inactive warehouse. Validate these conditions as part of the operation; do not rely on values previously returned by list endpoints.
- Guarantee correct stock under concurrent receipts, issues, and adjustments across application instances. Choose and document database-enforced concurrency control, such as conditional atomic updates, optimistic concurrency with complete-operation retry, or suitable transaction isolation/locking. An unprotected read-check-write sequence or a process-local lock is insufficient.
- Cover concurrent creation of the first stock row and duplicate product lines. Reject or aggregate duplicate lines consistently before checking stock so split lines cannot bypass the quantity check.
- Keep multi-product lock/update order consistent where relevant. Bound retries, handle conflicts explicitly, and retry the whole transaction without duplicating documents or partial changes.
- Document how concurrent adjustments and movements are ordered. A transaction alone does not prove that stock validation and updates are safe; verify the chosen provider's behavior with real concurrent requests. See the official EF Core guidance on [transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions) and [concurrency](https://learn.microsoft.com/en-us/ef/core/saving/concurrency).

## Authentication, sessions, and account state

- Seed exactly one Admin account. Do not expose Admin creation through the API. Admin creates Staff accounts with initial passwords; do not accept arbitrary role assignment from those requests.
- Store secure password hashes using an established password hasher. Supply initial Admin credentials through appropriate configuration/secret storage; make seeding idempotent and keep plaintext credentials out of source code and logs.
- On successful login, issue a JWT tied to a unique session identifier and store the user's current session in Redis. Atomically replace the previous session so only the new session is accepted, including under concurrent logins.
- Limit each session to an absolute maximum of eight hours. Align JWT expiry, Redis expiry, and lifetime-validation settings with that bound; no sliding extension beyond it. Redis supports setting a value together with its [expiration](https://redis.io/docs/latest/commands/set/).
- On every protected request, validate JWT signature, issuer, audience, and lifetime; match its session identifier to the current unexpired Redis session; and enforce the user's current active status. A signed JWT alone is insufficient. Integrate session/account checks into the centralized authentication flow, for example after [token validation](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.authentication.jwtbearer.jwtbearerevents.ontokenvalidated).
- Logout revokes the matching session. Use an atomic compare-and-delete or equivalent so logout from an old session cannot remove a newer login.
- A banned Staff account cannot log in or perform any protected operation with an existing token. Persist the ban and revoke its session; enforce the committed inactive state on subsequent requests even if Redis invalidation fails. Coordinate login and ban handling so races cannot restore access.
- Unbanning allows a fresh login; it must not resurrect a previously revoked session. Treat this and session comparison during logout as repository safeguards derived from the assignment's revocation requirements.
- Fail closed when session/account validation cannot be completed. Distinguish unavailable infrastructure from invalid credentials in centralized error handling; do not bypass Redis checks during outages.

## Authorization

| Capability | Staff | Admin |
| --- | --- | --- |
| List products, list warehouses, look up stock | Allowed | Allowed |
| Create receipts and issues | Allowed | Allowed |
| Create products and warehouses | Denied | Allowed |
| Adjust stock to a physical count | Denied | Allowed |
| Create, ban, and unban Staff accounts | Denied | Allowed |

- Active Admin and Staff accounts may log in and log out. Admin inherits all Staff permissions.
- Staff may work across all warehouses. Do not add per-warehouse ownership restrictions.
- Staff must be denied every other business function. Apply authorization policies centrally and enforce them on the server; Swagger visibility or UI restrictions do not provide authorization.
- Require authentication and a valid session for every business endpoint. Explicitly allow anonymous access only where intended, including login and minimal public health/status endpoints.

## API conventions

- Use `ProblemDetails` for HTTP errors and handle exceptions centrally. Do not expose stack traces or internal details to clients in production.
- Distinguish input validation failures, business errors, and infrastructure failures; map them to appropriate HTTP status codes as those cases are implemented.
- Validate request bodies, identifiers, references, quantities, and collection contents before applying changes. Return consistent HTTP responses for validation, authentication, authorization, missing resources, business conflicts, and infrastructure failures.
- Use structured logging with request/trace identifiers and enough operation context to investigate failed stock changes and authentication events. Never log passwords, raw bearer tokens, or secrets.
- Integrate Swagger with a bearer security scheme so authorized requests can be tested directly. Supply a Postman Collection with variables for the base URL and tokens, covering all required capabilities without embedding credentials or tokens.
- Keep Swagger UI and OpenAPI middleware limited to Development. Document implemented endpoints only. Add operation security requirements from authorization metadata, respecting anonymous endpoints; do not mark public status endpoints as requiring authentication. The Swagger bearer-token input does not implement JWT validation, login, or Redis sessions. Do not persist authorization tokens in the browser.
- Keep public health/status responses minimal. Do not expose configuration, credentials, or infrastructure details.
- When adding external dependencies, distinguish liveness from readiness if the system needs both. Do not claim to check database or external service connectivity unless those checks are implemented.

## Build and verification

Run from the repository root using the SDK selected by `global.json`:

```powershell
dotnet restore MiniWms.sln
dotnet build MiniWms.sln -c Release --no-restore
```

- Keep the build passing and resolve warnings introduced by changes. Do not disable analyzers or warnings across the repository to hide issues.
- `MiniWms.Infrastructure.Tests` verifies the model/generated PostgreSQL SQL, deterministic auditing through suppressed sync/async writes, and development fixture integrity/password hashing without opening a database connection. Run it with `dotnet test MiniWms.sln -c Release --no-build` after restore/build. These checks do not satisfy the assignment's business-flow integration tests or prove runtime concurrency correctness.
- Add appropriate test projects under `tests/`: unit tests for Domain rules and service/Application use cases, integration tests for persistence/API behavior, and architecture checks for dependency direction. Isolate unit-tested service logic from the data layer using test doubles for Application abstractions.
- Run the relevant tests after restoring and building. Update the README as service and integration test projects are introduced.
- For small changes, focused checks may run first. Before handing off code changes, run appropriate checks; broaden verification only when failures or unresolved risks justify it.
- Report the commands actually run and their results accurately. If verification is blocked by a missing SDK, dependency, or service, state the limitation; do not report a pass.

Required feature verification:

- At least one complete goods-issue integration flow with assertions on the actual persisted issue header/lines and final warehouse/product stock. This repository guidance uses a real instance of the selected relational provider; it is not a reconstruction of the missing assignment sentence.
- Test complete rollback when one line of a multi-line document is invalid, with no document or stock changes left behind.
- Test competing issues against insufficient combined stock, concurrent receipts/issues, and first stock-row creation. Assert final stock and persisted documents, not only HTTP status codes.
- Test absolute-count adjustments, inactive products/warehouses, and quantity validation.
- Test login replacement, logout, the eight-hour bound, immediate denial after a ban, fresh login after unban, and all Staff/Admin permission boundaries. Verify the relevant integration paths against real Redis.
- Use isolated test databases and reproducible fixtures. Do not use EF Core InMemory or a different database engine as proof of transaction, constraint, or concurrency correctness; see [EF Core testing guidance](https://learn.microsoft.com/en-us/ef/core/testing/choosing-a-testing-strategy).

## Documentation and handoff

- Update the README when setup, run, configuration, or testing instructions change.
- Keep the requirements, ERD/data dictionary, migrations, Swagger, and Postman Collection consistent with implemented behavior. Clearly label proposed artifacts and pending features.
- When handing off work, summarize what changed, why, verification results, and remaining limitations. Include specific file paths when they help readers inspect the result.
- Routine reading, editing, and verification within the requested scope may proceed directly. Do not introduce extra confirmation steps for these actions; follow environment restrictions and tool approval requirements when they arise.
