using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class TaskOccurrenceConfiguration : IEntityTypeConfiguration<TaskOccurrence>
{
    public void Configure(EntityTypeBuilder<TaskOccurrence> builder)
    {
        builder.ToTable("TaskOccurrences");

        builder.HasKey(occurrence => occurrence.Id);

        builder.Property(occurrence => occurrence.Status).HasConversion<int>();

        builder.Property(occurrence => occurrence.CompletedAt)
            .HasConversion(UtcInstantConverter.Instance);

        // O histórico e a tela "Hoje" filtram por conclusão.
        builder.HasIndex(occurrence => occurrence.CompletedAt);

        // Projeção do par data/hora; não é coluna.
        builder.Ignore(occurrence => occurrence.Schedule);

        // Position não ganha índice, e isso é decisão. A ordem da tela "Hoje" é
        // montada em memória pelo GetTodayBoardHandler (ADR-010): a coluna nunca
        // é predicado nem ORDER BY em SQL. Um índice aqui seria simetria com os
        // vizinhos, não necessidade — e custaria escrita em todo arrasto.
        // Mapeada por convenção (int? -> INTEGER nullable).

        // Guarda de idempotência da materialização de recorrências (ADR-003):
        // a mesma série não pode ter duas ocorrências no mesmo instante agendado.
        builder.HasIndex(occurrence => new
            {
                occurrence.TaskItemId,
                occurrence.ScheduledDate,
                occurrence.ScheduledTime,
            })
            .IsUnique();

        // O estado do lembrete desta ocorrência (ADR-004 revisado): um disparo
        // rolante, não uma linha por disparo.
        builder.OwnsOne(occurrence => occurrence.Reminder, reminder =>
        {
            reminder.Property(state => state.NextFireAtUtc)
                .HasColumnName("Reminder_NextFireAtUtc")
                .HasConversion(UtcInstantConverter.Instance);

            reminder.Property(state => state.Attempt)
                .HasColumnName("Reminder_Attempt")
                .IsRequired();

            reminder.Property(state => state.WaitingSinceUtc)
                .HasColumnName("Reminder_WaitingSinceUtc")
                .HasConversion(UtcInstantConverter.Instance);

            reminder.Property(state => state.LastFiredAtUtc)
                .HasColumnName("Reminder_LastFiredAtUtc")
                .HasConversion(UtcInstantConverter.Instance);

            reminder.Property(state => state.AcknowledgedAtUtc)
                .HasColumnName("Reminder_AcknowledgedAtUtc")
                .HasConversion(UtcInstantConverter.Instance);

            reminder.Property(state => state.AcknowledgedBy)
                .HasColumnName("Reminder_AcknowledgedBy")
                .HasConversion<int?>();

            // Indice quente do agendador, que roda a cada 30 s. Parcial: so as
            // ocorrencias realmente armadas ocupam espaco.
            reminder.HasIndex(state => state.NextFireAtUtc)
                .HasFilter(
                    "\"Reminder_NextFireAtUtc\" IS NOT NULL "
                    + "AND \"Reminder_AcknowledgedAtUtc\" IS NULL");
        });

        // Sem isto o EF nao distingue "owned ausente" de "todas as colunas nulas".
        builder.Navigation(occurrence => occurrence.Reminder).IsRequired();

        // O prazo (ADR-050): data e hora de parede, como o agendamento. O
        // instante é derivado na borda pelo fuso do usuário, nunca gravado.
        builder.Property(occurrence => occurrence.DeadlineDate).HasColumnName("Deadline_Date");
        builder.Property(occurrence => occurrence.DeadlineTime).HasColumnName("Deadline_Time");
        builder.Ignore(occurrence => occurrence.Deadline);

        // O despacho dos avisos de prazo e a tela "Hoje" procuram só pendentes
        // com prazo. Parcial: a maioria das tarefas não tem prazo nenhum.
        builder.HasIndex(occurrence => occurrence.DeadlineDate)
            .HasDatabaseName("IX_TaskOccurrences_Deadline")
            .HasFilter("\"Deadline_Date\" IS NOT NULL AND \"Status\" = 0");

        // O que já foi avisado sobre o prazo. Só o passado: o próximo aviso é
        // calculado a cada tique, então não há coluna "próximo" para envelhecer.
        builder.OwnsOne(occurrence => occurrence.DeadlineAlert, alert =>
        {
            alert.Property(state => state.LastStage)
                .HasColumnName("DeadlineAlert_LastStage")
                .HasConversion<int>()
                .IsRequired();

            alert.Property(state => state.LastAlertAtUtc)
                .HasColumnName("DeadlineAlert_LastAlertAtUtc")
                .HasConversion(UtcInstantConverter.Instance);

            alert.Property(state => state.SnoozedUntilUtc)
                .HasColumnName("DeadlineAlert_SnoozedUntilUtc")
                .HasConversion(UtcInstantConverter.Instance);
        });

        // A coluna obrigatória (LastStage) é o que permite isto; ver o lembrete acima.
        builder.Navigation(occurrence => occurrence.DeadlineAlert).IsRequired();

        // Suporta a tela "Hoje" e as consultas por período.
        builder.HasIndex(occurrence => new { occurrence.ScheduledDate, occurrence.Status });
    }
}
