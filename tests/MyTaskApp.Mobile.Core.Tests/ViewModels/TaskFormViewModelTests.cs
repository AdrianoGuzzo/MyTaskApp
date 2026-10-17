using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Core.Tests.ViewModels;

/// <summary>
/// O formulário do celular sobre os casos de uso de verdade: o que a tela
/// avisa antes, o que só o domínio recusa, e que a edição manda só o que mudou.
/// </summary>
public sealed class TaskFormViewModelTests : IAsyncLifetime
{
    private MobileHarness _app = null!;

    public async ValueTask InitializeAsync() => _app = await MobileHarness.CreateAsync();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task ANewTask_NeedsATitle_WithinTheLimit()
    {
        var form = _app.Get<TaskFormViewModel>();
        await form.InitializeNewAsync();

        form.PageTitle.Should().Be("Nova tarefa");
        form.TitleError.Should().Be("Dê um título à tarefa.");
        form.VisibleTitleError.Should().BeNull("o formulário novo não abre reclamando");
        form.CanSave.Should().BeFalse();

        form.Title = new string('a', TaskItem.MaxTitleLength + 1);
        form.TitleError.Should().Be("O título passa de 200 caracteres.");
        form.VisibleTitleError.Should().Be(form.TitleError, "depois de mexer no título, o aviso aparece");
        form.CanSave.Should().BeFalse();

        form.Title = "  Pagar a conta de luz  ";
        form.TitleError.Should().BeNull();
        form.CanSave.Should().BeTrue();
        form.TitleCount.Should().Be("20/200");
    }

    [Fact]
    public async Task SavingWithoutATitle_SaysWhy_AndCreatesNothing()
    {
        var form = _app.Get<TaskFormViewModel>();
        await form.InitializeNewAsync();

        await form.SaveAsync();

        _app.Dialogs.Alerts.Should().ContainSingle().Which.Message.Should().Be("Dê um título à tarefa.");
        (await Search()).Should().BeEmpty();
    }

