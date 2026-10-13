using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tags;
using MyTaskApp.Domain;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// As etiquetas no cabeçalho da janela da tarefa, logo abaixo do título: as
/// pílulas com o nome e o mesmo seletor da linha (ADR-025).
/// </summary>
/// <remarks>
/// <para>
/// Grava a cada marca, como o seletor da linha e o card do prazo: o "Salvar"
/// da janela é do título e da anotação, e uma etiqueta escolhida não pode
/// depender dele — nem se perder num "Cancelar".
/// </para>
/// <para>
/// Ao abrir, relê as etiquetas da tarefa, e não só a lista de todas: a janela
/// fica aberta por horas, e a linha pode ter mudado o conjunto nesse meio-tempo.
/// Como <see cref="SetTaskTags"/> grava o conjunto inteiro, marcar sobre um
/// retrato velho desfaria em silêncio o que foi feito pela linha.
/// </para>
/// </remarks>
public sealed partial class TaskTagEditorViewModel(
    IUseCaseRunner runner,
    ILogger<TaskTagEditorViewModel> logger) : ObservableObject
{
    /// <summary>
    /// Duas marcas rápidas gravam em ordem: conjuntos fora de ordem fariam o
    /// primeiro vencer. Sem <c>Dispose</c>: sem o <c>AvailableWaitHandle</c> não há
    /// recurso nativo, e um transitório descartável ficaria preso ao contêiner.
    /// </summary>
    private readonly SemaphoreSlim _saves = new(1, 1);

    private Guid _occurrenceId;

    [ObservableProperty]
    private TaskTagsViewModel _tags = new(Guid.Empty, tags: null);

    /// <summary>Concluída, a tarefa é registro: as pílulas ficam, o seletor e o "×" não.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    private bool _isReadOnly;

    public bool IsEditable => !IsReadOnly;

    /// <summary>Somente leitura e sem etiqueta, não há o que mostrar.</summary>
    public bool IsVisible => IsEditable || Tags.HasTags;

    /// <summary>A lista desenha as bolinhas: cada gravação a recarrega.</summary>
    public event Action? Changed;

    /// <summary>"Gerenciar etiquetas…": quem abre a janela é o <c>App</c>.</summary>
    public event Action? ManageRequested;

    public void Load(TaskRowViewModel row)
    {
        _occurrenceId = row.OccurrenceId;

        Tags.PropertyChanged -= OnTagsPropertyChanged;
        Tags = new TaskTagsViewModel(row.TaskId, row.Source.Tags, "Etiquetas desta tarefa");
        Tags.PropertyChanged += OnTagsPropertyChanged;

        IsReadOnly = row.IsCompleted;
        OnPropertyChanged(nameof(IsVisible));
    }

    /// <summary>
    /// Prepara o seletor: todas as etiquetas e as que a tarefa tem agora. Se a
    /// releitura da tarefa falhar, vale o que a janela já mostrava.
    /// </summary>
    [RelayCommand]
    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        var tags = Tags;

        tags.IsLoading = true;
        tags.ErrorMessage = null;

        try
        {
            var all = await runner.RunAsync<GetTagsHandler, IReadOnlyList<TagRow>>(
                (handler, token) => handler.HandleAsync(new GetTags(), token),
                cancellationToken);

            tags.ShowOptions(all);

            if (await CurrentTagsAsync(cancellationToken) is { } current)
            {
                tags.ShowCurrent(current);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "TaskTagEditorLoadFailed {TaskId}", tags.TaskId);
            tags.ErrorMessage = "Não foi possível carregar as etiquetas.";
        }
        finally
        {
            tags.IsLoading = false;
        }
    }

    /// <summary>Marca ou desmarca uma etiqueta no seletor. O seletor continua aberto.</summary>
    [RelayCommand]
    public async Task ToggleTagAsync(TagOptionViewModel option, CancellationToken cancellationToken)
    {
        if (!await SaveAsync(option.Owner.Toggled(option.Tag.Id), cancellationToken))
        {
            option.Resync();
        }
    }

    /// <summary>O "×" de uma pílula: tira a etiqueta sem abrir o seletor.</summary>
    [RelayCommand]
    public Task RemoveTagAsync(TagChipViewModel chip, CancellationToken cancellationToken) =>
        SaveAsync(Tags.Toggled(chip.Id), cancellationToken);

    [RelayCommand]
    private void Manage() => ManageRequested?.Invoke();

    private async Task<IReadOnlyCollection<Guid>?> CurrentTagsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var task = await runner.RunAsync<GetTodayBoardHandler, BoardTask?>(
                (handler, token) => handler.HandleAsync(new GetBoardTask(_occurrenceId), token),
                cancellationToken);

            return task is null ? null : [.. (task.Task.Tags ?? []).Select(tag => tag.Id)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TaskTagEditorRefreshFailed {TaskId}", Tags.TaskId);
            return null;
        }
    }

    /// <summary>
    /// Mostra o conjunto novo antes de gravar e desfaz se a gravação falhar. O
    /// erro fica no seletor, que é para onde o usuário está olhando.
    /// </summary>
    private async Task<bool> SaveAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (IsReadOnly)
        {
            return false;
        }

        var tags = Tags;
        var before = tags.SelectedIds;

        tags.ErrorMessage = null;
        tags.Apply(ids);

        await _saves.WaitAsync(cancellationToken);

        try
        {
            await runner.RunAsync<SetTaskTagsHandler>(
                (handler, token) => handler.HandleAsync(new SetTaskTags(tags.TaskId, ids), token),
                cancellationToken);

            Changed?.Invoke();
            return true;
        }
        catch (DomainException exception)
        {
            tags.ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "TaskTagEditorSaveFailed {TaskId}", tags.TaskId);
            tags.ErrorMessage = "Não foi possível salvar as etiquetas.";
        }
        finally
        {
            _saves.Release();
        }

        tags.Apply(before);
        return false;
    }

    private void OnTagsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskTagsViewModel.HasTags))
        {
            OnPropertyChanged(nameof(IsVisible));
        }
    }
}
