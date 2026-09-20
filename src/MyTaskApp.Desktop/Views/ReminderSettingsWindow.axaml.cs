using Avalonia.Controls;
using Avalonia.Interactivity;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

public sealed partial class ReminderSettingsWindow : Window
{
    public ReminderSettingsWindow() => InitializeComponent();

    public ReminderSettingsWindow(ReminderSettingsViewModel viewModel)
        : this()
    {
        DataContext = viewModel;

        // Salvar fecha: a tela tem um assunto só, e ficar aberta depois de
        // gravar só faria o usuário se perguntar se funcionou.
        viewModel.Saved += Hide;
    }

    /// <summary>
    /// Recarrega a cada abertura: a pausa pode ter sido ligada pela bandeja
    /// desde a última vez, e a tela precisa mostrar isso.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is ReminderSettingsViewModel viewModel)
        {
            _ = viewModel.LoadAsync(CancellationToken.None);
        }
    }
}
