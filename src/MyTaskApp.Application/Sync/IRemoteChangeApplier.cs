using MyTaskApp.Application.Abstractions;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Application.Sync;

/// <summary>Uma mudança do servidor que este computador não conseguiu aplicar.</summary>
public sealed record RemoteApplyFailure(string EntityType, Guid EntityId, string Code, string Message);

/// <summary>O que uma página aplicada mudou, para recalcular e redesenhar.</summary>
/// <param name="TaskIds">As tarefas que receberam alguma mudança (delas ou das ocorrências).</param>
/// <param name="RearmTaskIds">As que mudaram a política de lembrete ou o agendamento.</param>
public sealed record RemoteApplyResult(
    int Applied,
    int Skipped,
    IReadOnlyList<Guid> TaskIds,
    IReadOnlyList<Guid> RearmTaskIds,
    DataArea Areas,
    IReadOnlyList<RemoteApplyFailure> Failures)
{
    public static RemoteApplyResult Empty { get; } = new(0, 0, [], [], DataArea.None, []);
}

/// <summary>
/// Grava no banco local o que veio do servidor, sem gerar operação de saída
/// (ADR-061). Uma página por transação, e o cursor avança junto: ou a página
/// inteira entra, ou nada entra e o cursor fica onde estava.
/// </summary>
public interface IRemoteChangeApplier
{
    /// <param name="nextCursor">O cursor a gravar junto; <c>null</c> não mexe nele (fetch).</param>
    /// <param name="isolateFailures">
    /// Depois de a página falhar inteira algumas vezes: aplica o que der, marca
    /// a linha que não entra e segue, para uma linha ruim não travar tudo.
    /// </param>
    Task<RemoteApplyResult> ApplyAsync(
        IReadOnlyList<SyncChange> changes,
        string? nextCursor,
        bool isolateFailures,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A varredura da primeira conexão (e de depois de um upgrade): toda entidade
/// local que a sincronização ainda não conhece vira operação, e o que mudou
/// desde o último estado conhecido também.
/// </summary>
public interface ISyncReconciler
{
    /// <returns>Quantas operações entraram no outbox.</returns>
    Task<int> ScanAsync(CancellationToken cancellationToken = default);
}
