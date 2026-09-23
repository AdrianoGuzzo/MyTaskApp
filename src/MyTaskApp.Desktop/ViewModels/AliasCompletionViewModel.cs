using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.Notes;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// A lista de <c>@alias</c> sobre uma caixa de texto (ADR-026): o <c>@</c> abre,
/// o que vem depois filtra, e sair do alias fecha. Uma instância por caixa — a
/// anotação e o campo Diretório da aba Desenvolvimento (ADR-027) têm cada uma a
/// sua, com os mesmos diretórios.
/// </summary>
public sealed partial class AliasCompletionViewModel : ObservableObject, IAliasCompletionSource
{
    /// <summary>Os diretórios das etiquetas da tarefa, na última carga.</summary>
    private IReadOnlyList<TagDirectoryRow> _directories = [];

    /// <summary>Se cada pasta existia na última carga, por id do diretório.</summary>
    private IReadOnlyDictionary<Guid, bool> _existence = new Dictionary<Guid, bool>();

    /// <summary>
    /// O <c>@</c> que o usuário dispensou (Escape, ou o que acabou de ser
    /// aceito). A lista não reabre sobre ele; um <c>@</c> novo, sim.
    /// </summary>
    private int? _dismissedTokenStart;

    /// <summary>A lista de atalhos está aberta sobre o texto.</summary>
    [ObservableProperty]
    private bool _isCompletionOpen;

    [ObservableProperty]
    private AliasSuggestionViewModel? _selectedSuggestion;

    public ObservableCollection<AliasSuggestionViewModel> Suggestions { get; } = [];

    /// <summary>O <c>@texto</c> que a lista aberta completa.</summary>
    public AliasToken? CompletionToken { get; private set; }

    /// <summary>Desligado (tarefa só de leitura), a lista nunca abre.</summary>
    public bool IsEnabled { get; set; } = true;

    public bool HasDirectories => _directories.Count > 0;

    public IReadOnlyList<TagDirectoryRow> Directories => _directories;

    object? IAliasCompletionSource.SelectedItem => SelectedSuggestion;

    /// <summary>Aceitar um diretório insere o path: o alias é só atalho de digitação.</summary>
    string? IAliasCompletionSource.ReplacementFor(object? suggestion) =>
        suggestion is AliasSuggestionViewModel item && Suggestions.Contains(item) ? item.Path : null;

    public void SetDirectories(
        IReadOnlyList<TagDirectoryRow> directories,
        IReadOnlyDictionary<Guid, bool> existence)
    {
        _directories = directories;
        _existence = existence;
        OnPropertyChanged(nameof(HasDirectories));
    }

    /// <summary>Esquece tudo: outra tarefa, outros diretórios.</summary>
    public void Reset()
    {
        _dismissedTokenStart = null;
        SetDirectories([], new Dictionary<Guid, bool>());
        CloseCompletion();
    }

    /// <summary>
    /// Abre, filtra ou fecha a lista conforme o texto e o cursor. Chamado a cada
    /// tecla: o <c>@</c> abre, o que vem depois filtra, e sair do alias fecha.
    /// </summary>
    public void UpdateCompletion(string? text, int caretIndex)
    {
        var token = IsEnabled && _directories.Count > 0
            ? AliasCompletion.FindToken(text, caretIndex)
            : null;

        if (token is null)
        {
            _dismissedTokenStart = null;
            CloseCompletion();
            return;
        }

        if (token.Value.Start == _dismissedTokenStart)
        {
            CloseCompletion();
            return;
        }

        var matches = AliasCompletion.Filter(
            _directories,
            token.Value.Query,
            directory => directory.Alias,
            directory => directory.Name);

        if (matches.Count == 0)
        {
            CloseCompletion();
            return;
        }

        // Mantém a escolha do teclado enquanto ela continuar na lista: filtrar
        // mais uma letra não deveria jogar o destaque de volta para o topo.
        var previous = SelectedSuggestion?.Row.Id;

        Suggestions.Clear();

        foreach (var directory in matches)
        {
            Suggestions.Add(new AliasSuggestionViewModel(
                directory,
                _existence.TryGetValue(directory.Id, out var exists) ? exists : null));
        }

        CompletionToken = token;
        Select(Suggestions.FirstOrDefault(item => item.Row.Id == previous) ?? Suggestions[0]);
        IsCompletionOpen = true;
    }

    /// <summary>Anda na lista pelo teclado, dando a volta nas pontas.</summary>
    public void MoveSelection(int delta)
    {
        if (!IsCompletionOpen || Suggestions.Count == 0)
        {
            return;
        }

        var index = SelectedSuggestion is null ? -1 : Suggestions.IndexOf(SelectedSuggestion);
        var next = ((index + delta) % Suggestions.Count + Suggestions.Count) % Suggestions.Count;

        Select(Suggestions[next]);
    }

    public void Select(AliasSuggestionViewModel suggestion)
    {
        if (SelectedSuggestion is not null)
        {
            SelectedSuggestion.IsSelected = false;
        }

        suggestion.IsSelected = true;
        SelectedSuggestion = suggestion;
    }

    /// <summary>
    /// Fecha a lista sem inserir nada, e não a reabre sobre o mesmo <c>@</c>:
    /// quem apertou Escape quis escrever um "@" de verdade.
    /// </summary>
    public void DismissCompletion()
    {
        _dismissedTokenStart = CompletionToken?.Start;
        CloseCompletion();
    }

    /// <summary>
    /// O token sai <b>antes</b> de a lista fechar. O <c>IsOpen</c> do Popup é
    /// amarrado nos dois sentidos, e fechá-lo dispara o <c>Closed</c> na hora;
    /// quem trata o <c>Closed</c> ("fechou por fora? então dispensa") veria o
    /// token ainda lá e marcaria como dispensado um <c>@</c> que o usuário só
    /// estava filtrando — "@ecx" e Backspace não reabririam mais a lista.
    /// </summary>
    private void CloseCompletion()
    {
        CompletionToken = null;
        SelectedSuggestion = null;
        IsCompletionOpen = false;
        Suggestions.Clear();
    }
}
