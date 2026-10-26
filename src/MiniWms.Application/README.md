# MiniWms.Application

References only Domain. `Authentication` contains login, refresh rotation, logout, centralized session validation, and Admin ban/unban orchestration plus the abstractions those use cases consume.

`AuthService` uses a per-account transaction abstraction, durable refresh-token history, Redis session comparison, a password verifier, a token issuer, and an injected clock. It has no EF Core, ASP.NET Core, Redis-client, or configuration dependency. `MiniWms.Application.Tests` exercises these rules using transactional store/session/clock doubles.

Business/WarehouseService.cs implements catalog and inventory queries, product/warehouse/Staff creation, inbound/outbound documents, and absolute physical counts through IWarehouseStore/IStockTransaction. Domain StockRules enforce quantity limits and sufficient inventory; EF Core and HTTP remain outside this layer. The service tests isolate persistence with doubles. See the root README and `docs/AUTHENTICATION.md` for implemented contracts and remaining limits.
