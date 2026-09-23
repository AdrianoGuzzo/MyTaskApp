using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MyTaskApp.Desktop.Notes;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

/// <summary>
/// Uma caixa de comando pós-Worktree com o autocomplete de <c>@alias</c>
/// (ADR-028). O comportamento é o do <see cref="AliasCompletionBinder"/>, o
/// mesmo do campo Diretório.
/// </summary>
/// <remarks>
/// As teclas da lista (↑ ↓ Enter Tab Esc) chegam por <see cref="HandleKey"/>:
/// a janela tem um handler de túnel que fecharia tudo no Escape, e é ela quem
/// pergunta primeiro ao controle que tem o foco.
/// </remarks>
public sealed partial class CommandInputBox : UserControl
{
    private readonly AliasCompletionBinder _completion;

    public CommandInputBox()
    {
        InitializeComponent();

        _completion = new AliasCompletionBinder(
            CommandBox,
            CommandPopup,
            () => (DataContext as PostWorktreeCommandItemViewModel)?.Completion);

        // Uma janela sem o túnel da anotação (e os testes) também ganham as teclas.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>A tecla foi da lista aberta sobre esta caixa?</summary>
    public bool HandleKey(KeyEventArgs e) => _completion.HandleKey(e);

    /// <summary>Usado pelos testes, como o clique num item.</summary>
    internal void AcceptSuggestion(CommandSuggestionViewModel? suggestion) => _completion.Accept(suggestion);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.Handled && HandleKey(e))
        {
            e.Handled = true;
        }
    }

    private void OnSuggestionPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: CommandSuggestionViewModel suggestion })
        {
            e.Handled = true;
            _completion.Accept(suggestion);
        }
    }
}
