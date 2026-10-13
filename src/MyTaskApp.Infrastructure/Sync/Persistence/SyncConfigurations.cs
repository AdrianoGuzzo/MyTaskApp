using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Infrastructure.Persistence.Configurations;

namespace MyTaskApp.Infrastructure.Sync.Persistence;

internal sealed class SyncStateConfiguration : IEntityTypeConfiguration<SyncStateRow>
{
    public void Configure(EntityTypeBuilder<SyncStateRow> builder)
    {
        builder.ToTable("SyncState", table => table.HasCheckConstraint("CK_SyncState_SingleRow", "Id = 1"));

        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.Status).HasConversion<int>();
        builder.Property(row => row.ServerBaseUrl).HasMaxLength(2000);
        builder.Property(row => row.UserDisplayName).HasMaxLength(200);
        builder.Property(row => row.UserEmail).HasMaxLength(320);
        builder.Property(row => row.Cursor).HasMaxLength(200);
        builder.Property(row => row.LastErrorCode).HasMaxLength(100);
        builder.Property(row => row.LastError).HasMaxLength(2000);
        builder.Property(row => row.LinkedAt).HasConversion(UtcInstantConverter.Instance);
        builder.Property(row => row.LastPushAt).HasConversion(UtcInstantConverter.Instance);
        builder.Property(row => row.LastPullAt).HasConversion(UtcInstantConverter.Instance);
        builder.Property(row => row.LastSuccessAt).HasConversion(UtcInstantConverter.Instance);
        builder.Property(row => row.LastErrorAt).HasConversion(UtcInstantConverter.Instance);
    }
}

internal sealed class SyncEntityStateConfiguration : IEntityTypeConfiguration<SyncEntityStateRow>
{
    public void Configure(EntityTypeBuilder<SyncEntityStateRow> builder)
    {
        builder.ToTable("SyncEntityStates");

        builder.HasKey(row => new { row.EntityType, row.EntityId });
        builder.Property(row => row.EntityType).HasMaxLength(40);
        builder.Property(row => row.OriginCreatedAt).HasConversion(UtcInstantConverter.Instance);
        builder.Property(row => row.UpdatedAt).HasConversion(UtcInstantConverter.Instance);
        builder.Property(row => row.ApplyErrorCode).HasMaxLength(100);
        builder.Property(row => row.ApplyError).HasMaxLength(2000);

        // A lista de "não aplicados" na tela de sincronização.
        builder.HasIndex(row => row.ApplyErrorCode).HasFilter("\"ApplyErrorCode\" IS NOT NULL");
    }
}

internal sealed class SyncOutboxConfiguration : IEntityTypeConfiguration<SyncOutboxRow>
{
    public void Configure(EntityTypeBuilder<SyncOutboxRow> builder)
    {
        builder.ToTable("SyncOutbox");

        builder.HasKey(row => row.Seq);
        builder.Property(row => row.Seq).ValueGeneratedOnAdd();
        builder.HasIndex(row => row.OpId).IsUnique();
        builder.HasIndex(row => new { row.EntityType, row.EntityId });
        builder.Property(row => row.EntityType).HasMaxLength(40);
        builder.Property(row => row.Kind).HasConversion<int>();
        builder.Property(row => row.State).HasConversion<int>();
        builder.Property(row => row.OriginCreatedAt).HasConversion(UtcInstantConverter.Instance);
        builder.Property(row => row.CreatedAt).HasConversion(UtcInstantConverter.Instance);
        builder.Property(row => row.LastErrorCode).HasMaxLength(100);
        builder.Property(row => row.LastError).HasMaxLength(2000);
    }
}

internal sealed class SyncConflictConfiguration : IEntityTypeConfiguration<SyncConflictRow>
{
    public void Configure(EntityTypeBuilder<SyncConflictRow> builder)
    {
        builder.ToTable("SyncConflicts");

        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.EntityType).HasMaxLength(40);
        builder.Property(row => row.Field).HasMaxLength(100);
        builder.Property(row => row.Reason).HasMaxLength(100);
        builder.Property(row => row.EntityLabel).HasMaxLength(200);
        builder.Property(row => row.Resolution).HasMaxLength(40);
        builder.Property(row => row.DetectedAt).HasConversion(UtcInstantConverter.Instance);
        builder.Property(row => row.ResolvedAt).HasConversion(UtcInstantConverter.Instance);

        // Os abertos, na tela.
        builder.HasIndex(row => row.DetectedAt).HasFilter("\"ResolvedAt\" IS NULL");
    }
}
