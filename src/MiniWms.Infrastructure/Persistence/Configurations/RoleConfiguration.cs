using MiniWms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MiniWms.Infrastructure.Persistence.Configurations;

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles");
        builder.HasKey(role => role.RoleId);
        builder.Property(role => role.RoleId).ValueGeneratedNever();
        builder.Property(role => role.Name).HasMaxLength(50).IsRequired();
        builder.Property(role => role.Description).HasMaxLength(2000);
        builder.HasIndex(role => role.Name).IsUnique();
    }
}
