using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class TaskDevelopmentConfiguration : IEntityTypeConfiguration<TaskDevelopment>
{
    public void Configure(EntityTypeBuilder<TaskDevelopment> builder)
    {
        builder.ToTable("TaskDevelopments");

        builder.HasKey(development => development.Id);

        // O id nasce no domínio. Sem isto, o ambiente criado numa tarefa já
        // rastreada chega com chave preenchida e o EF gera UPDATE em vez de
        // INSERT — a mesma armadilha do TagDirectory.
        builder.Property(development => development.Id).ValueGeneratedNever();

        // Um ambiente por tarefa. TaskItem.BeginDevelopment reaproveita a linha
        // em vez de trocá-la, justamente para não esbarrar aqui.
        builder.HasIndex(development => development.TaskItemId).IsUnique();

        builder.Property(development => development.RepositoryPath)
            .IsRequired()
            .HasMaxLength(TaskDevelopment.MaxPathLength);

        builder.Property(development => development.SourceBranch)
            .IsRequired()
            .HasMaxLength(TaskDevelopment.MaxBranchLength);

        builder.Property(development => development.Branch)
            .IsRequired()
            .HasMaxLength(TaskDevelopment.MaxBranchLength);

        builder.Property(development => development.WorktreePath)
            .IsRequired()
            .HasMaxLength(TaskDevelopment.MaxPathLength);

        builder.Property(development => development.Status).HasConversion<int>();

        builder.Property(development => development.CreatedAt)
            .HasConversion(UtcInstantConverter.Instance)
            .IsRequired();

        builder.Property(development => development.StatusChangedAt)
            .HasConversion(UtcInstantConverter.Instance)
            .IsRequired();

        builder.Property(development => development.FailureReason)
            .HasMaxLength(TaskDevelopment.MaxFailureLength);

        // Os comandos pós-Worktree são parte do ambiente (ADR-028): tirar um da
        // lista apaga a linha, e apagar o ambiente leva todos.
        builder.HasMany(development => development.Commands)
            .WithOne()
            .HasForeignKey(command => command.TaskDevelopmentId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(development => development.Commands)
            .HasField("_commands")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
