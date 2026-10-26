using System.Net.Mail;
using MiniWms.Domain.Entities;
using MiniWms.Domain.Inventory;

namespace MiniWms.Application.Business;

public sealed class WarehouseService(IWarehouseStore store, IStaffPasswordHasher passwords) : IWarehouseService
{
    public Task<PageResult<ProductDto>> ListProductsAsync(string? search, bool? isActive, int page, int pageSize, CancellationToken ct)
    {
        ValidatePage(page, pageSize);
        return store.ListProductsAsync(OptionalText(search, "search", 200), isActive, page, pageSize, ct);
    }

    public Task<PageResult<WarehouseDto>> ListWarehousesAsync(string? search, bool? isActive, int page, int pageSize, CancellationToken ct)
    {
        ValidatePage(page, pageSize);
        return store.ListWarehousesAsync(OptionalText(search, "search", 200), isActive, page, pageSize, ct);
    }

    public Task<PageResult<StockDto>> ListStockAsync(Guid? productId, Guid? warehouseId, int page, int pageSize, CancellationToken ct)
    {
        ValidatePage(page, pageSize);
        if (productId.HasValue) { RequireId(productId.Value, "productId"); }
        if (warehouseId.HasValue) { RequireId(warehouseId.Value, "warehouseId"); }
        return store.ListStockAsync(productId, warehouseId, page, pageSize, ct);
    }

    public async Task<ProductDto> CreateProductAsync(CreateProductRequest? request, CancellationToken ct)
    {
        var product = new Product
        {
            SKU = Code(request?.SKU, "sku"), Name = Text(request?.Name, "name", 200),
            Description = OptionalText(request?.Description, "description", 2000), IsActive = request?.IsActive ?? true
        };
        await store.AddProductAsync(product, ct);
        return new(product.ProductId, product.SKU, product.Name, product.Description, product.IsActive);
    }

    public async Task<WarehouseDto> CreateWarehouseAsync(CreateWarehouseRequest? request, CancellationToken ct)
    {
        var warehouse = new Warehouse
        {
            Code = Code(request?.Code, "code"), Name = Text(request?.Name, "name", 200),
            Address = Text(request?.Address, "address", 500), IsActive = request?.IsActive ?? true
        };
        await store.AddWarehouseAsync(warehouse, ct);
        return new(warehouse.WarehouseId, warehouse.Code, warehouse.Name, warehouse.Address, warehouse.IsActive);
    }

    public async Task<StaffDto> CreateStaffAsync(CreateStaffRequest? request, CancellationToken ct)
    {
        var email = Text(request?.Email, "email", 254).ToLowerInvariant();
        if (!MailAddress.TryCreate(email, out var address) || !string.Equals(address.Address, email, StringComparison.Ordinal))
        {
            throw BusinessException.Invalid("email", "A valid email address is required.");
        }
        var name = Text(request?.FullName, "fullName", 200);
        var password = request?.Password;
        if (string.IsNullOrWhiteSpace(password) || password.Length is < 12 or > 128)
        {
            throw BusinessException.Invalid("password", "The initial password must contain 12 to 128 characters.");
        }
        var user = new User { Email = email, FullName = name, IsActive = true };
        user.PasswordHash = passwords.Hash(user, password);
        await store.AddStaffAsync(user, ct);
        return new(user.UserId, user.Email, user.FullName, "Staff", user.IsActive);
    }

