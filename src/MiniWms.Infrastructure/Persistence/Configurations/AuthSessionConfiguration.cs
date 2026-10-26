using MiniWms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MiniWms.Infrastructure.Persistence.Configurations;

public sealed class AuthSessionConfiguration : IEntityTypeConfiguration<AuthSession>
{
    public void Configure(EntityTypeBuilder<AuthSession> builder)
    {
        builder.ToTable("AuthSessions", table =>
            table.HasCheckConstraint("CK_AuthSessions_AuthVersion_Positive", "\"AuthVersion\" > 0"));
        builder.HasKey(session => session.SessionId);
        builder.Property(session => session.SessionId).ValueGeneratedNever();
        builder.Property(session => session.AuthVersion).IsRequired();
        builder.Property(session => session.ExpiresAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(session => session.RevokedAt).HasColumnType("timestamp with time zone");
        builder.HasIndex(session => session.UserId).IsUnique().HasFilter("\"RevokedAt\" IS NULL");
        builder.HasIndex(session => new { session.UserId, session.AuthVersion }).IsUnique();
        builder.HasOne(session => session.User).WithMany(user => user.AuthSessions)
            .HasForeignKey(session => session.UserId).IsRequired().OnDelete(DeleteBehavior.Restrict);
    }
}
