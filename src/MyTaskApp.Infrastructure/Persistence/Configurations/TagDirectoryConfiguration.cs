using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class TagDirectoryConfiguration : IEntityTypeConfiguration<TagDirectory>
{
    public void Configure(EntityTypeBuilder<TagDirectory> builder)
    {
        builder.ToTable("TagDirectories");

        builder.HasKey(directory => directory.Id);

        // O id nasce no domínio. Sem isto, um diretório acrescentado a uma
        // etiqueta já rastreada chega com chave preenchida e o EF o trata como
        // existente: gera UPDATE em vez de INSERT e falha por concorrência.
        builder.Property(directory => directory.Id).ValueGeneratedNever();

        // NOCASE e índice único com a etiqueta: "@Eco" e "@eco" são o mesmo
        // atalho dentro dela, mas duas etiquetas podem ter um "@eco" cada
        // (ADR-026). Tag.AddDirectory já recusa; o índice impede outro caminho.
        builder.Property(directory => directory.Alias)
            .IsRequired()
            .HasMaxLength(TagDirectory.MaxAliasLength)
            .UseCollation("NOCASE");

        builder.HasIndex(directory => new { directory.TagId, directory.Alias }).IsUnique();

        builder.Property(directory => directory.Path)
            .IsRequired()
            .HasMaxLength(TagDirectory.MaxPathLength);

        builder.Property(directory => directory.Name)
            .HasMaxLength(TagDirectory.MaxNameLength);

        builder.Property(directory => directory.Description)
            .HasMaxLength(TagDirectory.MaxDescriptionLength);

        builder.Property(directory => directory.DefaultBranch)
            .HasMaxLength(TagDirectory.MaxDefaultBranchLength);

        builder.Property(directory => directory.CreatedAt)
            .HasConversion(UtcInstantConverter.Instance)
            .IsRequired();

        // Os comandos rápidos do diretório são parte dele (ADR-051): excluir o
        // diretório, ou a etiqueta, leva as associações junto.
        builder.HasMany(directory => directory.Commands)
            .WithOne()
            .HasForeignKey(binding => binding.TagDirectoryId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(directory => directory.Commands)
            .HasField("_commands")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
