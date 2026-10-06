using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class DevelopmentCommandParameterConfiguration : IEntityTypeConfiguration<DevelopmentCommandParameter>
{
    public void Configure(EntityTypeBuilder<DevelopmentCommandParameter> builder)
    {
        builder.ToTable("DevelopmentCommandParameters");

        builder.HasKey(parameter => parameter.Id);

        // O id nasce no domínio: a mesma armadilha do TagDirectory.
        builder.Property(parameter => parameter.Id).ValueGeneratedNever();

        builder.Property(parameter => parameter.Name)
            .IsRequired()
            .HasMaxLength(CommandParameterSpec.MaxNameLength);

        builder.Property(parameter => parameter.Label)
            .HasMaxLength(CommandParameterSpec.MaxLabelLength);

        builder.Property(parameter => parameter.Type).HasConversion<int>();

        builder.Property(parameter => parameter.DefaultValue)
            .HasMaxLength(CommandParameterSpec.MaxValueLength);

        // Uma opção por linha; sem teto de coluna, o domínio limita quantas e de que tamanho.
        builder.Property(parameter => parameter.Options);

        // Não é único: reordenar reescreve as posições em lugar (ver TaskDevelopmentCommandConfiguration).
        builder.HasIndex(parameter => new { parameter.DevelopmentCommandId, parameter.Order });
    }
}
