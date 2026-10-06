using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

/// <summary>
/// Pergunta os parâmetros de um comando rápido e mostra a linha final antes de
/// executar (ADR-051). Porta, como <see cref="IConfirmationDialog"/>, para os
/// ViewModels serem testados sem janela.
/// </summary>
public interface IQuickCommandPrompt
{
    /// <summary>Os valores digitados, ou <c>null</c> se o usuário cancelou.</summary>
    Task<IReadOnlyDictionary<string, string>?> AskAsync(QuickCommandPlan plan);
}

public sealed partial class QuickCommandPromptWindow : Window
{
    public QuickCommandPromptWindow()
    {
        InitializeComponent();
    }

    public QuickCommandPromptWindow(QuickCommandPromptViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        Title = viewModel.Title;
    }

    /// <summary>A resposta, também fora do modal — como na <see cref="ConfirmWindow"/>.</summary>
    public bool Answer { get; private set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Com parâmetros, o foco vai para o primeiro; só confirmação, para
        // Cancelar — o Enter reflexo não roda um comando que pediu para ser lido.
        if (Fields.ItemCount > 0)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => FirstField()?.Focus());
        }
        else
        {
            CancelButton.Focus();
        }
    }

    private Control? FirstField() =>
        Fields.GetRealizedContainers()
            .Select(container => container.FindDescendantOfType<TextBox>() as Control
                                 ?? container.FindDescendantOfType<ComboBox>())
            .FirstOrDefault(control => control is not null);

    private void OnRun(object? sender, RoutedEventArgs e)
    {
        if (DataContext is QuickCommandPromptViewModel { CanAccept: false })
        {
            return;
        }

        Answer = true;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Answer = false;
        Close(false);
    }
}

/// <summary>A implementação de verdade: modal da janela ativa, como a confirmação.</summary>
internal sealed class QuickCommandPrompt : IQuickCommandPrompt
{
    public async Task<IReadOnlyDictionary<string, string>?> AskAsync(QuickCommandPlan plan)
    {
        var viewModel = new QuickCommandPromptViewModel(plan);
        var window = new QuickCommandPromptWindow(viewModel);
        var owner = FindOwner();
        bool answer;

        if (owner is not null)
        {
            answer = await window.ShowDialog<bool>(owner);
        }
        else
        {
            var closed = new TaskCompletionSource<bool>();

            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Closed += (_, _) => closed.TrySetResult(window.Answer);
            window.Show();
            answer = await closed.Task;
        }

        return answer ? viewModel.Values : null;
    }

    private static Window? FindOwner() =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.FirstOrDefault(candidate => candidate.IsActive) ?? desktop.MainWindow
            : null;
}
