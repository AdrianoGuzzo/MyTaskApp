using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Desktop.Views;
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
    private string _path = string.Empty;

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

    /// <summary>
    /// O "Procurar…" preenche o path e, se o alias ainda está vazio, sugere um a
    /// partir do nome da pasta.
    /// </summary>
    public void UseFolder(string path)
    {
        Path = path;

        if (string.IsNullOrWhiteSpace(Alias))
        {
            Alias = TagDirectoryItemViewModel.SuggestAlias(path);
        }
    }

    internal void ResetDirectoryForm()
    {
        EditingDirectoryId = null;
        Alias = string.Empty;
        Path = string.Empty;
        DirectoryName = string.Empty;
        DirectoryDescription = string.Empty;
        DirectoryDefaultBranch = string.Empty;
    }

    internal void EditDirectory(TagDirectoryItemViewModel directory)
    {
        EditingDirectoryId = directory.Row.Id;
        Alias = directory.Alias;
        Path = directory.Path;
        DirectoryName = directory.Name ?? string.Empty;
        DirectoryDescription = directory.Description ?? string.Empty;
        DirectoryDefaultBranch = directory.DefaultBranch ?? string.Empty;
    }

    public TagChipViewModel Chip { get; } = new(row.Id, row.Name, row.ColorHex);

    public string UsageLabel => Row.UsageCount switch
    {
        0 => "Ainda não usada",
        1 => "Usada em 1 tarefa",
        _ => $"Usada em {Row.UsageCount} tarefas",
    };
}

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
    ILogger<TagsViewModel> logger) : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isEmpty;

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

        // A recarga recria os cartões; os que estavam abertos continuam abertos,
        // senão salvar qualquer coisa fecharia a lista que o usuário está editando.
        var expanded = Tags.Where(item => item.IsExpanded).Select(item => item.Row.Id).ToHashSet();

        Tags.Clear();

        foreach (var row in rows)
        {
            Tags.Add(new TagListItemViewModel(row));
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

        var saved = await TryAsync(
            () => editingId is { } id
                ? runner.RunAsync<UpdateTagDirectoryHandler>(
                    (handler, token) => handler.HandleAsync(
                        new UpdateTagDirectory(tagId, id, alias, path, name, description, defaultBranch), token),
                    cancellationToken)
                : runner.RunAsync<AddTagDirectoryHandler, Guid>(
                    (handler, token) => handler.HandleAsync(
                        new AddTagDirectory(tagId, alias, path, name, description, defaultBranch), token),
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

    [RelayCommand]
    public async Task DeleteDirectoryAsync(
        TagDirectoryItemViewModel directory,
        CancellationToken cancellationToken)
    {
        var confirmed = await confirmation.AskAsync(new ConfirmationRequest(
            "Excluir diretório?",
            $"{directory.Alias} deixa de aparecer no autocomplete das anotações. "
            + "O que já foi escrito com este caminho continua como está.",
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
    /// Recarrega as pastas de um cartão e só depois confere quais existem: a
    /// lista aparece antes, e um caminho de rede lento não segura o resto.
    /// </summary>
    private async Task LoadDirectoriesAsync(TagListItemViewModel item, CancellationToken cancellationToken)
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

        foreach (var directory in item.Directories.ToList())
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
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
            return false;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "TagsScreenOperationFailed");
            ErrorMessage = fallbackMessage;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
