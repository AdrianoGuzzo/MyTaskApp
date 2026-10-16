namespace MyTaskApp.Application.Sync;

/// <summary>O que a bolinha da sincronização mostra, na ordem em que um estado vence o outro (ADR-063). O desktop e o celular (ADR-067) usam a mesma ordem.</summary>
public enum SyncIndicatorKind
{
    /// <summary>A build não tem servidor: não há o que mostrar nem o que conectar.</summary>
    Hidden,

    /// <summary>Sem conta: tudo fica neste computador.</summary>
    LocalOnly,

    /// <summary>Parou e só volta com a pessoa: login vencido, revogado, versão antiga, alteração recusada.</summary>
    Attention,

    /// <summary>Pausada de propósito.</summary>
    Paused,

    /// <summary>Um ciclo em andamento que já passou do instante, ou que leva alterações.</summary>
    Syncing,

    /// <summary>Sem conexão: o outbox guarda tudo e o agendador tenta de novo sozinho.</summary>
    Offline,

    /// <summary>Alterações esperando o próximo ciclo.</summary>
    Pending,

    /// <summary>Em dia, com algo para a pessoa revisar: conflito, comando, recebido com falha.</summary>
    Review,

    /// <summary>Em dia.</summary>
    Synced,
}

/// <summary>
/// O estado de relance da sincronização a partir do status lido: puro, para
/// a bolinha do desktop e o indicador do celular decidirem do mesmo jeito.
/// </summary>
public static class SyncIndicatorClassifier
{
    public static SyncIndicatorKind Classify(SyncStatusView status, bool syncing) => status switch
    {
        { IsAvailable: false } => SyncIndicatorKind.Hidden,
        { Local.State: SyncLinkState.Unlinked } => SyncIndicatorKind.LocalOnly,
        { Local.State: SyncLinkState.NeedsReconnect or SyncLinkState.DeviceRevoked } => SyncIndicatorKind.Attention,
        { LastCycle.Status: SyncCycleStatus.Blocked } => SyncIndicatorKind.Attention,
        { Counts.Failed: > 0 } => SyncIndicatorKind.Attention,
        { Local.IsEnabled: false } => SyncIndicatorKind.Paused,
        _ when syncing => SyncIndicatorKind.Syncing,
        { LastCycle.Status: SyncCycleStatus.Transient } => SyncIndicatorKind.Offline,

        // Logo depois de abrir o app ainda não houve ciclo: vale o último erro gravado.
        { LastCycle: null, Local.LastErrorCode: not null } => SyncIndicatorKind.Offline,
        { Counts.Pending: > 0 } => SyncIndicatorKind.Pending,
        _ when ToReview(status.Counts) > 0 => SyncIndicatorKind.Review,
        _ => SyncIndicatorKind.Synced,
    };

    /// <summary>O que pede revisão da pessoa: conflitos, comandos recebidos e recebidos com falha.</summary>
    public static int ToReview(SyncCounts counts) => counts.OpenConflicts + counts.CommandsToReview + counts.ApplyFailures;
}
