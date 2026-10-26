namespace MiniWms.Infrastructure.Persistence.Seeding;

public sealed class MockDataOptions
{
    public string AdminPassword { get; init; } = string.Empty;

    public string StaffPassword { get; init; } = string.Empty;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(AdminPassword) || string.IsNullOrWhiteSpace(StaffPassword))
        {
            throw new InvalidOperationException(
                "Configure MockData:AdminPassword and MockData:StaffPassword using User Secrets " +
                "or environment variables before seeding development data.");
        }
    }
}
