using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class McpServerSettingsConfiguration
    : IEntityTypeConfiguration<McpServerSettingsRow>
{
    public void Configure(EntityTypeBuilder<McpServerSettingsRow> builder)
    {
        // Linha única garantida pelo banco, como em DataRetentionSettings: duas
        // configurações seriam duas respostas para "em que porta o MCP escuta".
        builder.ToTable(
            "McpServerSettings",
            table => table.HasCheckConstraint("CK_McpServerSettings_SingleRow", "Id = 1"));

        builder.HasKey(row => row.Id);

        builder.Property(row => row.Id).ValueGeneratedNever();
    }
}
