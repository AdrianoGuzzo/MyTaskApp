using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A tela "Operação" (E3): o que a outra máquina oferece, o pedido, a saída
/// chegando, o cancelamento e as aprovações — sem servidor e sem rede.
/// </summary>
public class RemoteControlViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly MachineView Notebook = new(
        Guid.Parse("0199a000-0000-7000-8000-000000000002"), "Notebook", "linux", "1.25.0", true, false, Now, true, null, []);

    private static readonly CatalogCommand Tests = new(
        Guid.CreateVersion7(), "Testes", CommandScope.Global, null, false, [new CatalogParameter("filtro", true, null)]);

    private static readonly RemoteEnvironmentView Environment = new(Guid.CreateVersion7(), Guid.CreateVersion7(), "Validar antimicrobianos", "feature/ECO-12", [Tests]);

    private static readonly Guid OperationId = Guid.CreateVersion7();

    private readonly FakeUseCaseRunner _runner = new();

    public RemoteControlViewModelTests()
    {
        _runner.ResultsByHandler[typeof(GetRemoteControlHandler)] = new RemoteControlView(new AgentCatalog("h", [], [Tests], [], []), [Environment]);
        _runner.ResultsByHandler[typeof(GetPendingApprovalsHandler)] = (IReadOnlyList<ApprovalView>)[];
        _runner.ResultsByHandler[typeof(SendRemoteOperationHandler)] = Operation(RemoteOperationStatus.Queued);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RemoteControlViewModel ViewModel() => new(_runner, NullLogger<RemoteControlViewModel>.Instance);

    [AvaloniaFact]
    public async Task Opening_ShowsTheEnvironmentsThere_WithTheFirstChosen()
    {
        var viewModel = ViewModel();

        await viewModel.OpenAsync(Notebook, Ct);

        viewModel.Title.Should().Be("Controlar Notebook");
        var environment = viewModel.Environments.Should().ContainSingle().Subject;
        environment.Title.Should().Be("Validar antimicrobianos");
        environment.Details.Should().Be("feature/ECO-12 · 1 comando liberado");
        viewModel.SelectedEnvironment.Should().BeSameAs(environment);
        viewModel.HasNoEnvironments.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task AMachineThatNeverPublished_SaysWhatItNeeds()
    {
        _runner.FailuresByHandler[typeof(GetRemoteControlHandler)] = new SyncApiException(SyncFailureKind.NotFound, "404");
        var viewModel = ViewModel();

        await viewModel.OpenAsync(Notebook, Ct);

        viewModel.ErrorMessage.Should().Contain("ainda não publicou");
        viewModel.HasNoEnvironments.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task RemovingTheWorktreeThere_IsSent_AndWaitsForTheApprovalThere()
    {
        var viewModel = ViewModel();
        await viewModel.OpenAsync(Notebook, Ct);

        await viewModel.RemoveCommand.ExecuteAsync(viewModel.SelectedEnvironment!);

        _runner.Invoked.Should().Contain(typeof(SendRemoteOperationHandler));
        viewModel.OperationTitle.Should().Be("Apagar o worktree de Validar antimicrobianos em Notebook");
        viewModel.CanCancel.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task ARequiredValueLeftEmpty_IsNotSent()
    {
        var viewModel = ViewModel();
        await viewModel.OpenAsync(Notebook, Ct);

        await viewModel.RunCommand.ExecuteAsync(viewModel.SelectedEnvironment!.Commands[0]);

        viewModel.ErrorMessage.Should().Be("Preencha filtro.");
        _runner.Invoked.Should().NotContain(typeof(SendRemoteOperationHandler));
    }

    [AvaloniaFact]
    public async Task Running_SendsTheRequest_AndTheOutputArrivesWithEachTick_UntilItEnds()
    {
        var viewModel = ViewModel();
        await viewModel.OpenAsync(Notebook, Ct);
        var command = viewModel.SelectedEnvironment!.Commands[0];
        command.Parameters[0].Value = "Api";

        await viewModel.RunCommand.ExecuteAsync(command);

        viewModel.OperationTitle.Should().Be("\"Testes\" em Notebook · Validar antimicrobianos");
        viewModel.OperationState.Should().Be("Na fila, esperando a outra máquina");
        viewModel.CanCancel.Should().BeTrue();

        _runner.Enqueue<GetRemoteOperationProgressHandler>(
            new RemoteOperationProgress(
                Operation(RemoteOperationStatus.Running),
                [Event(1, RemoteEventKind.Output, "Restaurando\nCompilando"), Event(2, RemoteEventKind.Warning, "A saída passou de 1 MiB.")],
                2),
            new RemoteOperationProgress(
                Operation(RemoteOperationStatus.Succeeded, "\"Testes\" terminou bem."),
                [Event(3, RemoteEventKind.Output, "Aprovado: 12")],
                3));

        await viewModel.TickAsync(Ct);
        viewModel.Output.Should().Be("Restaurando\nCompilando\n» A saída passou de 1 MiB.\n");
        viewModel.OperationState.Should().Be("Rodando");

        await viewModel.TickAsync(Ct);
        viewModel.Output.Should().EndWith("Aprovado: 12\n");
        viewModel.OperationState.Should().Be("Terminou bem — \"Testes\" terminou bem.");
        viewModel.IsRunning.Should().BeFalse();
        viewModel.CanCancel.Should().BeFalse();

        var reads = _runner.Invoked.Count(type => type == typeof(GetRemoteOperationProgressHandler));
        await viewModel.TickAsync(Ct);
        _runner.Invoked.Count(type => type == typeof(GetRemoteOperationProgressHandler)).Should().Be(reads, "terminada, não pergunta mais");
    }

    [AvaloniaFact]
    public async Task Canceling_AsksOnce()
    {
        var viewModel = ViewModel();
        await viewModel.OpenAsync(Notebook, Ct);
        await viewModel.InspectCommand.ExecuteAsync(null);
        _runner.ResultsByHandler[typeof(CancelRemoteOperationHandler)] = Operation(RemoteOperationStatus.Running) with { CancelRequestedAt = Now };

        await viewModel.CancelCommand.ExecuteAsync(null);

        _runner.Invoked.Should().Contain(typeof(CancelRemoteOperationHandler));
        viewModel.CanCancel.Should().BeFalse("o pedido já foi; quem para é a outra máquina");
        viewModel.OperationState.Should().Be("Cancelando…");
    }

    [AvaloniaFact]
    public async Task AnApprovalThatAsksForTyping_IsOnlyAllowedWithTheExactText()
    {
        var approval = Approval("bd_producao");
        _runner.ResultsByHandler[typeof(GetPendingApprovalsHandler)] = (IReadOnlyList<ApprovalView>)[approval];
        _runner.ResultsByHandler[typeof(AnswerApprovalHandler)] = approval;
        var viewModel = ViewModel();
        await viewModel.OpenAsync(Notebook, Ct);

        var item = viewModel.Approvals.Should().ContainSingle().Subject;
        item.Details.Should().StartWith("Notebook pergunta");
        item.TypedConfirmation = "bd_produção";

        await viewModel.AllowCommand.ExecuteAsync(item);

        viewModel.ErrorMessage.Should().Be("Para permitir, digite exatamente: bd_producao");
        _runner.Invoked.Should().NotContain(typeof(AnswerApprovalHandler));

        viewModel.Approvals[0].TypedConfirmation = "bd_producao";
        await viewModel.AllowCommand.ExecuteAsync(viewModel.Approvals[0]);

        _runner.Invoked.Should().Contain(typeof(AnswerApprovalHandler));
        viewModel.StatusMessage.Should().Be("Permitido.");
    }

    [AvaloniaFact]
    public async Task DenyingNeedsNoTyping()
    {
        var approval = Approval("bd_producao");
        _runner.ResultsByHandler[typeof(GetPendingApprovalsHandler)] = (IReadOnlyList<ApprovalView>)[approval];
        _runner.ResultsByHandler[typeof(AnswerApprovalHandler)] = approval;
        var viewModel = ViewModel();
        await viewModel.OpenAsync(Notebook, Ct);

        await viewModel.DenyCommand.ExecuteAsync(viewModel.Approvals[0]);

        _runner.Invoked.Should().Contain(typeof(AnswerApprovalHandler));
        viewModel.StatusMessage.Should().Be("Negado.");
    }

    [AvaloniaFact]
    public async Task TheWindowOpensWithTheScreen()
    {
        var viewModel = ViewModel();
        await viewModel.OpenAsync(Notebook, Ct);

        var window = new RemoteControlWindow(viewModel);
        window.Show();

        window.Title.Should().Be("Controlar Notebook");
        window.Close();
    }

    private static RemoteOperationView Operation(RemoteOperationStatus status, string? summary = null) =>
        new(OperationId, Guid.CreateVersion7(), Notebook.DeviceId, Guid.CreateVersion7(), RemoteOperationKinds.CommandRun, Environment.TaskId, status, Now, Now.AddMinutes(5), Now, null, null, summary, 0);

    private static RemoteOperationEvent Event(long seq, RemoteEventKind kind, string text) =>
        new(OperationId, seq, Now, kind, "stdout", Text: text);

    private static ApprovalView Approval(string? hint) =>
        new(
            new ApprovalRequest(
                Guid.CreateVersion7(),
                OperationId,
                ApprovalKind.ProductionDatabase,
                "Copiar o banco de produção bd_producao",
                System.Text.Json.JsonSerializer.SerializeToElement(new { }),
                hint,
                Now.AddMinutes(5),
                new string('a', 64)),
            Notebook.DeviceId,
            ApprovalState.Pending,
            null,
            null,
            null);
}
