using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Notes;

/// <summary>
/// Liga uma <see cref="TextBox"/>, o <see cref="Popup"/> da lista e um
/// <see cref="AliasCompletionViewModel"/> (ADR-026, ADR-027). O que depende de
/// controle mora aqui: cursor, teclado, posição do popup e a troca do
/// <c>@alias</c> pelo caminho, feita direto na caixa para o cursor não pular.
/// </summary>
/// <remarks>
/// Existe para a anotação e o campo Diretório compartilharem o mesmo
/// comportamento sem cada janela repetir o code-behind — foi o que a primeira
/// versão fez, e o bug do Popup ficou num lugar só por sorte.
/// </remarks>
internal sealed class AliasCompletionBinder
{
    private readonly TextBox _box;

    private readonly Popup _popup;

    private readonly Func<AliasCompletionViewModel?> _viewModel;

    public AliasCompletionBinder(TextBox box, Popup popup, Func<AliasCompletionViewModel?> viewModel)
    {
        _box = box;
        _popup = popup;
        _viewModel = viewModel;

        // Cada tecla e cada movimento do cursor: clicar fora do "@" também tem de
        // fechar a lista.
        _box.TextChanged += (_, _) => Update();
        _box.PropertyChanged += OnBoxPropertyChanged;
        _popup.Closed += OnPopupClosed;
    }

    /// <summary>
    /// As teclas da lista, quando ela está aberta sobre <b>esta</b> caixa. Vêm
    /// antes de tudo — inclusive do Escape que fecharia a janela e do Enter que
    /// quebraria a linha. Devolve se a tecla foi consumida.
    /// </summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (_viewModel() is not { IsCompletionOpen: true } viewModel
            || !_box.IsKeyboardFocusWithin
            || e.KeyModifiers is not KeyModifiers.None)
        {
            return false;
        }

        switch (e.Key)
        {
            case Key.Down:
                viewModel.MoveSelection(1);
                return true;
            case Key.Up:
                viewModel.MoveSelection(-1);
                return true;
            case Key.Enter or Key.Tab:
                Accept(viewModel.SelectedSuggestion);
                return true;
            case Key.Escape:
                viewModel.DismissCompletion();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Se a sugestão é desta lista — para o clique saber a quem pertence.</summary>
    public bool Owns(AliasSuggestionViewModel suggestion) =>
        _viewModel()?.Suggestions.Contains(suggestion) == true;

    /// <summary>
    /// Troca o "@alias" pelo path real. O texto não guarda o alias: é atalho de
    /// digitação, não referência.
    /// </summary>
    public void Accept(AliasSuggestionViewModel? suggestion)
    {
        if (suggestion is null || _viewModel() is not { CompletionToken: { } token } viewModel)
        {
            return;
        }

        var edit = AliasCompletion.Accept(_box.Text, token, _box.CaretIndex, suggestion.Path);

        _box.Text = edit.Text;
        _box.CaretIndex = edit.CaretIndex;

        // Um path com "@" no fim não pode reabrir a lista sobre o que acabou de
        // ser inserido.
        viewModel.UpdateCompletion(_box.Text, _box.CaretIndex);
        viewModel.DismissCompletion();

        _box.Focus();
    }

    private void OnBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.CaretIndexProperty)
        {
            Update();
        }
    }

    /// <summary>A lista fechou por fora (clique fora dela): não reabre no mesmo "@".</summary>
    private void OnPopupClosed(object? sender, EventArgs e)
    {
        if (_viewModel() is { CompletionToken: not null } viewModel)
        {
            viewModel.DismissCompletion();
        }
    }

    private void Update()
    {
        if (_viewModel() is not { IsEnabled: true } viewModel || !_box.IsEffectivelyVisible)
        {
            return;
        }

        viewModel.UpdateCompletion(_box.Text, _box.CaretIndex);

        if (viewModel.IsCompletionOpen)
        {
            _popup.PlacementRect = CaretRect();
        }
    }

    /// <summary>
    /// Onde está o cursor, nas coordenadas da caixa — a lista abre logo abaixo
    /// dele. Sem o layout pronto, o popup cai embaixo da caixa inteira.
    /// </summary>
    private Rect? CaretRect()
    {
        var presenter = _box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();

        if (presenter is null)
        {
            return null;
        }

        var caret = presenter.TextLayout.HitTestTextPosition(_box.CaretIndex);

        return presenter.TranslatePoint(caret.TopLeft, _box) is { } origin
            ? new Rect(origin, caret.Size)
            : null;
    }
}
