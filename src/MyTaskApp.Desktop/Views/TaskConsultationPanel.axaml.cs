using Avalonia.Controls;
using Avalonia.Interactivity;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

/// <summary>
/// A aba Consulta Inteligente (ADR-081). O code-behind só faz o que é da
/// janela: o seletor de pastas do sistema e gravar as instruções ao sair da
/// caixa.
/// </summary>
public sealed partial class TaskConsultationPanel : UserControl
{
    public TaskConsultationPanel()
    {
        InitializeComponent();

        ConsultationInstructionsBox.LostFocus += OnInstructionsLostFocus;
        DataContextChanged += (_, _) => Attach();
    }

    private TaskConsultationViewModel? ViewModel => DataContext as TaskConsultationViewModel;

    private void Attach()
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.PickFolder = PickFolderAsync;
        }
    }

    private async Task<string?> PickFolderAsync(string title) =>
        TopLevel.GetTopLevel(this) is { } top
            ? await SystemPickers.PickFolderAsync(
                top.StorageProvider,
                title,
                message =>
                {
                    if (ViewModel is { } viewModel)
                    {
                        viewModel.ErrorMessage = message;
                    }
                })
            : null;

    private void OnInstructionsLostFocus(object? sender, RoutedEventArgs e) =>
        ViewModel?.CommitInstructionsCommand.Execute(null);
}
