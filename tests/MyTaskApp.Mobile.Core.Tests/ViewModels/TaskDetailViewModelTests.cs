using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Core.Tests.ViewModels;

/// <summary>O detalhe mostra o que o domínio sabe e só oferece o que a regra permite agora.</summary>
public sealed class TaskDetailViewModelTests : IAsyncLifetime
{
    private MobileHarness _app = null!;

    public async ValueTask InitializeAsync() => _app = await MobileHarness.CreateAsync();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task ItShowsTheWholeTask()
    {
        var tag = await _app.CreateTagAsync("Trabalho", "#EF4444");
        var task = await _app.CreateTaskAsync(
            "Revisar contrato",
            TaskPriority.High,
            notes: "Cláusulas 3 e 4",
            tags: [tag],
            deadline: new TaskDeadline(new DateOnly(2026, 10, 20), new TimeOnly(17, 0)));
        var detail = _app.Get<TaskDetailViewModel>();

        await detail.LoadAsync(task.TaskId);

        detail.Title.Should().Be("Revisar contrato");
        detail.Notes.Should().Be("Cláusulas 3 e 4");
        detail.HasNotes.Should().BeTrue();
        detail.StatusLabel.Should().Be("Pendente");
        detail.PriorityLabel.Should().Be("Alta");
        detail.Origin.Should().Be("Compartilhada");
        detail.Tags.Should().ContainSingle().Which.Name.Should().Be("Trabalho");
        detail.Deadline.Should().Contain("20/10 17:00");
        detail.Dates.Should().ContainSingle().Which.Should().StartWith("Criada em ");
        detail.History.Should().ContainSingle().Which.Should().Match<HistoryLine>(line => line.What == "Criada" && line.Who == "Pixel de teste");
        detail.Developments.Should().BeEmpty();
        detail.Provenance.Should().BeNull("a tarefa nunca passou pela sincronização");
    }

    [Fact]
    public async Task APendingTask_CanBeCompletedOrCancelled_NotReopened()
    {
        var detail = await OpenAsync("Pendente");

        detail.CanComplete.Should().BeTrue();
        detail.CanCancel.Should().BeTrue();
        detail.CanReopen.Should().BeFalse();
        detail.CanArchive.Should().BeTrue();
        detail.CanEdit.Should().BeTrue();
        detail.CanRestoreFromArchive.Should().BeFalse();
        detail.CanRestoreFromTrash.Should().BeFalse();
    }

    [Fact]
    public async Task Completing_ThenReopening_GoesThroughTheUseCases()
    {
        var detail = await OpenAsync("Concluir");

        await detail.CompleteAsync();

        detail.StatusLabel.Should().Be("Concluída");
        detail.CanReopen.Should().BeTrue();
        detail.CanComplete.Should().BeFalse();
        detail.Dates.Should().Contain(date => date.StartsWith("Concluída em ", StringComparison.Ordinal));
        detail.History.Select(line => line.What).Should().Contain("Concluída");

        await detail.ReopenAsync();

        detail.StatusLabel.Should().Be("Pendente");
        _app.Dialogs.Toasts.Should().Equal("Tarefa concluída.", "Tarefa reaberta.");
    }

    [Fact]
    public async Task Cancelling_AsksFirst()
    {
        var detail = await OpenAsync("Cancelar");
        _app.Dialogs.Confirms = false;

        await detail.CancelTaskAsync();
        detail.StatusLabel.Should().Be("Pendente");

        _app.Dialogs.Confirms = true;
        await detail.CancelTaskAsync();
        detail.StatusLabel.Should().Be("Cancelada");
        detail.CanReopen.Should().BeTrue();
    }

    [Fact]
    public async Task Archiving_LeavesOnlyRestore_AndRestoringBringsItBack()
    {
        var detail = await OpenAsync("Arquivar");

        await detail.ArchiveAsync();

        detail.Item!.Lifecycle.Should().Be(TaskLifecycle.Archived);
        detail.StatusLabel.Should().Be("Arquivada");
        detail.CanComplete.Should().BeFalse();
        detail.CanEdit.Should().BeFalse();
        detail.CanRestoreFromArchive.Should().BeTrue();

        await detail.RestoreFromArchiveAsync();

        detail.Item!.Lifecycle.Should().Be(TaskLifecycle.Active);
    }

    [Fact]
    public async Task TheTrash_AsksFirst_AndCanBeUndone()
    {
        var detail = await OpenAsync("Lixeira");

        await detail.MoveToTrashAsync();

        _app.Dialogs.Questions.Should().Contain("Mover para a lixeira");
        detail.Item!.Lifecycle.Should().Be(TaskLifecycle.Trashed);
        detail.CanRestoreFromTrash.Should().BeTrue();
        detail.CanMoveToTrash.Should().BeFalse();
        detail.Dates.Should().Contain(date => date.StartsWith("Na lixeira desde ", StringComparison.Ordinal));

        await detail.RestoreFromTrashAsync();

        detail.Item!.Lifecycle.Should().Be(TaskLifecycle.Active);
    }

    [Fact]
    public async Task ARefusedChange_ShowsTheDomainSentence()
    {
        var detail = await OpenAsync("Já concluída");
        await detail.CompleteAsync();

        // A tela esconderia o botão; a regra continua decidindo se ele for apertado mesmo assim.
        await detail.CompleteAsync();

        _app.Dialogs.Alerts.Should().ContainSingle().Which.Title.Should().Be(MobileViewModel.FailureTitle);
    }

    [Fact]
    public async Task EditOpensTheForm()
    {
        var detail = await OpenAsync("Editar");

        await detail.EditAsync();

        _app.Navigator.Last.Should().Be($"task/{detail.TaskId}/edit");
    }

    [Fact]
    public async Task AMissingTask_SaysSo()
    {
        var detail = _app.Get<TaskDetailViewModel>();

        await detail.LoadAsync(Guid.CreateVersion7());

        detail.LoadError.Should().Be("Tarefa não encontrada.");
        detail.Item.Should().BeNull();
        detail.CanComplete.Should().BeFalse();
    }

    private async Task<TaskDetailViewModel> OpenAsync(string title)
    {
        var task = await _app.CreateTaskAsync(title);
        var detail = _app.Get<TaskDetailViewModel>();
        await detail.LoadAsync(task.TaskId);
        return detail;
    }
}
