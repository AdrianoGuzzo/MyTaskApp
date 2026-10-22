using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Consultations;

namespace MyTaskApp.Application.Consultations;

/// <summary>
/// "Criar nova cópia local…" na aba da consulta: copia um banco (com as
/// confirmações da cópia) e, se der certo, passa a consultar o banco criado.
/// </summary>
public sealed record CopyConsultationDatabase(
    Guid TaskId,
    DatabaseCopyRequest Request,
    bool ProductionConfirmed,
    string? TypedConfirmation = null);

/// <summary>Como a cópia terminou, e a aba depois dela.</summary>
public sealed record ConsultationDatabaseCopyResult(DatabaseCopyResult Copy, TaskConsultationView View);

/// <remarks>
/// A cópia é a da janela Bancos de Dados (<see cref="RunDatabaseCopyHandler"/>):
/// política, confirmação de produção, uma operação por vez e auditoria ficam
/// lá. Aqui só entra o depois: o banco criado vira o da consulta — e só se a
/// cópia terminou bem. Nada que já existia é trocado antes disso.
/// </remarks>
public sealed class CopyConsultationDatabaseHandler(
    ITaskItemRepository tasks,
    ITaskConsultationRepository consultations,
    IAgentSessionRepository sessions,
    RunDatabaseCopyHandler copy,
    IUnitOfWork unitOfWork,
    ConsultationViewFactory views,
    TimeProvider timeProvider,
    ILogger<CopyConsultationDatabaseHandler> logger)
{
    public async Task<ConsultationDatabaseCopyResult> HandleAsync(
        CopyConsultationDatabase command,
        IProgress<DatabaseCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);

        if (task.IsOutOfTheMainList)
        {
            throw new DomainException("A tarefa está arquivada ou na lixeira: a consulta dela não muda.");
        }

        if (await sessions.ListConsultationsForTaskAsync(task.Id, 1, cancellationToken) is [{ IsActive: true }])
        {
            throw new DomainException("Há uma consulta aberta com o banco atual. Feche o terminal dela antes de copiar outro banco.");
        }

        var result = await copy.HandleAsync(
            new RunDatabaseCopy(command.Request, command.ProductionConfirmed, command.TypedConfirmation),
            progress,
            cancellationToken);

        if (!result.Succeeded || result.DestinationDatabase is not { } name)
        {
            // Falhou ou foi cancelada: o banco da consulta continua o de antes.
            return new ConsultationDatabaseCopyResult(result, await views.BuildAsync(task.Id, CancellationToken.None));
        }

        var now = timeProvider.GetUtcNow();
        var consultation = await consultations.FindAsync(task.Id, CancellationToken.None);

        if (consultation is null)
        {
            consultation = TaskConsultation.Create(task.Id, now);
            await consultations.AddAsync(consultation, CancellationToken.None);
        }

        // O banco já existe no servidor: registrar não pode ser cancelado.
        consultation.SetDatabase(command.Request.DestinationConnectionId, name, now);
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        logger.LogInformation(
            "ConsultationDatabaseCopied {TaskId} {AuditId} {ConnectionId} {Database}",
            task.Id,
            result.AuditId,
            command.Request.DestinationConnectionId,
            name);

        return new ConsultationDatabaseCopyResult(result, await views.BuildAsync(task.Id, CancellationToken.None));
    }
}
