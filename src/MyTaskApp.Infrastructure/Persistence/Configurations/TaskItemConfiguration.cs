using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class TaskItemConfiguration : IEntityTypeConfiguration<TaskItem>
{
    public void Configure(EntityTypeBuilder<TaskItem> builder)
    {
        builder.ToTable("Tasks");

        builder.HasKey(task => task.Id);

        builder.Property(task => task.Title)
            .IsRequired()
            .HasMaxLength(TaskItem.MaxTitleLength);

        builder.Property(task => task.Priority).HasConversion<int>();

        builder.Property(task => task.CreatedAt)
            .HasConversion(UtcInstantConverter.Instance)
            .IsRequired();

        // ---------------------------------------------------------------
        // Ciclo de vida (§9). Três instantes e um nome; o estado em si é
        // derivado deles em TaskItem.Lifecycle e não tem coluna própria —
        // guardar o enum seria uma segunda fonte da verdade a divergir.
        // ---------------------------------------------------------------
        builder.Property(task => task.ArchivedAt)
            .HasConversion(UtcInstantConverter.Instance);

        builder.Property(task => task.DeletedAt)
            .HasConversion(UtcInstantConverter.Instance);

        builder.Property(task => task.ConcludedAt)
            .HasConversion(UtcInstantConverter.Instance);

        builder.Property(task => task.DeletedBy).HasMaxLength(TaskItem.MaxTitleLength);

        // Índice quente da varredura de arquivamento: só concluídos que ainda
        // estão na lista principal. Parcial porque, em regime, a maioria das
        // linhas não é candidata a nada.
        builder.HasIndex(task => task.ConcludedAt)
            .HasDatabaseName("IX_Tasks_ReadyToArchive")
            .HasFilter(
                "\"ConcludedAt\" IS NOT NULL "
                + "AND \"ArchivedAt\" IS NULL "
                + "AND \"DeletedAt\" IS NULL");

        // Índice da lixeira: serve tanto à listagem quanto à varredura de
        // exclusão definitiva.
        builder.HasIndex(task => task.DeletedAt)
            .HasDatabaseName("IX_Tasks_Trashed")
            .HasFilter("\"DeletedAt\" IS NOT NULL");

        // Índice da área de arquivados.
        builder.HasIndex(task => task.ArchivedAt)
            .HasDatabaseName("IX_Tasks_Archived")
            .HasFilter("\"ArchivedAt\" IS NOT NULL AND \"DeletedAt\" IS NULL");

        // A política de lembrete da série. Owned: seis colunas na mesma tabela,
        // sem join e sem uma entidade que ninguém consulta sozinha.
        builder.OwnsOne(task => task.Reminder, reminder =>
        {
            reminder.Property(policy => policy.IsEnabled)
                .HasColumnName("Reminder_IsEnabled")
                .IsRequired();

            reminder.Property(policy => policy.Anchor)
                .HasColumnName("Reminder_Anchor")
                .HasConversion<int>()
                .IsRequired();

            reminder.Property(policy => policy.Offset)
                .HasColumnName("Reminder_OffsetTicks")
                .HasConversion(TimeSpanTicksConverter.Instance)
                .IsRequired();

            reminder.Property(policy => policy.RepeatUntilAcknowledged)
                .HasColumnName("Reminder_Repeat")
                .IsRequired();

            reminder.Property(policy => policy.RepeatEvery)
                .HasColumnName("Reminder_RepeatEveryTicks")
                .HasConversion(TimeSpanTicksConverter.Instance)
                .IsRequired();

            reminder.Property(policy => policy.Channels)
                .HasColumnName("Reminder_Channels")
                .HasConversion<int>()
                .IsRequired();
        });

        // Sem isto o EF não distingue "owned ausente" de "todas as colunas nulas".
        builder.Navigation(task => task.Reminder).IsRequired();

        builder.HasMany(task => task.Occurrences)
            .WithOne()
            .HasForeignKey(occurrence => occurrence.TaskItemId)
            // Sem isto o EF trata a FK como opcional e a consulta da tela "Hoje"
            // deixa de ser traduzível para SQL.
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        // A coleção pública é somente-leitura; o EF escreve direto no campo.
        builder.Navigation(task => task.Occurrences)
            .HasField("_occurrences")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
