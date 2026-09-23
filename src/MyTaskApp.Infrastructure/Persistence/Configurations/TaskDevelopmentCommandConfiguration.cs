using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class TaskDevelopmentCommandConfiguration : IEntityTypeConfiguration<TaskDevelopmentCommand>
{
    public void Configure(EntityTypeBuilder<TaskDevelopmentCommand> builder)
    {
        builder.ToTable("TaskDevelopmentCommands");

        builder.HasKey(command => command.Id);

        // O id nasce no domínio: a mesma armadilha do TagDirectory.
        builder.Property(command => command.Id).ValueGeneratedNever();

        builder.Property(command => command.Command)
            .IsRequired()
            .HasMaxLength(TaskDevelopmentCommand.MaxCommandLength);

        // Não é único: reordenar reescreve as posições em lugar, e o SQLite
        // confere índice único linha a linha, no meio do UPDATE.
        builder.HasIndex(command => new { command.TaskDevelopmentId, command.Order });
    }
}
