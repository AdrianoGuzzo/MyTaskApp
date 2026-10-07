using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class DevelopmentCommandConfiguration : IEntityTypeConfiguration<DevelopmentCommand>
{
    public void Configure(EntityTypeBuilder<DevelopmentCommand> builder)
    {
        builder.ToTable("DevelopmentCommands");

        builder.HasKey(command => command.Id);

        builder.Property(command => command.Id).ValueGeneratedNever();

        // Global: "@Restore" e "@restore" são o mesmo apelido em todo o app
        // (ADR-028). O caso de uso já recusa; o índice impede outro caminho.
        // O comando só do diretório não tem apelido (ADR-054), e o SQLite não
        // conta NULLs repetidos como duplicata.
        builder.Property(command => command.Alias)
            .HasMaxLength(DevelopmentCommand.MaxAliasLength)
            .UseCollation("NOCASE");

        builder.HasIndex(command => command.Alias).IsUnique();

        // O dono do comando só do diretório (ADR-054). Excluir o diretório, ou
        // a etiqueta, leva o comando junto: ele não tem onde mais aparecer.
        builder.HasOne<TagDirectory>()
            .WithMany()
            .HasForeignKey(command => command.TagDirectoryId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(command => command.Command)
            .IsRequired()
            .HasMaxLength(DevelopmentCommand.MaxCommandLength);

        builder.Property(command => command.Description)
            .HasMaxLength(DevelopmentCommand.MaxDescriptionLength);

        builder.Property(command => command.CreatedAt)
            .HasConversion(UtcInstantConverter.Instance)
            .IsRequired();

        builder.Property(command => command.UpdatedAt)
            .HasConversion(UtcInstantConverter.Instance)
            .IsRequired();

        // O comando rápido (ADR-051). Os padrões são o comportamento de antes:
        // quem atualiza continua com comandos escondidos, na raiz do worktree.
        builder.Property(command => command.Name)
            .HasMaxLength(DevelopmentCommand.MaxNameLength);

        builder.Property(command => command.Mode).HasConversion<int>();

        builder.Property(command => command.WorkingDirectory)
            .HasMaxLength(CommandWorkingDirectory.MaxLength);

        // Sem HasDefaultValue(true): o EF tomaria o false do CLR por "não
        // informado" e gravaria true no lugar. O true das linhas antigas vem da
        // migration QuickCommands, escrito à mão.

        // As definições dos parâmetros são parte do comando: excluí-lo leva todas.
        builder.HasMany(command => command.Parameters)
            .WithOne()
            .HasForeignKey(parameter => parameter.DevelopmentCommandId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(command => command.Parameters)
            .HasField("_parameters")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
