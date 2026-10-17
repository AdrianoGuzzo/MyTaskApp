using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Views;

/// <summary>
/// Criar (<c>taskform</c>) ou editar (<c>taskform?id=…</c>). O voltar do
/// sistema e o da barra passam pela pergunta de descartar alterações.
/// </summary>
public partial class TaskFormPage : IQueryAttributable
{
    private readonly TaskFormViewModel _viewModel;

    private Guid? _taskId;

    private bool _initialized;

    public TaskFormPage(TaskFormViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        _taskId = query.TryGetValue("id", out var value) && Guid.TryParse(value?.ToString(), out var id) ? id : null;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_initialized)
        {
            return;
        }

        _initialized = true;

        if (_taskId is { } id)
        {
            await _viewModel.InitializeEditAsync(id);
        }
        else
        {
            await _viewModel.InitializeNewAsync();
        }
    }

    /// <summary>O botão voltar do Android: pergunta antes de perder o que foi escrito.</summary>
    protected override bool OnBackButtonPressed()
    {
        _ = _viewModel.CloseCommand.ExecuteAsync(null);
        return true;
    }
}
