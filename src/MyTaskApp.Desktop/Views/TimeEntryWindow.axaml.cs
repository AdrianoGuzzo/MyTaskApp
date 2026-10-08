using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

/// <summary>O diálogo de um período de trabalho (ADR-052).</summary>
public sealed partial class TimeEntryWindow : Window
{
    /// <summary>Pôr o dia digitado no calendário ao abrir não é escolher.</summary>
    private bool _syncingCalendar;

    public TimeEntryWindow()
    {
        InitializeComponent();
    }

    public TimeEntryWindow(TimeEntryEditorViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        Title = viewModel.Heading;
    }

    /// <summary>A resposta, também fora do modal — como na <see cref="ConfirmWindow"/>.</summary>
    public bool Answer { get; private set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // O horário é o que se corrige quase sempre; o dia já vem certo.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => StartBox.Focus());
    }

    /// <summary>Entrar no campo seleciona tudo: digitar <c>0831</c> substitui o que estava lá.</summary>
    private void OnFieldGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.SelectAll();
        }
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e) =>
        (DataContext as TimeEntryEditorViewModel)?.Tidy();

    /// <summary>O calendário abre no dia digitado e não oferece o futuro.</summary>
    private void OnCalendarOpening(object? sender, EventArgs e)
    {
        if (DataContext is not TimeEntryEditorViewModel viewModel)
        {
            return;
        }

        var today = viewModel.Today.ToDateTime(TimeOnly.MinValue);
        var day = viewModel.Date?.ToDateTime(TimeOnly.MinValue);

        _syncingCalendar = true;

        try
        {
            // Riscados, e não escondidos: o DisplayDateEnd deixaria um buraco no mês.
            DayCalendar.BlackoutDates.Clear();
            DayCalendar.BlackoutDates.Add(new CalendarDateRange(today.AddDays(1), today.AddYears(10)));
            DayCalendar.SelectedDate = day <= today ? day : null;
            DayCalendar.DisplayDate = day <= today ? day.Value : today;
        }
        finally
        {
            _syncingCalendar = false;
        }
    }

    private void OnCalendarPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingCalendar
            || DataContext is not TimeEntryEditorViewModel viewModel
            || DayCalendar.SelectedDate is not { } picked)
        {
            return;
        }

        viewModel.PickDate(DateOnly.FromDateTime(picked));
        CalendarButton.Flyout?.Hide();
        StartBox.Focus();
    }

    /// <summary>
    /// Grava pelo caso de uso e só fecha se ele aceitou: uma sobreposição recusada
    /// deixa a mensagem na tela e os campos como estavam.
    /// </summary>
    private async void OnAccept(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TimeEntryEditorViewModel viewModel)
        {
            return;
        }

        if (await viewModel.AcceptAsync())
        {
            Answer = true;
            Close(true);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Answer = false;
        Close(false);
    }
}

/// <summary>A implementação de verdade: modal da janela ativa, como a confirmação.</summary>
internal sealed class TimeEntryEditor : ITimeEntryEditor
{
    public async Task<bool> EditAsync(TimeEntryEditorRequest request)
    {
        var window = new TimeEntryWindow(new TimeEntryEditorViewModel(request));
        var owner = FindOwner();

        if (owner is not null)
        {
            return await window.ShowDialog<bool>(owner);
        }

        var closed = new TaskCompletionSource<bool>();

        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Closed += (_, _) => closed.TrySetResult(window.Answer);
        window.Show();

        return await closed.Task;
    }

    private static Window? FindOwner() =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.FirstOrDefault(candidate => candidate.IsActive) ?? desktop.MainWindow
            : null;
}
