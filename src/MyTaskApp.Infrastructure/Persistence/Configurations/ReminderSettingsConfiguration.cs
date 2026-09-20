using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class ReminderSettingsConfiguration : IEntityTypeConfiguration<ReminderSettingsRow>
{
    public void Configure(EntityTypeBuilder<ReminderSettingsRow> builder)
    {
        // Linha única garantida pelo banco: preferência de usuário duplicada
        // seria uma dessas que só aparece quando já está errada há meses.
        builder.ToTable(
            "ReminderSettings",
            table => table.HasCheckConstraint("CK_ReminderSettings_SingleRow", "Id = 1"));

        builder.HasKey(row => row.Id);

        builder.Property(row => row.Id).ValueGeneratedNever();

        builder.Property(row => row.Anchor).HasConversion<int>();

        builder.Property(row => row.Offset).HasConversion(TimeSpanTicksConverter.Instance);

        builder.Property(row => row.RepeatEvery).HasConversion(TimeSpanTicksConverter.Instance);

        builder.Property(row => row.Channels).HasConversion<int>();

        builder.Property(row => row.PausedUntilUtc).HasConversion(UtcInstantConverter.Instance);
    }
}
