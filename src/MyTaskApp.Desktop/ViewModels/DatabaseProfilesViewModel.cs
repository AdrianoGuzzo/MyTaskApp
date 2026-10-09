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
    IClipboardWriter clipboard,
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
    [NotifyCanExecuteChangedFor(nameof(GenerateScriptCommand), nameof(ValidateOnServerCommand))]
    private Guid? _editingAnonymizationId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAnonymizationProfileCommand))]
    private string _anonymizationName = string.Empty;

    [ObservableProperty]
    private string _anonymizationDescription = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAnonymizationProfileCommand), nameof(SuggestColumnsCommand))]
    [NotifyPropertyChangedFor(nameof(NeedsProfileDatabase))]
    private DatabaseConnectionItemViewModel? _maskedConnection;

    /// <summary>
    /// O banco em que ler colunas, gerar o script e validar, quando a conexão
    /// mascarada é só o servidor (ADR-057). Não é gravado no perfil: o vínculo
    /// banco ↔ anonimização fica no apelido.
    /// </summary>
    [ObservableProperty]
    private string _profileDatabase = string.Empty;

    [ObservableProperty]
    private string _policyName = AnonymizationProfile.DefaultPolicyName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScript))]
    private string? _script;

    public ObservableCollection<DatabaseConnectionItemViewModel> Connections { get; } = [];

    public ObservableCollection<DatabaseCopyProfileRow> CopyProfiles { get; } = [];

    public ObservableCollection<AnonymizationProfileRow> AnonymizationProfiles { get; } = [];

    public ObservableCollection<AnonymizationRuleItemViewModel> Rules { get; } = [];

    public ObservableCollection<SavedDatabaseItemViewModel> SavedDatabases { get; } = [];

    public bool HasSavedDatabases => SavedDatabases.Count > 0;

    public bool CopyNeedsSourceDatabase => CopySource is { HasDatabase: false };

    public bool NeedsProfileDatabase => MaskedConnection is { HasDatabase: false };

    public ObservableCollection<string> ServerFindings { get; } = [];

    public bool IsEditingCopy => EditingCopyId is not null;

    public string CopyFormTitle => IsEditingCopy ? "Editar perfil de cópia" : "Novo perfil de cópia";

    public bool IsEditingAnonymization => EditingAnonymizationId is not null;

    public string AnonymizationFormTitle => IsEditingAnonymization ? "Editar perfil de anonimização" : "Novo perfil de anonimização";

    public bool HasScript => !string.IsNullOrEmpty(Script);

    public bool HasRules => Rules.Count > 0;

    public bool HasServerFindings => ServerFindings.Count > 0;

    public string RulesSummary =>
        $"{Rules.Count(rule => rule.IsConfirmed)} de {Rules.Count} coluna(s) confirmada(s).";

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
        MaskedConnection = null;
        PolicyName = AnonymizationProfile.DefaultPolicyName;
        Script = null;
        Rules.Clear();
        ServerFindings.Clear();
        NotifyRules();
    }

    [RelayCommand]
    public void EditAnonymizationProfile(AnonymizationProfileRow profile)
    {
        EditingAnonymizationId = profile.Id;
        AnonymizationName = profile.Name;
        AnonymizationDescription = profile.Description ?? string.Empty;
        MaskedConnection = Connections.FirstOrDefault(connection => connection.Id == profile.ConnectionId);
        PolicyName = profile.PolicyName;
        Script = null;
        ServerFindings.Clear();
        Replace(Rules, profile.Rules.Select(rule => new AnonymizationRuleItemViewModel(rule, confirmed: true)));
        NotifyRules();
    }

    private bool CanSuggest() => MaskedConnection is not null;

    /// <summary>
    /// Lê só os nomes e tipos das colunas e sugere. O que já é regra fica como
    /// está; o novo chega desmarcado, com a probabilidade e o motivo.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSuggest))]
    public async Task SuggestColumnsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ColumnSuggestion>? suggestions = null;

        await TryAsync(
            async () => suggestions = await runner.RunAsync<SuggestSensitiveColumnsHandler, IReadOnlyList<ColumnSuggestion>>(
                (handler, token) => handler.HandleAsync(new SuggestSensitiveColumns(MaskedConnection!.Id, ChosenProfileDatabase), token), cancellationToken),
            "Não foi possível ler as colunas do banco.");

        if (suggestions is null)
        {
            return;
        }

        var known = Rules.Select(rule => rule.ColumnKey).ToHashSet(StringComparer.Ordinal);
        var added = 0;

        foreach (var suggestion in suggestions.Where(suggestion => !known.Contains(suggestion.ColumnKey)))
        {
            Rules.Add(new AnonymizationRuleItemViewModel(
                new AnonymizationRuleRow(suggestion.Schema, suggestion.Table, suggestion.Column, suggestion.Kind, suggestion.Expression, suggestion.Sensitivity),
                confirmed: false,
                suggestion.Reason));
            added++;
        }

        NotifyRules();
        StatusMessage = added == 0
            ? "Nenhuma coluna nova parece dado pessoal."
            : $"{added} possível(is) dado(s) sensível(is). Marque o que é de fato dado pessoal e salve.";
    }

    private bool CanSaveAnonymizationProfile() => !string.IsNullOrWhiteSpace(AnonymizationName) && MaskedConnection is not null;

    [RelayCommand(CanExecute = nameof(CanSaveAnonymizationProfile))]
    public async Task SaveAnonymizationProfileAsync(CancellationToken cancellationToken = default)
    {
        var command = new SaveAnonymizationProfile(
            EditingAnonymizationId,
            AnonymizationName,
            AnonymizationDescription,
            MaskedConnection!.Id,
            PolicyName,
            Rules.Where(rule => rule.IsConfirmed).Select(rule => rule.ToRow()).ToList());

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
            StatusMessage = "Perfil de anonimização salvo. Gere o script e peça ao DBA para executá-lo no banco de origem.";
            Changed?.Invoke();
        }
    }

    [RelayCommand]
    public async Task DeleteAnonymizationProfileAsync(AnonymizationProfileRow profile, CancellationToken cancellationToken = default)
    {
        if (!await confirmation.AskAsync(new ConfirmationRequest(
                $"Excluir {profile.Name}?",
                "As regras saem do app. As que o DBA aplicou no servidor continuam lá.",
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

    private bool HasSavedAnonymizationProfile() => EditingAnonymizationId is not null;

    [RelayCommand(CanExecute = nameof(HasSavedAnonymizationProfile))]
    public async Task GenerateScriptAsync(CancellationToken cancellationToken = default)
    {
        string? script = null;

        await TryAsync(
            async () => script = await runner.RunAsync<GenerateMaskingScriptHandler, string>(
                (handler, token) => handler.HandleAsync(new GenerateMaskingScript(EditingAnonymizationId!.Value, ChosenProfileDatabase), token), cancellationToken),
            "Não foi possível gerar o script.");

        Script = script;
    }

    [RelayCommand]
    public async Task CopyScriptAsync()
    {
        if (Script is null)
        {
            return;
        }

        try
        {
            await clipboard.WriteAsync(Script);
            StatusMessage = "Script copiado. O app não o executa: quem roda é o DBA.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            ErrorMessage = "Não foi possível copiar.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSavedAnonymizationProfile))]
    public async Task ValidateOnServerAsync(CancellationToken cancellationToken = default)
    {
        AnonymizationValidation? validation = null;

        await TryAsync(
            async () => validation = await runner.RunAsync<ValidateAnonymizationProfileHandler, AnonymizationValidation>(
                (handler, token) => handler.HandleAsync(new ValidateAnonymizationProfile(EditingAnonymizationId!.Value, ChosenProfileDatabase), token), cancellationToken),
            "Não foi possível validar o perfil no servidor.");

        if (validation is null)
        {
            return;
        }

        var findings = new List<string>();
        findings.AddRange(validation.Problems().Select(problem => "✗ " + problem));

        if (!validation.Status.TransparentMaskingOn)
        {
            findings.Add("⚠ anon.transparent_dynamic_masking está desligado no banco.");
        }

        if (!validation.Status.CurrentRoleMasked)
        {
            findings.Add("⚠ O usuário da conexão mascarada não está marcado como MASKED.");
        }

        if (validation.ExtraOnServer.Count > 0)
        {
            findings.Add($"⚠ Regras no servidor fora do perfil: {string.Join(", ", validation.ExtraOnServer.Take(10))}.");
        }

        if (validation.UncoveredCandidates.Count > 0)
        {
            findings.Add($"⚠ {validation.UncoveredCandidates.Count} coluna(s) candidata(s) sem regra ({validation.UncoveredHigh} de alta probabilidade).");
        }

        if (findings.Count == 0 || validation.IsValid && findings.All(finding => finding.StartsWith('⚠')))
        {
            findings.Insert(0, $"✓ O servidor tem as {validation.MaskedColumns} regra(s) do perfil.");
        }

        Replace(ServerFindings, findings);
        OnPropertyChanged(nameof(HasServerFindings));
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
