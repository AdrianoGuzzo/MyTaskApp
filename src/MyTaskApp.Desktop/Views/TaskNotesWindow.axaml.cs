using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MyTaskApp.Desktop.Notes;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

/// <summary>
/// A tela de anotações de um item do checklist (§12). Abre maximizada: o texto
/// livre é a única coisa que interessa enquanto ela está aberta, e o painel de
/// 360px não tem onde escrever.
/// </summary>
/// <remarks>
/// <para>
/// É uma janela <b>por item</b>, e não um singleton como a de gerenciamento de
/// dados: duas anotações diferentes podem estar abertas ao mesmo tempo, e o
/// truque do "X que esconde" existe justamente para uma instância só. Quem
/// impede duas janelas do <i>mesmo</i> item é o composition root, que guarda as
/// abertas por identificador.
/// </para>
/// <para>
/// A barra de formatação vive no code-behind porque depende da seleção da
/// <see cref="TextBox"/>, que é estado de controle e não de ViewModel. O que
/// dá para separar foi separado: a aritmética inteira está em
/// <see cref="MarkdownEditing"/>, testada sem janela nenhuma.
/// </para>
/// </remarks>
public sealed partial class TaskNotesWindow : Window
{
    private readonly IConfirmationDialog? _confirmation;

    /// <summary>
    /// Liga depois de o usuário confirmar o descarte: sem isto, o
    /// <see cref="Close()"/> que vem da confirmação cairia na mesma pergunta.
    /// </summary>
    private bool _discarding;

    public TaskNotesWindow()
    {
        InitializeComponent();

        // Túnel: a TextBox recebe o KeyDown antes da janela, e os atalhos de
        // formatação precisam chegar primeiro. Mesmo motivo do Enter da captura
        // rápida em TodayView.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    public TaskNotesWindow(TaskNotesViewModel viewModel, IConfirmationDialog confirmation)
        : this()
    {
        _confirmation = confirmation;

        DataContext = viewModel;
        viewModel.CloseRequested += Close;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is not TaskNotesViewModel { IsEditable: true })
        {
            return;
        }

        // Cursor no fim do que já existe: abrir para acrescentar é o caso comum,
        // e começar com o texto todo selecionado convidaria a apagá-lo.
        Editor.Focus();
        Editor.CaretIndex = Editor.Text?.Length ?? 0;
    }

    /// <summary>
    /// Fechar com texto não salvo pergunta antes. É a única perda possível nesta
    /// tela — salvar é explícito, então o "X" é um caminho fácil para jogar fora
    /// meia hora de anotação sem perceber.
    /// </summary>
    /// <remarks>
    /// Só o fechamento pedido pelo usuário é interceptado. Encerrar o app fecha
    /// por outro motivo, e segurar <b>aquele</b> numa caixa de pergunta deixaria
    /// o processo pendurado na bandeja.
    /// </remarks>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_discarding
            && e.CloseReason is WindowCloseReason.WindowClosing
            && _confirmation is not null
            && DataContext is TaskNotesViewModel { HasUnsavedChanges: true } viewModel)
        {
            e.Cancel = true;
            _ = AskThenCloseAsync(viewModel);
        }

        base.OnClosing(e);
    }

    private async Task AskThenCloseAsync(TaskNotesViewModel viewModel)
    {
        var discard = await _confirmation!.AskAsync(new ConfirmationRequest(
            "Descartar esta anotação?",
            "O que você escreveu ainda não foi salvo. Fechar agora perde o texto.",
            "Descartar"));

        if (!discard)
        {
            return;
        }

        viewModel.Discard();
        _discarding = true;
        Close();
    }

    private void OnFormatClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string tag }
            && Enum.TryParse<MarkdownCommand>(tag, out var command))
        {
            Format(command);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        var command = e.Key switch
        {
            Key.B => MarkdownCommand.Bold,
            Key.I => MarkdownCommand.Italic,
            Key.U => MarkdownCommand.Underline,
            _ => (MarkdownCommand?)null,
        };

        if (command is null)
        {
            return;
        }

        e.Handled = true;
        Format(command.Value);
    }

    private void Format(MarkdownCommand command)
    {
        if (DataContext is not TaskNotesViewModel { IsEditable: true })
        {
            return;
        }

        var start = Editor.SelectionStart;
        var length = Editor.SelectionEnd - Editor.SelectionStart;

        var edit = MarkdownEditing.Apply(Editor.Text, start, length, command);

        // Na caixa, e não no ViewModel: trocar o texto pela amarração faria a
        // TextBox recolocar o cursor no fim antes de a seleção ser aplicada.
        Editor.Text = edit.Text;
        Editor.SelectionStart = edit.SelectionStart;
        Editor.SelectionEnd = edit.SelectionStart + edit.SelectionLength;

        Editor.Focus();
    }
}
