using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class DataRetentionSettingsConfiguration
    : IEntityTypeConfiguration<DataRetentionSettingsRow>
{
    public void Configure(EntityTypeBuilder<DataRetentionSettingsRow> builder)
    {
        // Linha única garantida pelo banco, como em ReminderSettings: duas
        // políticas de retenção seriam duas respostas para "quando apagar".
        builder.ToTable(
            "DataRetentionSettings",
            table => table.HasCheckConstraint("CK_DataRetentionSettings_SingleRow", "Id = 1"));

        builder.HasKey(row => row.Id);

        builder.Property(row => row.Id).ValueGeneratedNever();
    }
}
