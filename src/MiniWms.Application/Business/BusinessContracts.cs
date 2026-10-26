using MiniWms.Domain.Entities;

namespace MiniWms.Application.Business;

public sealed record PageResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
public sealed record ProductDto(Guid ProductId, string SKU, string Name, string? Description, bool IsActive);
public sealed record WarehouseDto(Guid WarehouseId, string Code, string Name, string Address, bool IsActive);
public sealed record StockDto(Guid WarehouseId, string WarehouseCode, Guid ProductId, string SKU, string ProductName, decimal Quantity);
public sealed record StaffDto(Guid UserId, string Email, string FullName, string Role, bool IsActive);
public sealed record MovementLine(Guid ProductId, decimal Quantity);
public sealed record ReceiptDto(Guid ReceiptId, string ReceiptCode, Guid WarehouseId, Guid UserId,
    DateTimeOffset DocumentDate, string? RecipientName, string? Note, IReadOnlyList<MovementLine> Lines);
public sealed record AdjustmentDto(Guid WarehouseId, Guid ProductId, decimal PreviousQuantity, decimal Quantity);
public sealed record CreateProductRequest(string? SKU, string? Name, string? Description = null, bool IsActive = true);
public sealed record CreateWarehouseRequest(string? Code, string? Name, string? Address, bool IsActive = true);
public sealed record CreateStaffRequest(string? Email, string? FullName, string? Password);
public sealed record CreateInboundRequest(string? ReceiptCode, Guid WarehouseId, DateTimeOffset? ReceivedAt,
    IReadOnlyList<MovementLine?>? Lines, string? Note = null);
public sealed record CreateOutboundRequest(string? ReceiptCode, Guid WarehouseId, string? RecipientName,
    DateTimeOffset? IssuedAt, IReadOnlyList<MovementLine?>? Lines, string? Note = null);
public sealed record AdjustStockRequest(Guid WarehouseId, Guid ProductId, decimal? Quantity);

public interface IWarehouseService
{
    Task<PageResult<ProductDto>> ListProductsAsync(string? search, bool? isActive, int page, int pageSize, CancellationToken ct);
    Task<PageResult<WarehouseDto>> ListWarehousesAsync(string? search, bool? isActive, int page, int pageSize, CancellationToken ct);
    Task<PageResult<StockDto>> ListStockAsync(Guid? productId, Guid? warehouseId, int page, int pageSize, CancellationToken ct);
    Task<ProductDto> CreateProductAsync(CreateProductRequest? request, CancellationToken ct);
    Task<WarehouseDto> CreateWarehouseAsync(CreateWarehouseRequest? request, CancellationToken ct);
    Task<StaffDto> CreateStaffAsync(CreateStaffRequest? request, CancellationToken ct);
    Task<ReceiptDto> CreateInboundAsync(CreateInboundRequest? request, Guid actorId, CancellationToken ct);
    Task<ReceiptDto> CreateOutboundAsync(CreateOutboundRequest? request, Guid actorId, CancellationToken ct);
    Task<AdjustmentDto> AdjustStockAsync(AdjustStockRequest? request, CancellationToken ct);
}

public interface IWarehouseStore
{
    Task<PageResult<ProductDto>> ListProductsAsync(string? search, bool? isActive, int page, int pageSize, CancellationToken ct);
    Task<PageResult<WarehouseDto>> ListWarehousesAsync(string? search, bool? isActive, int page, int pageSize, CancellationToken ct);
    Task<PageResult<StockDto>> ListStockAsync(Guid? productId, Guid? warehouseId, int page, int pageSize, CancellationToken ct);
    Task AddProductAsync(Product product, CancellationToken ct);
    Task AddWarehouseAsync(Warehouse warehouse, CancellationToken ct);
    Task AddStaffAsync(User user, CancellationToken ct);
    Task<IStockTransaction> BeginStockChangeAsync(Guid warehouseId, IReadOnlyCollection<Guid> productIds, CancellationToken ct);
}

public interface IStaffPasswordHasher
{
    string Hash(User user, string password);
}

/// <summary>Holds the warehouse lock through stock reads, document writes, and commit.</summary>
public interface IStockTransaction : IAsyncDisposable
{
    Warehouse? Warehouse { get; }
    IReadOnlyDictionary<Guid, Product> Products { get; }
    IReadOnlyDictionary<Guid, StockBalance> Balances { get; }
    void AddBalance(StockBalance balance);
    void AddInbound(InboundReceipt receipt);
    void AddOutbound(OutboundReceipt receipt);
    Task CommitAsync(CancellationToken ct);
}

public enum BusinessErrorKind { Validation, NotFound, Conflict, Infrastructure }

public sealed class BusinessException(
    BusinessErrorKind kind, string message, Exception? inner = null,
    IReadOnlyDictionary<string, string[]>? errors = null) : Exception(message, inner)
{
    public BusinessErrorKind Kind { get; } = kind;
    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;
    public static BusinessException Invalid(string field, string message) =>
        new(BusinessErrorKind.Validation, message, errors: new Dictionary<string, string[]> { [field] = [message] });
    public static BusinessException Missing(string resource) => new(BusinessErrorKind.NotFound, resource + " was not found.");
    public static BusinessException Conflict(string message) => new(BusinessErrorKind.Conflict, message);
}
