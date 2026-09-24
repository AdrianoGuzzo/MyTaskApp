using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
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
/// <para>
/// Desde o ADR-027 a janela tem a aba Desenvolvimento. O autocomplete de
/// <c>@alias</c> serve às duas caixas — a anotação e o campo Diretório — por
/// um <see cref="AliasCompletionBinder"/> cada.
/// </para>
/// </remarks>
public sealed partial class TaskNotesWindow : Window
{
    private readonly IConfirmationDialog? _confirmation;

    private readonly AliasCompletionBinder _notesCompletion;

    private readonly AliasCompletionBinder _directoryCompletion;

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

        _notesCompletion = new AliasCompletionBinder(
            Editor,
            AliasPopup,
            () => ViewModel?.Completion);

        _directoryCompletion = new AliasCompletionBinder(
            DirectoryBox,
            DirectoryPopup,
            () => ViewModel?.Developments.DirectoryCompletion);

        // A cada ativação, e não só na abertura: etiquetas e diretórios podem ter
        // mudado em outra janela enquanto esta estava aberta.
        Activated += (_, _) =>
        {
            if (ViewModel is { IsEditable: true } viewModel)
            {
                _ = viewModel.LoadAliasesAsync(CancellationToken.None);
                _ = viewModel.Developments.Selected?.LoadGlobalCommandsAsync(CancellationToken.None);
            }
        };
    }

    public TaskNotesWindow(TaskNotesViewModel viewModel, IConfirmationDialog confirmation)
        : this()
    {
        _confirmation = confirmation;

        DataContext = viewModel;
        viewModel.CloseRequested += Close;
    }

    private TaskNotesViewModel? ViewModel => DataContext as TaskNotesViewModel;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (ViewModel is not { IsEditable: true, IsNotesTab: true })
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
    /// <para>
    /// A criação do worktree também segura a janela (ADR-027): ela não pode ser
    /// interrompida, e fechar no meio esconderia o resultado. A preparação,
    /// sim, pode — e fechar a cancela.
    /// </para>
    /// </remarks>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_discarding
            && e.CloseReason is WindowCloseReason.WindowClosing
            && ViewModel is { } viewModel)
        {
            // Qualquer repositório da tarefa ocupado segura a janela (ADR-031).
            var busy = viewModel.Developments.Busy;

            if (busy is { IsRunning: true })
            {
                e.Cancel = true;

                if (busy.CanCancel)
                {
                    busy.CancelRun();
                }
                else
                {
                    viewModel.SelectedTabIndex = TaskNotesViewModel.DevelopmentTab;
                    viewModel.Developments.Selected = busy;
                    busy.Message = "Aguarde a criação do worktree terminar para fechar.";
                }
            }
            else if (busy is { Commands.IsRunning: true })
            {
                // Fechar com um comando rodando o mataria sem ninguém ver o
                // output: o primeiro "X" cancela e mostra; o segundo fecha.
                e.Cancel = true;
                viewModel.SelectedTabIndex = TaskNotesViewModel.DevelopmentTab;
                viewModel.Developments.Selected = busy;
                busy.CancelCommands();
                busy.Message = "Execução dos comandos cancelada. Feche de novo para sair.";
            }
            else if (_confirmation is not null && viewModel.HasUnsavedChanges)
            {
                e.Cancel = true;
                _ = AskThenCloseAsync(viewModel);
            }
        }

        base.OnClosing(e);
    }

    private async Task AskThenCloseAsync(TaskNotesViewModel viewModel)
    {
        var discard = await _confirmation!.AskAsync(new ConfirmationRequest(
            "Descartar as alterações?",
            "O que você mudou no título ou na anotação ainda não foi salvo. Fechar agora perde as alterações.",
            "Descartar"));

        if (!discard)
        {
            return;
        }

        viewModel.Discard();
        _discarding = true;
        Close();
    }

    /// <summary>
    /// O título é uma linha só: Enter não quebra, leva para a anotação — o
    /// próximo lugar onde se escreve. Gravar continua sendo Ctrl+S ou o botão.
    /// </summary>
    private void OnTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return) || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        e.Handled = true;
        Editor.Focus();
        Editor.CaretIndex = Editor.Text?.Length ?? 0;
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
        // Com uma lista aberta, as teclas dela vêm antes de tudo — inclusive do
        // Escape que fecha a janela e do Enter que quebraria a linha.
        if (_notesCompletion.HandleKey(e)
            || _directoryCompletion.HandleKey(e)
            || ((e.Source as Visual)?.FindAncestorOfType<CommandInputBox>(includeSelf: true)?.HandleKey(e) ?? false))
        {
            e.Handled = true;
            return;
        }

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

    private void OnAliasPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: AliasSuggestionViewModel suggestion })
        {
            return;
        }

        e.Handled = true;

        if (_directoryCompletion.Owns(suggestion))
        {
            _directoryCompletion.Accept(suggestion);
        }
        else
        {
            _notesCompletion.Accept(suggestion);
        }
    }

    /// <summary>Usado pelos testes de renderização, como o clique num item.</summary>
    internal void AcceptSuggestion(AliasSuggestionViewModel? suggestion) =>
        _notesCompletion.Accept(suggestion);

    /// <summary>
    /// O "@" ao lado do campo Diretório: abre a lista de todos os diretórios das
    /// etiquetas, como se o usuário tivesse digitado "@".
    /// </summary>
    private void OnDirectoryAliasesClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.Developments.Selected is not { } development)
        {
            return;
        }

        if (!development.DirectoryCompletion.HasDirectories)
        {
            development.Message =
                "As etiquetas desta tarefa não têm diretórios. Cadastre-os em Etiquetas, ou digite o caminho.";
            return;
        }

        DirectoryBox.Text = "@";
        DirectoryBox.Focus();
        DirectoryBox.CaretIndex = 1;
    }

    private async void OnBrowseDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Escolher o repositório") is { } path
            && ViewModel?.Developments.Selected is { } development)
        {
            development.DirectoryText = path;
        }
    }

    /// <summary>
    /// "Escolher outro caminho": escolhe a pasta onde o worktree vai morar e
    /// mantém o nome sugerido — o worktree é uma pasta nova, que ainda não existe.
    /// </summary>
    private async void OnBrowseAlternativeClick(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Escolher onde criar o worktree") is { } parent
            && ViewModel?.Developments.Selected is { } development)
        {
            var name = Path.GetFileName(development.AlternativePath.TrimEnd('\\', '/'));
            development.AlternativePath = Path.Combine(parent, string.IsNullOrEmpty(name) ? "worktree" : name);
        }
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private void Format(MarkdownCommand command)
    {
        if (ViewModel is not { IsEditable: true, IsNotesTab: true })
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
