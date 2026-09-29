using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class AgentAlertSoundConfiguration : IEntityTypeConfiguration<AgentAlertSoundRow>
{
    public const int MaxSoundIdLength = 300;

    public void Configure(EntityTypeBuilder<AgentAlertSoundRow> builder)
    {
        builder.ToTable("AgentAlertSounds");

        // O número do AgentActivity: o enum já é gravado assim nas sessões.
        builder.HasKey(row => row.Activity);

        builder.Property(row => row.Activity).ValueGeneratedNever();

        builder.Property(row => row.SoundId).HasMaxLength(MaxSoundIdLength).IsRequired();
    }
}
