using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class BranchSettingsConfiguration : IEntityTypeConfiguration<BranchSettingsRow>
{
    public void Configure(EntityTypeBuilder<BranchSettingsRow> builder)
    {
        // Linha única garantida pelo banco, como as outras configurações: duas
        // convenções seriam duas respostas para "como se chama a branch".
        builder.ToTable(
            "BranchSettings",
            table => table.HasCheckConstraint("CK_BranchSettings_SingleRow", "Id = 1"));

        builder.HasKey(row => row.Id);

        builder.Property(row => row.Id).ValueGeneratedNever();

        builder.Property(row => row.Conventions).IsRequired();
    }
}
