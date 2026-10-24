using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class RemoteSettingsConfiguration : IEntityTypeConfiguration<RemoteSettingsRow>
{
    public void Configure(EntityTypeBuilder<RemoteSettingsRow> builder)
    {
        // Linha única garantida pelo banco, como as do MCP: duas linhas seriam
        // duas respostas para "este computador aceita ser controlado?".
        builder.ToTable(
            "RemoteSettings",
            table => table.HasCheckConstraint("CK_RemoteSettings_SingleRow", "Id = 1"));

        builder.HasKey(row => row.Id);

        builder.Property(row => row.Id).ValueGeneratedNever();
    }
}
