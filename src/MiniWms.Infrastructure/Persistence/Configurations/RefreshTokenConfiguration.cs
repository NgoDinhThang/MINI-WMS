using MiniWms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MiniWms.Infrastructure.Persistence.Configurations;

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens", table =>
            table.HasCheckConstraint("CK_RefreshTokens_SecretHash_Format", "\"SecretHash\" ~ '^[0-9A-F]{64}$'"));
        builder.HasKey(token => token.RefreshTokenId);
        builder.Property(token => token.RefreshTokenId).ValueGeneratedNever();
        builder.Property(token => token.SecretHash).HasMaxLength(64).IsRequired();
        builder.Property(token => token.ExpiresAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(token => token.ConsumedAt).HasColumnType("timestamp with time zone");
        builder.HasIndex(token => token.SecretHash).IsUnique();
        builder.HasOne(token => token.Session).WithMany(session => session.RefreshTokens)
            .HasForeignKey(token => token.SessionId).IsRequired().OnDelete(DeleteBehavior.Restrict);
    }
}
