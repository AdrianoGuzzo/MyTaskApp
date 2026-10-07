using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Domain.StickyNotes;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class StickyNoteConfiguration : IEntityTypeConfiguration<StickyNote>
{
    public void Configure(EntityTypeBuilder<StickyNote> builder)
    {
        builder.ToTable("StickyNotes");

        builder.HasKey(note => note.Id);

        // O id nasce no domínio, como nas outras entidades.
        builder.Property(note => note.Id).ValueGeneratedNever();

        // TEXT sem teto no SQLite; o teto é regra do domínio (MaxContentLength).
        builder.Property(note => note.Content).IsRequired();

        // A cor da etiqueta é lida na consulta, nunca copiada. Excluir a
        // etiqueta solta o post-it (SET NULL) em vez de levá-lo junto: perder a
        // anotação porque a etiqueta saiu seria o pior efeito colateral possível.
        builder.HasOne<Tag>()
            .WithMany()
            .HasForeignKey(note => note.TagId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Property(note => note.ColorMode).HasConversion<int>();
        builder.Property(note => note.PaletteColor).HasConversion<int?>();
        builder.Property(note => note.Emphasis).HasConversion<int>();

        builder.Property(note => note.CreatedAt)
            .HasConversion(UtcInstantConverter.Instance)
            .IsRequired();

        builder.Property(note => note.UpdatedAt)
            .HasConversion(UtcInstantConverter.Instance)
            .IsRequired();

        builder.Property(note => note.ArchivedAt).HasConversion(UtcInstantConverter.Instance);
        builder.Property(note => note.DeletedAt).HasConversion(UtcInstantConverter.Instance);

        // Os mesmos dois índices parciais do checklist (ADR-020): a aba
        // Arquivados e a varredura da lixeira leem só o que lhes cabe.
        builder.HasIndex(note => note.DeletedAt)
            .HasDatabaseName("IX_StickyNotes_Trashed")
            .HasFilter("\"DeletedAt\" IS NOT NULL");

        builder.HasIndex(note => note.ArchivedAt)
            .HasDatabaseName("IX_StickyNotes_Archived")
            .HasFilter("\"ArchivedAt\" IS NOT NULL AND \"DeletedAt\" IS NULL");
    }
}
