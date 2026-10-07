using Avalonia.Controls;
using Avalonia.Input;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

/// <summary>O painel "Histórico · 7 dias" (ADR-053). Quem o abre e fecha é o flyout do cabeçalho.</summary>
public sealed partial class ActivityHistoryPanel : UserControl
{
    public ActivityHistoryPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Esc fecha quando o foco está dentro do painel — depois de um clique nele.
    /// Com o foco ainda na janela, quem fecha é a <see cref="MainWindow"/>.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ActivityHistoryViewModel history)
        {
            history.Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
