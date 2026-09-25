using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Application.Commands;
using MyTaskApp.Desktop.Notes;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// Os comandos globais como a aba Desenvolvimento os conhece (ADR-028): uma
/// lista só, lida por todas as caixas de comando da tarefa.
/// </summary>
public sealed partial class DevelopmentCommandCatalog : ObservableObject
{
    private IReadOnlyDictionary<string, DevelopmentCommandRow> _byAlias =
        new Dictionary<string, DevelopmentCommandRow>(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCommands))]
    private IReadOnlyList<DevelopmentCommandRow> _rows = [];

    public bool HasCommands => Rows.Count > 0;

    public DevelopmentCommandRow? Find(string alias) =>
        _byAlias.TryGetValue(alias, out var row) ? row : null;

    partial void OnRowsChanged(IReadOnlyList<DevelopmentCommandRow> value) =>
        _byAlias = value
            .GroupBy(row => row.Alias, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>Um item do autocomplete de comandos: o apelido e o que ele roda.</summary>
public sealed partial class CommandSuggestionViewModel(DevelopmentCommandRow row) : ObservableObject
{
    public DevelopmentCommandRow Row { get; } = row;

    public string Alias => Row.Alias;

    public string Command => Row.Command;

    public string? Description => Row.Description;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// A lista de <c>@alias</c> sobre uma caixa de comando pós-Worktree (ADR-028).
/// Mesmo gesto dos diretórios, com duas diferenças: só o <b>primeiro</b> termo
/// é alias — é o único que a execução resolve —, e aceitar insere o próprio
/// apelido, que é referência e não atalho.
/// </summary>
public sealed partial class CommandCompletionViewModel(DevelopmentCommandCatalog catalog)
    : ObservableObject, IAliasCompletionSource
{
    private int? _dismissedTokenStart;

    [ObservableProperty]
    private bool _isCompletionOpen;

    [ObservableProperty]
    private CommandSuggestionViewModel? _selectedSuggestion;

    public ObservableCollection<CommandSuggestionViewModel> Suggestions { get; } = [];

    public AliasToken? CompletionToken { get; private set; }

    public bool IsEnabled { get; set; } = true;

    object? IAliasCompletionSource.SelectedItem => SelectedSuggestion;

    /// <summary>
    /// O apelido e, se o comando tem <c>{nome}</c>, os <c>nome=</c> a preencher:
    /// o usuário só digita o valor.
    /// </summary>
    string? IAliasCompletionSource.ReplacementFor(object? suggestion)
    {
        if (suggestion is not CommandSuggestionViewModel item || !Suggestions.Contains(item))
        {
            return null;
        }

        var parameters = CommandParameters.Names(item.Command);

        return parameters.Count == 0
            ? item.Alias
            : string.Join(' ', parameters.Select(name => $"{name}=").Prepend(item.Alias));
    }

    public void UpdateCompletion(string? text, int caretIndex)
    {
        var token = IsEnabled && catalog.HasCommands
            ? AliasCompletion.FindToken(text, caretIndex)
            : null;

        // Só o começo da linha: "npm run @x" não é alias de ninguém.
        if (token is null || !string.IsNullOrWhiteSpace(text![..token.Value.Start]))
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

        // O comando também conta no filtro: quem lembra de "npm" acha @npm-install.
        var matches = AliasCompletion.Filter(
            catalog.Rows,
            token.Value.Query,
            row => row.Alias,
            row => row.Command);

        if (matches.Count == 0)
        {
            CloseCompletion();
            return;
        }

        var previous = SelectedSuggestion?.Row.Id;

        Suggestions.Clear();

        foreach (var row in matches)
        {
            Suggestions.Add(new CommandSuggestionViewModel(row));
        }

        CompletionToken = token;
        Select(Suggestions.FirstOrDefault(item => item.Row.Id == previous) ?? Suggestions[0]);
        IsCompletionOpen = true;
    }

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

    public void Select(CommandSuggestionViewModel suggestion)
    {
        if (SelectedSuggestion is not null)
        {
            SelectedSuggestion.IsSelected = false;
        }

        suggestion.IsSelected = true;
        SelectedSuggestion = suggestion;
    }

    public void DismissCompletion()
    {
        _dismissedTokenStart = CompletionToken?.Start;
        CloseCompletion();
    }

    /// <summary>O token sai antes de a lista fechar (ver <see cref="AliasCompletionViewModel"/>).</summary>
    private void CloseCompletion()
    {
        CompletionToken = null;
        SelectedSuggestion = null;
        IsCompletionOpen = false;
        Suggestions.Clear();
    }
}
