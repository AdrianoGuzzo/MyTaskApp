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

        builder.Property(row => row.Arguments).HasMaxLength(AgentArguments.MaxLength).IsRequired();
    }
}
