using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// Uma etiqueta na lista da janela de gerenciamento. Expandida, mostra as pastas
/// dela e o formulário de diretório (ADR-026).
/// </summary>
public sealed partial class TagListItemViewModel(TagRow row) : ObservableObject
{
    public TagRow Row { get; } = row;

    public ObservableCollection<TagDirectoryItemViewModel> Directories { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoDirectories))]
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectoriesLabel), nameof(HasNoDirectories))]
    private int _directoryCount = row.DirectoryCount;

    /// <summary>O diretório em edição; <c>null</c> = o formulário acrescenta um novo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingDirectory), nameof(DirectorySaveLabel))]
    private Guid? _editingDirectoryId;

    [ObservableProperty]
    private string _alias = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClone))]
    private string _path = string.Empty;

    /// <summary>De onde a pasta é clonada (ADR-064). Escolher uma pasta que é repositório preenche pelo <c>origin</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCloneOffered), nameof(CanClone))]
    private string _repositoryUrl = string.Empty;

    /// <summary>
    /// O Git foi encontrado: só então a URL e o clone aparecem no formulário
    /// (ADR-064). Sem ele, a URL que já existe é mantida como está.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCloneOffered))]
    private bool _isGitAvailable;

    /// <summary>A pasta do campo Caminho existe? <c>null</c> enquanto não se sabe.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCloneOffered))]
    private bool? _pathExists;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClone))]
    private bool _isCloning;

    /// <summary>
    /// Onde clonar quando o caminho está vazio: a pasta-mãe mais comum dos
    /// diretórios deste computador (ver <see cref="SuggestCloneFolderHandler"/>).
    /// </summary>
    public string? CloneFolder { get; set; }

    // O que a sugestão escreveu. Enquanto o usuário não mexer, uma URL nova troca a sugestão.
    private string? _suggestedPath;
    private string? _suggestedAlias;

    [ObservableProperty]
    private string _directoryName = string.Empty;

    [ObservableProperty]
    private string _directoryDescription = string.Empty;

    /// <summary>A origem que "Iniciar implementação" já traz escolhida para este repositório.</summary>
    [ObservableProperty]
    private string _directoryDefaultBranch = string.Empty;

    public string DirectoriesLabel => DirectoryCount == 0 ? "Diretórios" : $"Diretórios ({DirectoryCount})";

    public bool HasNoDirectories => IsExpanded && DirectoryCount == 0;

    public bool IsEditingDirectory => EditingDirectoryId is not null;

    public string DirectorySaveLabel => IsEditingDirectory ? "Salvar diretório" : "Adicionar diretório";

    /// <summary>Tem URL e a pasta ainda não existe: o formulário oferece clonar nela (ADR-064).</summary>
    public bool IsCloneOffered => IsGitAvailable && !string.IsNullOrWhiteSpace(RepositoryUrl) && PathExists is not true;

    public bool CanClone => !IsCloning && !string.IsNullOrWhiteSpace(RepositoryUrl) && !string.IsNullOrWhiteSpace(Path);

    /// <summary>
    /// O "Procurar…" preenche o path e, se o alias ainda está vazio (ou é o que
    /// a URL sugeriu), sugere um a partir do nome da pasta.
    /// </summary>
    public void UseFolder(string path)
    {
        _suggestedPath = null;
        Path = path;
        SuggestAliasFrom(path);
    }

    /// <summary>"Outra pasta…" do clone: a pasta escolhida é a mãe, e o repositório entra numa subpasta com o nome dele.</summary>
    public void UseCloneFolder(string parent)
    {
        var name = GitRemoteUrl.FolderName(RepositoryUrl);

        _suggestedPath = name is null ? parent : System.IO.Path.Combine(parent, name);
        Path = _suggestedPath;
        SuggestAliasFrom(_suggestedPath);
    }

    /// <summary>
    /// A URL digitada com o caminho em branco já escolhe onde clonar:
    /// <c>{pasta de clone}\{nome do repositório}</c>, e o alias junto
    /// (ADR-064). Um caminho escrito pelo usuário não é tocado.
    /// </summary>
    partial void OnRepositoryUrlChanged(string value)
    {
        if (!IsGitAvailable
            || CloneFolder is null
            || (!string.IsNullOrWhiteSpace(Path) && !string.Equals(Path, _suggestedPath, StringComparison.Ordinal)))
        {
            return;
        }

        var name = GitRemoteUrl.FolderName(value);

        _suggestedPath = name is null ? string.Empty : System.IO.Path.Combine(CloneFolder, name);
        Path = _suggestedPath;
        SuggestAliasFrom(_suggestedPath);
    }

    private void SuggestAliasFrom(string path)
    {
        if (!string.IsNullOrWhiteSpace(Alias) && !string.Equals(Alias, _suggestedAlias, StringComparison.Ordinal))
        {
            return;
        }

        _suggestedAlias = path.Length == 0 ? string.Empty : TagDirectoryItemViewModel.SuggestAlias(path);
        Alias = _suggestedAlias;
    }

    internal void ResetDirectoryForm()
    {
        EditingDirectoryId = null;
        Alias = string.Empty;
        Path = string.Empty;
        RepositoryUrl = string.Empty;
        DirectoryName = string.Empty;
        DirectoryDescription = string.Empty;
        DirectoryDefaultBranch = string.Empty;
        PathExists = null;
        _suggestedPath = null;
        _suggestedAlias = null;
    }

    /// <summary>
    /// O caminho vem antes da URL: num diretório que chegou de outro computador
    /// sem pasta, a URL encontra o caminho vazio e já sugere onde clonar.
    /// </summary>
    internal void EditDirectory(TagDirectoryItemViewModel directory)
    {
        _suggestedPath = null;
        _suggestedAlias = null;
        EditingDirectoryId = directory.Row.Id;
        RepositoryUrl = string.Empty;
        Alias = directory.Alias;
        Path = directory.Path;
        PathExists = directory.IsUnmapped ? false : directory.Exists;
        DirectoryName = directory.Name ?? string.Empty;
        DirectoryDescription = directory.Description ?? string.Empty;
        DirectoryDefaultBranch = directory.DefaultBranch ?? string.Empty;
        RepositoryUrl = directory.RepositoryUrl ?? string.Empty;
    }

    public TagChipViewModel Chip { get; } = new(row.Id, row.Name, row.ColorHex);

    public string UsageLabel => Row.UsageCount switch
    {
        0 => "Ainda não usada",
        1 => "Usada em 1 tarefa",
        _ => $"Usada em {Row.UsageCount} tarefas",
    };

    /// <summary>"Aparece só neste computador" (ADR-062); <c>null</c> sem outro computador na conta.</summary>
    public string? PlacementLabel { get; init; }

    public bool HasPlacementLabel => PlacementLabel is not null;
}

