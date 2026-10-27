# Warehouse business API

All business routes require a current JWT/Redis/account session. Admin can use every route; Staff can list products/warehouses, look up stock, and create inbound/outbound receipts. Existing login, refresh, logout, ban, and unban behavior is described in [AUTHENTICATION.md](AUTHENTICATION.md).

## Routes

| Method | Route | Role | Successful response |
| --- | --- | --- | --- |
| GET | /products | Admin, Staff | 200 paginated products |
| POST | /products | Admin | 201 product |
| GET | /warehouses | Admin, Staff | 200 paginated warehouses |
| POST | /warehouses | Admin | 201 warehouse |
| GET | /inventory | Admin, Staff | 200 paginated stock |
| POST | /inbound-receipts | Admin, Staff | 201 receipt and lines |
| POST | /outbound-receipts | Admin, Staff | 201 receipt and lines |
| POST | /inventory/adjustments | Admin | 200 previous and resulting count |
| POST | /staff | Admin | 201 account details without credentials |
| POST | /staff/{userId}/ban | Admin | 204 |
| POST | /staff/{userId}/unban | Admin | 204 |

Lists accept page (default 1) and pageSize (default 50, maximum 200). Products/warehouses accept search (case-insensitive literal substring of code/name) and isActive. Inventory accepts productId and/or warehouseId; a missing referenced ID returns 404. With both IDs present, an existing pair without a balance returns quantity zero. Broader queries list recorded pairs, including recorded zero balances, rather than a Cartesian product of all products and warehouses. List responses contain items, total, page, and pageSize. There are no receipt list/detail endpoints in this scope; receipt creation returns its complete document representation.

## Request examples

Create a product (Admin):

```json
{ "sku": "SKU-NEW-01", "name": "New product", "description": "Optional description", "isActive": true }
```

Create a warehouse (Admin):

```json
{ "code": "WH-NEW-01", "name": "New warehouse", "address": "Warehouse address", "isActive": true }
```

Create inbound goods (Admin or Staff):

```json
{
  "receiptCode": "IN-NEW-01",
  "warehouseId": "<warehouseId>",
  "receivedAt": "2026-10-27T08:00:00Z",
  "note": "Optional note",
  "lines": [
    { "productId": "<productId>", "quantity": 10.500 }
  ]
}
```

Create outbound goods (Admin or Staff):

```json
{
  "receiptCode": "OUT-NEW-01",
  "warehouseId": "<warehouseId>",
  "recipientName": "Customer or organization",
  "issuedAt": "2026-10-27T09:00:00Z",
  "note": "Optional note",
  "lines": [
    { "productId": "<productId>", "quantity": 3.250 }
  ]
}
```

The document response uses receiptId, receiptCode, warehouseId, userId, documentDate, recipientName (null for inbound), note, and lines. The creator comes exclusively from the authenticated account; body userId/role fields cannot assign the creator or account role.

Adjust a physical count (Admin):

```json
{ "warehouseId": "<warehouseId>", "productId": "<productId>", "quantity": 5 }
```

This sets the balance to 5 regardless of its previous quantity, and returns previousQuantity and quantity. Zero is allowed. An adjustment can create the first balance. This implementation requires active products and warehouses for adjustments as well as movements.

Create Staff (Admin):

```json
{ "email": "<staff-email>", "fullName": "New Staff", "password": "<initial-password>" }
```

Provide a nonblank initial password of 12 to 128 characters. Email is trimmed/lowercased, name is trimmed, active status is true, and role is always Staff. ASP.NET Core Identity stores a salted password hash; the creation response omits the password/hash. Admin creation, password changes, and password-reset endpoints are outside this scope.

## Validation and uniqueness

Codes are trimmed and invariant-uppercase ASCII letters, digits, dots, underscores, or hyphens, maximum 64 characters. Names are required, trimmed, maximum 200; warehouse address maximum 500; optional description/note maximum 2000. Optional multiline descriptions/notes permit newline/tab characters. SKU, warehouse code, and user email are unique. Creation also checks preexisting case variants, while database unique constraints arbitrate concurrent canonical API writes. Direct SQL/import writers must use the same normalization; the schema's indexes remain exact-value indexes.

