using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.Lifecycle;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// "Gerenciamento de dados": Concluídos, Arquivados, Lixeira e as configurações
/// de retenção (§3, §5, §11).
/// </summary>
/// <remarks>
/// Uma janela só, fora do painel, porque é aí que o §12 é atendido: a lista
/// principal não ganha nenhum botão novo, e o que é raro mora onde só vai quem
/// procura.
/// </remarks>
public sealed partial class DataManagementViewModel(
    IUseCaseRunner runner,
    IConfirmationDialog confirmation,
    ILogger<DataManagementViewModel> logger) : ObservableObject
{
    private DataRetentionPolicy _retention = DataRetentionPolicy.Factory;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string _concludedSearch = string.Empty;

    [ObservableProperty]
    private string _archivedSearch = string.Empty;

    [ObservableProperty]
    private string _trashSearch = string.Empty;

    [ObservableProperty]
    private int _concludedPeriodIndex;

    [ObservableProperty]
    private int _archivedPeriodIndex;

    [ObservableProperty]
    private int _trashPeriodIndex;

    [ObservableProperty]
    private bool _autoArchiveEnabled;

    [ObservableProperty]
    private string? _settingsStatus;

    /// <summary>
    /// O recorte por período (§3). Poucas opções e a primeira sendo "tudo": um
    /// filtro que começa filtrando esconderia itens de quem nem sabe que ele
    /// existe.
    /// </summary>
    /// <remarks>
    /// Propriedade de instância, e não estática, porque binding compilado do
    /// Avalonia resolve membros do <c>x:DataType</c> — um estático compilaria e
    /// deixaria o combo vazio na tela.
    /// </remarks>
    public IReadOnlyList<string> PeriodOptions { get; } =
    [
        "Qualquer data",
        "Últimos 30 dias",
        "Últimos 90 dias",
        "Último ano",
    ];

    private static readonly int?[] PeriodDays = [null, 30, 90, 365];

    /// <summary>Quantas opções o combo deve mostrar, para a tela poder conferir.</summary>
    public static int PeriodOptionCount => PeriodDays.Length;

    /// <summary>
    /// O histórico do que foi concluído: a tela "Hoje" só mostra o que terminou
    /// hoje, e sem esta área o concluído de ontem sumiria sem deixar onde olhar.
    /// </summary>
    public ObservableCollection<ChecklistCardViewModel> Concluded { get; } = [];

    public ObservableCollection<ChecklistCardViewModel> Archived { get; } = [];

    public ObservableCollection<ChecklistCardViewModel> Trashed { get; } = [];

    public RetentionChoiceViewModel ArchiveAfter { get; } = new();

    public RetentionChoiceViewModel TrashRetention { get; } = new();

    public bool HasConcluded => Concluded.Count > 0;

    public bool HasArchived => Archived.Count > 0;

    public bool HasTrashed => Trashed.Count > 0;

    /// <summary>A lista principal mudou e precisa ser recarregada.</summary>
    public event Action? ChecklistsChanged;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);
        await RefreshConcludedAsync(cancellationToken);
        await RefreshArchivedAsync(cancellationToken);
        await RefreshTrashAsync(cancellationToken);
    }

    [RelayCommand]
    public Task RefreshConcludedAsync(CancellationToken cancellationToken) =>
        FillAsync(
            ChecklistScope.Concluded,
            ConcludedSearch,
            WindowOf(ConcludedPeriodIndex),
            Concluded,
            cancellationToken);

    [RelayCommand]
    public Task RefreshArchivedAsync(CancellationToken cancellationToken) =>
        FillAsync(
            ChecklistScope.Archived,
            ArchivedSearch,
            WindowOf(ArchivedPeriodIndex),
            Archived,
            cancellationToken);

    [RelayCommand]
    public Task RefreshTrashAsync(CancellationToken cancellationToken) =>
        FillAsync(
            ChecklistScope.Trashed,
            TrashSearch,
            WindowOf(TrashPeriodIndex),
            Trashed,
            cancellationToken);

    /// <summary>Índice fora da lista vira "qualquer data", nunca uma exceção.</summary>
    private static int? WindowOf(int index) =>
        index >= 0 && index < PeriodDays.Length ? PeriodDays[index] : null;

    /// <summary>Guarda um concluído no arquivo (§1). Não pergunta: se desfaz em dois cliques.</summary>
    [RelayCommand]
    public async Task ArchiveAsync(ChecklistCardViewModel card, CancellationToken cancellationToken)
    {
        var archived = await TryAsync(
            () => runner.RunAsync<ArchiveChecklistHandler>(
                (handler, token) => handler.HandleAsync(new ArchiveChecklist(card.TaskId), token),
                cancellationToken),
            "Não foi possível arquivar este checklist.");

        if (archived)
        {
            await AfterChangeAsync("Checklist arquivado.", cancellationToken);
        }
    }

    /// <summary>Tira do arquivo e devolve à lista principal (§1).</summary>
    [RelayCommand]
    public async Task RestoreAsync(ChecklistCardViewModel card, CancellationToken cancellationToken)
    {
        var restored = await TryAsync(
            () => runner.RunAsync<RestoreChecklistHandler>(
                (handler, token) => handler.HandleAsync(new RestoreChecklist(card.TaskId), token),
                cancellationToken),
            "Não foi possível restaurar este checklist.");

        if (restored)
        {
            await AfterChangeAsync("Checklist restaurado com sucesso.", cancellationToken);
        }
    }

    /// <summary>Dos concluídos ou do arquivo para a lixeira — ainda reversível (§4).</summary>
    [RelayCommand]
    public async Task MoveToTrashAsync(
        ChecklistCardViewModel card,
        CancellationToken cancellationToken)
    {
        if (!await confirmation.AskAsync(TrashPrompt(card.Title, _retention.TrashRetentionDays)))
        {
            return;
        }

        var moved = await TryAsync(
            () => runner.RunAsync<MoveChecklistToTrashHandler>(
                (handler, token) => handler.HandleAsync(
                    new MoveChecklistToTrash(card.TaskId), token),
                cancellationToken),
            "Não foi possível mover este checklist para a lixeira.");

        if (moved)
        {
            await AfterChangeAsync("Checklist movido para a lixeira.", cancellationToken);
        }
    }

    /// <summary>Tira da lixeira dentro do prazo (§5).</summary>
    [RelayCommand]
    public async Task RestoreFromTrashAsync(
        ChecklistCardViewModel card,
        CancellationToken cancellationToken)
    {
        var restored = await TryAsync(
            () => runner.RunAsync<RestoreChecklistFromTrashHandler>(
                (handler, token) => handler.HandleAsync(
                    new RestoreChecklistFromTrash(card.TaskId), token),
                cancellationToken),
            "Não foi possível restaurar este checklist.");

        if (!restored)
        {
            return;
        }

        // Diz para onde ele voltou. Restaurar e não achar na tela "Hoje" porque
        // ele continuou arquivado seria a surpresa mais fácil de evitar aqui.
        await AfterChangeAsync(
            card.WasArchivedBeforeTrash
                ? "Checklist restaurado para os arquivados."
                : "Checklist restaurado com sucesso.",
            cancellationToken);
    }

    /// <summary>
    /// Exclusão definitiva (§7). Confirmação forte, e a única operação do app
    /// que não tem volta.
    /// </summary>
    [RelayCommand]
    public async Task PurgeAsync(ChecklistCardViewModel card, CancellationToken cancellationToken)
    {
        if (!await confirmation.AskAsync(PurgePrompt(card.Title)))
        {
            return;
        }

        var purged = await TryAsync(
            () => runner.RunAsync<PurgeChecklistHandler>(
                (handler, token) => handler.HandleAsync(new PurgeChecklist(card.TaskId), token),
                cancellationToken),
            "Não foi possível excluir este checklist.");

        if (purged)
        {
            await AfterChangeAsync(
                "Checklist excluído definitivamente. Essa ação não pode ser desfeita.",
                cancellationToken);
        }
    }

    /// <summary>
    /// Abre e fecha os detalhes do cartão (§3), buscando a trilha de auditoria
    /// na primeira abertura.
    /// </summary>
    [RelayCommand]
    public async Task ToggleDetailsAsync(
        ChecklistCardViewModel card,
        CancellationToken cancellationToken)
    {
        card.IsExpanded = !card.IsExpanded;

        if (card.IsExpanded)
        {
            await ShowAuditAsync(card, cancellationToken);
        }
    }

    /// <summary>
    /// Traz a trilha de auditoria do cartão aberto. Uma vez por cartão: abrir e
    /// fechar não pode virar uma consulta por clique.
    /// </summary>
    [RelayCommand]
    public async Task ShowAuditAsync(
        ChecklistCardViewModel card,
        CancellationToken cancellationToken)
    {
        if (card.IsAuditLoaded)
        {
            return;
        }

        IReadOnlyList<TaskAuditEntry>? entries = null;

        var loaded = await TryAsync(
            async () => entries = await runner
                .RunAsync<GetChecklistAuditHandler, IReadOnlyList<TaskAuditEntry>>(
                    (handler, token) => handler.HandleAsync(
                        new GetChecklistAudit(card.TaskId), token),
                    cancellationToken),
            "Não foi possível carregar o histórico deste checklist.");

        if (!loaded)
        {
            return;
        }

        card.Audit.Clear();

        foreach (var entry in entries!)
        {
            card.Audit.Add(new ChecklistAuditLineViewModel(entry));
        }

        card.HasNoAudit = card.Audit.Count == 0;
        card.IsAuditLoaded = true;
    }

    [RelayCommand]
    public async Task SaveSettingsAsync(CancellationToken cancellationToken)
    {
        var saved = await TryAsync(
            () => runner.RunAsync<UpdateDataRetentionSettingsHandler>(
                (handler, token) => handler.HandleAsync(
                    new UpdateDataRetentionSettings(
                        AutoArchiveEnabled,
                        ArchiveAfter.Days,
                        TrashRetention.Days),
                    token),
                cancellationToken),
            "Não foi possível salvar as configurações.");

        if (!saved)
        {
            return;
        }

        await LoadSettingsAsync(cancellationToken);

        // Os prazos novos mudam o "restam N dias" de cada cartão, então a
        // lixeira é redesenhada com o que acabou de ser gravado.
        await RefreshTrashAsync(cancellationToken);

        SettingsStatus = "Configurações salvas.";
    }

    [RelayCommand]
    public Task RestoreDefaultSettingsAsync(CancellationToken cancellationToken)
    {
        Show(DataRetentionPolicy.Factory);
        SettingsStatus = "Padrão de fábrica restaurado. Salve para aplicar.";

        return Task.CompletedTask;
    }

    /// <summary>A confirmação de exclusão reversível, com o prazo real (§4).</summary>
    public static ConfirmationRequest TrashPrompt(string title, int retentionDays) =>
        new(
            "Mover para a lixeira?",
            $"“{title}” será movido para a lixeira. Ele poderá ser restaurado "
            + $"durante os próximos {retentionDays} "
            + (retentionDays == 1 ? "dia" : "dias")
            + ". Após esse período, será excluído definitivamente.",
            "Mover para a lixeira");

    /// <summary>A confirmação forte (§7).</summary>
    public static ConfirmationRequest PurgePrompt(string title) =>
        new(
            "Excluir definitivamente?",
            $"Esta ação excluirá definitivamente “{title}” e seus dados "
            + "relacionados. Não será possível restaurá-lo posteriormente. "
            + "Deseja continuar?",
            "Excluir definitivamente",
            IsIrreversible: true);

    private async Task LoadSettingsAsync(CancellationToken cancellationToken)
    {
        DataRetentionPolicy? policy = null;

        var loaded = await TryAsync(
            async () => policy = await runner
                .RunAsync<GetDataRetentionSettingsHandler, DataRetentionPolicy>(
                    (handler, token) => handler.HandleAsync(
                        new GetDataRetentionSettings(), token),
                    cancellationToken),
            "Não foi possível carregar as configurações.");

        if (loaded)
        {
            Show(policy!);
        }
    }

    private void Show(DataRetentionPolicy policy)
    {
        _retention = policy;

        AutoArchiveEnabled = policy.AutoArchiveEnabled;
        ArchiveAfter.Load(policy.AutoArchiveAfterDays);
        TrashRetention.Load(policy.TrashRetentionDays);
    }

    private async Task FillAsync(
        ChecklistScope scope,
        string? search,
        int? withinDays,
        ObservableCollection<ChecklistCardViewModel> target,
        CancellationToken cancellationToken)
    {
        ChecklistArchiveView? view = null;

        var loaded = await TryAsync(
            async () => view = await runner
                .RunAsync<GetChecklistArchiveHandler, ChecklistArchiveView>(
                    (handler, token) => handler.HandleAsync(
                        new GetChecklistArchive(scope, search, withinDays), token),
                    cancellationToken),
            "Não foi possível carregar os checklists.");

        if (!loaded)
        {
            return;
        }

        _retention = view!.Retention;

        target.Clear();

        foreach (var row in view.Items)
        {
            target.Add(new ChecklistCardViewModel(row, view.Retention, view.AsOfUtc));
        }

        OnPropertyChanged(nameof(HasConcluded));
        OnPropertyChanged(nameof(HasArchived));
        OnPropertyChanged(nameof(HasTrashed));
    }

    /// <summary>
    /// Toda operação de ciclo de vida mexe em mais de uma área: arquivar tira
    /// dos concluídos, restaurar tira dos arquivados, excluir põe na lixeira.
    /// Recarregar todas é mais barato do que raciocinar sobre qual lista mudou —
    /// e nunca deixa a tela mentindo.
    /// </summary>
    private async Task AfterChangeAsync(string message, CancellationToken cancellationToken)
    {
        await RefreshConcludedAsync(cancellationToken);
        await RefreshArchivedAsync(cancellationToken);
        await RefreshTrashAsync(cancellationToken);

        StatusMessage = message;
        ChecklistsChanged?.Invoke();
    }

    /// <summary>Mesmo caminho de erro do resto do app (ADR-008).</summary>
    private async Task<bool> TryAsync(Func<Task> operation, string fallbackMessage)
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        SettingsStatus = null;

        try
        {
            await operation();
            return true;
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
            return false;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "DataManagementOperationFailed");
            ErrorMessage = fallbackMessage;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
