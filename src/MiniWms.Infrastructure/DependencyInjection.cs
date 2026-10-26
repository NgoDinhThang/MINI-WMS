using MiniWms.Application.Business;
using MiniWms.Infrastructure.Business;
using MiniWms.Domain.Entities;
using MiniWms.Infrastructure.Persistence;
using MiniWms.Infrastructure.Persistence.Seeding;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MiniWms.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<WmsDbContext>(options => options.UseNpgsql(connectionString));
        services.TryAddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddScoped<MockDataSeeder>();
        services.AddScoped<IWarehouseStore, PostgresWarehouseStore>();
        services.AddScoped<IStaffPasswordHasher, StaffPasswordHasher>();
        services.AddScoped<IWarehouseService, WarehouseService>();

        return services;
    }
}
