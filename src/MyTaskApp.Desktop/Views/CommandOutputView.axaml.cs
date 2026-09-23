using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

/// <summary>
/// O terminal de um comando (ADR-028). O código daqui é só a rolagem: a linha
/// nova aparece, a menos que o usuário tenha subido para ler o que passou.
/// </summary>
public sealed partial class CommandOutputView : UserControl
{
    private CommandOutputViewModel? _observed;

    public CommandOutputView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Observe(DataContext as CommandOutputViewModel);
    }

    private void Observe(CommandOutputViewModel? viewModel)
    {
        if (_observed is not null)
        {
            _observed.Lines.CollectionChanged -= OnLinesChanged;
        }

        _observed = viewModel;

        if (_observed is not null)
        {
            _observed.Lines.CollectionChanged += OnLinesChanged;
            ScrollToEnd();
        }
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add && IsFollowing())
        {
            // Depois do layout da linha nova, senão rola até a penúltima.
            Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
        }
    }

    /// <summary>Está no fim (ou perto dele): segue a saída.</summary>
    private bool IsFollowing()
    {
        if (LinesList.Scroll is not { } scroll)
        {
            return true;
        }

        return scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 40;
    }

    private void ScrollToEnd()
    {
        if (_observed is { Lines.Count: > 0 } viewModel)
        {
            LinesList.ScrollIntoView(viewModel.Lines.Count - 1);
        }
    }
}
