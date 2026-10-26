namespace MiniWms.Domain.Inventory;

public static class StockRules
{
    public const decimal MaximumQuantity = 999999999999999.999m;

    public static bool IsValidQuantity(decimal quantity, bool allowZero) =>
        quantity >= (allowZero ? 0 : 0.001m) &&
        quantity <= MaximumQuantity && decimal.Round(quantity, 3) == quantity;

    public static decimal Receive(decimal current, decimal quantity)
    {
        if (!IsValidQuantity(current, true) || !IsValidQuantity(quantity, false) ||
            quantity > MaximumQuantity - current)
        {
            throw new StockRuleException("The resulting stock exceeds the supported quantity range.");
        }
        return current + quantity;
    }

    public static decimal Issue(decimal current, decimal quantity)
    {
        if (!IsValidQuantity(current, true) || !IsValidQuantity(quantity, false) || quantity > current)
        {
            throw new StockRuleException("The requested issue exceeds available stock.");
        }
        return current - quantity;
    }
}

public sealed class StockRuleException(string message) : Exception(message);
