using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.History;
using MyTaskApp.Application.Planning;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// O painel "Histórico · 7 dias" (ADR-053), aberto pelo ↺ do cabeçalho. Só lê:
/// carrega a cada abertura, porque o que se quer ver é o agora, e não guarda
/// nada entre uma abertura e outra.
/// </summary>
public sealed partial class ActivityHistoryViewModel(IUseCaseRunner runner, ILogger logger) : ObservableObject
{
    /// <summary>Descarta a resposta de uma carga que outra mais nova já superou.</summary>
    private int _load;

    [ObservableProperty]
    private IReadOnlyList<ActivityDayView> _days = [];

    /// <summary>"18 concluídas · 24h 35min", ou "Nenhuma atividade registrada".</summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    public string Heading => $"Histórico · {GetActivityHistory.Week} dias";

    /// <summary>Pede o fechamento do painel. Quem fecha é a view, que é dona do flyout.</summary>
    public event Action? CloseRequested;

    /// <summary>
    /// Pede a janela da tarefa, a mesma do resto do app. Leva a linha inteira,
    /// como o <c>NotesRequested</c> do quadro: a janela abre com tudo na mão.
    /// </summary>
    public event Action<TaskRowViewModel>? OpenRequested;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var load = ++_load;
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var history = await runner.RunAsync<GetActivityHistoryHandler, ActivityHistoryView>(
                (handler, token) => handler.HandleAsync(new GetActivityHistory(), token),
                cancellationToken);

            if (load != _load)
            {
                return;
            }

            Days = history.Days;
            Summary = history.Summary;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "ActivityHistoryLoadFailed");

            if (load == _load)
            {
                ErrorMessage = "Não foi possível carregar o histórico.";
            }
        }
        finally
        {
            if (load == _load)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>
    /// Abre a tarefa daquela linha — a ocorrência daquele dia, e não a série.
    /// Nada é criado: a tarefa é lida como está.
    /// </summary>
    [RelayCommand]
    public async Task OpenAsync(ActivityItemView item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ErrorMessage = null;

        try
        {
            var found = await runner.RunAsync<GetTodayBoardHandler, BoardTask?>(
                (handler, token) => handler.HandleAsync(new GetBoardTask(item.OccurrenceId), token),
                cancellationToken);

            if (found is null)
            {
                // Foi para a lixeira depois de o painel abrir.
                ErrorMessage = "Esta tarefa não está mais disponível.";
                return;
            }

            CloseRequested?.Invoke();
            OpenRequested?.Invoke(new TaskRowViewModel(found.Task, found.IsCompleted));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "ActivityHistoryOpenFailed {OccurrenceId}", item.OccurrenceId);
            ErrorMessage = "Não foi possível abrir a tarefa.";
        }
    }

    [RelayCommand]
    public void Close() => CloseRequested?.Invoke();
}
