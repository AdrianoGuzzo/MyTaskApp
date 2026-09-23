using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Uma etiqueta na lista da janela de gerenciamento.</summary>
public sealed class TagListItemViewModel(TagRow row)
{
    public TagRow Row { get; } = row;

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

        Tags.Clear();

        foreach (var row in rows)
        {
            Tags.Add(new TagListItemViewModel(row));
        }

        IsEmpty = Tags.Count == 0;
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
