using MyTaskApp.Application.Sync;

namespace MyTaskApp.Mobile.Core.Presentation;

/// <summary>O estado da sincronização como o celular mostra: curto no topo, explicado na Conta.</summary>
public sealed record SyncBadge(SyncIndicatorKind Kind, string Title, string Detail)
{
    /// <summary>Sincronizado, pendente e falha precisam ser distinguíveis de relance.</summary>
    public bool NeedsAttention => Kind is SyncIndicatorKind.Attention or SyncIndicatorKind.Review;

    public bool IsVisible => Kind is not SyncIndicatorKind.Hidden;
}

/// <summary>
/// A mesma precedência do desktop (<see cref="SyncIndicatorClassifier"/>,
/// ADR-063), com o texto do celular: "Este celular" em vez de "este computador".
/// </summary>
public static class SyncStatusText
{
    public static SyncBadge Describe(SyncStatusView status, bool syncing)
    {
        ArgumentNullException.ThrowIfNull(status);

        var kind = SyncIndicatorClassifier.Classify(status, syncing);
        var pending = status.Counts.Pending;

        return kind switch
        {
            SyncIndicatorKind.Hidden => new(kind, string.Empty, "Esta versão não tem servidor de sincronização."),
            SyncIndicatorKind.LocalOnly => new(kind, "Só neste celular", "Entre com sua conta para ver as tarefas dos outros dispositivos."),
            SyncIndicatorKind.Attention => new(kind, "Atenção", Attention(status)),
            SyncIndicatorKind.Paused => new(kind, "Pausada", Kept(pending, "A sincronização está pausada.")),
            SyncIndicatorKind.Syncing => new(kind, "Sincronizando…", "Enviando e recebendo alterações."),
            SyncIndicatorKind.Offline => new(kind, "Sem conexão", Kept(pending, "Sem conexão com o servidor; tenta de novo sozinho.")),
            SyncIndicatorKind.Pending => new(kind, $"Pendente ({pending})", Kept(pending, "Alterações esperando o próximo envio.")),
            SyncIndicatorKind.Review => new(kind, "Para revisar", Review(status.Counts)),
            _ => new(kind, "Sincronizado", "Tudo em dia com o servidor."),
        };
    }

    private static string Attention(SyncStatusView status) => status switch
    {
        { Local.State: SyncLinkState.NeedsReconnect } => "O login venceu. Entre de novo para continuar sincronizando.",
        { Local.State: SyncLinkState.DeviceRevoked } => "Este celular foi removido da conta. Entre de novo para registrá-lo outra vez.",
        { Counts.Failed: > 0 } => $"{Count(status.Counts.Failed, "alteração foi recusada", "alterações foram recusadas")} pelo servidor.",
        _ => string.IsNullOrWhiteSpace(status.Local.LastError) ? "A sincronização parou e precisa de você." : status.Local.LastError!,
    };

    private static string Review(SyncCounts counts)
    {
        var parts = new List<string>();

        if (counts.OpenConflicts > 0)
        {
            parts.Add(Count(counts.OpenConflicts, "conflito", "conflitos"));
        }

        if (counts.ApplyFailures > 0)
        {
            parts.Add(Count(counts.ApplyFailures, "recebido com falha", "recebidos com falha"));
        }

        if (counts.CommandsToReview > 0)
        {
            parts.Add(Count(counts.CommandsToReview, "comando para liberar no computador", "comandos para liberar no computador"));
        }

        return "Em dia, com " + string.Join(", ", parts) + ".";
    }

    private static string Kept(int pending, string sentence) =>
        pending > 0 ? $"{sentence} {Count(pending, "alteração guardada", "alterações guardadas")} neste celular." : sentence;

    private static string Count(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";
}
