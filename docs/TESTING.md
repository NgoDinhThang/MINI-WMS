# Testing and coverage

The suite uses NUnit 4 and Moq 4.20.72. Coverlet 6.0.4 collects line, branch, and method coverage; the pinned ReportGenerator tool builds browsable HTML reports.

## Verified results

The 2026-10-27 Release verification passed the enforced 100% gate with no failed or skipped tests.

| Phase | Tests passed |
| --- | ---: |
| Application unit | 203 |
| Infrastructure unit | 149 |
| API unit | 62 |
| API integration with real PostgreSQL/Redis | 80 |
| Total | 494 |

| Source assembly | Lines | Branches | Methods |
| --- | ---: | ---: | ---: |
| MiniWms.Domain | 100% | 100% | 100% |
| MiniWms.Application | 100% | 100% | 100% |
| MiniWms.Infrastructure | 100% | 100% | 100% |
| MiniWms.Api | 100% | 100% | 100% |

The combined HTML report records 2,444 covered executable lines, 626 covered branches, and 345 covered methods, with no remaining uncovered items. These are ReportGenerator's merged counters. Unit-only and combined results are separate: Domain/Application already reach 100% using unit tests; full Infrastructure/API coverage also needs the real-provider integration suite. The overall unit-only report is not presented as 100%.

## Run unit tests without PostgreSQL or Redis

From the solution directory:

```powershell
dotnet restore MiniWms.sln
dotnet build MiniWms.sln -c Release --no-restore
dotnet test MiniWms.sln -c Release --no-build --filter "TestCategory=Unit"
```

The 414 Unit-category tests require no running database or Redis. They use real pure functions and model/configuration objects, with Moq at I/O and service boundaries.

## Run real-provider integration tests

```powershell
.\scripts\Test-AuthIntegration.ps1 -NoBuild -Filter "TestCategory=Integration"
```

The helper's historical name is retained; it includes authentication, inventory, seeding, and persistence tests. It reads local connection configuration without printing secrets. CI can provide MINIWMS_TEST_POSTGRES and MINIWMS_TEST_REDIS instead.

The fixture creates a guarded, disposable miniwms_auth_tests_<guid> PostgreSQL database, applies committed migrations, and uses its own Redis key prefix. It cleans up its own database/keys afterward. The PostgreSQL account needs permission to create/drop this test database. It does not clear or reseed the development mini_wms database.

## Enforce complete coverage

```powershell
dotnet tool restore
.\scripts\Test-Coverage.ps1
```

The script runs four ordered phases and merges their coverage. It checks fresh TRX/coverage results, rejects an empty selection or any failed/skipped test, and uses Coverlet's per-module minimum threshold of 100 for line, branch, and method coverage. Its exit code is nonzero if the requirement is not met. Test connection environment variables are restored afterward.

Options:

- -NoBuild uses existing matching binaries.
- -Configuration Debug selects Debug; Release is the default.
- -MeasureOnly produces measurements without enforcing the 100% threshold. It still requires all selected tests to pass without skips.

Open these local generated artifacts:

| Artifact | Location |
| --- | --- |
| Combined source coverage | coverage/report/index.html |
| Unit-only source coverage | coverage/unit-report/index.html |
| Machine-readable combined summary | coverage/report/Summary.json |
| Per-phase passed counts | coverage/test-results.json |
| Raw combined Coverlet JSON | coverage/Api.Integration/coverage.json |
| TRX for each phase | coverage/<phase>/coverage-tests.trx |

The coverage directory is ignored by Git. Reports can be regenerated from the committed test sources and pinned tools.

## Coverage scope

tests/Coverage.props includes the four MiniWms source assemblies, excluding test assemblies and external dependencies. All executable handwritten source is included, including EF configurations, migration Up/Down methods, the seeder, Program.cs, API endpoint handlers, validation, transaction cleanup, and error handling.

Only EF-generated *.Designer.cs files and *ModelSnapshot.cs are excluded by file pattern. Files in obj/bin are generated build output. Interfaces and comments without executable instructions are not executable coverage lines. No business service, API endpoint, repository, or failure-handling method is excluded or marked ExcludeFromCodeCoverage.

## What the tests assert

- Authentication: normalized input, password-work behavior, valid/invalid account roles, JWT/session claims, lifetime limits, single current session, rotation/replay, logout, ban/unban, cancellation, and dependency failures.
- Inventory: exact decimal boundaries, references and active state, duplicate lines, whole-document rollback, first-balance creation, cross-host concurrent receipts/issues, capacity overflow, count/movement ordering, filters, and pagination.
- API: request forwarding, nullable bodies, current-account context, claim validation, database role replacement, ProblemDetails, rate limits, liveness failures, Development/Production Swagger, and startup/seed guards.
- Persistence: query translation against PostgreSQL, transaction ownership, commit/rollback/dispose ordering and failures, conflict classification, audit fields, model relationships, reversible migration SQL, and seed refusal/idempotency.
- Secrets: mocks use test-only values or runtime-generated credentials. Logging assertions and endpoint contracts avoid exposing passwords, bearer tokens, and signing keys.

Strict Moq mocks and Verify/Times assertions check permitted calls, rejected calls, cancellation propagation, and ordering. PostgreSQL queries/concurrency are tested with the real provider rather than a mocked DbSet.

## Implementation changes made while adding tests

The new filter tests found an EF Core translation error when inventory was queried without both IDs. Ordering/pagination now happen on a server-translatable anonymous projection before the final StockDto projection. No client-side loading of the entire inventory was introduced.

Null business requests are normalized to the same invalid input defaults before validation, removing unreachable null-condition paths. The redundant refresh-session null guard is replaced by an accurate NotNullWhen annotation. Refresh decoding retains exact-length, GUID, alphabet, canonical encoding, hash, and memory-clearing checks; decode-error paths impossible after those format checks were removed.

Redis connection creation is an injectable factory so connection failures can be tested with Moq. Transaction implementation types are internal and visible only to the two relevant test assemblies. Authentication setup cleanup uses finally with explicit ownership transfer, preserving rollback/error behavior. Fixture stock projection is an internal helper whose negative-ledger guard is independently tested; the seeder still checks every business-table count.

These testability changes do not close the previously reviewed in-flight ban/receipt race. Revocation still takes effect at the authorization check; fencing an already authorized business commit against a later ban remains a separate behavior change documented in AUTHENTICATION.md.
