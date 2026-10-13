using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Development;

/// <summary>O banco copiado para um ambiente, como a tela o mostra (ADR-060).</summary>
public sealed record DevelopmentDatabaseView(
    string Name,
    string Host,
    int Port,
    string Username,
    string ConnectionName,
    Guid ConnectionId,
    DateTimeOffset CopiedAt)
{
    public static DevelopmentDatabaseView? From(TaskDevelopmentDatabase? database) =>
        database is null
            ? null
            : new DevelopmentDatabaseView(
                database.Name,
                database.Host,
                database.Port,
                database.Username,
                database.ConnectionName,
                database.ConnectionId,
                database.CopiedAt);
}

/// <summary>
/// Confere, sem processo nenhum, a cópia que vai para o ambiente da tarefa
/// (ADR-060). A tela pergunta antes do Git: descobrir a recusa com o worktree
/// já criado é ter um ambiente pela metade.
/// </summary>
/// <param name="DevelopmentId">O ambiente pronto que recebe a cópia; <c>null</c> = o worktree ainda vai ser criado.</param>
public sealed record ValidateDevelopmentDatabaseCopy(Guid TaskId, DatabaseCopyRequest Request, Guid? DevelopmentId = null);

public sealed class ValidateDevelopmentDatabaseCopyHandler(
    ITaskItemRepository tasks,
    DatabaseOperationGate gate,
    ValidateDatabaseCopyHandler validate)
{
    public async Task<DatabaseCopyValidation> HandleAsync(
        ValidateDevelopmentDatabaseCopy query,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(query.TaskId, cancellationToken);

        if (query.DevelopmentId is { } developmentId)
        {
            task.EnsureDevelopmentDatabaseCanChange(developmentId);
        }
        else
        {
            // Mesma guarda de começar a implementação: checklist arquivado não ganha ambiente nem banco.
            task.EnsureDevelopmentCanBegin(developmentId: null);
        }

        if (gate.IsBusy)
        {
            throw new DomainException(
                "Já há uma operação de banco em andamento. Espere terminar ou cancele antes de copiar para este ambiente.");
        }

        return await validate.HandleAsync(new ValidateDatabaseCopy(query.Request), cancellationToken);
    }
}

/// <summary>
/// Copia um banco para o ambiente da tarefa e guarda o nome criado (ADR-060):
/// é ele que os comandos recebem em <c>{database}</c>.
/// </summary>
public sealed record CopyDevelopmentDatabase(
    Guid TaskId,
    Guid DevelopmentId,
    DatabaseCopyRequest Request,
    bool ProductionConfirmed,
    string? TypedConfirmation = null);

/// <summary>Como a cópia terminou, e o ambiente depois dela.</summary>
public sealed record DevelopmentDatabaseCopyResult(DatabaseCopyResult Copy, TaskDevelopmentView Development);

/// <remarks>
/// <para>
/// A cópia é a da janela Bancos de Dados (<see cref="RunDatabaseCopyHandler"/>):
/// política, confirmação de produção, uma operação por vez e auditoria ficam
/// lá. Aqui só entra o antes (o ambiente pode receber o banco?) e o depois (o
/// nome gerado fica no ambiente).
/// </para>
/// <para>
/// A tarefa é lida antes da cópia e salva depois, no mesmo escopo. Enquanto a
/// cópia roda, a tela não deixa remover nem esquecer o ambiente.
/// </para>
/// </remarks>
public sealed class CopyDevelopmentDatabaseHandler(
    ITaskItemRepository tasks,
    IDatabaseConnectionRepository connections,
    RunDatabaseCopyHandler copy,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<CopyDevelopmentDatabaseHandler> logger)
{
    public async Task<DevelopmentDatabaseCopyResult> HandleAsync(
        CopyDevelopmentDatabase command,
        IProgress<DatabaseCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);
        task.EnsureDevelopmentDatabaseCanChange(command.DevelopmentId);

        var result = await copy.HandleAsync(
            new RunDatabaseCopy(command.Request, command.ProductionConfirmed, command.TypedConfirmation),
            progress,
            cancellationToken);

        var development = task.GetDevelopment(command.DevelopmentId);

        if (!result.Succeeded || result.DestinationDatabase is not { } name)
        {
            // Falhou ou foi cancelada: o banco anterior, se havia, continua o do ambiente.
            return new DevelopmentDatabaseCopyResult(result, TaskDevelopmentView.From(development));
        }

        var destination = await connections.FindByIdAsync(command.Request.DestinationConnectionId, CancellationToken.None)
            ?? throw new DomainException("A conexão de destino não existe mais. O banco foi criado, mas não ficou no ambiente.");

        task.SetDevelopmentDatabase(
            command.DevelopmentId,
            TaskDevelopmentDatabase.Create(
                destination.Id,
                destination.Name,
                destination.Host,
                destination.Port,
                destination.Username,
                name,
                result.AuditId,
                timeProvider.GetUtcNow()));

        // O banco já existe no servidor: registrar não pode ser cancelado.
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        logger.LogInformation(
            "DevelopmentDatabaseCopied {TaskId} {DevelopmentId} {AuditId} {Database}",
            task.Id,
            development.Id,
            result.AuditId,
            name);

        return new DevelopmentDatabaseCopyResult(result, TaskDevelopmentView.From(development));
    }
}