    public async Task<ReceiptDto> CreateInboundAsync(CreateInboundRequest? request, Guid actorId, CancellationToken ct)
    {
        var input = ValidateMovement(request?.ReceiptCode, request?.WarehouseId ?? Guid.Empty,
            request?.ReceivedAt, request?.Lines, request?.Note, actorId);
        await using var transaction = await store.BeginStockChangeAsync(input.WarehouseId, input.Lines.Select(l => l.ProductId).ToArray(), ct);
        ValidateReferences(transaction, input.Lines);
        var receipt = new InboundReceipt
        {
            ReceiptCode = input.Code, WarehouseId = input.WarehouseId, UserId = actorId,
            ReceivedAt = input.Date, Note = input.Note
        };
        foreach (var line in input.Lines)
        {
            var balance = GetOrCreateBalance(transaction, input.WarehouseId, line.ProductId);
            balance.Quantity = ChangeQuantity(balance.Quantity, line.Quantity, inbound: true);
            receipt.Details.Add(new InboundReceiptDetail
            {
                InboundReceiptId = receipt.InboundReceiptId, ProductId = line.ProductId, Quantity = line.Quantity
            });
        }
        transaction.AddInbound(receipt);
        await transaction.CommitAsync(ct);
        return new(receipt.InboundReceiptId, receipt.ReceiptCode, receipt.WarehouseId, receipt.UserId,
            receipt.ReceivedAt, null, receipt.Note, input.Lines);
    }

    public async Task<ReceiptDto> CreateOutboundAsync(CreateOutboundRequest? request, Guid actorId, CancellationToken ct)
    {
        var input = ValidateMovement(request?.ReceiptCode, request?.WarehouseId ?? Guid.Empty,
            request?.IssuedAt, request?.Lines, request?.Note, actorId);
        var recipient = Text(request?.RecipientName, "recipientName", 200);
        await using var transaction = await store.BeginStockChangeAsync(input.WarehouseId, input.Lines.Select(l => l.ProductId).ToArray(), ct);
        ValidateReferences(transaction, input.Lines);
        // Calculate every resulting balance before changing any tracked quantities.
        var quantities = input.Lines.ToDictionary(l => l.ProductId, l =>
            ChangeQuantity(transaction.Balances.GetValueOrDefault(l.ProductId)?.Quantity ?? 0, l.Quantity, inbound: false));
        var receipt = new OutboundReceipt
        {
            ReceiptCode = input.Code, WarehouseId = input.WarehouseId, UserId = actorId,
            IssuedAt = input.Date, RecipientName = recipient, Note = input.Note
        };
        foreach (var line in input.Lines)
        {
            transaction.Balances[line.ProductId].Quantity = quantities[line.ProductId];
            receipt.Details.Add(new OutboundReceiptDetail
            {
                OutboundReceiptId = receipt.OutboundReceiptId, ProductId = line.ProductId, Quantity = line.Quantity
            });
        }
        transaction.AddOutbound(receipt);
        await transaction.CommitAsync(ct);
        return new(receipt.OutboundReceiptId, receipt.ReceiptCode, receipt.WarehouseId, receipt.UserId,
            receipt.IssuedAt, receipt.RecipientName, receipt.Note, input.Lines);
    }

    public async Task<AdjustmentDto> AdjustStockAsync(AdjustStockRequest? request, CancellationToken ct)
    {
        var warehouseId = request?.WarehouseId ?? Guid.Empty;
        var productId = request?.ProductId ?? Guid.Empty;
        RequireId(warehouseId, "warehouseId");
        RequireId(productId, "productId");
        if (request?.Quantity is not decimal quantity || !StockRules.IsValidQuantity(quantity, true))
        {
            throw BusinessException.Invalid("quantity", "A nonnegative quantity with at most 3 decimal places and 15 integer digits is required.");
        }
        await using var transaction = await store.BeginStockChangeAsync(warehouseId, [productId], ct);
        ValidateReferences(transaction, [new MovementLine(productId, 0)]);
        var balance = GetOrCreateBalance(transaction, warehouseId, productId);
        var previous = balance.Quantity;
        balance.Quantity = quantity;
        await transaction.CommitAsync(ct);
        return new(warehouseId, productId, previous, quantity);
    }

