using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// A aba Desenvolvimento com vários repositórios (ADR-031): uma aba pequena
/// por ambiente da tarefa, e o painel de sempre mostrando a escolhida.
/// </summary>
/// <remarks>
/// Cada ambiente é um <see cref="TaskDevelopmentViewModel"/> inteiro — o
/// formulário, a execução, o worktree pronto e o agente. Este só busca a lista,
/// mantém as abas em dia com ela e diz qual está na frente. Um repositório
/// ainda no formulário é um rascunho: vira ambiente quando o worktree é criado
/// ou quando a criação falha e fica gravada.
/// </remarks>
public sealed partial class TaskDevelopmentsViewModel(
    IUseCaseRunner runner,
    Func<TaskDevelopmentViewModel> createEnvironment,
    ILogger<TaskDevelopmentsViewModel> logger) : ObservableObject
{
    private Guid _taskId;

    private string _taskTitle = string.Empty;

    private bool _isReadOnly;

    private IReadOnlyList<TaskDevelopmentView> _views = [];

    public ObservableCollection<TaskDevelopmentViewModel> Items { get; } = [];

    [ObservableProperty]
    private TaskDevelopmentViewModel? _selected;

    /// <summary>Os aliases da tarefa, os mesmos para todos os ambientes.</summary>
    public AliasCompletionViewModel DirectoryCompletion { get; } = new();

    /// <summary>O <c>@</c> no texto do agente: os ambientes desta tarefa e os arquivos deles (ADR-039).</summary>
    public ReferenceCompletionViewModel PromptReferences { get; } = new(runner, logger);

    /// <summary>As abas só aparecem com algum ambiente gravado: o primeiro repositório é só o formulário.</summary>
    public bool ShowStrip => Items.Any(item => !item.IsDraft);

    public bool CanAddRepository => !_isReadOnly && Items.All(item => !item.IsDraft);

    /// <summary>O ambiente com algo em andamento que fechar a janela interromperia.</summary>
    public TaskDevelopmentViewModel? Busy => Items.FirstOrDefault(item => item.IsBusy);

    /// <summary>Pede a janela de comandos globais.</summary>
    public event Action? CommandsRequested;

    /// <summary>A tarefa. Chamado uma vez, na abertura da janela.</summary>
    public void Load(Guid taskId, string taskTitle, bool isReadOnly)
    {
        _taskId = taskId;
        _taskTitle = taskTitle;
        _isReadOnly = isReadOnly;
        _views = [];
        DirectoryCompletion.IsEnabled = !isReadOnly;
        PromptReferences.Load(taskId, isReadOnly);

        foreach (var item in Items.ToList())
        {
            Detach(item);
        }

        Items.Clear();
        Selected = null;
        NotifyItemsChanged();
    }

    /// <summary>
    /// A cada vez que a aba aparece: busca os ambientes, acerta as abas e
    /// ativa a que está na frente.
    /// </summary>
    public async Task ActivateAsync(CancellationToken cancellationToken)
    {
        try
        {
            _views = await runner.RunAsync<GetTaskDevelopmentsHandler, IReadOnlyList<TaskDevelopmentView>>(
                (handler, token) => handler.HandleAsync(new GetTaskDevelopments(_taskId), token),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "TaskDevelopmentsLoadFailed {TaskId}", _taskId);
            Selected ??= Items.FirstOrDefault() ?? AddEnvironment();
            Selected.ShowLoadFailure();
            NotifyItemsChanged();
            return;
        }

        Reconcile();
        await ActivateSelectedAsync(cancellationToken);
        await RefreshAgentsAsync(except: Selected);
    }

    /// <summary>"+ Adicionar repositório": uma aba nova, já com a branch dos outros ambientes.</summary>
    [RelayCommand]
    public void AddRepository()
    {
        if (_isReadOnly)
        {
            return;
        }

        var draft = Items.FirstOrDefault(item => item.IsDraft);

        if (draft is null)
        {
            draft = AddEnvironment();
            draft.SuggestFrom(_views);
            NotifyItemsChanged();
        }

        Selected = draft;
    }

    /// <summary>O agente pode ter aberto ou fechado: todas as abas conferem.</summary>
    public Task RefreshAgentsAsync() => RefreshAgentsAsync(except: null);

    /// <remarks>
    /// A aba da frente já conferiu o seu ao ativar; as outras conferem aqui,
    /// para a bolinha de agente aberto aparecer nelas também.
    /// </remarks>
    private async Task RefreshAgentsAsync(TaskDevelopmentViewModel? except)
    {
        foreach (var item in Items.Where(item => !item.IsDraft && item != except).ToList())
        {
            await item.Agent.RefreshAsync(CancellationToken.None);
        }
    }

    partial void OnSelectedChanged(TaskDevelopmentViewModel? oldValue, TaskDevelopmentViewModel? newValue)
    {
        if (newValue is null && oldValue is not null && Items.Contains(oldValue))
        {
            // A lista (ListBox) desmarca quando a aba some e volta: a frente não fica vazia.
            Selected = oldValue;
            return;
        }

        PromptReferences.SetCurrent(newValue?.DevelopmentId);

        if (newValue is not null && oldValue is not null && newValue != oldValue)
        {
            _ = ActivateSelectedAsync(CancellationToken.None);
        }
    }

    private Task ActivateSelectedAsync(CancellationToken cancellationToken) =>
        Selected is { } selected
            ? selected.ActivateAsync(ViewOf(selected), cancellationToken)
            : Task.CompletedTask;

    private TaskDevelopmentView? ViewOf(TaskDevelopmentViewModel item) =>
        item.DevelopmentId is { } id ? _views.FirstOrDefault(view => view.Id == id) : null;

    /// <summary>
    /// Acerta as abas com a lista gravada: atualiza as que existem, cria as
    /// que faltam, tira as que saíram. Um rascunho que falhou ao criar vira o
    /// ambiente gravado com o erro, em vez de ganhar uma aba gêmea.
    /// </summary>
    private void Reconcile()
    {
        foreach (var view in _views)
        {
            var item = Items.FirstOrDefault(existing => existing.DevelopmentId == view.Id)
                ?? Items.FirstOrDefault(existing => existing.IsDraft
                    && WorktreePathPlanner.SamePath(existing.RepositoryPath, view.RepositoryPath)
                    && existing.State is DevelopmentPanelState.Failed);

            // Só o dado: a tela de cada aba (a falha que o usuário está lendo,
            // inclusive) continua como está.
            (item ?? AddEnvironment()).UpdateRecord(view);
        }

        // Dois caminhos para o mesmo ambiente (um rascunho que reaproveitou um
        // removido do mesmo repositório): fica a aba que está na frente.
        foreach (var group in Items.Where(item => !item.IsDraft).GroupBy(item => item.DevelopmentId).ToList())
        {
            var keep = group.FirstOrDefault(item => item == Selected) ?? group.First();

            foreach (var twin in group.Where(item => item != keep).ToList())
            {
                Remove(twin);
            }
        }

        foreach (var gone in Items.Where(item => !item.IsDraft && ViewOf(item) is null).ToList())
        {
            Remove(gone);
        }

        if (Items.Count == 0)
        {
            AddEnvironment();
        }

        if (Selected is null || !Items.Contains(Selected))
        {
            Selected = Items.FirstOrDefault(item => item.Development?.Status == TaskDevelopmentStatus.Ready)
                ?? Items[0];
        }

        PromptReferences.SetEnvironments(_views, Selected?.DevelopmentId);
        NotifyItemsChanged();
    }

    private TaskDevelopmentViewModel AddEnvironment()
    {
        var item = createEnvironment();
        item.Load(_taskId, _taskTitle, _isReadOnly, DirectoryCompletion, PromptReferences);
        item.Changed += OnEnvironmentChanged;
        item.CommandsRequested += OnCommandsRequested;
        item.PropertyChanged += OnEnvironmentPropertyChanged;
        Items.Add(item);
        return item;
    }

    private void Remove(TaskDevelopmentViewModel item)
    {
        Detach(item);
        Items.Remove(item);
    }

    private void Detach(TaskDevelopmentViewModel item)
    {
        item.Changed -= OnEnvironmentChanged;
        item.CommandsRequested -= OnCommandsRequested;
        item.PropertyChanged -= OnEnvironmentPropertyChanged;
    }

    private void OnCommandsRequested() => CommandsRequested?.Invoke();

    private void OnEnvironmentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(TaskDevelopmentViewModel.IsDraft))
        {
            NotifyItemsChanged();
        }
        else if (args.PropertyName is nameof(TaskDevelopmentViewModel.IsBusy))
        {
            OnPropertyChanged(nameof(Busy));
        }
    }

    /// <summary>Uma aba criou, falhou, removeu ou saiu: a lista é buscada de novo.</summary>
    private async void OnEnvironmentChanged(TaskDevelopmentViewModel item)
    {
        try
        {
            _views = await runner.RunAsync<GetTaskDevelopmentsHandler, IReadOnlyList<TaskDevelopmentView>>(
                (handler, token) => handler.HandleAsync(new GetTaskDevelopments(_taskId), token),
                CancellationToken.None);

            var wasSelected = Selected;
            Reconcile();

            // A aba da frente saiu da lista: a nova da frente precisa carregar.
            if (Selected != wasSelected)
            {
                await ActivateSelectedAsync(CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "TaskDevelopmentsReloadFailed {TaskId}", _taskId);
        }
    }

    private void NotifyItemsChanged()
    {
        OnPropertyChanged(nameof(ShowStrip));
        OnPropertyChanged(nameof(CanAddRepository));
        OnPropertyChanged(nameof(Busy));
        AddRepositoryCommand.NotifyCanExecuteChanged();
    }
}
