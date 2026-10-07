using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class TimeEntryConfiguration : IEntityTypeConfiguration<TimeEntry>
{
    /// <summary>
    /// "Um cronômetro ativo no app inteiro" no banco (ADR-052). Criado em SQL cru
    /// pela migration <c>TimeEntries</c>, porque o EF não modela índice sobre
    /// expressão: um índice único filtrado em <c>EndedAt</c> não seguraria nada,
    /// já que no SQLite os NULLs são distintos num índice único. Indexar a
    /// expressão <c>("EndedAt" IS NULL)</c>, que vale 1 em toda linha ativa, é o
    /// que faz a segunda colidir com a primeira.
    /// </summary>
    /// <remarks>
    /// O modelo não sabe dele, então uma migration futura que reconstrua a tabela
    /// (o SQLite faz isso para várias alterações de coluna) o perderia sem aviso.
    /// <c>TimeEntryPersistenceTests</c> confere que ele existe depois de todas as
    /// migrations, e é isso que avisa.
    /// </remarks>
    public const string SingleActiveIndex = "IX_TimeEntries_SingleActive";

    public void Configure(EntityTypeBuilder<TimeEntry> builder)
    {
        builder.ToTable("TimeEntries");

        builder.HasKey(entry => entry.Id);

        // O id nasce no domínio, como nas outras entidades.
        builder.Property(entry => entry.Id).ValueGeneratedNever();

        // A ocorrência é a unidade de trabalho (ADR-001). Apagar a tarefa de vez
        // leva os períodos junto, pela cascata Tasks → TaskOccurrences → aqui.
        builder.HasOne<TaskOccurrence>()
            .WithMany()
            .HasForeignKey(entry => entry.TaskOccurrenceId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        // O histórico da tarefa, a soma do quadro e a conferência de sobreposição
        // perguntam todos "os períodos desta ocorrência, em ordem". Substitui o
        // índice simples da chave estrangeira, que seria prefixo deste.
        builder.HasIndex(entry => new { entry.TaskOccurrenceId, entry.StartedAt });

        // "Quanto trabalhei nesta semana" — a pergunta dos relatórios que virão.
        builder.HasIndex(entry => entry.StartedAt);

        builder.Property(entry => entry.StartedAt)
            .HasConversion(UtcInstantConverter.Instance)
            .IsRequired();

        builder.Property(entry => entry.EndedAt)
            .HasConversion(UtcInstantConverter.Instance);

        builder.Property(entry => entry.Source).HasConversion<int>();

        builder.Property(entry => entry.Note)
            .HasMaxLength(TimeEntry.MaxNoteLength);

        builder.Property(entry => entry.CreatedAt)
            .HasConversion(UtcInstantConverter.Instance)
            .IsRequired();

        builder.Property(entry => entry.UpdatedAt)
            .HasConversion(UtcInstantConverter.Instance);
    }
}