/// <summary>Um lugar escolhido no "Onde aparece…" de uma etiqueta (ADR-062).</summary>
public sealed record TagPlacementChoice(TagListItemViewModel Item, PlacementOption Option);

/// <summary>Uma cor da paleta de um clique.</summary>
public sealed partial class SwatchViewModel(string colorHex) : ObservableObject
{
    public string ColorHex { get; } = colorHex;

    public IBrush Fill { get; } = new ImmutableSolidColorBrush(Color.Parse(colorHex));

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// A janela "Etiquetas": criar, editar e excluir (ADR-025). Fora do painel pelo
/// mesmo motivo do gerenciamento de dados — é coisa de vez em quando, e a lista
/// de hoje não pode virar uma barra de ferramentas.
/// </summary>
public sealed partial class TagsViewModel(
    IUseCaseRunner runner,
    IConfirmationDialog confirmation,
    IDirectoryProbe directoryProbe,
    IDirectoryCommandEditor commandEditor,
    ILogger<TagsViewModel> logger,
    DevicePlacementState? placements = null) : ObservableObject
{
    /// <summary>A conta tem outro computador: cada etiqueta diz onde aparece e pode mudar (ADR-062).</summary>
    public bool HasOtherComputers => placements is { HasOtherComputers: true };

    /// <summary>Os lugares do "Onde aparece…" de uma etiqueta, com o atual marcado.</summary>
    public IReadOnlyList<TagPlacementChoice> PlacementChoices(TagListItemViewModel item) =>
        [.. PlacementOptions.For(placements, item.Row.Placement).Select(option => new TagPlacementChoice(item, option))];

    /// <summary>
    /// Leva a etiqueta para outro computador, para todos, ou de volta para este
    /// (ADR-062). Mandada embora, ela sai do seletor daqui, mas continua nas
    /// tarefas que já marca.
    /// </summary>
    [RelayCommand]
    public async Task PlaceTagAsync(TagPlacementChoice choice, CancellationToken cancellationToken)
    {
        if (choice.Option.IsCurrent)
        {
            return;
        }

        PlaceResult? result = null;

        var placed = await TryAsync(
            async () => result = await runner.RunAsync<PlaceTagHandler, PlaceResult>(
                (handler, token) => handler.HandleAsync(new PlaceTag(choice.Item.Row.Id, choice.Option.Placement), token),
                cancellationToken),
            "Não foi possível mudar onde a etiqueta aparece.");

        if (!placed)
        {
            return;
        }

        Changed?.Invoke();
        await LoadAsync(cancellationToken);
        StatusMessage = result!.LeftThisComputer
            ? $"Etiqueta enviada para {result.Destination}."
            : $"A etiqueta aparece em {result.Destination}.";
    }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>O Git foi encontrado na última abertura: a URL do repositório e o clone aparecem (ADR-064).</summary>
    [ObservableProperty]
    private bool _isGitAvailable;

    private string? _cloneFolder;

    /// <summary>A etiqueta em edição; <c>null</c> = o formulário cria uma nova.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(FormTitle), nameof(SaveLabel))]
    private Guid? _editingId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewName))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name = string.Empty;

    /// <summary>Sempre <c>#RRGGBB</c>: é o que vai para o banco.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewFill), nameof(PreviewForeground))]
    private string _colorHex = TagColor.Palette[0];

    /// <summary>A mesma cor, no tipo do <c>ColorView</c>. As duas se seguem.</summary>
    [ObservableProperty]
    private Color _color = Color.Parse(TagColor.Palette[0]);

    /// <summary>O seletor livre aberto. Fechado por padrão: a paleta resolve quase sempre.</summary>
    [ObservableProperty]
    private bool _isCustomizing;

    public ObservableCollection<TagListItemViewModel> Tags { get; } = [];

    public IReadOnlyList<SwatchViewModel> Palette { get; } =
        [.. TagColor.Palette.Select(hex => new SwatchViewModel(hex) { IsSelected = hex == TagColor.Palette[0] })];

    public bool IsEditing => EditingId is not null;

    public string FormTitle => IsEditing ? "Editar etiqueta" : "Nova etiqueta";

    public string SaveLabel => IsEditing ? "Salvar alterações" : "Criar etiqueta";

    public bool CanSave => !string.IsNullOrWhiteSpace(Name);

    /// <summary>A prévia mostra o nome que vai ficar, ou um exemplo enquanto está vazio.</summary>
    public string PreviewName => string.IsNullOrWhiteSpace(Name) ? "Prévia" : Name.Trim();

    public IBrush PreviewFill => new ImmutableSolidColorBrush(Color.Parse(ColorHex));

    public IBrush PreviewForeground => TagChipViewModel.TextOn(ColorHex);

    /// <summary>Alguma etiqueta mudou. O composition root recarrega o painel.</summary>
    public event Action? Changed;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TagRow> rows = [];

        var loaded = await TryAsync(
            async () => rows = await runner.RunAsync<GetTagsHandler, IReadOnlyList<TagRow>>(
                (handler, token) => handler.HandleAsync(new GetTags(), token),
                cancellationToken),
            "Não foi possível carregar as etiquetas.");

        if (!loaded)
        {
            return;
        }

        await DetectGitAsync(cancellationToken);

        // A recarga recria os cartões; os que estavam abertos continuam abertos,
        // senão salvar qualquer coisa fecharia a lista que o usuário está editando.
        var expanded = Tags.Where(item => item.IsExpanded).Select(item => item.Row.Id).ToHashSet();

        Tags.Clear();

        foreach (var row in rows)
        {
            var item = new TagListItemViewModel(row)
            {
                PlacementLabel = PlacementOptions.Describe(placements, row.Placement),
                IsGitAvailable = IsGitAvailable,
                CloneFolder = _cloneFolder,
            };

            item.PropertyChanged += OnItemPropertyChanged;
            Tags.Add(item);
        }

        IsEmpty = Tags.Count == 0;

        foreach (var item in Tags.Where(item => expanded.Contains(item.Row.Id)).ToList())
        {
            item.IsExpanded = true;
            await LoadDirectoriesAsync(item, cancellationToken);
        }
    }

    [RelayCommand]
    public async Task ToggleDirectoriesAsync(TagListItemViewModel item, CancellationToken cancellationToken)
    {
        item.IsExpanded = !item.IsExpanded;

        if (item.IsExpanded)
        {
            item.ResetDirectoryForm();
            await LoadDirectoriesAsync(item, cancellationToken);
        }
    }

    [RelayCommand]
    public void EditDirectory(TagDirectoryItemViewModel directory)
    {
        directory.Owner.EditDirectory(directory);
        ErrorMessage = null;
        StatusMessage = null;
    }

    [RelayCommand]
    public void CancelDirectoryEdit(TagListItemViewModel item) => item.ResetDirectoryForm();

    /// <summary>
    /// Acrescenta ou altera. Mudar o path não reescreve anotação nenhuma: o que
    /// já foi inserido no texto é texto, e fica como está (ADR-026).
    /// </summary>
    [RelayCommand]
    public async Task SaveDirectoryAsync(TagListItemViewModel item, CancellationToken cancellationToken)
    {
        var tagId = item.Row.Id;
        var editingId = item.EditingDirectoryId;
        var alias = item.Alias;
        var path = item.Path;
        var name = item.DirectoryName;
        var description = item.DirectoryDescription;
        var defaultBranch = item.DirectoryDefaultBranch;

        // A pasta manda (ADR-064): se ela é um repositório, a URL é a do origin dela.
        var repositoryUrl = item.IsGitAvailable && await ReadOriginAsync(path, cancellationToken) is { } origin
            ? origin
            : item.RepositoryUrl;

        var saved = await TryAsync(
            () => editingId is { } id
                ? runner.RunAsync<UpdateTagDirectoryHandler>(
                    (handler, token) => handler.HandleAsync(
                        new UpdateTagDirectory(tagId, id, alias, path, name, description, defaultBranch, repositoryUrl), token),
                    cancellationToken)
                : runner.RunAsync<AddTagDirectoryHandler, Guid>(
                    (handler, token) => handler.HandleAsync(
                        new AddTagDirectory(tagId, alias, path, name, description, defaultBranch, repositoryUrl), token),
                    cancellationToken),
            "Não foi possível salvar o diretório.");

        if (!saved)
        {
            // Formulário preservado: o usuário corrige e tenta de novo.
            return;
        }

        item.ResetDirectoryForm();
        await LoadDirectoriesAsync(item, cancellationToken);
        StatusMessage = editingId is null ? "Diretório adicionado." : "Diretório atualizado.";
    }

    /// <summary>
    /// O "Procurar…" escolheu uma pasta: se ela é um repositório, a URL vem do
    /// <c>origin</c> dela (ADR-064), e os dois campos ficam de acordo.
    /// </summary>
    public async Task UseFolderAsync(TagListItemViewModel item, string path, CancellationToken cancellationToken)
    {
        item.UseFolder(path);

        if (item.IsGitAvailable && await ReadOriginAsync(path, cancellationToken) is { } origin)
        {
            item.RepositoryUrl = origin;
        }
    }

    /// <summary>
    /// Clona na pasta do formulário e salva o diretório em seguida (ADR-064):
    /// colar a URL e clicar em "Clonar" já termina o cadastro. Se salvar
    /// falhar (um alias repetido), o clone fica e o formulário espera a correção.
    /// </summary>
    [RelayCommand(IncludeCancelCommand = true)]
    public async Task CloneRepositoryAsync(TagListItemViewModel item, CancellationToken cancellationToken)
    {
        var url = item.RepositoryUrl;
        var path = item.Path;
        ClonedRepository? cloned = null;
        var wasEditing = item.IsEditingDirectory;

        item.IsCloning = true;

        bool succeeded;

        try
        {
            succeeded = await new ScreenErrorPath(
                    busy => IsBusy = busy,
                    error => ErrorMessage = error,
                    exception => logger.LogError(exception, "TagsScreenCloneFailed"),
                    ClearStatus: () => StatusMessage = null,
                    CancellationIsSilent: true)
                .TryAsync(
                    async () => cloned = await runner.RunAsync<CloneTagRepositoryHandler, ClonedRepository>(
                        (handler, token) => handler.HandleAsync(new CloneTagRepository(url, path), token),
                        cancellationToken),
                    "Não foi possível clonar o repositório.");
        }
        finally
        {
            item.IsCloning = false;
        }

        if (!succeeded)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                StatusMessage = $"Clone cancelado. Se sobrou uma pasta pela metade em {path}, apague-a antes de tentar de novo.";
            }

            return;
        }

        item.Path = cloned!.Path;
        item.RepositoryUrl = cloned.RepositoryUrl;
        item.PathExists = true;

        await SaveDirectoryAsync(item, cancellationToken);

        StatusMessage = (ErrorMessage is null, cloned.AlreadyCloned) switch
        {
            (true, true) => wasEditing
                ? "A pasta já era um clone deste repositório. Diretório atualizado."
                : "A pasta já era um clone deste repositório. Diretório adicionado.",
            (true, false) => wasEditing
                ? $"Repositório clonado em {cloned.Path}. Diretório atualizado."
                : $"Repositório clonado em {cloned.Path}. Diretório adicionado.",
            _ => $"Repositório clonado em {cloned.Path}. Corrija o formulário e salve o diretório.",
        };
    }

    [RelayCommand]
    public async Task DeleteDirectoryAsync(
        TagDirectoryItemViewModel directory,
        CancellationToken cancellationToken)
    {
        var confirmed = await confirmation.AskAsync(new ConfirmationRequest(
            "Excluir diretório?",
            $"{directory.Alias} deixa de aparecer no autocomplete das anotações. "
            + "O que já foi escrito com este caminho continua como está."
            + (directory.CommandCount > 0
                ? " Os botões de comando dele saem dos worktrees, e os comandos criados só nele são excluídos."
                : string.Empty),
            "Excluir"));

        if (!confirmed)
        {
            return;
        }

        var item = directory.Owner;

        var deleted = await TryAsync(
            () => runner.RunAsync<RemoveTagDirectoryHandler>(
                (handler, token) => handler.HandleAsync(
                    new RemoveTagDirectory(item.Row.Id, directory.Row.Id), token),
                cancellationToken),
            "Não foi possível excluir o diretório.");

        if (!deleted)
        {
            return;
        }

        if (item.EditingDirectoryId == directory.Row.Id)
        {
            item.ResetDirectoryForm();
        }

        await LoadDirectoriesAsync(item, cancellationToken);
        StatusMessage = "Diretório excluído.";
    }

    /// <summary>
    /// Abre (ou fecha) os comandos rápidos do diretório (ADR-051). Abrir lê os
    /// globais de novo: a janela de comandos pode ter mudado com esta aberta.
    /// </summary>
    [RelayCommand]
    public async Task ToggleDirectoryCommandsAsync(TagDirectoryItemViewModel directory, CancellationToken cancellationToken)
    {
        directory.IsCommandsExpanded = !directory.IsCommandsExpanded;

        if (directory.IsCommandsExpanded)
        {
            await LoadDirectoryCommandsAsync(directory, cancellationToken);
        }
    }

    [RelayCommand]
    public async Task AddDirectoryCommandAsync(TagDirectoryItemViewModel directory, CancellationToken cancellationToken)
    {
        if (directory.SelectedNewCommand is not { } command)
        {
            ErrorMessage = "Escolha um comando. Cadastre-os em Comandos globais.";
            return;
        }

        var tagId = directory.Owner.Row.Id;
        var directoryId = directory.Row.Id;

        await ChangeDirectoryCommandsAsync(
            directory,
            () => runner.RunAsync<AddTagDirectoryCommandHandler, Guid>(
                (handler, token) => handler.HandleAsync(new AddTagDirectoryCommand(tagId, directoryId, command.Id), token),
                cancellationToken),
            $"{command.DisplayName} adicionado a {directory.Alias}.",
            cancellationToken);
    }

    /// <summary>Liga ou desliga sem tirar da lista: a personalização fica.</summary>
    [RelayCommand]
    public Task ToggleDirectoryCommandAsync(TagDirectoryCommandItemViewModel item, CancellationToken cancellationToken)
    {
        var (tagId, directoryId, bindingId) = Ids(item);
        var enabled = !item.IsEnabled;

        return ChangeDirectoryCommandsAsync(
            item.Owner,
            () => runner.RunAsync<SetTagDirectoryCommandEnabledHandler>(
                (handler, token) => handler.HandleAsync(
                    new SetTagDirectoryCommandEnabled(tagId, directoryId, bindingId, enabled), token),
                cancellationToken),
            null,
            cancellationToken);
    }

    [RelayCommand]
    public Task MoveDirectoryCommandUpAsync(TagDirectoryCommandItemViewModel item, CancellationToken cancellationToken) =>
        MoveDirectoryCommandAsync(item, -1, cancellationToken);

    [RelayCommand]
    public Task MoveDirectoryCommandDownAsync(TagDirectoryCommandItemViewModel item, CancellationToken cancellationToken) =>
        MoveDirectoryCommandAsync(item, +1, cancellationToken);

    /// <summary>
    /// Tira o global do diretório sem perguntar: ele continua cadastrado. O
    /// comando só do diretório (ADR-055) é excluído, e por isso pergunta antes.
    /// </summary>
    [RelayCommand]
    public async Task RemoveDirectoryCommandAsync(TagDirectoryCommandItemViewModel item, CancellationToken cancellationToken)
    {
        var (tagId, directoryId, bindingId) = Ids(item);

        if (item.IsDirectoryOnly
            && !await confirmation.AskAsync(new ConfirmationRequest(
                $"Excluir {item.DisplayName}?",
                $"Ele só existe em {item.Owner.Alias}: o botão sai dos worktrees e o comando não pode ser recuperado. "
                + "O histórico de execuções fica.",
                "Excluir")))
        {
            return;
        }

        await ChangeDirectoryCommandsAsync(
            item.Owner,
            () => runner.RunAsync<RemoveTagDirectoryCommandHandler>(
                (handler, token) => handler.HandleAsync(new RemoveTagDirectoryCommand(tagId, directoryId, bindingId), token),
                cancellationToken),
            item.IsDirectoryOnly
                ? $"{item.DisplayName} excluído."
                : $"{item.DisplayName} saiu de {item.Owner.Alias}. O comando global continua cadastrado.",
            cancellationToken);
    }

    /// <summary>
    /// "+ Novo comando": um comando só deste diretório (ADR-055), sem passar por
    /// Comandos globais. Entra no fim da lista, ligado.
    /// </summary>
    [RelayCommand]
    public async Task NewDirectoryCommandAsync(TagDirectoryItemViewModel directory, CancellationToken cancellationToken)
    {
        var tagId = directory.Owner.Row.Id;
        var directoryId = directory.Row.Id;
        // O diálogo fecha na tentativa que gravou: a última é a que vale.
        string? created = null;

        var saved = await commandEditor.EditAsync(new DirectoryCommandEditorRequest(
            $"Novo comando de {directory.Alias}",
            "Criar comando",
            null,
            (draft, token) =>
            {
                created = draft.Settings.Name;

                return SaveCommandAsync(() => runner.RunAsync<CreateDirectoryOnlyCommandHandler, Guid>(
                    (handler, inner) => handler.HandleAsync(
                        new CreateDirectoryOnlyCommand(tagId, directoryId, draft.Command, draft.Description, draft.Settings),
                        inner),
                    token));
            }));

        if (!saved)
        {
            return;
        }

        await LoadDirectoryCommandsAsync(directory, cancellationToken);
        StatusMessage = $"{created} criado em {directory.Alias}.";
    }

    /// <summary>Abre o comando só do diretório no mesmo diálogo, com o que está gravado agora.</summary>
    [RelayCommand]
    public async Task EditDirectoryOnlyCommandAsync(TagDirectoryCommandItemViewModel item, CancellationToken cancellationToken)
    {
        var commandId = item.Row.CommandId;
        DevelopmentCommandRow? current = null;

        if (!await TryAsync(
                async () => current = await runner.RunAsync<GetDirectoryOnlyCommandHandler, DevelopmentCommandRow>(
                    (handler, token) => handler.HandleAsync(new GetDirectoryOnlyCommand(commandId), token),
                    cancellationToken),
                "Não foi possível abrir o comando.")
            || current is null)
        {
            return;
        }

        var saved = await commandEditor.EditAsync(new DirectoryCommandEditorRequest(
            $"Editar comando de {item.Owner.Alias}",
            "Salvar alterações",
            current,
            (draft, token) => SaveCommandAsync(() => runner.RunAsync<UpdateDirectoryOnlyCommandHandler, DevelopmentCommandRow>(
                (handler, inner) => handler.HandleAsync(
                    new UpdateDirectoryOnlyCommand(commandId, draft.Command, draft.Description, draft.Settings),
                    inner),
                token))));

        if (!saved)
        {
            return;
        }

        await LoadDirectoryCommandsAsync(item.Owner, cancellationToken);
        StatusMessage = "Comando atualizado.";
    }

    /// <summary>
    /// O gravar do diálogo: a recusa do domínio volta como mensagem, e o diálogo
    /// fica aberto mostrando-a, com o que foi digitado.
    /// </summary>
    private async Task<string?> SaveCommandAsync(Func<Task> save)
    {
        try
        {
            await save();
            return null;
        }
        catch (DomainException exception)
        {
            return exception.Message;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "DirectoryOnlyCommandSaveFailed");
            return "Não foi possível salvar o comando.";
        }
    }

    [RelayCommand]
    public void BeginCustomizeDirectoryCommand(TagDirectoryCommandItemViewModel item) => item.BeginCustomize();

    [RelayCommand]
    public void CancelCustomizeDirectoryCommand(TagDirectoryCommandItemViewModel item) => item.IsCustomizing = false;

    /// <summary>
    /// "Usar configuração global" apaga a personalização. Personalizar com a
    /// mesma linha do global guarda só a pasta: o texto continua seguindo o global.
    /// </summary>
    [RelayCommand]
    public Task SaveDirectoryCommandCustomizationAsync(
        TagDirectoryCommandItemViewModel item,
        CancellationToken cancellationToken)
    {
        var (tagId, directoryId, bindingId) = Ids(item);

        string? command = null;
        string? folder = null;

        if (item.Customize)
        {
            var typed = item.CommandOverride.Trim();

            command = typed.Length == 0 || typed == item.GlobalCommand ? null : typed;
            folder = string.IsNullOrWhiteSpace(item.WorkingDirectoryOverride) ? null : item.WorkingDirectoryOverride;
        }

        return ChangeDirectoryCommandsAsync(
            item.Owner,
            () => runner.RunAsync<CustomizeTagDirectoryCommandHandler>(
                (handler, token) => handler.HandleAsync(
                    new CustomizeTagDirectoryCommand(tagId, directoryId, bindingId, command, folder), token),
                cancellationToken),
            command is null && folder is null
                ? $"{item.DisplayName} usa a configuração global."
                : $"{item.DisplayName} personalizado para {item.Owner.Alias}.",
            cancellationToken);
    }

    private Task MoveDirectoryCommandAsync(TagDirectoryCommandItemViewModel item, int offset, CancellationToken cancellationToken)
    {
        var (tagId, directoryId, bindingId) = Ids(item);

        return ChangeDirectoryCommandsAsync(
            item.Owner,
            () => runner.RunAsync<MoveTagDirectoryCommandHandler>(
                (handler, token) => handler.HandleAsync(
                    new MoveTagDirectoryCommand(tagId, directoryId, bindingId, offset), token),
                cancellationToken),
            null,
            cancellationToken);
    }

    private static (Guid TagId, Guid DirectoryId, Guid BindingId) Ids(TagDirectoryCommandItemViewModel item) =>
        (item.Owner.Owner.Row.Id, item.Owner.Row.Id, item.Row.Id);

    private async Task ChangeDirectoryCommandsAsync(
        TagDirectoryItemViewModel directory,
        Func<Task> change,
        string? done,
        CancellationToken cancellationToken)
    {
        if (!await TryAsync(change, "Não foi possível alterar os comandos do diretório."))
        {
            return;
        }

        await LoadDirectoryCommandsAsync(directory, cancellationToken);

        if (done is not null)
        {
            StatusMessage = done;
        }
    }

    private async Task LoadDirectoryCommandsAsync(TagDirectoryItemViewModel directory, CancellationToken cancellationToken)
    {
        try
        {
            var bindings = await runner.RunAsync<GetTagDirectoryCommandsHandler, IReadOnlyList<TagDirectoryCommandRow>>(
                (handler, token) => handler.HandleAsync(new GetTagDirectoryCommands(directory.Row.Id), token),
                cancellationToken);

            var globals = await runner.RunAsync<GetDevelopmentCommandsHandler, IReadOnlyList<DevelopmentCommandRow>>(
                (handler, token) => handler.HandleAsync(new GetDevelopmentCommands(), token),
                cancellationToken);

            directory.ShowCommands(bindings, globals);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "TagDirectoryCommandsLoadFailed {DirectoryId}", directory.Row.Id);
            ErrorMessage = "Não foi possível carregar os comandos do diretório.";
        }
    }

    /// <summary>
    /// Recarrega as pastas de um cartão e só depois confere quais existem: a
    /// lista aparece antes, e um caminho de rede lento não segura o resto. Por
    /// último, a URL de cada pasta acompanha o <c>origin</c> dela (ADR-064);
    /// mudou alguma, a lista é lida de novo.
    /// </summary>
    private async Task LoadDirectoriesAsync(
        TagListItemViewModel item,
        CancellationToken cancellationToken,
        bool refreshRepositories = true)
    {
        IReadOnlyList<TagDirectoryRow> rows = [];

        try
        {
            rows = await runner.RunAsync<GetTagDirectoriesHandler, IReadOnlyList<TagDirectoryRow>>(
                (handler, token) => handler.HandleAsync(new GetTagDirectories(item.Row.Id), token),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "TagDirectoriesLoadFailed {TagId}", item.Row.Id);
            ErrorMessage = "Não foi possível carregar os diretórios.";
            return;
        }

        item.Directories.Clear();

        foreach (var row in rows)
        {
            item.Directories.Add(new TagDirectoryItemViewModel(item, row));
        }

        item.DirectoryCount = rows.Count;

        foreach (var directory in item.Directories.Where(directory => !directory.IsUnmapped).ToList())
        {
            try
            {
                directory.Exists = await directoryProbe.ExistsAsync(directory.Path, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "TagDirectoryProbeFailed {DirectoryId}", directory.Row.Id);
            }
        }

        if (refreshRepositories && IsGitAvailable && item.Directories.Any(directory => directory.IsFound))
        {
            await RefreshRepositoriesAsync(item, cancellationToken);
        }
    }

    private async Task RefreshRepositoriesAsync(TagListItemViewModel item, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> changed;

        try
        {
            changed = await runner.RunAsync<RefreshTagDirectoryRepositoriesHandler, IReadOnlyList<Guid>>(
                (handler, token) => handler.HandleAsync(new RefreshTagDirectoryRepositories(item.Row.Id), token),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Só uma conveniência: a lista já está na tela, com a URL que estava gravada.
            logger.LogWarning(exception, "TagDirectoryRepositoriesRefreshFailed {TagId}", item.Row.Id);
            return;
        }

        if (changed.Count > 0)
        {
            await LoadDirectoriesAsync(item, cancellationToken, refreshRepositories: false);
        }
    }

    /// <summary>
    /// O Git existe? E, se existe, onde clonar por padrão. Falhar aqui só
    /// esconde a URL e o clone: o resto da janela não depende do Git.
    /// </summary>
    private async Task DetectGitAsync(CancellationToken cancellationToken)
    {
        try
        {
            var installation = await runner.RunAsync<DetectGitHandler, GitInstallation>(
                (handler, token) => handler.HandleAsync(new DetectGit(), token),
                cancellationToken);

            IsGitAvailable = installation.IsInstalled;

            if (IsGitAvailable)
            {
                _cloneFolder = await runner.RunAsync<SuggestCloneFolderHandler, string>(
                    (handler, token) => handler.HandleAsync(new SuggestCloneFolder(), token),
                    cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TagsGitDetectionFailed");
            IsGitAvailable = false;
        }
    }

    /// <summary>O <c>origin</c> da pasta, ou <c>null</c>. Nunca falha: é preenchimento, não validação.</summary>
    private async Task<string?> ReadOriginAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await runner.RunAsync<ReadRepositoryUrlHandler, string?>(
                (handler, token) => handler.HandleAsync(new ReadRepositoryUrl(path), token),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "RepositoryUrlReadFailed");
            return null;
        }
    }

    /// <summary>O caminho do formulário mudou: a pasta existe? Decide se o clone é oferecido.</summary>
    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is TagListItemViewModel item && e.PropertyName == nameof(TagListItemViewModel.Path))
        {
            _ = ProbeFormPathAsync(item);
        }
    }

    private async Task ProbeFormPathAsync(TagListItemViewModel item)
    {
        var path = item.Path.Trim().Trim('"').Trim();

        if (path.Length == 0 || !System.IO.Path.IsPathFullyQualified(path))
        {
            item.PathExists = null;
            return;
        }

        try
        {
            var exists = await directoryProbe.ExistsAsync(path, CancellationToken.None);

            // Digitou mais enquanto conferia: a resposta é de um caminho que já não está no campo.
            if (string.Equals(item.Path.Trim().Trim('"').Trim(), path, StringComparison.Ordinal))
            {
                item.PathExists = exists;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "TagDirectoryFormProbeFailed");
        }
    }

    [RelayCommand]
    public void PickColor(string colorHex) => ColorHex = TagColor.Normalize(colorHex);

    [RelayCommand]
    public void Edit(TagListItemViewModel item)
    {
        EditingId = item.Row.Id;
        Name = item.Row.Name;
        ColorHex = item.Row.ColorHex;
        IsCustomizing = !TagColor.Palette.Contains(item.Row.ColorHex);
        ErrorMessage = null;
        StatusMessage = null;
    }

    [RelayCommand]
    public void CancelEdit() => ResetForm();

    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        var name = Name;
        var colorHex = ColorHex;
        var editingId = EditingId;

        var saved = await TryAsync(
            () => editingId is { } id
                ? runner.RunAsync<UpdateTagHandler>(
                    (handler, token) => handler.HandleAsync(new UpdateTag(id, name, colorHex), token),
                    cancellationToken)
                : runner.RunAsync<CreateTagHandler, Guid>(
                    (handler, token) => handler.HandleAsync(new CreateTag(name, colorHex), token),
                    cancellationToken),
            "Não foi possível salvar a etiqueta.");

        if (!saved)
        {
            // Formulário preservado: o usuário corrige o nome e tenta de novo.
            return;
        }

        ResetForm();

        await AfterChangeAsync(
            editingId is null ? "Etiqueta criada." : "Etiqueta atualizada.",
            cancellationToken);
    }

    /// <summary>
    /// Exclui depois de perguntar. A pergunta diz quantas tarefas perdem a
    /// etiqueta: "excluir" sem esse número esconderia o que realmente acontece.
    /// </summary>
    [RelayCommand]
    public async Task DeleteAsync(TagListItemViewModel item, CancellationToken cancellationToken)
    {
        if (!await confirmation.AskAsync(DeletePrompt(item.Row)))
        {
            return;
        }

        var deleted = await TryAsync(
            () => runner.RunAsync<DeleteTagHandler>(
                (handler, token) => handler.HandleAsync(new DeleteTag(item.Row.Id), token),
                cancellationToken),
            "Não foi possível excluir a etiqueta.");

        if (!deleted)
        {
            return;
        }

        if (EditingId == item.Row.Id)
        {
            ResetForm();
        }

        await AfterChangeAsync("Etiqueta excluída.", cancellationToken);
    }

    public static ConfirmationRequest DeletePrompt(TagRow tag) =>
        new(
            "Excluir etiqueta?",
            tag.UsageCount switch
            {
                0 => $"“{tag.Name}” será excluída.",
                1 => $"“{tag.Name}” está em 1 tarefa e será removida dela.",
                _ => $"“{tag.Name}” está em {tag.UsageCount} tarefas e será removida de todas.",
            }
            + tag.DirectoryCount switch
            {
                0 => string.Empty,
                1 => " O diretório dela também será excluído.",
                _ => $" Os {tag.DirectoryCount} diretórios dela também serão excluídos.",
            },
            "Excluir",
            IsIrreversible: true);

    partial void OnColorHexChanged(string value)
    {
        foreach (var swatch in Palette)
        {
            swatch.IsSelected = swatch.ColorHex == value;
        }

        var color = Color.Parse(value);

        if (Color != color)
        {
            Color = color;
        }
    }

    /// <summary>
    /// O <c>ColorView</c> escreve aqui. Alfa é descartado: a cor vai para o banco
    /// como <c>#RRGGBB</c>, e uma bolinha translúcida some no fundo escuro.
    /// </summary>
    partial void OnColorChanged(Color value)
    {
        var hex = string.Create(
            CultureInfo.InvariantCulture,
            $"#{value.R:X2}{value.G:X2}{value.B:X2}");

        if (ColorHex != hex)
        {
            ColorHex = hex;
        }
    }

    private void ResetForm()
    {
        EditingId = null;
        Name = string.Empty;
        ColorHex = TagColor.Palette[0];
        IsCustomizing = false;
    }

    private async Task AfterChangeAsync(string message, CancellationToken cancellationToken)
    {
        Changed?.Invoke();

        // A mensagem vem depois da recarga: LoadAsync passa pelo TryAsync, que
        // limpa o status ao começar.
        await LoadAsync(cancellationToken);
        StatusMessage = message;
    }

    private Task<bool> TryAsync(Func<Task> operation, string fallbackMessage) =>
        new ScreenErrorPath(
            busy => IsBusy = busy,
            error => ErrorMessage = error,
            exception => logger.LogError(exception, "TagsScreenOperationFailed"),
            ClearStatus: () => StatusMessage = null)
        .TryAsync(operation, fallbackMessage);
}
