using MyTaskApp.Domain.Lifecycle;

namespace MyTaskApp.Application.Lifecycle;

/// <summary>A consulta das áreas de Arquivados e Lixeira (§3, §5).</summary>
/// <param name="WithinDays">
/// Janela do recorte por período, contada para trás a partir de agora.
/// <c>null</c> traz tudo. Chega em dias, e não como instante, para que "agora"
/// continue vindo do <c>TimeProvider</c> e não do relógio da tela.
/// </param>
public sealed record GetChecklistArchive(
    ChecklistScope Scope,
    string? Search = null,
    int? WithinDays = null);

/// <summary>
/// O que a tela precisa desenhar de uma vez. A política vem junto porque
/// "restam N dias" é função dela e do instante da consulta — calcular isso na
/// view exigiria uma segunda ida ao banco e abriria espaço para a tela usar um
/// prazo diferente do que a varredura usa.
/// </summary>
public sealed record ChecklistArchiveView(
    ChecklistScope Scope,
    IReadOnlyList<ChecklistSummaryRow> Items,
    DataRetentionPolicy Retention,
    DateTimeOffset AsOfUtc);

public sealed class GetChecklistArchiveHandler(
    IChecklistArchiveQuery query,
    IDataRetentionSettingsStore settings,
    TimeProvider timeProvider)
{
    public async Task<ChecklistArchiveView> HandleAsync(
        GetChecklistArchive command,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        var since = command.WithinDays is > 0 and { } days
            ? now.AddDays(-days)
            : (DateTimeOffset?)null;

        var items = await query.SearchAsync(
            command.Scope,
            command.Search,
            since,
            cancellationToken);

        var retention = await settings.GetAsync(cancellationToken);

        return new ChecklistArchiveView(command.Scope, items, retention, now);
    }
}
