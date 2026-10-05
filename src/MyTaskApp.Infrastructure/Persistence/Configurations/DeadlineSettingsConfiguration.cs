using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class DeadlineSettingsConfiguration : IEntityTypeConfiguration<DeadlineSettingsRow>
{
    public void Configure(EntityTypeBuilder<DeadlineSettingsRow> builder)
    {
        // Linha única garantida pelo banco, como a dos lembretes.
        builder.ToTable(
            "DeadlineSettings",
            table => table.HasCheckConstraint("CK_DeadlineSettings_SingleRow", "Id = 1"));

        builder.HasKey(row => row.Id);

        builder.Property(row => row.Id).ValueGeneratedNever();

        builder.Property(row => row.Stages).HasConversion<int>();

        builder.Property(row => row.OverdueRepeatEvery)
            .HasColumnName("OverdueRepeatEveryTicks")
            .HasConversion(TimeSpanTicksConverter.Instance);
    }
}
