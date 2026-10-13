using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Domain.DatabaseOperations;
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

        // Vários ambientes por tarefa, um por repositório (ADR-031). A regra do
        // repositório fica no domínio: no Windows, dois textos diferentes podem
        // ser a mesma pasta, e um índice único não saberia disso.
        builder.HasIndex(development => development.TaskItemId);

        builder.Property(development => development.RepositoryPath)
            .IsRequired()
            .HasMaxLength(TaskDevelopment.MaxPathLength);

        builder.Property(development => development.SourceBranch)
            .IsRequired()
            .HasMaxLength(TaskDevelopment.MaxBranchLength);

        builder.Property(development => development.SourceTag)
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

        builder.Property(development => development.AgentPrompt)
            .HasMaxLength(TaskDevelopment.MaxAgentPromptLength);

        // O banco copiado para o ambiente (ADR-060). Owned e opcional, como o
        // vínculo externo da tarefa: quem diz "existe" é o Name, obrigatório no
        // objeto — tudo nulo é ambiente sem banco.
        builder.OwnsOne(development => development.Database, database =>
        {
            database.Property(value => value.ConnectionId)
                .HasColumnName("Database_ConnectionId")
                .IsRequired();

            database.Property(value => value.ConnectionName)
                .HasColumnName("Database_ConnectionName")
                .HasMaxLength(DatabaseConnection.MaxNameLength)
                .IsRequired();

            database.Property(value => value.Host)
                .HasColumnName("Database_Host")
                .HasMaxLength(DatabaseConnection.MaxHostLength)
                .IsRequired();

            database.Property(value => value.Port)
                .HasColumnName("Database_Port")
                .IsRequired();

            database.Property(value => value.Username)
                .HasColumnName("Database_Username")
                .HasMaxLength(DatabaseConnection.MaxIdentifierLength)
                .IsRequired();

            database.Property(value => value.Name)
                .HasColumnName("Database_Name")
                .HasMaxLength(DatabaseConnection.MaxIdentifierLength)
                .IsRequired();

            database.Property(value => value.AuditId)
                .HasColumnName("Database_AuditId");

            database.Property(value => value.CopiedAt)
                .HasColumnName("Database_CopiedAt")
                .HasConversion(UtcInstantConverter.Instance)
                .IsRequired();
        });

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
