namespace MyTaskApp.Application.Lifecycle;

/// <summary>
/// As duas perguntas que a varredura automática faz ao banco (§2, §6).
/// </summary>
/// <remarks>
/// <b>A idempotência das rotinas automáticas começa aqui.</b> Os predicados já
/// excluem o que foi processado — arquivado não volta a ser candidato a
/// arquivamento, e o que saiu da lixeira deixa de ser candidato a exclusão —
/// então rodar a varredura duas vezes seguidas não reprocessa nada, sem
/// depender de marca-d'água ou de tabela de controle.
/// </remarks>
public interface ILifecycleSweepQuery
{
    /// <summary>
    /// Checklists concluídos antes de <paramref name="concludedBefore"/> que
    /// ainda não estão arquivados nem na lixeira.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetReadyToArchiveAsync(
        DateTimeOffset concludedBefore,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checklists na lixeira desde antes de <paramref name="deletedBefore"/>.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetReadyToPurgeAsync(
        DateTimeOffset deletedBefore,
        int limit,
        CancellationToken cancellationToken = default);
}
