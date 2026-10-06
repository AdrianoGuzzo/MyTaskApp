using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class TagDirectoryCommandConfiguration : IEntityTypeConfiguration<TagDirectoryCommand>
{
    public void Configure(EntityTypeBuilder<TagDirectoryCommand> builder)
    {
        builder.ToTable("TagDirectoryCommands");

        builder.HasKey(binding => binding.Id);

        // O id nasce no domínio: a mesma armadilha do TagDirectory.
        builder.Property(binding => binding.Id).ValueGeneratedNever();

        // O comando é de outro agregado. Excluir o global tira o botão de todos
        // os diretórios que o ofereciam (ADR-051): a associação não teria o que rodar.
        builder.HasOne<DevelopmentCommand>()
            .WithMany()
            .HasForeignKey(binding => binding.DevelopmentCommandId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        // Um comando aparece uma vez por diretório. Tag.AddDirectoryCommand já recusa.
        builder.HasIndex(binding => new { binding.TagDirectoryId, binding.DevelopmentCommandId }).IsUnique();

        // Não é único: reordenar reescreve as posições em lugar.
        builder.HasIndex(binding => new { binding.TagDirectoryId, binding.Order });

        builder.Property(binding => binding.CommandOverride)
            .HasMaxLength(DevelopmentCommand.MaxCommandLength);

        builder.Property(binding => binding.WorkingDirectoryOverride)
            .HasMaxLength(CommandWorkingDirectory.MaxLength);

        builder.Property(binding => binding.CreatedAt)
            .HasConversion(UtcInstantConverter.Instance)
            .IsRequired();
    }
}
