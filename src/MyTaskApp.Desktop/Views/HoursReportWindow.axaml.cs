using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

public sealed partial class HoursReportWindow : Window
{
    public HoursReportWindow()
    {
        InitializeComponent();
    }

    public HoursReportWindow(HoursReportViewModel viewModel)
        : this()
    {
        DataContext = viewModel;

        // "Salvar como" é serviço da janela; o que gravar é do ViewModel.
        viewModel.SaveCsv = (name, content) => SystemPickers.SaveTextFileAsync(
            StorageProvider,
            new FilePickerSaveOptions
            {
                Title = "Exportar o relatório de horas",
                SuggestedFileName = name,
                DefaultExtension = "csv",
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("Planilha CSV (separada por ;)") { Patterns = ["*.csv"] }],
            },
            content,
            message => viewModel.ErrorMessage = message);
    }

    /// <summary>O "X" esconde, não fecha: singleton, como as outras janelas do menu.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;

            if (DataContext is HoursReportViewModel viewModel)
            {
                viewModel.IsShown = false;
            }

            Hide();
        }

        base.OnClosing(e);
    }

    /// <summary>Recarrega a cada abertura: o relatório é sempre o de agora.</summary>
    public void Reveal()
    {
        if (DataContext is HoursReportViewModel viewModel)
        {
            _ = viewModel.ShowAsync(CancellationToken.None);
        }
    }

    /// <summary>Esc fecha, se o gráfico não usou a tecla para desfazer a escolha.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape && !e.Handled)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>O duplo clique na linha desce um nível, como no gráfico.</summary>
    private void OnTableDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is HoursReportViewModel viewModel && sender is ListBox { SelectedIndex: >= 0 } table)
        {
            viewModel.DrillCommand.Execute(table.SelectedIndex);
        }
    }
}
