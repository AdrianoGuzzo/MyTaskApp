using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Domain.Consultations;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

/// <summary>
/// A Consulta Inteligente da tarefa (ADR-081). Só a tarefa tem chave
/// estrangeira: etiqueta, diretório, ambiente e conexão são referências
/// conferidas na leitura, para excluir um deles não depender da consulta.
/// </summary>
internal sealed class TaskConsultationConfiguration : IEntityTypeConfiguration<TaskConsultation>
{
    public void Configure(EntityTypeBuilder<TaskConsultation> builder)
    {
        builder.ToTable("TaskConsultations");

        builder.HasKey(consultation => consultation.TaskItemId);
        builder.Property(consultation => consultation.TaskItemId).ValueGeneratedNever();

        // Uma por tarefa, e some com ela.
        builder.HasOne<TaskItem>()
            .WithOne()
            .HasForeignKey<TaskConsultation>(consultation => consultation.TaskItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(consultation => consultation.DatabaseName).HasMaxLength(DatabaseConnection.MaxIdentifierLength);
        builder.Property(consultation => consultation.Instructions).HasMaxLength(TaskConsultation.MaxInstructionsLength);
        builder.Property(consultation => consultation.Model).HasMaxLength(TaskConsultation.MaxOptionLength);
        builder.Property(consultation => consultation.Effort).HasMaxLength(TaskConsultation.MaxOptionLength);
        builder.Property(consultation => consultation.LastQuestion).HasMaxLength(TaskConsultation.MaxInstructionsLength);
        builder.Property(consultation => consultation.CreatedAt).HasConversion(UtcInstantConverter.Instance).IsRequired();
        builder.Property(consultation => consultation.UpdatedAt).HasConversion(UtcInstantConverter.Instance).IsRequired();

        builder.HasMany(consultation => consultation.Sources)
            .WithOne()
            .HasForeignKey(source => source.TaskItemId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(consultation => consultation.Sources)
            .HasField("_sources")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class TaskConsultationSourceConfiguration : IEntityTypeConfiguration<TaskConsultationSource>
{
    public void Configure(EntityTypeBuilder<TaskConsultationSource> builder)
    {
        builder.ToTable("TaskConsultationSources");

        builder.HasKey(source => source.Id);
        builder.Property(source => source.Id).ValueGeneratedNever();
        builder.Property(source => source.Kind).HasConversion<int>();

        builder.Ignore(source => source.Reference);

        builder.HasIndex(source => new { source.TaskItemId, source.Kind, source.ReferenceId }).IsUnique();
    }
}
