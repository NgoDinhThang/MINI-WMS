using MiniWms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MiniWms.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users", table =>
            table.HasCheckConstraint("CK_Users_AuthVersion_Nonnegative", "\"AuthVersion\" >= 0"));
        builder.HasKey(user => user.UserId);
        builder.Property(user => user.UserId).ValueGeneratedNever();
        builder.Property(user => user.FullName).HasMaxLength(200).IsRequired();
        builder.Property(user => user.Email).HasMaxLength(254).IsRequired();
        builder.Property(user => user.PasswordHash).HasMaxLength(1024).IsRequired();
        builder.Property(user => user.IsActive).IsRequired();
        builder.Property(user => user.AuthVersion).HasDefaultValue(0L).IsConcurrencyToken();
        builder.HasIndex(user => user.Email).IsUnique();

        builder.HasOne(user => user.Role)
            .WithMany(role => role.Users)
            .HasForeignKey(user => user.RoleId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
    }
}
