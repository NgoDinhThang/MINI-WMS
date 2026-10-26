# Mini WMS Project Requirements

## Purpose and status

The project is named MINI WMS (Mini Warehouse Management System), with `MiniWms` used for the solution, projects, and C# namespaces as requested by the user. Build a RESTful backend API for receiving goods, issuing goods, adjusting stock after physical counts, and looking up inventory.

This document records the assignment requirements supplied by the user. It defines intended behavior and deliverables; it does not assert that any feature has been implemented.

## Required technology and architecture

- ASP.NET Core on .NET 8.
- Entity Framework Core with either SQL Server or PostgreSQL.
- Redis for login session management.
- xUnit or NUnit for automated tests.
- Clean Architecture with dependency injection, clear responsibilities, and maintainable, extensible code.

Follow the dependency direction and implementation rules in the repository's root `AGENTS.md`. The assignment does not require both database providers or both test frameworks.

Subsequent project decisions: the user selected PostgreSQL and requested an ERD-guided EF model-first workflow using [mini_wms_erd.drawio](../mini_wms_erd.drawio). After the models were prepared, the user installed PostgreSQL and authorized database creation; the initial EF migration is now applied to the local `mini_wms` database. The repository uses NUnit for model tests. [Database design](DATABASE_DESIGN.md) records the concrete nine-table model and mapping decisions; these decisions do not change the original assignment's minimum requirements.

The user subsequently required `CreatedAt` and `UpdatedAt` on every business table and mock data in the local database. Preserve the original forty-three ERD business fields and add eighteen required UTC audit columns across the nine tables. Context-managed saves set both timestamps on insertion, preserve `CreatedAt` on updates, and refresh `UpdatedAt`. Add a new migration while preserving the original drawing and initial migration. Provide an explicit Development-only, reproducible seed command with one Admin, multiple Staff, products, warehouses, documents, and ledger-consistent stock; source account passwords from secret configuration and store only secure hashes. These are later user requirements, separate from the supplied assignment.

## Database design and deliverables

Design the database and provide a complete entity-relationship diagram (ERD). Choose table names, primary and foreign keys, data types, relationships, constraints, and indexes based on the business requirements.

The data model must store at least the following information:

| Business concept | Required information |
| --- | --- |
| Product | Unique product code, product name, description, and whether the product is still offered for business. |
| Warehouse | Unique warehouse code, warehouse name, address, and active status. |
| Goods receipt | Document code, receiving warehouse, receipt date, creator, and notes. |
| Goods receipt line | Product and received quantity, associated with its receipt. |
| Goods issue | Document code, issuing warehouse, recipient person or organization, issue date, creator, and notes. |
| Goods issue line | Product and issued quantity, associated with its issue. |
| User | Full name, unique email, securely stored password hash, role, and active status. Never store plaintext passwords. |
| Role | Role name and description. |
| Current inventory | Current quantity of each product in each warehouse, supporting fast lookups by product and warehouse. |

Choose how to store and maintain current inventory. Preserve referential integrity, prevent inconsistent stock data, and optimize inventory queries. The assignment requires a complete schema and ERD, but does not prescribe an inventory storage technique or database naming convention.

## Accounts, authentication, and sessions

- Provide a login API that returns a JWT bearer token.
- Maintain exactly one Admin account in the initial seed data. Do not expose an API to create Admin accounts.
- Support multiple Staff accounts. Only Admin may create Staff accounts and assign their initial passwords.
- Staff members may work in every warehouse; do not introduce per-warehouse Staff access restrictions.
- Each user may have only one valid device session at a time. A successful login on a new device invalidates the previous session.
- Use Redis to manage the current login session. Each session has a maximum lifetime of eight hours.
- Provide logout and invalidate the corresponding login session.
- A banned Staff account cannot log in or perform any operation, including when the account already has a JWT and an active session. Once the ban is committed, deny subsequent protected requests even when they use a token issued before the ban; do not rely solely on token expiration.
- Admin may unban Staff accounts.

JWT validation, session validity, and account status must collectively enforce these rules on protected endpoints. The assignment does not require public registration or additional roles.

## Required API capabilities and authorization

Login is available using valid credentials for an active account. All business endpoints require authentication and a valid session. The following table describes permissions for active, authenticated users; login is included to keep the required capability list complete.

| Capability | Staff | Admin |
| --- | --- | --- |
| Log in | Allowed with valid credentials | Allowed with valid credentials |
| Log out | Allowed | Allowed |
| List products | Allowed | Allowed |
| List warehouses | Allowed | Allowed |
| Look up inventory by product and/or warehouse | Allowed | Allowed |
| Create a goods receipt | Allowed | Allowed |
| Create a goods issue | Allowed | Allowed |
| Create a product | Denied | Allowed |
| Create a warehouse | Denied | Allowed |
| Adjust inventory after a physical count | Denied | Allowed |
| Create a Staff account with an initial password | Denied | Allowed |
| Ban a Staff account | Denied | Allowed |
| Unban a Staff account | Denied | Allowed |

