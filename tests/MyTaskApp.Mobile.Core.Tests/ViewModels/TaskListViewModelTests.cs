using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Core.Presentation;
using MyTaskApp.Mobile.Core.ViewModels;
using NSubstitute;

namespace MyTaskApp.Mobile.Core.Tests.ViewModels;

/// <summary>
/// A lista do celular sobre a pilha de verdade: o que ela pede ao banco, os
/// estados de tela e o gesto de concluir pelos casos de uso do desktop.
/// </summary>
public sealed class TaskListViewModelTests : IAsyncLifetime
{
    private MobileHarness _app = null!;

    private TaskListViewModel List => _app.Get<TaskListViewModel>();

    public async ValueTask InitializeAsync() => _app = await MobileHarness.CreateAsync();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task WithNothingYet_TheListSaysItIsEmpty_NotFiltered()
    {
        await List.LoadAsync();

        List.State.Should().Be(ListState.Empty);
        List.IsEmpty.Should().BeTrue();
        List.Tasks.Should().BeEmpty();
    }

    [Fact]
    public async Task AllTasks_ShowUp_NewestFirst()
    {
        await _app.CreateTaskAsync("Primeira");
        _app.Clock.Advance(TimeSpan.FromMinutes(1));
        await _app.CreateTaskAsync("Segunda", TaskPriority.High, notes: "Comprar pão\nE leite");

        await List.LoadAsync();

        List.State.Should().Be(ListState.Ready);
        List.Tasks.Select(row => row.Title).Should().Equal("Segunda", "Primeira");
        List.Tasks[0].Summary.Should().Be("Comprar pão");
        List.Tasks[0].PriorityLabel.Should().Be("Alta");
        List.Total.Should().Be(2);
        List.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task ManyTasks_ComeInPages()
    {
        for (var i = 0; i < TaskListViewModel.PageSize + 5; i++)
        {
            await _app.CreateTaskAsync($"Tarefa {i:D2}");
        }

        await List.LoadAsync();
        List.Tasks.Should().HaveCount(TaskListViewModel.PageSize);
        List.HasMore.Should().BeTrue();

        await List.LoadMoreAsync();

        List.Tasks.Should().HaveCount(TaskListViewModel.PageSize + 5);
        List.Tasks.Select(row => row.TaskId).Should().OnlyHaveUniqueItems();
        List.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task ThePresets_SplitPendingCompletedAndCancelled()
    {
        var done = await _app.CreateTaskAsync("Feita");
        var cancelled = await _app.CreateTaskAsync("Desistida");
        await _app.CreateTaskAsync("Por fazer");
        await _app.Runner.RunAsync<CompleteOccurrenceHandler>((handler, token) => handler.HandleAsync(new CompleteOccurrence(done.OccurrenceId), token), TestContext.Current.CancellationToken);
        await _app.Runner.RunAsync<CancelOccurrenceHandler>((handler, token) => handler.HandleAsync(new CancelOccurrence(cancelled.OccurrenceId), token), TestContext.Current.CancellationToken);

        await List.SelectPresetAsync(TaskListPreset.Pending);
        List.Tasks.Select(row => row.Title).Should().Equal("Por fazer");
        List.Presets.Single(choice => choice.IsSelected).Value.Should().Be(TaskListPreset.Pending);

        await List.SelectPresetAsync(TaskListPreset.Completed);
        List.Tasks.Select(row => row.Title).Should().Equal("Feita");

        await List.SelectPresetAsync(TaskListPreset.Cancelled);
        List.Tasks.Select(row => row.Title).Should().Equal("Desistida");
        List.Tasks[0].CanToggle.Should().BeFalse("cancelada se reabre pelo detalhe");
    }

    [Fact]
    public async Task Overdue_IsTheBoardsCriterion()
    {
        var late = await _app.CreateTaskAsync("Vence amanhã", deadline: new TaskDeadline(new DateOnly(2026, 10, 16), new TimeOnly(9, 0)));
        await _app.CreateTaskAsync("Sem prazo");
        _app.Clock.Advance(TimeSpan.FromDays(2));

        await List.SelectPresetAsync(TaskListPreset.Overdue);

        List.Tasks.Should().ContainSingle().Which.TaskId.Should().Be(late.TaskId);
        List.Tasks[0].IsOverdue.Should().BeTrue();
    }

    [Fact]
    public async Task ACheckOnTheRow_CompletesThroughTheUseCase_AndTheListFollows()
    {
        var task = await _app.CreateTaskAsync("Ligar para o banco");
        await List.LoadAsync();

        await List.ToggleAsync(List.Tasks.Single());

        (await _app.DetailsAsync(task.TaskId)).Status.Should().Be(TaskItemStatus.Completed);
        _app.Dialogs.Toasts.Should().Contain("Tarefa concluída.");

        // O aviso de mudança recarrega a lista, sem a tela pedir.
        await WaitUntilAsync(() => List.Tasks.Single().IsCompleted);

        await List.ToggleAsync(List.Tasks.Single());

        (await _app.DetailsAsync(task.TaskId)).Status.Should().Be(TaskItemStatus.Pending);
        _app.Dialogs.Toasts.Should().Contain("Tarefa reaberta.");
    }

    [Fact]
    public async Task TheSearch_WaitsForTheTypingToStop()
    {
        await _app.CreateTaskAsync("Relatório mensal");
        await _app.CreateTaskAsync("Dentista");
        await List.LoadAsync();

        List.SearchText = "rel";
        List.SearchText = "relat";
        List.Tasks.Should().HaveCount(2, "ainda está digitando");

        _app.Clock.Advance(TaskListViewModel.SearchDelay);
        await WaitUntilAsync(() => List.Tasks.Count == 1);

        List.Tasks.Single().Title.Should().Be("Relatório mensal");
        List.Filter.Text.Should().Be("relat");
    }

    [Fact]
    public async Task NothingMatching_IsNoResults_AndClearingBringsEverythingBack()
    {
        await _app.CreateTaskAsync("Dentista");
        await List.SelectPresetAsync(TaskListPreset.Completed);

        List.State.Should().Be(ListState.NoResults);
        List.IsNoResults.Should().BeTrue();

        await List.ClearFiltersAsync();

        List.State.Should().Be(ListState.Ready);
        List.Filter.Should().Be(TaskFilter.Default);
        List.Presets.Single(choice => choice.IsSelected).Value.Should().Be(TaskListPreset.All);
    }

    [Fact]
    public async Task TheFilterSheet_NarrowsByPriorityAndTag()
    {
        var work = await _app.CreateTagAsync("Trabalho", "#EF4444");
        await _app.CreateTaskAsync("Urgente do trabalho", TaskPriority.Urgent, tags: [work]);
        await _app.CreateTaskAsync("Urgente pessoal", TaskPriority.Urgent);
        await _app.CreateTaskAsync("Normal do trabalho", tags: [work]);

        await List.OpenFiltersAsync();
        _app.Navigator.Last.Should().Be("filters");
        List.TagChoices.Should().ContainSingle().Which.ColorHex.Should().Be("#EF4444");

        List.PriorityChoices.Single(choice => choice.Value == TaskPriority.Urgent).IsSelected = true;
        List.TagChoices.Single().IsSelected = true;
        await List.ApplyFiltersAsync();

        List.Tasks.Select(row => row.Title).Should().Equal("Urgente do trabalho");
        List.FiltersLabel.Should().Be("Filtros (2)");
        _app.Navigator.Last.Should().Be("back");
        List.Tasks.Single().Tags.Single().Name.Should().Be("Trabalho");
    }

    [Fact]
    public async Task TheOrigin_IsExact_AndOnlyOffersWhatTheAccountHas()
    {
        await List.OpenFiltersAsync();
        List.OriginChoices.Should().Equal(OriginCatalog.AnyOrigin);
        List.ShowsOrigin.Should().BeFalse();

        var (_, notebook) = _app.Link();
        await _app.CreateTaskAsync("Do notebook", placement: DevicePlacement.On(notebook));
        await _app.CreateTaskAsync("De todos", placement: DevicePlacement.Everywhere);

        await List.OpenFiltersAsync();
        List.ShowsOrigin.Should().BeTrue();
        List.SelectedOrigin = List.OriginChoices.Single(option => option.Label == "Notebook");
        await List.ApplyFiltersAsync();

        List.Tasks.Select(row => row.Title).Should().Equal("Do notebook");
        List.Tasks.Single().Origin.Should().Be("Notebook");

        await List.OpenFiltersAsync();
        List.SelectedOrigin.Label.Should().Be("Notebook", "a folha reabre com o que está aplicado");
        List.SelectedOrigin = List.OriginChoices.Single(option => option.Label == "Compartilhadas");
        await List.ApplyFiltersAsync();

        List.Tasks.Select(row => row.Title).Should().Equal("De todos");
    }

    [Fact]
    public async Task TheList_IsEveryDevicesTasks_NotOnlyThisPhones()
    {
        var (_, notebook) = _app.Link();
        await _app.CreateTaskAsync("Só no notebook", placement: DevicePlacement.On(notebook));

        await List.LoadAsync();

        List.Tasks.Should().ContainSingle().Which.Origin.Should().Be("Notebook");
    }

    [Fact]
    public async Task AFailingRead_ShowsTheError_AndRetryingRecovers()
    {
        var runner = Substitute.For<IUseCaseRunner>();
        runner.RunAsync(Arg.Any<Func<SearchTasksHandler, CancellationToken, Task<TaskSearchResult>>>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromException<TaskSearchResult>(new IOException("disco")),
                _ => Task.FromResult(new TaskSearchResult([], 0, 0, 50, false)));
        var list = Create(runner);

        await list.LoadAsync();

        list.State.Should().Be(ListState.Error);
        list.ErrorMessage.Should().Be("Não foi possível ler as tarefas deste celular.");

        await list.LoadAsync();

        list.State.Should().Be(ListState.Empty);
        list.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task ARefusedRead_ShowsTheDomainSentence()
    {
        var runner = Substitute.For<IUseCaseRunner>();
        runner.RunAsync(Arg.Any<Func<SearchTasksHandler, CancellationToken, Task<TaskSearchResult>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<TaskSearchResult>(new DomainException("O texto da busca passa de 200 caracteres.")));
        var list = Create(runner);

        await list.LoadAsync();

        list.ErrorMessage.Should().Be("O texto da busca passa de 200 caracteres.");
    }

    [Fact]
    public async Task WithoutAnAccount_PullingDoesNotWaitForAServer()
    {
        await _app.CreateTaskAsync("Local");
        await List.LoadSyncAsync();
        List.Sync!.Kind.Should().Be(SyncIndicatorKind.LocalOnly);
        List.Sync.Title.Should().Be("Só neste celular");

        await List.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        List.Tasks.Should().ContainSingle();
        List.IsRefreshing.Should().BeFalse();
    }

    [Fact]
    public async Task Navigation_GoesToTheTask_TheForm_AndTheAccount()
    {
        var task = await _app.CreateTaskAsync("Abrir");
        await List.LoadAsync();

        await List.OpenAsync(List.Tasks.Single());
        await List.NewTaskAsync();
        await List.OpenAccountAsync();

        _app.Navigator.Visits.Should().Equal($"task/{task.TaskId}", "task/new", "account");
    }

    private TaskListViewModel Create(IUseCaseRunner runner) => new(
        runner,
        _app.Get<OriginCatalog>(),
        _app.Get<ISyncAccount>(),
        _app.Get<SyncScheduler>(),
        _app.Get<IDataChangeNotifier>(),
        _app.Get<DevicePlacementState>(),
        _app.Get<IUiDispatcher>(),
        _app.Get<INavigator>(),
        _app.Get<IDialogService>(),
        _app.Clock,
        NullLogger<TaskListViewModel>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
