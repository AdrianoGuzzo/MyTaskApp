using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class AgentSessionConfiguration : IEntityTypeConfiguration<AgentSession>
{
    public void Configure(EntityTypeBuilder<AgentSession> builder)
    {
        builder.ToTable("AgentSessions");

        builder.HasKey(session => session.Id);

        // O id nasce no domínio, como nas outras entidades.
        builder.Property(session => session.Id).ValueGeneratedNever();

        // Apagar a tarefa de vez leva o histórico de sessões junto — ele só
        // existe para dizer qual terminal é de qual tarefa.
        builder.HasOne<TaskItem>()
            .WithMany()
            .HasForeignKey(session => session.TaskItemId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        // "A sessão mais recente da tarefa" é a pergunta de toda tela.
        builder.HasIndex(session => new { session.TaskItemId, session.StartedAt });

        // Uma sessão ativa por tarefa (ADR-030). O caso de uso já recusa a
        // segunda; o índice parcial impede que outro caminho a grave.
        // Nomeado para não se confundir com o índice comum da chave estrangeira.
        builder.HasIndex(session => session.TaskItemId, "IX_AgentSessions_TaskItemId_Active")
            .IsUnique()
            .HasFilter($"\"Status\" IN ({(int)AgentSessionStatus.Starting}, {(int)AgentSessionStatus.Running})");

        builder.HasIndex(session => session.Status);

        builder.Property(session => session.ProviderId)
            .IsRequired()
            .HasMaxLength(AgentSession.MaxProviderIdLength);

        builder.Property(session => session.Command)
            .IsRequired()
            .HasMaxLength(AgentSession.MaxPathLength);

        builder.Property(session => session.WorkingDirectory)
            .IsRequired()
            .HasMaxLength(AgentSession.MaxPathLength);

        builder.Property(session => session.Status).HasConversion<int>();

        builder.Property(session => session.ProcessStartedAt)
            .HasConversion(UtcInstantConverter.Instance);

        builder.Property(session => session.StartedAt)
            .HasConversion(UtcInstantConverter.Instance)
            .IsRequired();

        builder.Property(session => session.EndedAt)
            .HasConversion(UtcInstantConverter.Instance);

        builder.Property(session => session.FailureReason)
            .HasMaxLength(AgentSession.MaxFailureLength);
    }
}