Staff access is limited to receipt and issue operations and the product, warehouse, and inventory lookups needed for those operations, plus authentication and logout. Deny all other business functions to Staff. Admin has all Staff permissions and the additional permissions listed above.

## Inventory business rules

1. A receipt or issue can contain multiple products.
2. Validate the entire document. If any line is invalid, reject the entire request: create no document or document lines and apply no inventory changes.
3. Never allow negative inventory. In particular, reject an issue that requests more stock than is available for any product in its warehouse.
4. After an inventory adjustment, the product's quantity in the warehouse equals the quantity physically counted. Treat the supplied count as the resulting quantity, not as a quantity to add or subtract.
5. Do not receive or issue a discontinued product.
6. Do not receive or issue goods through an inactive warehouse.
7. Multiple Staff members may concurrently receive or issue the same product in the same warehouse. Inventory must remain accurate in every case, without overselling or lost updates.

Select and document a transaction and concurrency strategy that enforces atomic document creation and accurate stock updates. The assignment specifies the guarantees, not a particular locking, isolation, or concurrency-control mechanism.

## API quality and documentation

- Validate incoming data before applying changes.
- Handle errors centrally and return consistent HTTP errors using the repository's `ProblemDetails` conventions.
- Log relevant application operations and failures without exposing passwords, tokens, or other secrets.
- Integrate Swagger so users can inspect and exercise all required endpoints, including endpoints protected by JWT authentication.
- Supply a Postman Collection for exercising the API.

## Automated testing

- Unit tests must exercise business logic in the service/Application layer while isolating it from the data layer.
- Integration tests must include at least one complete goods-issue creation flow and verify the actual result. During implementation, verify the persisted document and stock outcome against the selected real database provider to substantiate this behavior.
- Follow the root `AGENTS.md` testing guidance for meaningful Domain, use-case, integration, and architecture coverage as features are introduced.

The supplied assignment text ends partway through its final integration-test sentence, immediately after the equivalent of "verify the actual result in ...". The remaining text was not provided. The complete goods-issue flow and actual-result verification above are the known requirement; do not claim additional missing acceptance criteria were supplied by the assignment.

## Decisions still open

Record implementation decisions when they become necessary. The original assignment leaves the following choices open; selected repository decisions are noted where available:

- SQL Server versus PostgreSQL: PostgreSQL selected by the user.
- xUnit versus NUnit: NUnit selected for the repository tests.
- Quantity units: still open; the model permits fractional quantities with `numeric(18,3)` precision.
- Repeated product lines: rejected by a unique product-per-document index.
- Schema and inventory storage: nine ERD tables, with a composite-key stock-balance table, documented in `DATABASE_DESIGN.md`.
- Runtime transaction strategy: read-committed transactions lock the warehouse row through stock validation and complete document commit; referenced products and balances are locked as described in BUSINESS_API.md. Concurrency/lock conflicts roll back and return 409 for a complete caller retry. Routes and contracts are implemented; xmin remains an additional concurrency safeguard.
- The missing end of the integration-test requirement.

These open decisions must not weaken the explicit requirements, including all-or-nothing documents, nonnegative and accurate stock, single-device sessions, or immediate enforcement of Staff bans. Implement and document decisions within the user's requested scope without presenting optional choices as assignment requirements.

## Subsequent authentication requirements and implementation

The user subsequently selected access tokens and refresh tokens for login. The implemented design uses short-lived JWT access tokens (default 15 minutes), rotating opaque refresh credentials, and a fixed session deadline no longer than eight hours. Refresh does not extend the deadline. PostgreSQL stores durable session/replay/revocation state and Redis stores the current accepted session. JSON API-client transport is implemented; browser cookie/CSRF integration remains separate.

Implemented routes are POST /auth/login, POST /auth/refresh, POST /auth/logout, GET /auth/me, and Admin-only POST /staff/{userId}/ban and /unban. Existing development accounts remain seeded with hashed configured passwords. Staff creation and warehouse business endpoints are now implemented; see BUSINESS_API.md for routes, validation, transaction guarantees, and limits. These decisions supplement the supplied assignment; they do not reconstruct its missing final sentence. See AUTHENTICATION.md for transaction, replay, and failure behavior.

## Implemented core business capabilities

The later request to implement all listed core endpoints is fulfilled by GET/POST /products, GET/POST /warehouses, GET /inventory, POST /inbound-receipts, POST /outbound-receipts, POST /inventory/adjustments, and POST /staff, together with the authentication and ban/unban routes above. Admin/Staff policies enforce the permissions table. The authenticated actor owns document creation; Staff cannot create Admin accounts.

Quantities remain numeric(18,3), codes are canonicalized, complete documents are atomic, physical counts set an absolute nonnegative balance, and active references are rechecked within the warehouse-lock transaction. Real-provider integration tests verify the complete goods-issue flow, database results, rollback, and concurrent stock writes across API instances. The original incomplete final integration-test sentence remains unreconstructed. See [BUSINESS_API.md](BUSINESS_API.md) for concrete decisions and the current adjustment-history limitation.