    private static MovementInput ValidateMovement(string? code, Guid warehouseId, DateTimeOffset? date,
        IReadOnlyList<MovementLine?>? lines, string? note, Guid actorId)
    {
        var normalizedCode = Code(code, "receiptCode");
        RequireId(warehouseId, "warehouseId");
        RequireId(actorId, "userId");
        if (date is null || date.Value == default || date.Value.Offset != TimeSpan.Zero)
        {
            throw BusinessException.Invalid("documentDate", "An explicit nondefault UTC date (Z or +00:00) is required.");
        }
        var normalizedNote = OptionalText(note, "note", 2000);
        if (lines is null || lines.Count is < 1 or > 100)
        {
            throw BusinessException.Invalid("lines", "A document must contain 1 to 100 product lines.");
        }
        var validated = new List<MovementLine>(lines.Count);
        var ids = new HashSet<Guid>();
        foreach (var line in lines)
        {
            if (line is null) { throw BusinessException.Invalid("lines", "Product lines cannot be null."); }
            RequireId(line.ProductId, "lines.productId");
            if (!ids.Add(line.ProductId)) { throw BusinessException.Invalid("lines", "A product may appear only once per document."); }
            if (!StockRules.IsValidQuantity(line.Quantity, false))
            {
                throw BusinessException.Invalid("lines.quantity", "Quantities must be positive, with at most 3 decimal places and 15 integer digits.");
            }
            validated.Add(line);
        }
        return new(normalizedCode, warehouseId, date.Value, normalizedNote, validated);
    }

    private static void ValidateReferences(IStockTransaction transaction, IReadOnlyList<MovementLine> lines)
    {
        if (transaction.Warehouse is not { } warehouse) { throw BusinessException.Missing("Warehouse"); }
        if (!warehouse.IsActive) { throw BusinessException.Conflict("The warehouse is inactive."); }
        foreach (var line in lines)
        {
            if (!transaction.Products.TryGetValue(line.ProductId, out var product)) { throw BusinessException.Missing("Product"); }
            if (!product.IsActive) { throw BusinessException.Conflict("The product is discontinued."); }
        }
    }

    private static StockBalance GetOrCreateBalance(IStockTransaction transaction, Guid warehouseId, Guid productId)
    {
        if (transaction.Balances.TryGetValue(productId, out var balance)) { return balance; }
        balance = new StockBalance { WarehouseId = warehouseId, ProductId = productId };
        transaction.AddBalance(balance);
        return balance;
    }

    private static decimal ChangeQuantity(decimal current, decimal quantity, bool inbound)
    {
        try { return inbound ? StockRules.Receive(current, quantity) : StockRules.Issue(current, quantity); }
        catch (StockRuleException exception) { throw new BusinessException(BusinessErrorKind.Conflict, exception.Message, exception); }
    }

    private static string Code(string? value, string field)
    {
        var code = Text(value, field, 64).ToUpperInvariant();
        if (code.Length > 64 || code.Any(c => !(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.')))
        {
            throw BusinessException.Invalid(field, "Codes allow only ASCII letters, digits, hyphens, underscores, and dots.");
        }
        return code;
    }

    private static string Text(string? value, string field, int maximum)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl))
        {
            throw BusinessException.Invalid(field, $"A value of 1 to {maximum} characters without control characters is required.");
        }
        return text;
    }

    private static string? OptionalText(string? value, string field, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) { return null; }
        var text = value.Trim();
        if (text.Length > maximum || text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
        {
            throw BusinessException.Invalid(field, $"The value must contain at most {maximum} characters.");
        }
        return text;
    }

    private static void RequireId(Guid id, string field)
    {
        if (id == Guid.Empty) { throw BusinessException.Invalid(field, "A nonempty identifier is required."); }
    }

    private static void ValidatePage(int page, int pageSize)
    {
        if (page < 1 || pageSize is < 1 or > 200 || page > int.MaxValue / 200)
        {
            throw BusinessException.Invalid("page", "Page must be positive and pageSize must be between 1 and 200.");
        }
    }

    private sealed record MovementInput(string Code, Guid WarehouseId, DateTimeOffset Date, string? Note, IReadOnlyList<MovementLine> Lines);
}
