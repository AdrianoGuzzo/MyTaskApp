using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Domain.Lifecycle;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// Um checklist nas áreas de Arquivados e Lixeira (§3, §5). Carrega só o que a
/// tela desenha; a trilha de auditoria chega depois, quando alguém abre os
/// detalhes.
/// </summary>
public sealed partial class ChecklistCardViewModel : ObservableObject
{
    /// <summary>Abaixo disso o prazo vira alerta em vez de informação.</summary>
    private const int ExpiringSoonDays = 3;

    public ChecklistCardViewModel(
        ChecklistSummaryRow row,
        DataRetentionPolicy retention,
        DateTimeOffset nowUtc)
    {
        TaskId = row.TaskId;
        Title = row.Title;
        Description = row.Description;
        HasDescription = !string.IsNullOrWhiteSpace(row.Description);
        IsInTrash = row.DeletedAt is not null;

        // Um checklist que já estava arquivado quando foi excluído volta para o
        // arquivo, e não para a lista principal. Dizer isso aqui evita a
        // surpresa de restaurar e não encontrar nada em "Hoje".
        WasArchivedBeforeTrash = IsInTrash && row.ArchivedAt is not null;

        StateLabel = IsInTrash ? "NA LIXEIRA" : "ARQUIVADO";

        WhenLabel = IsInTrash
            ? $"Excluído em {Format(row.DeletedAt)}"
            : $"Arquivado em {Format(row.ArchivedAt)}";

        DeletedByLabel = row.DeletedBy is { } who ? $"por {who}" : null;
        HasDeletedBy = DeletedByLabel is not null;

        ConcludedLabel = row.ConcludedAt is not null
            ? $"Concluído em {Format(row.ConcludedAt)}"
            : "Não concluído";

        ItemsLabel = row.TotalItems == 1
            ? $"{row.CompletedItems} de 1 item concluído"
            : $"{row.CompletedItems} de {row.TotalItems} itens concluídos";

        CreatedLabel = $"Criado em {Format(row.CreatedAt)}";

        if (IsInTrash && row.DeletedAt is { } deletedAt)
        {
            var daysLeft = retention.DaysLeftInTrash(deletedAt, nowUtc);

            DaysLeftLabel = daysLeft switch
            {
                0 => "Será excluído definitivamente na próxima verificação",
                1 => "Resta 1 dia até a exclusão definitiva",
                _ => $"Restam {daysLeft} dias até a exclusão definitiva",
            };

            IsExpiringSoon = daysLeft <= ExpiringSoonDays;
        }
    }

    public Guid TaskId { get; }

    public string Title { get; }

    public string? Description { get; }

    public bool HasDescription { get; }

    public bool IsInTrash { get; }

    public bool WasArchivedBeforeTrash { get; }

    public string StateLabel { get; }

    public string WhenLabel { get; }

    public string? DeletedByLabel { get; }

    public bool HasDeletedBy { get; }

    public string ConcludedLabel { get; }

    public string CreatedLabel { get; }

    public string ItemsLabel { get; }

    /// <summary>Nulo fora da lixeira: no arquivo não há prazo correndo.</summary>
    public string? DaysLeftLabel { get; }

    public bool HasDaysLeft => DaysLeftLabel is not null;

    public bool IsExpiringSoon { get; }

    /// <summary>A trilha de auditoria (§8), carregada sob demanda.</summary>
    public ObservableCollection<ChecklistAuditLineViewModel> Audit { get; } = [];

    /// <summary>
    /// Abre os detalhes. A tela escuta para buscar a trilha uma vez só — abrir e
    /// fechar o mesmo cartão não pode virar uma consulta por clique.
    /// </summary>
    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isAuditLoaded;

    [ObservableProperty]
    private bool _hasNoAudit;

    private static string Format(DateTimeOffset? instant) =>
        instant is { } value
            ? value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
            : "—";
}
