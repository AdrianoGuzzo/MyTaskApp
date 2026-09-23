using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Domain.Commands;

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
        builder.Property(command => command.Alias)
            .IsRequired()
            .HasMaxLength(DevelopmentCommand.MaxAliasLength)
            .UseCollation("NOCASE");

        builder.HasIndex(command => command.Alias).IsUnique();

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
    }
}
