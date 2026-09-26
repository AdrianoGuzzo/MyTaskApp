using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Application.Agents;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class AgentSettingsConfiguration : IEntityTypeConfiguration<AgentSettingsRow>
{
    public void Configure(EntityTypeBuilder<AgentSettingsRow> builder)
    {
        builder.ToTable("AgentSettings");

        builder.HasKey(row => row.ProviderId);

        builder.Property(row => row.ProviderId).HasMaxLength(64).ValueGeneratedNever();

        // Anulável desde o ADR-037: a linha pode existir só pelo acompanhamento,
        // e aí os parâmetros continuam "nunca salvos".
        builder.Property(row => row.Arguments).HasMaxLength(AgentArguments.MaxLength);
    }
}
