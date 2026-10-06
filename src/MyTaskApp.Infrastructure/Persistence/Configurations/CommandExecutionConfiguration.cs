using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class CommandExecutionConfiguration : IEntityTypeConfiguration<CommandExecution>
{
    public void Configure(EntityTypeBuilder<CommandExecution> builder)
    {
        builder.ToTable("CommandExecutions");

        builder.HasKey(execution => execution.Id);

        // O id nasce no domínio, como nas outras entidades.
        builder.Property(execution => execution.Id).ValueGeneratedNever();

        // Apagar a tarefa de vez leva o histórico junto, como as sessões do agente.
        builder.HasOne<TaskItem>()
            .WithMany()
            .HasForeignKey(execution => execution.TaskItemId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        // O resto só perde o vínculo: o histórico guarda cópias do que rodou
        // (ADR-051), e tirar o ambiente da lista, excluir o comando global ou a
        // associação não apaga o que já aconteceu.
        builder.HasOne<TaskDevelopment>()
            .WithMany()
            .HasForeignKey(execution => execution.TaskDevelopmentId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<DevelopmentCommand>()
            .WithMany()
            .HasForeignKey(execution => execution.DevelopmentCommandId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<TagDirectoryCommand>()
            .WithMany()
            .HasForeignKey(execution => execution.TagDirectoryCommandId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        // "A última execução de cada comando do ambiente" é a pergunta da tela.
        builder.HasIndex(execution => new { execution.TaskDevelopmentId, execution.StartedAt });

        builder.HasIndex(execution => new { execution.TaskItemId, execution.StartedAt });

        // A reconciliação só procura as que ainda não terminaram.
        builder.HasIndex(execution => execution.Status, "IX_CommandExecutions_Active")
            .HasFilter(
                $"\"Status\" IN ({(int)CommandExecutionStatus.Queued}, {(int)CommandExecutionStatus.Running})");

        builder.Property(execution => execution.CommandName)
            .IsRequired()
            .HasMaxLength(CommandExecution.MaxNameLength);

        builder.Property(execution => execution.CommandLine)
            .IsRequired()
            .HasMaxLength(CommandExecution.MaxCommandLineLength);

        builder.Property(execution => execution.WorkingDirectory)
            .IsRequired()
            .HasMaxLength(CommandExecution.MaxPathLength);

        builder.Property(execution => execution.Mode).HasConversion<int>();

        builder.Property(execution => execution.Status).HasConversion<int>();

        builder.Property(execution => execution.StartedAt)
            .HasConversion(UtcInstantConverter.Instance)
            .IsRequired();

        builder.Property(execution => execution.FinishedAt)
            .HasConversion(UtcInstantConverter.Instance);

        builder.Property(execution => execution.ProcessStartedAt)
            .HasConversion(UtcInstantConverter.Instance);

        builder.Property(execution => execution.Output)
            .HasMaxLength(CommandExecution.MaxOutputLength);

        builder.Property(execution => execution.ErrorOutput)
            .HasMaxLength(CommandExecution.MaxOutputLength);

        builder.Property(execution => execution.FailureReason)
            .HasMaxLength(CommandExecution.MaxFailureLength);
    }
}
