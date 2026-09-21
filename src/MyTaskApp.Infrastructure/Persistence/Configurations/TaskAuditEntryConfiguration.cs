using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Domain.Auditing;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class TaskAuditEntryConfiguration : IEntityTypeConfiguration<TaskAuditEntry>
{
    public void Configure(EntityTypeBuilder<TaskAuditEntry> builder)
    {
        builder.ToTable("TaskAuditEntries");

        builder.HasKey(entry => entry.Id);

        // ---------------------------------------------------------------
        // NÃO declarar relacionamento com Tasks. Não é esquecimento: é o §8.
        //
        // Com chave estrangeira, o DELETE da exclusão definitiva levaria junto
        // o registro dessa mesma exclusão (cascata) ou passaria a falhar (sem
        // cascata). Sem FK, a linha sobrevive ao checklist — que é exatamente o
        // que se quer poder investigar depois.
        //
        // A convenção do EF não cria a FK sozinha aqui porque não há
        // propriedade de navegação e "TaskId" não casa com "TaskItemId"; ainda
        // assim, este comentário existe para que ninguém "conserte" isso.
        // ---------------------------------------------------------------
        builder.Property(entry => entry.TaskId).IsRequired();

        builder.Property(entry => entry.TaskTitle)
            .IsRequired()
            .HasMaxLength(TaskAuditEntry.MaxTitleLength);

        builder.Property(entry => entry.Operation).HasConversion<int>().IsRequired();

        builder.Property(entry => entry.Actor).HasConversion<int>().IsRequired();

        builder.Property(entry => entry.OccurredAt)
            .HasConversion(UtcInstantConverter.Instance)
            .IsRequired();

        builder.Property(entry => entry.ActorName).HasMaxLength(TaskAuditEntry.MaxTitleLength);

        builder.Property(entry => entry.Details).HasMaxLength(TaskAuditEntry.MaxDetailsLength);

        // A trilha de um checklist, na ordem em que é lida.
        builder.HasIndex(entry => new { entry.TaskId, entry.OccurredAt });

        // A trilha inteira por período, para uma investigação que não parte de
        // um id — que é o caso depois de uma exclusão definitiva.
        builder.HasIndex(entry => entry.OccurredAt);
    }
}
