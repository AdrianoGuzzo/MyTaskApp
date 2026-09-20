namespace MyTaskApp.Desktop.ViewModels;

public sealed class TodaySectionViewModel(string header, IReadOnlyList<TaskRowViewModel> items)
{
    public string Header { get; } = header;

    public IReadOnlyList<TaskRowViewModel> Items { get; } = items;
}