Receipt codes are unique within each document type, across all warehouses. The same code may be used once for inbound and once for outbound. Dates are required and must have UTC offset zero. Documents contain 1 to 100 nonnull lines with distinct product IDs. Movement quantities must be greater than zero, exactly representable with at most 3 fractional digits, and no larger than 999999999999999.999. Resulting stock must remain within that range. References are checked again inside the write transaction; inactive warehouses and discontinued products cause a conflict.

Errors are ProblemDetails with traceId: invalid input 400, invalid credentials/session 401, insufficient role 403, missing product/warehouse 404, insufficient stock/inactive reference/duplicate code/concurrency conflict 409, unavailable dependencies 503.

## Atomicity and concurrency

PostgresWarehouseStore starts a read-committed PostgreSQL transaction. It locks the selected Warehouses row FOR UPDATE before reading stock, then locks referenced product rows FOR SHARE and existing stock rows FOR UPDATE in product-ID order. Every API receipt, issue, and adjustment follows this protocol, including first-balance creation.

The warehouse lock serializes writes within one warehouse across API instances, including operations targeting different products. Different warehouses can proceed concurrently. Shared product locks prevent those products from becoming inactive during a movement without serializing ordinary movements in other warehouses. Header, details, and all balance changes commit together; exceptions and request cancellation roll back the transaction. Stock xmin checking and database quantity/uniqueness constraints remain additional safeguards.

Lock waits are bounded by five seconds. Lock timeout, deadlock, serialization failure, or xmin conflict returns 409 after rollback; there is no internal partial-operation retry. The caller may retry the entire request after resolving the conflict. Duplicate document codes return 409 and do not apply stock again. Creation is not a general idempotency-key protocol: a network failure after commit can leave an uncertain client outcome.

Concurrent counts and movements are ordered by acquisition of the same warehouse lock. A movement before a count is included in the count's previousQuantity and then overwritten by that absolute count; a movement after the count adds/subtracts from the counted value. External stock writers must honor this locking/validation protocol. See [PostgreSQL row locks](https://www.postgresql.org/docs/18/explicit-locking.html) and [EF Core transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions).

## Review and verification

Swagger documents all routes and bearer security. The Postman folder **Business flow - run in order** logs in Admin, creates a product/warehouse/Staff, logs in the new Staff, receives/issues goods, checks balances and oversell rejection, adjusts stock as Admin, and checks ban/unban. Set the existing Admin email/password variables before running the folder. An empty newStaffPassword is filled with a generated per-run value in Postman's local variables. The folder intentionally creates persistent business data and changes the newly created Staff's state; it leaves that account unbanned. Other security-test folders can invalidate previously captured tokens, so run this folder from its first request.

The real-provider test fixture creates its own guarded disposable PostgreSQL database, applies migrations, uses a unique Redis prefix, and cleans up only those resources. Tests exercise complete receipt/issue persistence, rollback on a failing later line, missing/inactive references, precision/duplicate-line validation, cross-host first inserts and oversell, count/movement ordering, normalization/uniqueness, permissions, account creation/ban/unban, filters, and Swagger metadata. Run scripts/Test-AuthIntegration.ps1 to execute the fixture with configured real services; despite its historical name it includes warehouse tests.

No schema migration is needed for these endpoints. The existing eleven tables and development fixture are preserved. Adjustments update the current balance/audit timestamp and emit a structured log with actor, pair, old/new counts, and trace ID; there is no durable adjustment-history table, so inbound-minus-outbound history alone cannot reconstruct stock after physical counts. Browser cookie/CSRF integration, deployment hardening, and a durable adjustment ledger remain outside this implementation.

The comprehensive Moq/real-provider suite and coverage commands are documented in [TESTING.md](TESTING.md). Tests now exercise product-only, warehouse-only, and unfiltered inventory queries against PostgreSQL; ordering/pagination precede the final DTO projection to keep these queries translatable.
