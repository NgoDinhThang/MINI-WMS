using MiniWms.Domain.Entities;

namespace MiniWms.Infrastructure.Persistence.Seeding;

public sealed record MockDataCounts(
    int Roles,
    int Users,
    int Products,
    int Warehouses,
    int InboundReceipts,
    int InboundReceiptDetails,
    int OutboundReceipts,
    int OutboundReceiptDetails,
    int StockBalances);

public sealed record MockDataSeedResult(bool IsAlreadySeeded, MockDataCounts Counts);

public sealed record MockDataSet(
    IReadOnlyList<Role> Roles,
    IReadOnlyList<User> Users,
    IReadOnlyList<Product> Products,
    IReadOnlyList<Warehouse> Warehouses,
    IReadOnlyList<InboundReceipt> InboundReceipts,
    IReadOnlyList<InboundReceiptDetail> InboundReceiptDetails,
    IReadOnlyList<OutboundReceipt> OutboundReceipts,
    IReadOnlyList<OutboundReceiptDetail> OutboundReceiptDetails,
    IReadOnlyList<StockBalance> StockBalances)
{
    public MockDataCounts Counts => new(
        Roles.Count,
        Users.Count,
        Products.Count,
        Warehouses.Count,
        InboundReceipts.Count,
        InboundReceiptDetails.Count,
        OutboundReceipts.Count,
        OutboundReceiptDetails.Count,
        StockBalances.Count);
}
