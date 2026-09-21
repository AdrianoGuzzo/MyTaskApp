using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.Lifecycle;

namespace MyTaskApp.Application.Lifecycle;

/// <summary>Um tique da manutenção do ciclo de vida (§2, §6).</summary>
public sealed record RunLifecycleMaintenance;

public sealed record LifecycleMaintenanceResult(int Archived, int Purged)
{
    public static LifecycleMaintenanceResult Nothing { get; } = new(0, 0);

    public bool DidSomething => Archived > 0 || Purged > 0;
}

/// <summary>
/// Arquiva o que já passou do prazo desde a conclusão e apaga de vez o que
/// venceu na lixeira.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotência em três camadas, de propósito.</b> Os predicados da consulta
/// já excluem o que foi processado; o estado é reconferido em memória depois de
/// carregar o agregado, porque entre a consulta e a escrita o usuário pode ter
/// restaurado o item na tela; e o próprio agregado recusa arquivar o que já está
/// arquivado. Rodar duas varreduras concorrentes não duplica nada — e o
/// <c>Mutex</c> nomeado do ADR-019 garante que não exista um segundo processo
/// tentando.
/// </para>
/// <para>
/// <b>Um único SaveChanges para o tique inteiro.</b> Arquivar 40 checklists e
/// apagar 12 é uma transação só: uma queda no meio não deixa metade do lote
/// aplicado e a outra metade com auditoria gravada. O teto por tique existe
/// pela mesma razão do <c>DispatchDueReminders</c> — abrir o app depois de meses
/// não pode virar uma transação de milhares de linhas.
/// </para>
/// </remarks>
public sealed class RunLifecycleMaintenanceHandler(
    ILifecycleSweepQuery sweep,
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    ITaskAuditLog audit,
    IDataRetentionSettingsStore settings,
    TimeProvider timeProvider,
    ILogger<RunLifecycleMaintenanceHandler> logger)
{
    /// <summary>Teto por tique. O que sobrar vem na próxima passagem.</summary>
    public const int BatchSize = 100;

    public async Task<LifecycleMaintenanceResult> HandleAsync(
        RunLifecycleMaintenance command,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var policy = await settings.GetAsync(cancellationToken);

        // Desligado quer dizer desligado: nem consulta o banco.
        var archived = policy.AutoArchiveEnabled
            ? await ArchiveConcludedAsync(policy, now, cancellationToken)
            : 0;

        var purged = await PurgeExpiredAsync(policy, now, cancellationToken);

        if (archived == 0 && purged == 0)
        {
            // Debug, e não Information: um tique vazio não é notícia, e enche o
            // log de quem nunca arquivou nada.
            logger.LogDebug("LifecycleSweepFoundNothing {Now}", now);
            return LifecycleMaintenanceResult.Nothing;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("LifecycleSweepRan {Archived} {Purged}", archived, purged);

        return new LifecycleMaintenanceResult(archived, purged);
    }

    private async Task<int> ArchiveConcludedAsync(
        DataRetentionPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cutoff = policy.AutoArchiveCutoff(now);
        var candidates = await sweep.GetReadyToArchiveAsync(cutoff, BatchSize, cancellationToken);

        var count = 0;

        foreach (var taskId in candidates)
        {
            var task = await tasks.FindByIdAsync(taskId, cancellationToken);

            // Reconferência em memória: entre a consulta e agora o usuário pode
            // ter reaberto um item, excluído o checklist ou arquivado na mão.
            // Nenhum desses casos é falha — é a tela ganhando da varredura, que
            // é como tem de ser.
            if (task is null
                || task.IsOutOfTheMainList
                || task.ConcludedAt is not { } concludedAt
                || concludedAt > cutoff)
            {
                continue;
            }

            try
            {
                task.Archive(now);
            }
            catch (DomainException exception)
            {
                // Uma regra recusando um item não pode derrubar o lote inteiro.
                logger.LogWarning(exception, "LifecycleAutoArchiveRefused {TaskId}", taskId);
                continue;
            }

            await audit.RecordAsync(
                TaskAuditEntry.BySystem(
                    task.Id,
                    task.Title,
                    TaskAuditOperation.Archived,
                    now,
                    $"Arquivado automaticamente {policy.AutoArchiveAfterDays} dias "
                    + "após a conclusão."),
                cancellationToken);

            count++;
        }

        return count;
    }

    private async Task<int> PurgeExpiredAsync(
        DataRetentionPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cutoff = policy.TrashCutoff(now);
        var candidates = await sweep.GetReadyToPurgeAsync(cutoff, BatchSize, cancellationToken);

        var count = 0;

        foreach (var taskId in candidates)
        {
            var task = await tasks.FindByIdAsync(taskId, cancellationToken);

            // A reconferência aqui vale por todas: esta é a única operação do
            // app que não tem volta. Restaurado da lixeira entre a consulta e
            // agora, ou com prazo ainda correndo, não se apaga.
            if (task is null
                || task.DeletedAt is not { } deletedAt
                || deletedAt > cutoff)
            {
                continue;
            }

            await audit.RecordAsync(
                TaskAuditEntry.BySystem(
                    task.Id,
                    task.Title,
                    TaskAuditOperation.PermanentlyDeleted,
                    now,
                    $"Excluído automaticamente após {policy.TrashRetentionDays} dias na lixeira."),
                cancellationToken);

            tasks.Remove(task);

            count++;
        }

        return count;
    }
}
