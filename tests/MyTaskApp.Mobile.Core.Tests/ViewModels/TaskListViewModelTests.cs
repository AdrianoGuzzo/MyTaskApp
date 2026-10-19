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
    public async Task TheTasksTab_IsOnlyWhatIsPending()
    {
        var done = await _app.CreateTaskAsync("Feita");
        var cancelled = await _app.CreateTaskAsync("Desistida");
        await _app.CreateTaskAsync("Por fazer");
        await _app.Runner.RunAsync<CompleteOccurrenceHandler>((handler, token) => handler.HandleAsync(new CompleteOccurrence(done.OccurrenceId), token), TestContext.Current.CancellationToken);
        await _app.Runner.RunAsync<CancelOccurrenceHandler>((handler, token) => handler.HandleAsync(new CancelOccurrence(cancelled.OccurrenceId), token), TestContext.Current.CancellationToken);

        await List.LoadAsync();

        List.Tasks.Select(row => row.Title).Should().Equal("Por fazer");
        List.Tasks.Single().ShowsStatus.Should().BeFalse("\"Pendente\" em toda linha não diz nada");
        List.Presets.Select(choice => choice.Label).Should().Equal("Pendentes", "Vencidas");
        List.Presets.Single(choice => choice.IsSelected).Value.Should().Be(TaskListPreset.Pending);
        List.Filter.IsNarrowed.Should().BeFalse("pendentes é o ponto de partida da aba");
    }

    [Fact]
    public async Task WithEverythingDone_TheTasksTabIsEmpty_NotFiltered()
    {
        var done = await _app.CreateTaskAsync("Feita");
        await _app.Runner.RunAsync<CompleteOccurrenceHandler>((handler, token) => handler.HandleAsync(new CompleteOccurrence(done.OccurrenceId), token), TestContext.Current.CancellationToken);

        await List.LoadAsync();

        List.State.Should().Be(ListState.Empty);
        List.EmptyTitle.Should().Be("Nada pendente");
        List.OffersSignInWhenEmpty.Should().BeTrue();
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
    public async Task ACheckOnTheRow_CompletesThroughTheUseCase_AndTheTaskMovesToTheHistory()
    {
        var task = await _app.CreateTaskAsync("Ligar para o banco");
        var history = _app.Get<HistoryViewModel>();
        await List.LoadAsync();
        await history.LoadAsync();

        await List.ToggleAsync(List.Tasks.Single());

        (await _app.DetailsAsync(task.TaskId)).Status.Should().Be(TaskItemStatus.Completed);
        List.UndoMessage.Should().Be("Tarefa concluída.");
        List.HasUndo.Should().BeTrue();

        // O aviso de mudança recarrega as duas abas, sem a tela pedir.
        await WaitUntilAsync(() => List.Tasks.Count == 0 && history.Tasks.Count == 1);
        history.Tasks.Single().IsCompleted.Should().BeTrue();

        await history.ToggleAsync(history.Tasks.Single());

        (await _app.DetailsAsync(task.TaskId)).Status.Should().Be(TaskItemStatus.Pending);
        history.UndoMessage.Should().Be("Tarefa reaberta.", "cada aba tem a sua barra");
        _app.Dialogs.Toasts.Should().BeEmpty("a barra Desfazer faz as vezes do aviso, igual no iOS e no Android");
        await WaitUntilAsync(() => List.Tasks.Count == 1 && history.Tasks.Count == 0);
    }

    [Fact]
    public async Task Undo_ReopensWhatTheTouchCompleted()
    {
        var task = await _app.CreateTaskAsync("Pagar a conta de luz");
        await List.LoadAsync();

        await List.ToggleAsync(List.Tasks.Single());
        await List.UndoAsync();

        (await _app.DetailsAsync(task.TaskId)).Status.Should().Be(TaskItemStatus.Pending);
        List.HasUndo.Should().BeFalse();
        await WaitUntilAsync(() => List.Tasks.Count == 1 && !List.Tasks[0].IsCompleted);

        // Desfazer de novo, sem barra, não faz nada.
        await List.UndoAsync();
        (await _app.DetailsAsync(task.TaskId)).Status.Should().Be(TaskItemStatus.Pending);
    }

    [Fact]
    public async Task Undo_CompletesAgainWhatTheTouchReopened()
    {
        var task = await _app.CreateTaskAsync("Revisar contrato");
        var history = _app.Get<HistoryViewModel>();
        await List.LoadAsync();
        await List.ToggleAsync(List.Tasks.Single());
        await history.LoadAsync();

        // Reabrir é no Histórico, onde a concluída mora.
        await history.ToggleAsync(history.Tasks.Single());
        await history.UndoAsync();

        (await _app.DetailsAsync(task.TaskId)).Status.Should().Be(TaskItemStatus.Completed);
    }

    [Fact]
    public async Task TheUndoBar_GoesAwayByItself()
    {
        await _app.CreateTaskAsync("Some depois");
        await List.LoadAsync();

        await List.ToggleAsync(List.Tasks.Single());
        _app.Clock.Advance(TaskListViewModel.UndoWindow - TimeSpan.FromSeconds(1));
        List.HasUndo.Should().BeTrue();

        _app.Clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => !List.HasUndo);
    }

    [Fact]
    public async Task ANewTouch_RestartsTheUndoWindow()
    {
        await _app.CreateTaskAsync("Primeira");
        await _app.CreateTaskAsync("Segunda");
        await List.LoadAsync();

        await List.ToggleAsync(List.Tasks[0]);
        await WaitUntilAsync(() => List.Tasks.Count == 1);
        _app.Clock.Advance(TimeSpan.FromSeconds(4));
        await List.ToggleAsync(List.Tasks.Single());
        _app.Clock.Advance(TimeSpan.FromSeconds(4));

        List.HasUndo.Should().BeTrue("o prazo da primeira não apaga a barra da segunda");
    }

    [Fact]
    public async Task TheSyncLine_SaysHowLongAgo_AndKeepsCounting()
    {
        var badge = new SyncBadge(SyncIndicatorKind.Synced, "Sincronizado", "Tudo em dia.", MobileHarness.Now);
        var list = List;
        list.Sync = badge;

        list.SyncLine.Should().Be("Sincronizado · agora há pouco");

        var changed = new List<string?>();
        list.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _app.Clock.Advance(TaskListViewModel.TickEvery * 4);

        changed.Should().Contain(nameof(TaskListViewModel.SyncLine), "o relógio refaz o texto sem reler o banco");
        list.SyncLine.Should().Be("Sincronizado · há 2 min");

        list.Sync = badge with { Kind = SyncIndicatorKind.Pending, Title = "Pendente (2)" };
        list.SyncLine.Should().Be("Pendente (2)", "a idade só aparece quando está em dia");
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
        await List.SelectPresetAsync(TaskListPreset.Overdue);

        List.State.Should().Be(ListState.NoResults);
        List.IsNoResults.Should().BeTrue();

        await List.ClearFiltersAsync();

        List.State.Should().Be(ListState.Ready);
        List.Filter.Should().Be(TaskFilter.For(TaskListPreset.Pending));
        List.Presets.Single(choice => choice.IsSelected).Value.Should().Be(TaskListPreset.Pending);
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
    public async Task TheDevice_IsExact_AndOnlyOffersWhatTheAccountHas()
    {
        List.Devices.Select(chip => chip.Label).Should().Equal("Todas");
        List.ShowsDevices.Should().BeFalse();

        var (_, notebook) = _app.Link();
        await _app.CreateTaskAsync("Do notebook", placement: DevicePlacement.On(notebook));
        await _app.CreateTaskAsync("De todos", placement: DevicePlacement.Everywhere);

        List.ShowsDevices.Should().BeTrue("ligar a conta refaz os chips");
        List.Devices.Select(chip => chip.Label).Should().Equal("Todas", "Compartilhadas", "Este celular", "Notebook");
        List.Devices.Single(chip => chip.IsSelected).Label.Should().Be("Todas");

        await List.SelectDeviceAsync(notebook.ToString("D"));

        List.Tasks.Select(row => row.Title).Should().Equal("Do notebook");
        List.Tasks.Single().Origin.Should().Be("Notebook");
        List.Devices.Single(chip => chip.IsSelected).Label.Should().Be("Notebook");
        List.FiltersLabel.Should().Be("Filtros", "o dispositivo fica nos chips, não na folha");

        await List.SelectDeviceAsync(OriginCatalog.SharedKey);

        List.Tasks.Select(row => row.Title).Should().Equal("De todos");
    }

    [Fact]
    public async Task TheChosenDevice_ComesBackWhenTheAppOpensAgain()
    {
        var (_, notebook) = _app.Link();
        await _app.CreateTaskAsync("Do notebook", placement: DevicePlacement.On(notebook));
        await _app.CreateTaskAsync("De todos", placement: DevicePlacement.Everywhere);

        await List.SelectDeviceAsync(notebook.ToString("D"));

        _app.Preferences.Get(DeviceFocus.PreferenceKey).Should().Be(notebook.ToString("D"));

        // Abrir de novo: um foco e uma lista novos, sobre as mesmas preferências.
        var reopened = Create(_app.Runner, new DeviceFocus(_app.Preferences, _app.Get<OriginCatalog>()));
        await reopened.LoadAsync();

        reopened.Devices.Single(chip => chip.IsSelected).Label.Should().Be("Notebook");
        reopened.Tasks.Select(row => row.Title).Should().Equal("Do notebook");

        await reopened.SelectDeviceAsync(OriginCatalog.AnyKey);

        _app.Preferences.Get(DeviceFocus.PreferenceKey).Should().BeNull("\"Todas\" é o padrão, não precisa ficar gravado");
    }

    [Fact]
    public async Task TheChosenDevice_FiltersEvenBeforeTheDirectoryArrives()
    {
        var (_, notebook) = _app.Link();
        await _app.CreateTaskAsync("Do notebook", placement: DevicePlacement.On(notebook));
        await _app.CreateTaskAsync("De todos", placement: DevicePlacement.Everywhere);
        _app.Preferences.Set(DeviceFocus.PreferenceKey, notebook.ToString("D"));

        // Abriu o app: a conta está ligada, mas o primeiro ciclo ainda não trouxe os dispositivos.
        _app.Get<SyncDeviceDirectory>().Update([]);

        var opened = Create(_app.Runner, new DeviceFocus(_app.Preferences, _app.Get<OriginCatalog>()));
        await opened.LoadAsync();

        opened.Tasks.Select(row => row.Title).Should().Equal("Do notebook");
    }

    [Fact]
    public async Task ADeviceTheAccountNoLongerHas_OrNoAccount_ShowsEverything()
    {
        await _app.CreateTaskAsync("Uma");
        _app.Preferences.Set(DeviceFocus.PreferenceKey, Guid.CreateVersion7().ToString("D"));

        var unlinked = Create(_app.Runner, new DeviceFocus(_app.Preferences, _app.Get<OriginCatalog>()));
        await unlinked.LoadAsync();
        unlinked.Tasks.Should().ContainSingle("sem conta, a escolha gravada não filtra");

        _app.Link();
        var linked = Create(_app.Runner, new DeviceFocus(_app.Preferences, _app.Get<OriginCatalog>()));
        await linked.LoadAsync();

        linked.Tasks.Should().ContainSingle("o dispositivo gravado não está mais na conta");
        linked.Devices.Single(chip => chip.IsSelected).Label.Should().Be("Todas");
    }

    [Fact]
    public async Task TheDevice_IsTheSameInBothTabs_AndClearingGoesBackToAll()
    {
        var (phone, notebook) = _app.Link();
        await _app.CreateTaskAsync("Do notebook", placement: DevicePlacement.On(notebook));
        await _app.CreateTaskAsync("Do celular", placement: DevicePlacement.On(phone));
        var done = await _app.CreateTaskAsync("Feita no celular", placement: DevicePlacement.On(phone));
        await _app.Runner.RunAsync<CompleteOccurrenceHandler>((handler, token) => handler.HandleAsync(new CompleteOccurrence(done.OccurrenceId), token), TestContext.Current.CancellationToken);
        var history = _app.Get<HistoryViewModel>();
        await history.LoadAsync();
        history.Tasks.Should().ContainSingle();

        await List.SelectDeviceAsync(notebook.ToString("D"));

        await WaitUntilAsync(() => history.Tasks.Count == 0);
        history.Devices.Single(chip => chip.IsSelected).Label.Should().Be("Notebook");

        await history.ClearFiltersAsync();

        history.Tasks.Should().ContainSingle();
        await WaitUntilAsync(() => List.Tasks.Count == 2);
        List.Devices.Single(chip => chip.IsSelected).Label.Should().Be("Todas");
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

    private TaskListViewModel Create(IUseCaseRunner runner, DeviceFocus? focus = null) => new(
        runner,
        _app.Get<OriginCatalog>(),
        focus ?? _app.Get<DeviceFocus>(),
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
