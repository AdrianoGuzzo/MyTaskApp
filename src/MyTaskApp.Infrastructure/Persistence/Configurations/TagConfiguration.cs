using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.ToTable("Tags");

        builder.HasKey(tag => tag.Id);

        // NOCASE na coluna, e não só na consulta: o índice único passa a valer
        // para "Urgente" e "urgente", e a busca por nome usa o mesmo critério.
        builder.Property(tag => tag.Name)
            .IsRequired()
            .HasMaxLength(Tag.MaxNameLength)
            .UseCollation("NOCASE");

        builder.HasIndex(tag => tag.Name).IsUnique();

        builder.Property(tag => tag.ColorHex)
            .IsRequired()
            .HasMaxLength(TagColor.HexLength);

        builder.Property(tag => tag.CreatedAt)
            .HasConversion(UtcInstantConverter.Instance)
            .IsRequired();
    }
}