    [Fact]
    public async Task ANewTask_IsCreatedWithEverythingInOneGo()
    {
        var home = await _app.CreateTagAsync("Casa", "#22C55E");
        var form = _app.Get<TaskFormViewModel>();
        await form.InitializeNewAsync();

        form.Title = "Trocar a lâmpada";
        form.Notes = "Da cozinha";
        form.Priority = TaskFormViewModel.Priorities.Single(choice => choice.Priority == TaskPriority.High);
        form.TagChoices.Single().IsSelected = true;
        form.HasDeadline = true;
        form.DeadlineDate = new DateTime(2026, 10, 20);
        form.DeadlineTime = new TimeSpan(10, 30, 0);
        await form.SaveAsync();

        var created = (await Search()).Single();
        created.Task.Title.Should().Be("Trocar a lâmpada");
        created.Task.Notes.Should().Be("Da cozinha");
        created.Task.Priority.Should().Be(TaskPriority.High);
        created.Task.Tags!.Single().Id.Should().Be(home);
        created.Task.Deadline!.Deadline.Should().Be(new TaskDeadline(new DateOnly(2026, 10, 20), new TimeOnly(10, 30)));
        _app.Dialogs.Toasts.Should().Contain("Tarefa criada.");
        _app.Navigator.Last.Should().Be("back");
        form.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task ADeadlineInThePast_IsRefusedByTheDomain_WithItsOwnSentence()
    {
        var form = _app.Get<TaskFormViewModel>();
        await form.InitializeNewAsync();

        form.Title = "Entregar relatório";
        form.HasDeadline = true;
        form.DeadlineDate = new DateTime(2026, 10, 1);
        await form.SaveAsync();

        _app.Dialogs.Alerts.Should().ContainSingle().Which.Message.Should().Contain("prazo");
        (await Search()).Should().BeEmpty("a criação inteira volta junto");
        _app.Navigator.Visits.Should().BeEmpty();
    }

    [Fact]
    public async Task Linked_ANewTaskIsSharedByDefault_AndCanStayOnThisPhone()
    {
        var (phone, _) = _app.Link();
        var form = _app.Get<TaskFormViewModel>();
        await form.InitializeNewAsync();

        form.ShowsOrigin.Should().BeTrue();
        form.Origin!.Placement.Should().Be(DevicePlacement.Everywhere);

        form.Title = "Só minha";
        form.Origin = form.OriginOptions.Single(choice => choice.Label == "Só neste celular");
        await form.SaveAsync();

        (await Search()).Single().Task.Placement.Should().Be(DevicePlacement.On(phone));
    }

    [Fact]
    public async Task Editing_LoadsTheTask_AndSendsOnlyWhatChanged()
    {
        var task = await _app.CreateTaskAsync("Original", TaskPriority.Low, notes: "Nota", deadline: new TaskDeadline(new DateOnly(2026, 10, 16), new TimeOnly(9, 0)));

        // O prazo vence: reenviá-lo seria recusado, então trocar só o título precisa passar.
        _app.Clock.Advance(TimeSpan.FromDays(2));

        var form = _app.Get<TaskFormViewModel>();
        await form.InitializeEditAsync(task.TaskId);

        form.PageTitle.Should().Be("Editar tarefa");
        form.Title.Should().Be("Original");
        form.Priority.Priority.Should().Be(TaskPriority.Low);
        form.HasDeadline.Should().BeTrue();
        form.IsDirty.Should().BeFalse();
        form.CanSave.Should().BeFalse("nada mudou");

        form.Title = "Renomeada";
        form.IsDirty.Should().BeTrue();
        await form.SaveAsync();

        _app.Dialogs.Alerts.Should().BeEmpty();
        var saved = await _app.DetailsAsync(task.TaskId);
        saved.Task.Title.Should().Be("Renomeada");
        saved.Task.Notes.Should().Be("Nota");
        saved.Task.Deadline!.Deadline.Date.Should().Be(new DateOnly(2026, 10, 16));
    }

    [Theory]
    [InlineData(TaskItemStatus.Pending, TaskItemStatus.Completed)]
    [InlineData(TaskItemStatus.Pending, TaskItemStatus.Cancelled)]
    [InlineData(TaskItemStatus.Completed, TaskItemStatus.Cancelled)]
    [InlineData(TaskItemStatus.Cancelled, TaskItemStatus.Completed)]
    [InlineData(TaskItemStatus.Completed, TaskItemStatus.Pending)]
    public async Task TheStatus_ChangesThroughTheDomainTransitions(TaskItemStatus from, TaskItemStatus to)
    {
        var task = await _app.CreateTaskAsync("Mudar status");
        await SetStatusAsync(task, from);

        var form = _app.Get<TaskFormViewModel>();
        await form.InitializeEditAsync(task.TaskId);
        form.Status = TaskFormViewModel.Statuses.Single(choice => choice.Status == to);
        await form.SaveAsync();

        _app.Dialogs.Alerts.Should().BeEmpty();
        (await _app.DetailsAsync(task.TaskId)).Status.Should().Be(to);
    }

    [Fact]
    public async Task ReopeningAndMovingTheDeadline_IsAllowed_BecauseItReopensFirst()
    {
        var task = await _app.CreateTaskAsync("Reabrir e adiar");
        await SetStatusAsync(task, TaskItemStatus.Completed);

        var form = _app.Get<TaskFormViewModel>();
        await form.InitializeEditAsync(task.TaskId);
        form.Status = TaskFormViewModel.Statuses[0];
        form.HasDeadline = true;
        form.DeadlineDate = new DateTime(2026, 10, 30);
        await form.SaveAsync();

        _app.Dialogs.Alerts.Should().BeEmpty();
        var saved = await _app.DetailsAsync(task.TaskId);
        saved.Status.Should().Be(TaskItemStatus.Pending);
        saved.Task.Deadline.Should().NotBeNull();
    }

    [Fact]
    public async Task ChangingTheOrigin_MovesTheTask_AndKeepingItMovesNothing()
    {
        var (phone, notebook) = _app.Link();

        // Pelo caso de uso, sem lugar pedido, nasce neste dispositivo (o padrão do desktop).
        var task = await _app.CreateTaskAsync("Daqui");

        var form = _app.Get<TaskFormViewModel>();
        await form.InitializeEditAsync(task.TaskId);
        form.Origin!.Label.Should().Be("Só neste celular");
        form.Title = "Daqui renomeada";
        await form.SaveAsync();
        (await _app.DetailsAsync(task.TaskId)).Task.Placement.Should().Be(DevicePlacement.On(phone), "salvar sem mexer na origem não move");

        form = _app.Get<TaskFormViewModel>();
        await form.InitializeEditAsync(task.TaskId);
        form.Origin = form.OriginOptions.Single(choice => choice.Label == "Só em Notebook");
        await form.SaveAsync();

        (await _app.DetailsAsync(task.TaskId)).Task.Placement.Should().Be(DevicePlacement.On(notebook));
    }

    [Fact]
    public async Task ALegacyTaskWithoutPlace_ShowsAsShared_AndSavingDoesNotMoveIt()
    {
        var task = await _app.CreateTaskAsync("De antes da conta");
        _app.Link();

        var form = _app.Get<TaskFormViewModel>();
        await form.InitializeEditAsync(task.TaskId);
        form.Origin!.Placement.Should().Be(DevicePlacement.Everywhere);
        form.IsDirty.Should().BeFalse();

        form.Priority = TaskFormViewModel.Priorities.Single(choice => choice.Priority == TaskPriority.Urgent);
        await form.SaveAsync();

        (await _app.DetailsAsync(task.TaskId)).Task.Placement.Should().BeNull("o legado continua aparecendo em todos, sem ganhar lugar");
    }

    [Fact]
    public async Task LeavingWithChanges_AsksFirst()
    {
        var form = _app.Get<TaskFormViewModel>();
        await form.InitializeNewAsync();

        (await form.CanLeaveAsync()).Should().BeTrue("nada escrito, nada a perder");
        _app.Dialogs.Questions.Should().BeEmpty();

        form.Title = "Rascunho";
        _app.Dialogs.Confirms = false;
        await form.CloseAsync();

        _app.Dialogs.Questions.Should().ContainSingle("Descartar alterações?");
        _app.Navigator.Visits.Should().BeEmpty("escolheu continuar editando");

        _app.Dialogs.Confirms = true;
        await form.CloseAsync();
        _app.Navigator.Last.Should().Be("back");
    }

    [Fact]
    public async Task TogglingATag_MakesTheFormDirty()
    {
        await _app.CreateTagAsync("Estudos");
        var form = _app.Get<TaskFormViewModel>();
        await form.InitializeNewAsync();

        form.ToggleTag(form.TagChoices.Single());

        form.TagChoices.Single().IsSelected.Should().BeTrue();
        form.IsDirty.Should().BeTrue();
    }

    [Fact]
    public async Task EditingATaskThatIsGone_SaysSo()
    {
        var form = _app.Get<TaskFormViewModel>();

        await form.InitializeEditAsync(Guid.CreateVersion7());

        form.LoadError.Should().Be("Tarefa não encontrada.");
        form.IsLoaded.Should().BeFalse();
    }

    private async Task SetStatusAsync(CreateTaskResult task, TaskItemStatus status)
    {
        if (status == TaskItemStatus.Completed)
        {
            await _app.Runner.RunAsync<CompleteOccurrenceHandler>((handler, token) => handler.HandleAsync(new CompleteOccurrence(task.OccurrenceId), token));
        }
        else if (status == TaskItemStatus.Cancelled)
        {
            await _app.Runner.RunAsync<CancelOccurrenceHandler>((handler, token) => handler.HandleAsync(new CancelOccurrence(task.OccurrenceId), token));
        }
    }

    private async Task<IReadOnlyList<TaskListItem>> Search() =>
        (await _app.Runner.RunAsync<SearchTasksHandler, TaskSearchResult>(
            (handler, token) => handler.HandleAsync(new SearchTasks(Device: MyTaskApp.Application.Devices.DeviceView.All), token))).Items;
}
