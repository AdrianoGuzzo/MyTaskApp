using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tags;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Uma etiqueta no seletor, marcada ou não para esta linha.</summary>
public sealed partial class TagOptionViewModel(TaskTagsViewModel owner, TagChipViewModel tag)
    : ObservableObject
{
    public TaskTagsViewModel Owner { get; } = owner;

    public TagChipViewModel Tag { get; } = tag;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Devolve ao CheckBox o estado verdadeiro depois de uma gravação recusada:
    /// o clique já tinha virado a marca na tela, e o valor da propriedade não
    /// mudou, então sem o aviso o binding não teria por que reler.
    /// </summary>
    public void Resync() => OnPropertyChanged(nameof(IsSelected));
}

/// <summary>
/// As etiquetas de uma linha: as bolinhas que ela mostra e o seletor que o
/// botão de etiqueta abre (ADR-025).
/// </summary>
/// <remarks>
/// A gravação não mora aqui, e sim no <see cref="TodayViewModel"/>, que já tem
/// o executor de casos de uso e o tratamento de erro. Isto só guarda estado e
/// responde "qual seria o conjunto se eu marcasse esta?".
/// </remarks>
public sealed partial class TaskTagsViewModel : ObservableObject
{
    /// <summary>
    /// Quantas bolinhas cabem antes do "+N". A linha tem 360px e o título é o
    /// assunto: mais do que isso começaria a espremer o texto.
    /// </summary>
    public const int MaxDots = 5;

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Ordem alfabética de gente: "Ágil" vem antes de "Banco".</summary>
    private static readonly StringComparer NameOrder = StringComparer.Create(Culture, ignoreCase: true);

    private List<TagChipViewModel> _all = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _hasNoTagsAtAll;

    [ObservableProperty]
    private bool _hasNoMatches;

    [ObservableProperty]
    private bool _hasOverflow;

    [ObservableProperty]
    private string _overflowLabel = string.Empty;

    [ObservableProperty]
    private string _overflowTip = string.Empty;

    [ObservableProperty]
    private bool _hasTags;

    public TaskTagsViewModel(Guid taskId, IReadOnlyList<TagBadge>? tags, string heading = "")
    {
        TaskId = taskId;
        Heading = heading;

        foreach (var tag in (tags ?? []).OrderBy(tag => tag.Name, NameOrder))
        {
            Selected.Add(new TagChipViewModel(tag.Id, tag.Name, tag.ColorHex) { Owner = this });
        }

        ShowDots();
    }

    public Guid TaskId { get; }

    /// <summary>O título do seletor: o nome da tarefa, ou o que a captura vai criar.</summary>
    public string Heading { get; }

    /// <summary>
    /// A escolha da caixa de captura: não há tarefa ainda, então marcar só
    /// guarda — quem grava é a própria captura, junto com as tarefas.
    /// </summary>
    public bool IsDraft { get; private init; }

    /// <summary>O seletor da caixa de captura, sem tarefa por trás.</summary>
    public static TaskTagsViewModel Draft() =>
        new(Guid.Empty, tags: null, "Etiquetas das novas tarefas") { IsDraft = true };

    /// <summary>As etiquetas desta linha, em ordem alfabética: as pílulas do seletor.</summary>
    public ObservableCollection<TagChipViewModel> Selected { get; } = [];

    /// <summary>As bolinhas da linha: no máximo <see cref="MaxDots"/>.</summary>
    public ObservableCollection<TagChipViewModel> Dots { get; } = [];

    /// <summary>As etiquetas que casam com a busca.</summary>
    public ObservableCollection<TagOptionViewModel> Options { get; } = [];

    /// <summary>Houve gravação desde que o seletor abriu — a lista precisa recarregar ao fechar.</summary>
    public bool HasChanged { get; private set; }

    public IReadOnlyCollection<Guid> SelectedIds => [.. Selected.Select(tag => tag.Id)];

    /// <summary>Recebe a lista de etiquetas ao abrir o seletor.</summary>
    public void ShowOptions(IReadOnlyList<TagRow> all)
    {
        _all = [.. all.Select(tag => new TagChipViewModel(tag.Id, tag.Name, tag.ColorHex) { Owner = this })];
        HasChanged = false;
        ErrorMessage = null;
        SearchText = string.Empty;
        HasNoTagsAtAll = _all.Count == 0;

        // Uma etiqueta excluída noutra janela sai das pílulas também: o banco já
        // levou o vínculo junto (ADR-025).
        var existing = _all.Select(tag => tag.Id).ToHashSet();
        ShowSelected([.. Selected.Where(tag => existing.Contains(tag.Id)).Select(tag => tag.Id)]);
        Filter();
    }

    /// <summary>O conjunto que resultaria de marcar ou desmarcar esta etiqueta.</summary>
    public IReadOnlyCollection<Guid> Toggled(Guid tagId)
    {
        var ids = SelectedIds.ToHashSet();

        if (!ids.Remove(tagId))
        {
            ids.Add(tagId);
        }

        return ids;
    }

    /// <summary>Aplica o conjunto que acabou de ser gravado.</summary>
    public void Apply(IReadOnlyCollection<Guid> ids)
    {
        HasChanged = true;
        ShowSelected(ids);
    }

    partial void OnSearchTextChanged(string value) => Filter();

    private void ShowSelected(IReadOnlyCollection<Guid> ids)
    {
        // A etiqueta vem da lista completa quando ela já foi carregada: é lá que
        // estão o nome e a cor atuais, se alguém acabou de renomear.
        var known = _all.Concat(Selected).DistinctBy(tag => tag.Id).ToDictionary(tag => tag.Id);

        var chips = ids
            .Where(known.ContainsKey)
            .Select(id => known[id])
            .OrderBy(tag => tag.Name, NameOrder)
            .ToList();

        Selected.Clear();

        foreach (var chip in chips)
        {
            Selected.Add(chip);
        }

        ShowDots();

        // Só as marcas mudam: refazer a lista a cada clique faria a rolagem
        // voltar ao topo debaixo do mouse.
        var marked = SelectedIds.ToHashSet();

        foreach (var option in Options)
        {
            option.IsSelected = marked.Contains(option.Tag.Id);
        }
    }

    private void ShowDots()
    {
        Dots.Clear();

        foreach (var tag in Selected.Take(MaxDots))
        {
            Dots.Add(tag);
        }

        var hidden = Selected.Skip(MaxDots).Select(tag => tag.Name).ToList();

        HasTags = Selected.Count > 0;
        HasOverflow = hidden.Count > 0;
        OverflowLabel = hidden.Count > 0 ? $"+{hidden.Count}" : string.Empty;
        OverflowTip = string.Join(", ", hidden);
    }

    private void Filter()
    {
        var term = SearchText.Trim();
        var selected = SelectedIds.ToHashSet();

        Options.Clear();

        foreach (var tag in _all)
        {
            if (term.Length > 0
                && Culture.CompareInfo.IndexOf(tag.Name, term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) < 0)
            {
                continue;
            }

            Options.Add(new TagOptionViewModel(this, tag) { IsSelected = selected.Contains(tag.Id) });
        }

        HasNoMatches = _all.Count > 0 && Options.Count == 0;
    }
}
