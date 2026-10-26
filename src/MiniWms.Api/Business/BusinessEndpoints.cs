using Microsoft.AspNetCore.Mvc;
using MiniWms.Api.Authentication;
using MiniWms.Application.Business;

namespace MiniWms.Api.Business;

public static class BusinessEndpoints
{
    public static void MapBusinessEndpoints(this WebApplication app)
    {
        var products = app.MapGroup("/products").WithTags("Products");
        Document(products.MapGet("", async (IWarehouseService service, CancellationToken ct,
            string? search = null, bool? isActive = null, int page = 1, int pageSize = 50) =>
                TypedResults.Ok(await service.ListProductsAsync(search, isActive, page, pageSize, ct))),
            "ListProducts", "List products with optional search and active-status filters", false)
            .Produces<PageResult<ProductDto>>();

        Document(products.MapPost("", async (CreateProductRequest? request, IWarehouseService service, CancellationToken ct) =>
                TypedResults.Json(await service.CreateProductAsync(request, ct), statusCode: StatusCodes.Status201Created)),
            "CreateProduct", "Create a product (Admin only)", true).Produces<ProductDto>(201);

        var warehouses = app.MapGroup("/warehouses").WithTags("Warehouses");
        Document(warehouses.MapGet("", async (IWarehouseService service, CancellationToken ct,
            string? search = null, bool? isActive = null, int page = 1, int pageSize = 50) =>
                TypedResults.Ok(await service.ListWarehousesAsync(search, isActive, page, pageSize, ct))),
            "ListWarehouses", "List warehouses with optional search and active-status filters", false)
            .Produces<PageResult<WarehouseDto>>();

        Document(warehouses.MapPost("", async (CreateWarehouseRequest? request, IWarehouseService service, CancellationToken ct) =>
                TypedResults.Json(await service.CreateWarehouseAsync(request, ct), statusCode: StatusCodes.Status201Created)),
            "CreateWarehouse", "Create a warehouse (Admin only)", true).Produces<WarehouseDto>(201);

        Document(app.MapGet("/inventory", async (IWarehouseService service, CancellationToken ct,
            Guid? productId = null, Guid? warehouseId = null, int page = 1, int pageSize = 50) =>
                TypedResults.Ok(await service.ListStockAsync(productId, warehouseId, page, pageSize, ct))).WithTags("Inventory"),
            "LookupInventory", "Look up inventory by product and/or warehouse", false)
            .WithDescription("Lists recorded stock pairs. An exact existing product/warehouse pair returns quantity zero if no balance has been created.")
            .Produces<PageResult<StockDto>>();

        Document(app.MapPost("/inbound-receipts", async (
            CreateInboundRequest? request, IWarehouseService service, HttpContext context, ILogger<WarehouseService> logger, CancellationToken ct) =>
        {
            var receipt = await service.CreateInboundAsync(request,
                ApiAuthenticationRegistration.ReadSessionIdentity(context.User).UserId, ct);
            logger
                .LogInformation("Inbound receipt {ReceiptId} committed by {UserId} in warehouse {WarehouseId}. TraceId: {TraceId}",
                    receipt.ReceiptId, receipt.UserId, receipt.WarehouseId, context.TraceIdentifier);
            return TypedResults.Json(receipt, statusCode: StatusCodes.Status201Created);
        }).WithTags("Receipts"), "CreateInboundReceipt", "Receive goods atomically across all product lines", false)
            .Produces<ReceiptDto>(201);

        Document(app.MapPost("/outbound-receipts", async (
            CreateOutboundRequest? request, IWarehouseService service, HttpContext context, ILogger<WarehouseService> logger, CancellationToken ct) =>
        {
            var receipt = await service.CreateOutboundAsync(request,
                ApiAuthenticationRegistration.ReadSessionIdentity(context.User).UserId, ct);
            logger
                .LogInformation("Outbound receipt {ReceiptId} committed by {UserId} in warehouse {WarehouseId}. TraceId: {TraceId}",
                    receipt.ReceiptId, receipt.UserId, receipt.WarehouseId, context.TraceIdentifier);
            return TypedResults.Json(receipt, statusCode: StatusCodes.Status201Created);
        }).WithTags("Receipts"), "CreateOutboundReceipt", "Issue goods atomically without allowing negative stock", false)
            .Produces<ReceiptDto>(201);

        Document(app.MapPost("/inventory/adjustments", async (
            AdjustStockRequest? request, IWarehouseService service, HttpContext context, ILogger<WarehouseService> logger, CancellationToken ct) =>
        {
            var result = await service.AdjustStockAsync(request, ct);
            logger
                .LogInformation("Stock adjusted by {UserId} in warehouse {WarehouseId} for {ProductId}: {PreviousQuantity} to {Quantity}. TraceId: {TraceId}",
                    ApiAuthenticationRegistration.ReadSessionIdentity(context.User).UserId,
                    result.WarehouseId, result.ProductId, result.PreviousQuantity, result.Quantity, context.TraceIdentifier);
            return TypedResults.Ok(result);
        }).WithTags("Inventory"), "AdjustInventory", "Set stock to the physically counted quantity (Admin only)", true)
            .WithDescription("Sets an absolute nonnegative quantity. Does not add or subtract the supplied quantity.")
            .Produces<AdjustmentDto>();

        Document(app.MapPost("/staff", async (CreateStaffRequest? request, IWarehouseService service, CancellationToken ct) =>
                TypedResults.Json(await service.CreateStaffAsync(request, ct), statusCode: StatusCodes.Status201Created))
            .WithTags("Staff accounts"), "CreateStaff", "Create an active Staff account with an initial password (Admin only)", true)
            .WithDescription("Role is always Staff. Password must contain 12 to 128 characters and is stored only as an Identity password hash.")
            .Produces<StaffDto>(201);
    }

    private static RouteHandlerBuilder Document(RouteHandlerBuilder endpoint, string name, string summary, bool adminOnly) =>
        endpoint.WithName(name).WithSummary(summary)
            .RequireAuthorization(adminOnly ? ApiAuthenticationRegistration.AdminPolicy : ApiAuthenticationRegistration.StaffPolicy)
            .Produces<HttpValidationProblemDetails>(400, "application/problem+json")
            .Produces<ProblemDetails>(401, "application/problem+json")
            .Produces<ProblemDetails>(403, "application/problem+json")
            .Produces<ProblemDetails>(404, "application/problem+json")
            .Produces<ProblemDetails>(409, "application/problem+json")
            .Produces<ProblemDetails>(503, "application/problem+json");
}
