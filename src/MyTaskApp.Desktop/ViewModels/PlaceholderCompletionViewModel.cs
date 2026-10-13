using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Application.Commands;
using MyTaskApp.Desktop.Notes;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Um item do <c>{</c>: o <c>{nome}</c>, o que ele é e, quando se sabe, o valor.</summary>
public sealed partial class PlaceholderSuggestionViewModel(CommandPlaceholder placeholder) : ObservableObject
{
    public CommandPlaceholder Placeholder { get; } = placeholder;

    public string Text => Placeholder.Placeholder;

    /// <summary>"= lock_eco_core_1010_20261013_101500", ou a descrição enquanto não há valor.</summary>
    public string Detail => Placeholder.Value is { Length: > 0 } value ? $"= {value}" : Placeholder.Description;

    public bool IsDatabaseAlias => Placeholder.IsDatabaseAlias;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// A lista do <c>{</c> numa caixa de comando ou num valor de parâmetro (ADR-060):
/// os apelidos de banco e as variáveis do ambiente. Mesmo gesto do <c>@</c>,
/// mas vale em qualquer lugar da linha, e aceitar insere o <c>{nome}</c> já
/// fechado.
/// </summary>
public sealed partial class PlaceholderCompletionViewModel : ObservableObject, IAliasCompletionSource
{
    private int? _dismissedTokenStart;

    [ObservableProperty]
    private bool _isCompletionOpen;

    [ObservableProperty]
    private PlaceholderSuggestionViewModel? _selectedSuggestion;

    /// <summary>O que a lista oferece; quem hospeda troca quando os apelidos ou o ambiente mudam.</summary>
    public IReadOnlyList<CommandPlaceholder> Items { get; set; } = CommandPlaceholders.Variables();

    public ObservableCollection<PlaceholderSuggestionViewModel> Suggestions { get; } = [];

    public AliasToken? CompletionToken { get; private set; }

    public bool IsEnabled { get; set; } = true;

    object? IAliasCompletionSource.SelectedItem => SelectedSuggestion;

    /// <summary>Os caracteres de um nome de variável, com o ponto de <c>{database.host}</c>.</summary>
    public static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.';

    bool IAliasCompletionSource.IsTokenChar(char c) => IsNameChar(c);

    string? IAliasCompletionSource.ReplacementFor(object? suggestion) =>
        suggestion is PlaceholderSuggestionViewModel item && Suggestions.Contains(item) ? item.Text : null;

    public void UpdateCompletion(string? text, int caretIndex)
    {
        var token = IsEnabled && Items.Count > 0
            ? AliasCompletion.FindToken(text, caretIndex, IsNameChar, '{')
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

        // A descrição também conta: "banco" acha os apelidos e {database}.
        var matches = AliasCompletion.Filter(Items, token.Value.Query, item => item.Name, item => item.Description);

        if (matches.Count == 0)
        {
            CloseCompletion();
            return;
        }

        var previous = SelectedSuggestion?.Placeholder.Name;

        Suggestions.Clear();

        foreach (var item in matches)
        {
            Suggestions.Add(new PlaceholderSuggestionViewModel(item));
        }

        CompletionToken = token;
        Select(Suggestions.FirstOrDefault(item => item.Placeholder.Name == previous) ?? Suggestions[0]);
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

    public void Select(PlaceholderSuggestionViewModel suggestion)
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

    private void CloseCompletion()
    {
        CompletionToken = null;
        SelectedSuggestion = null;
        IsCompletionOpen = false;
        Suggestions.Clear();
    }
}
