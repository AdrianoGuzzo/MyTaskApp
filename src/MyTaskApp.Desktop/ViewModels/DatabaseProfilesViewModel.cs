using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// A aba "Perfis" (ADR-056): os perfis de cópia, que se repetem, e os de
/// anonimização, com as regras confirmadas, as sugestões e o script do DBA.
/// </summary>
/// <remarks>
/// As sugestões chegam desmarcadas: "name" numa tabela de produtos não é dado
/// pessoal, e só quem conhece o banco decide. Só o que foi marcado é gravado.
/// </remarks>
public sealed partial class DatabaseProfilesViewModel(
    IUseCaseRunner runner,
    IConfirmationDialog confirmation,
    ILogger<DatabaseProfilesViewModel> logger) : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    // --- Perfil de cópia ------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingCopy), nameof(CopyFormTitle))]
    private Guid? _editingCopyId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCopyProfileCommand))]
    private string _copyName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCopyProfileCommand))]
    [NotifyPropertyChangedFor(nameof(CopyNeedsSourceDatabase))]
    private DatabaseConnectionItemViewModel? _copySource;

    /// <summary>O banco da origem, quando ela é só o servidor (ADR-057).</summary>
    [ObservableProperty]
    private string _copySourceDatabase = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCopyProfileCommand))]
    private DatabaseConnectionItemViewModel? _copyDestination;

    [ObservableProperty]
    private AnonymizationProfileRow? _copyAnonymizationProfile;

    [ObservableProperty]
    private bool _copyRequireAnonymization = true;

    [ObservableProperty]
    private bool _copyIncludeSchema = true;

    [ObservableProperty]
    private bool _copyIncludeData = true;

    [ObservableProperty]
    private bool _copyRecreateDestination = true;

    [ObservableProperty]
    private bool _copyVerifyAfterRestore = true;

    [ObservableProperty]
    private bool _copyKeepAnonymizedArtifact;

    // --- Perfil de anonimização -----------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingAnonymization), nameof(AnonymizationFormTitle))]
    private Guid? _editingAnonymizationId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAnonymizationProfileCommand))]
    private string _anonymizationName = string.Empty;

    [ObservableProperty]
    private string _anonymizationDescription = string.Empty;

    /// <summary>A conexão de onde ler as colunas para sugerir e pré-visualizar — em geral, a própria origem (ADR-058).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAnonymizationProfileCommand), nameof(SuggestColumnsCommand), nameof(PreviewCommand))]
    [NotifyPropertyChangedFor(nameof(NeedsProfileDatabase))]
    private DatabaseConnectionItemViewModel? _columnsConnection;

    /// <summary>
    /// O banco em que ler as colunas e pré-visualizar, quando a conexão é só o
    /// servidor (ADR-057). Não é gravado no perfil: o vínculo banco ↔
    /// anonimização fica no apelido.
    /// </summary>
    [ObservableProperty]
    private string _profileDatabase = string.Empty;

    /// <summary>Filtra a lista de colunas por schema, tabela, coluna ou motivo. "Selecionar todas" vale só para as visíveis.</summary>
    [ObservableProperty]
    private string _ruleFilter = string.Empty;

    /// <summary>Filtra a lista de tabelas por schema ou nome.</summary>
    [ObservableProperty]
    private string _tableFilter = string.Empty;

    private ObservableCollection<AnonymizationRuleItemViewModel>? _rules;

    private ObservableCollection<SkippedTableItemViewModel>? _tables;

    public ObservableCollection<DatabaseConnectionItemViewModel> Connections { get; } = [];

    public ObservableCollection<DatabaseCopyProfileRow> CopyProfiles { get; } = [];

    public ObservableCollection<AnonymizationProfileRow> AnonymizationProfiles { get; } = [];

    /// <summary>
    /// As colunas do perfil: as já confirmadas e as sugestões. Marcar ou
    /// desmarcar uma delas atualiza a contagem e o "Selecionar todas".
    /// </summary>
    public ObservableCollection<AnonymizationRuleItemViewModel> Rules => _rules ??= TrackRules();

    /// <summary>
    /// As tabelas do banco (depois de "Sugerir colunas") e as já marcadas no
    /// perfil. Marcada, a tabela vai vazia para o destino.
    /// </summary>
    public ObservableCollection<SkippedTableItemViewModel> Tables => _tables ??= TrackTables();

    public bool HasTables => Tables.Count > 0;

    /// <summary>"3 de 120 tabela(s) vão sem dados" — e quantas o filtro escondeu.</summary>
    public string TablesSummary
    {
        get
        {
            var skipped = Tables.Count(table => table.IsSkipped);
            var hidden = Tables.Count(table => !table.IsShown);
            var summary = $"{skipped} de {Tables.Count} tabela(s) vão sem dados.";

            return hidden == 0 ? summary : $"{summary} {hidden} escondida(s) pelo filtro.";
        }
    }

    public ObservableCollection<SavedDatabaseItemViewModel> SavedDatabases { get; } = [];

    public bool HasSavedDatabases => SavedDatabases.Count > 0;

    public bool CopyNeedsSourceDatabase => CopySource is { HasDatabase: false };

    public bool NeedsProfileDatabase => ColumnsConnection is { HasDatabase: false };

    /// <summary>A pré-visualização: as colunas marcadas, com alguns valores já mascarados pelo servidor.</summary>
    public ObservableCollection<MaskedPreviewItemViewModel> Previews { get; } = [];

    /// <summary>O que impede as máscaras, visto na pré-visualização (tipo errado, chave, coluna que sumiu).</summary>
    public ObservableCollection<string> PreviewProblems { get; } = [];

    public bool IsEditingCopy => EditingCopyId is not null;

    public string CopyFormTitle => IsEditingCopy ? "Editar perfil de cópia" : "Novo perfil de cópia";

    public bool IsEditingAnonymization => EditingAnonymizationId is not null;

    public string AnonymizationFormTitle => IsEditingAnonymization ? "Editar perfil de anonimização" : "Novo perfil de anonimização";

    public bool HasRules => Rules.Count > 0;

    public bool HasPreview => Previews.Count > 0 || PreviewProblems.Count > 0;

    public string RulesSummary =>
        $"{Rules.Count(rule => rule.IsConfirmed)} de {Rules.Count} coluna(s) confirmada(s).";

    private IEnumerable<AnonymizationRuleItemViewModel> ShownRules => Rules.Where(rule => rule.IsShown);

    /// <summary>
    /// O estado do "Selecionar todas": marcado com todas as visíveis marcadas,
    /// vazio com nenhuma, e indeterminado (—) no meio.
    /// </summary>
    public bool? AllShownConfirmed
    {
        get
        {
            var shown = ShownRules.ToList();

            if (shown.Count == 0 || shown.All(rule => !rule.IsConfirmed))
            {
                return false;
            }

            return shown.All(rule => rule.IsConfirmed) ? true : null;
        }
    }

    public bool HasShownRules => ShownRules.Any();

    /// <summary>"12 de 40 colunas serão mascaradas" — e quantas o filtro escondeu.</summary>
    public string SelectionSummary
    {
        get
        {
            var confirmed = Rules.Count(rule => rule.IsConfirmed);
            var hidden = Rules.Count(rule => !rule.IsShown);
            var summary = $"{confirmed} de {Rules.Count} coluna(s) marcada(s) para mascarar.";

            return hidden == 0 ? summary : $"{summary} {hidden} escondida(s) pelo filtro.";
        }
    }

    /// <summary>Algo foi salvo ou excluído: os seletores das outras abas recarregam.</summary>
    public event Action? Changed;

    /// <summary>"Usar na cópia": a janela troca para a aba Copiar Banco com o perfil aplicado.</summary>
    public event Action<DatabaseCopyProfileRow>? UseRequested;

    /// <summary>"Usar na cópia" de um apelido: a janela troca para a aba Copiar Banco com ele escolhido.</summary>
    public event Action<SavedDatabaseRow>? UseSavedDatabaseRequested;

    public void SetCatalog(
        IReadOnlyList<DatabaseConnectionItemViewModel> connections,
        IReadOnlyList<AnonymizationProfileRow> anonymizationProfiles,
        IReadOnlyList<DatabaseCopyProfileRow> copyProfiles,
        IReadOnlyList<SavedDatabaseRow>? savedDatabases = null)
    {
        Replace(Connections, connections);
        Replace(AnonymizationProfiles, anonymizationProfiles);
        Replace(CopyProfiles, copyProfiles);
        Replace(SavedDatabases, (savedDatabases ?? []).Select(saved => new SavedDatabaseItemViewModel(
            saved,
            ConnectionName(saved.ConnectionId),
            anonymizationProfiles.FirstOrDefault(profile => profile.Id == saved.AnonymizationProfileId)?.Name)));
        OnPropertyChanged(nameof(HasSavedDatabases));
    }

    public string ConnectionName(Guid id) => Connections.FirstOrDefault(connection => connection.Id == id)?.Name ?? "?";

    // --- Perfis de cópia ---------------------------------------------------------

    [RelayCommand]
    public void NewCopyProfile()
    {
        EditingCopyId = null;
        CopyName = string.Empty;
        CopySource = null;
        CopySourceDatabase = string.Empty;
        CopyDestination = null;
        CopyAnonymizationProfile = null;
        ApplyCopyOptions(DatabaseCopyOptions.Default);
    }

    [RelayCommand]
    public void EditCopyProfile(DatabaseCopyProfileRow profile)
    {
        EditingCopyId = profile.Id;
        CopyName = profile.Name;
        CopySource = Connections.FirstOrDefault(connection => connection.Id == profile.SourceConnectionId);
        CopySourceDatabase = profile.SourceDatabase ?? string.Empty;
        CopyDestination = Connections.FirstOrDefault(connection => connection.Id == profile.DestinationConnectionId);
        CopyAnonymizationProfile = AnonymizationProfiles.FirstOrDefault(item => item.Id == profile.AnonymizationProfileId);
        ApplyCopyOptions(profile.Options);
    }

    [RelayCommand]
    public void UseCopyProfile(DatabaseCopyProfileRow profile) => UseRequested?.Invoke(profile);

    private bool CanSaveCopyProfile() =>
        !string.IsNullOrWhiteSpace(CopyName) && CopySource is not null && CopyDestination is not null;

    [RelayCommand(CanExecute = nameof(CanSaveCopyProfile))]
    public async Task SaveCopyProfileAsync(CancellationToken cancellationToken = default)
    {
        var command = new SaveDatabaseCopyProfile(
            EditingCopyId,
            CopyName,
            CopySource!.Id,
            CopyDestination!.Id,
            CopyRequireAnonymization ? CopyAnonymizationProfile?.Id : null,
            new DatabaseCopyOptions(
                CopyRequireAnonymization,
                CopyIncludeSchema,
                CopyIncludeData,
                CopyRecreateDestination,
                CopyVerifyAfterRestore,
                CopyKeepAnonymizedArtifact),
            CopyNeedsSourceDatabase ? CopySourceDatabase : null);

        if (await TryAsync(
                () => runner.RunAsync<SaveDatabaseCopyProfileHandler, Guid>((handler, token) => handler.HandleAsync(command, token), cancellationToken),
                "Não foi possível salvar o perfil de cópia."))
        {
            NewCopyProfile();
            StatusMessage = "Perfil de cópia salvo.";
            Changed?.Invoke();
        }
    }

    [RelayCommand]
    public async Task DeleteCopyProfileAsync(DatabaseCopyProfileRow profile, CancellationToken cancellationToken = default)
    {
        if (!await confirmation.AskAsync(new ConfirmationRequest(
                $"Excluir {profile.Name}?",
                "Só o perfil sai. Conexões, bancos e o histórico das cópias ficam.",
                "Excluir")))
        {
            return;
        }

        if (await TryAsync(
                () => runner.RunAsync<DeleteDatabaseCopyProfileHandler>(
                    (handler, token) => handler.HandleAsync(new DeleteDatabaseCopyProfile(profile.Id), token), cancellationToken),
                "Não foi possível excluir o perfil."))
        {
            StatusMessage = "Perfil de cópia excluído.";
            Changed?.Invoke();
        }
    }

    // --- Apelidos (ADR-057) ------------------------------------------------------

    [RelayCommand]
    public void UseSavedDatabase(SavedDatabaseItemViewModel item) => UseSavedDatabaseRequested?.Invoke(item.Row);

    [RelayCommand]
    public async Task DeleteSavedDatabaseAsync(SavedDatabaseItemViewModel item, CancellationToken cancellationToken = default)
    {
        if (!await confirmation.AskAsync(new ConfirmationRequest(
                $"Excluir o apelido {item.Alias}?",
                "Só o apelido sai. Os bancos já copiados com ele continuam no destino.",
                "Excluir")))
        {
            return;
        }

        if (await TryAsync(
                () => runner.RunAsync<DeleteSavedDatabaseHandler>(
                    (handler, token) => handler.HandleAsync(new DeleteSavedDatabase(item.Id), token), cancellationToken),
                "Não foi possível excluir o apelido."))
        {
            StatusMessage = "Apelido excluído.";
            Changed?.Invoke();
        }
    }

    // --- Perfis de anonimização ------------------------------------------------

    [RelayCommand]
    public void NewAnonymizationProfile()
    {
        EditingAnonymizationId = null;
        AnonymizationName = string.Empty;
        AnonymizationDescription = string.Empty;
        ColumnsConnection = null;
        RuleFilter = string.Empty;
        TableFilter = string.Empty;
        Rules.Clear();
        Tables.Clear();
        ClearPreview();
        NotifyRules();
    }

    [RelayCommand]
    public void EditAnonymizationProfile(AnonymizationProfileRow profile)
    {
        EditingAnonymizationId = profile.Id;
        AnonymizationName = profile.Name;
        AnonymizationDescription = profile.Description ?? string.Empty;
        ColumnsConnection = Connections.FirstOrDefault(connection => connection.Id == profile.ConnectionId);
        RuleFilter = string.Empty;
        TableFilter = string.Empty;
        ClearPreview();
        Replace(Tables, profile.SkippedTables.Select(table => new SkippedTableItemViewModel(table.Schema, table.Table, skipped: true)));
        Replace(Rules, profile.Rules.Select(rule => new AnonymizationRuleItemViewModel(rule, confirmed: true)));
        MarkSkippedRules();
        NotifyRules();
    }

    partial void OnTableFilterChanged(string value)
    {
        foreach (var table in Tables)
        {
            table.IsShown = table.Matches(value.Trim());
        }

        NotifyTables();
    }

    private ObservableCollection<SkippedTableItemViewModel> TrackTables()
    {
        var tables = new ObservableCollection<SkippedTableItemViewModel>();

        tables.CollectionChanged += (_, change) =>
        {
            foreach (var table in change.OldItems?.OfType<SkippedTableItemViewModel>() ?? [])
            {
                table.PropertyChanged -= OnTableChanged;
            }

            foreach (var table in change.NewItems?.OfType<SkippedTableItemViewModel>() ?? [])
            {
                table.PropertyChanged += OnTableChanged;
                table.IsShown = table.Matches(TableFilter.Trim());
            }

            NotifyTables();
        };

        return tables;
    }

    private void OnTableChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs change)
    {
        if (change.PropertyName is nameof(SkippedTableItemViewModel.IsSkipped))
        {
            MarkSkippedRules();
            NotifyTables();
        }
    }

    /// <summary>As colunas de uma tabela sem dados avisam que a máscara delas não chega a ser usada.</summary>
    private void MarkSkippedRules()
    {
        var skipped = Tables.Where(table => table.IsSkipped).Select(table => table.TableKey).ToHashSet(StringComparer.Ordinal);

        foreach (var rule in Rules)
        {
            rule.IsTableSkipped = skipped.Contains(rule.TableKey);
        }
    }

    private void NotifyTables()
    {
        OnPropertyChanged(nameof(HasTables));
        OnPropertyChanged(nameof(TablesSummary));
    }

    private List<SkippedTableRow> SkippedTableRows() =>
        Tables.Where(table => table.IsSkipped).Select(table => table.ToRow()).ToList();

    /// <summary>
    /// "Selecionar todas": com todas as visíveis marcadas, desmarca; senão
    /// (nenhuma ou só algumas), marca todas. O filtro limita o alcance.
    /// </summary>
    [RelayCommand]
    public void ToggleAllShown()
    {
        var target = AllShownConfirmed != true;

        foreach (var rule in ShownRules)
        {
            rule.IsConfirmed = target;
        }

        NotifyRules();
    }

    /// <summary>Marca as visíveis de alta probabilidade, sem mexer nas outras.</summary>
    [RelayCommand]
    public void ConfirmHighProbability()
    {
        foreach (var rule in ShownRules.Where(rule => rule.IsHigh))
        {
            rule.IsConfirmed = true;
        }

        NotifyRules();
    }

    partial void OnRuleFilterChanged(string value) => ApplyRuleFilter();

    private void ApplyRuleFilter()
    {
        var filter = RuleFilter.Trim();

        foreach (var rule in Rules)
        {
            rule.IsShown = rule.Matches(filter);
        }

        NotifyRules();
    }

    private ObservableCollection<AnonymizationRuleItemViewModel> TrackRules()
    {
        var rules = new ObservableCollection<AnonymizationRuleItemViewModel>();

        rules.CollectionChanged += (_, change) =>
        {
            foreach (var rule in change.OldItems?.OfType<AnonymizationRuleItemViewModel>() ?? [])
            {
                rule.PropertyChanged -= OnRuleChanged;
            }

            foreach (var rule in change.NewItems?.OfType<AnonymizationRuleItemViewModel>() ?? [])
            {
                rule.PropertyChanged += OnRuleChanged;
                rule.IsShown = rule.Matches(RuleFilter.Trim());
            }

            // Clear() não lista os itens que saíram; quem fica sem lista não avisa mais ninguém.
            NotifyRules();
        };

        return rules;
    }

    private void OnRuleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs change)
    {
        if (change.PropertyName is nameof(AnonymizationRuleItemViewModel.IsConfirmed))
        {
            NotifyRules();
        }
    }

    private bool CanSuggest() => ColumnsConnection is not null;

    /// <summary>
    /// Lê só os nomes e tipos das colunas e sugere. O que já é regra fica como
    /// está; o novo chega desmarcado, com a probabilidade e o motivo. Lê também
    /// a lista de tabelas, para escolher as que vão sem dados.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSuggest))]
    public async Task SuggestColumnsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ColumnSuggestion>? suggestions = null;
        IReadOnlyList<SourceTableRow>? tables = null;
        var connectionId = ColumnsConnection!.Id;
        var database = ChosenProfileDatabase;

        await TryAsync(
            async () =>
            {
                suggestions = await runner.RunAsync<SuggestSensitiveColumnsHandler, IReadOnlyList<ColumnSuggestion>>(
                    (handler, token) => handler.HandleAsync(new SuggestSensitiveColumns(connectionId, database), token), cancellationToken);
                tables = await runner.RunAsync<GetSourceTablesHandler, IReadOnlyList<SourceTableRow>>(
                    (handler, token) => handler.HandleAsync(new GetSourceTables(connectionId, database), token), cancellationToken);
            },
            "Não foi possível ler as colunas do banco.");

        if (suggestions is null || tables is null)
        {
            return;
        }

        MergeTables(tables);

        var known = Rules.Select(rule => rule.ColumnKey).ToHashSet(StringComparer.Ordinal);
        var added = 0;

        foreach (var suggestion in suggestions.Where(suggestion => !known.Contains(suggestion.ColumnKey)))
        {
            Rules.Add(new AnonymizationRuleItemViewModel(
                new AnonymizationRuleRow(suggestion.Schema, suggestion.Table, suggestion.Column, suggestion.Method, suggestion.Argument, suggestion.Sensitivity),
                confirmed: false,
                suggestion.Reason));
            added++;
        }

        MarkSkippedRules();
        NotifyRules();
        StatusMessage = added == 0
            ? "Nenhuma coluna nova parece dado pessoal."
            : $"{added} possível(is) dado(s) sensível(is). Marque o que é de fato dado pessoal e salve.";
    }

    /// <summary>As tabelas lidas do banco, com as já marcadas no perfil mantidas — mesmo as que sumiram dele.</summary>
    private void MergeTables(IReadOnlyList<SourceTableRow> rows)
    {
        var known = Tables.ToDictionary(table => table.TableKey, StringComparer.Ordinal);
        var merged = new List<SkippedTableItemViewModel>();

        foreach (var row in rows)
        {
            if (known.Remove(row.TableKey, out var existing))
            {
                existing.Refresh(row);
                merged.Add(existing);
            }
            else
            {
                merged.Add(new SkippedTableItemViewModel(row.Schema, row.Table, skipped: false, row));
            }
        }

        merged.AddRange(known.Values.Where(table => table.IsSkipped));
        Replace(Tables, merged);
    }

    private bool CanSaveAnonymizationProfile() => !string.IsNullOrWhiteSpace(AnonymizationName) && ColumnsConnection is not null;

    [RelayCommand(CanExecute = nameof(CanSaveAnonymizationProfile))]
    public async Task SaveAnonymizationProfileAsync(CancellationToken cancellationToken = default)
    {
        var command = new SaveAnonymizationProfile(
            EditingAnonymizationId,
            AnonymizationName,
            AnonymizationDescription,
            ColumnsConnection!.Id,
            Rules.Where(rule => rule.IsConfirmed).Select(rule => rule.ToRow()).ToList())
        {
            SkippedTables = SkippedTableRows(),
        };

        Guid id = default;

        if (await TryAsync(
                async () => id = await runner.RunAsync<SaveAnonymizationProfileHandler, Guid>(
                    (handler, token) => handler.HandleAsync(command, token), cancellationToken),
                "Não foi possível salvar o perfil de anonimização."))
        {
            EditingAnonymizationId = id;

            // As sugestões não marcadas saem: o perfil é o que foi confirmado.
            Replace(Rules, Rules.Where(rule => rule.IsConfirmed).ToList());
            NotifyRules();
            StatusMessage = "Perfil de anonimização salvo. Nada foi instalado no banco: as máscaras entram no SELECT de cada cópia.";
            Changed?.Invoke();
        }
    }

    [RelayCommand]
    public async Task DeleteAnonymizationProfileAsync(AnonymizationProfileRow profile, CancellationToken cancellationToken = default)
    {
        if (!await confirmation.AskAsync(new ConfirmationRequest(
                $"Excluir {profile.Name}?",
                "As regras saem do app. Os bancos já copiados com elas continuam como estão.",
                "Excluir")))
        {
            return;
        }

        if (await TryAsync(
                () => runner.RunAsync<DeleteAnonymizationProfileHandler>(
                    (handler, token) => handler.HandleAsync(new DeleteAnonymizationProfile(profile.Id), token), cancellationToken),
                "Não foi possível excluir o perfil."))
        {
            if (EditingAnonymizationId == profile.Id)
            {
                NewAnonymizationProfile();
            }

            StatusMessage = "Perfil de anonimização excluído.";
            Changed?.Invoke();
        }
    }

    private bool CanPreview() => ColumnsConnection is not null && Rules.Any(rule => rule.IsConfirmed);

    /// <summary>
    /// As colunas marcadas, com alguns valores já mascarados pelo servidor —
    /// o mesmo SELECT da cópia, com LIMIT. O valor real não chega à tela.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPreview))]
    public async Task PreviewAsync(CancellationToken cancellationToken = default)
    {
        MaskingPreviewResult? result = null;
        var query = new PreviewMasking(
            ColumnsConnection!.Id,
            ChosenProfileDatabase,
            Rules.Where(rule => rule.IsConfirmed).Select(rule => rule.ToRow()).ToList())
        {
            SkippedTables = SkippedTableRows(),
        };

        ClearPreview();

        await TryAsync(
            async () => result = await runner.RunAsync<PreviewMaskingHandler, MaskingPreviewResult>(
                (handler, token) => handler.HandleAsync(query, token), cancellationToken),
            "Não foi possível pré-visualizar as máscaras.");

        if (result is null)
        {
            return;
        }

        Replace(PreviewProblems, result.Problems);
        Replace(Previews, result.Columns.Select(column => new MaskedPreviewItemViewModel(column)));
        OnPropertyChanged(nameof(HasPreview));
        StatusMessage = result.Problems.Count == 0
            ? "Valores já mascarados pelo servidor. O dado real não saiu de lá."
            : null;
    }

    private void ClearPreview()
    {
        Previews.Clear();
        PreviewProblems.Clear();
        OnPropertyChanged(nameof(HasPreview));
    }

    private string? ChosenProfileDatabase => NeedsProfileDatabase ? ProfileDatabase : null;

    private void ApplyCopyOptions(DatabaseCopyOptions options)
    {
        CopyRequireAnonymization = options.RequireAnonymization;
        CopyIncludeSchema = options.IncludeSchema;
        CopyIncludeData = options.IncludeData;
        CopyRecreateDestination = options.RecreateDestination;
        CopyVerifyAfterRestore = options.VerifyAfterRestore;
        CopyKeepAnonymizedArtifact = options.KeepAnonymizedArtifact;
    }

    private void NotifyRules()
    {
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(RulesSummary));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(AllShownConfirmed));
        OnPropertyChanged(nameof(HasShownRules));
        PreviewCommand.NotifyCanExecuteChanged();
    }

    private async Task<bool> TryAsync(Func<Task> operation, string fallbackMessage)
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            await operation();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
            return false;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "DatabaseProfilesScreenOperationFailed");
            ErrorMessage = fallbackMessage;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var snapshot = items.ToList();
        target.Clear();

        foreach (var item in snapshot)
        {
            target.Add(item);
        }
    }
}
