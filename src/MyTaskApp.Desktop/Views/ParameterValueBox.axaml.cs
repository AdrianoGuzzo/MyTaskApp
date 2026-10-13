using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MyTaskApp.Desktop.Notes;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

/// <summary>
/// O valor de um parâmetro do comando rápido com o <c>{</c> (ADR-060): o mesmo
/// <see cref="AliasCompletionBinder"/> das caixas de comando.
/// </summary>
public sealed partial class ParameterValueBox : UserControl
{
    private readonly AliasCompletionBinder _placeholders;

    public ParameterValueBox()
    {
        InitializeComponent();

        _placeholders = new AliasCompletionBinder(
            ValueBox,
            PlaceholderPopup,
            () => (DataContext as QuickCommandFieldViewModel)?.Placeholders);

        // Antes do Enter que executaria e do Esc que cancelaria o diálogo.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Usado pelos testes, como o clique num item.</summary>
    internal void AcceptPlaceholder(PlaceholderSuggestionViewModel? suggestion) => _placeholders.Accept(suggestion);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.Handled && _placeholders.HandleKey(e))
        {
            e.Handled = true;
        }
    }

    private void OnPlaceholderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: PlaceholderSuggestionViewModel suggestion })
        {
            e.Handled = true;
            _placeholders.Accept(suggestion);
        }
    }
}
