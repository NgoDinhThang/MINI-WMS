# MiniWms.Domain

Contains Mini WMS entities, value objects, and business rules for stock receipts, issues, and physical-count adjustments.
Does not reference other projects, ASP.NET Core, or persistence libraries.

`Entities/` contains the nine models from `mini_wms_erd.drawio`, including foreign-key properties and initialized collection navigations. Reference navigations are nullable while unloaded; their database relationships remain required. Follow the root `AGENTS.md` and `docs/DATABASE_DESIGN.md`. Inventory/StockRules.cs defines exact quantity precision/capacity checks, receipt addition, and issue subtraction with insufficient-stock rejection. Physical counts are validated and set as absolute values by the Application use case.

Each entity implements `Common/IAuditableEntity` and exposes `DateTimeOffset CreatedAt` and `UpdatedAt`. These eighteen audit properties supplement the original forty-three business fields. The interface has no dependency on EF Core, and the concrete entities remain separate mapped types. Infrastructure stamps both fields in UTC on insertion and preserves `CreatedAt` while refreshing `UpdatedAt` on updates through `WmsDbContext`.
