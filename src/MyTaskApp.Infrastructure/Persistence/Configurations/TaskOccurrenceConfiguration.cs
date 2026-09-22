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

        // Suporta a tela "Hoje" e as consultas por período.
        builder.HasIndex(occurrence => new { occurrence.ScheduledDate, occurrence.Status });
    }
}
