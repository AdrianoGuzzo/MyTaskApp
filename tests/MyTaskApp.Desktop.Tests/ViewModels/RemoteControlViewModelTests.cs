using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Remote.Contracts;
using NSubstitute;

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

    private static readonly CatalogAgentProfile Read = new("read", "Ler", "Lê o código, sem editar nem rodar nada.");

    private static readonly CatalogAgentProfile EditAndTest = new("edit-test", "Editar e testar", "Edita no worktree e roda build e testes.");

    private readonly FakeUseCaseRunner _runner = new();

    public RemoteControlViewModelTests()
    {
        _runner.ResultsByHandler[typeof(GetRemoteControlHandler)] = new RemoteControlView(new AgentCatalog("h", [], [Tests], [], [Read, EditAndTest]), [Environment]);
        _runner.ResultsByHandler[typeof(GetPendingApprovalsHandler)] = (IReadOnlyList<ApprovalView>)[];
        _runner.ResultsByHandler[typeof(SendRemoteOperationHandler)] = Operation(RemoteOperationStatus.Queued);
        _runner.ResultsByHandler[typeof(ListRemoteSessionsHandler)] = new RemoteSessionList([]);
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

    /// <summary>
    /// Com o hub ligado (ADR-094), a saída é lida no aviso, e o relógio de meio
    /// segundo só cobre o aviso perdido, a cada 5 s. Fechar a janela solta o hub.
    /// </summary>
    [AvaloniaFact]
    public async Task WithTheHub_TheOutputIsReadOnTheNotice_AndTheClockOnlyEveryFiveSeconds()
    {
        var watch = new FakeRemoteWatch { IsConnected = true };
        var viewModel = new RemoteControlViewModel(_runner, NullLogger<RemoteControlViewModel>.Instance, watch);
        await viewModel.OpenAsync(Notebook, Ct);
        watch.Watchers.Should().Be(1);
        var command = viewModel.SelectedEnvironment!.Commands[0];
        command.Parameters[0].Value = "Api";
        await viewModel.RunCommand.ExecuteAsync(command);
        _runner.ResultsByHandler[typeof(GetRemoteOperationProgressHandler)] = new RemoteOperationProgress(Operation(RemoteOperationStatus.Running), [], 0);
        int Reads() => _runner.Invoked.Count(type => type == typeof(GetRemoteOperationProgressHandler));

        await viewModel.TickAsync(Ct);
        Reads().Should().Be(0, "sem aviso, a volta de meio segundo não lê");

        watch.Notify(RemoteNoticeKind.Events, OperationId);
        await viewModel.TickAsync(Ct);
        Reads().Should().Be(1);

        for (var tick = 0; tick < RemoteControlViewModel.LiveTicks; tick++)
        {
            await viewModel.TickAsync(Ct);
        }

        Reads().Should().Be(2, "o relógio cobre o aviso perdido");

        await viewModel.CloseAsync();
        watch.Watchers.Should().Be(0);
    }

    /// <summary>O "terminou" chegou com saída ainda no servidor: a janela lê até o fim antes de parar.</summary>
    [AvaloniaFact]
    public async Task AFinishedOperationWithEventsLeft_KeepsReadingUntilDrained()
    {
        var viewModel = ViewModel();
        await viewModel.OpenAsync(Notebook, Ct);
        var command = viewModel.SelectedEnvironment!.Commands[0];
        command.Parameters[0].Value = "Api";
        await viewModel.RunCommand.ExecuteAsync(command);

        _runner.Enqueue<GetRemoteOperationProgressHandler>(
            new RemoteOperationProgress(Operation(RemoteOperationStatus.Succeeded), [Event(1, RemoteEventKind.Output, "Compilando")], 1, HasMore: true),
            new RemoteOperationProgress(Operation(RemoteOperationStatus.Succeeded), [Event(2, RemoteEventKind.Output, "Aprovado: 12")], 2));

        await viewModel.TickAsync(Ct);
        viewModel.IsRunning.Should().BeFalse();

        await viewModel.TickAsync(Ct);
        viewModel.Output.Should().Be("Compilando\nAprovado: 12\n", "a última linha chega mesmo depois do estado final");

        var reads = _runner.Invoked.Count(type => type == typeof(GetRemoteOperationProgressHandler));
        await viewModel.TickAsync(Ct);
        _runner.Invoked.Count(type => type == typeof(GetRemoteOperationProgressHandler)).Should().Be(reads, "drenada, não pergunta mais");
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
    public async Task TheClaudeThere_OpensAConversation_AndTheNextPromptGoesInIt()
    {
        var conversation = Guid.CreateVersion7();
        var viewModel = ViewModel();
        await viewModel.OpenAsync(Notebook, Ct);

        viewModel.AgentProfiles.Should().Equal(Read, EditAndTest);
        viewModel.SelectedProfile.Should().Be(Read);
        viewModel.PromptButtonText.Should().Be("Abrir conversa");

        await viewModel.SendPromptCommand.ExecuteAsync(null);
        viewModel.ErrorMessage.Should().Be("Escreva o que o Claude deve fazer.");
        _runner.Invoked.Should().NotContain(typeof(SendRemoteOperationHandler));

        _runner.ResultsByHandler[typeof(SendRemoteOperationHandler)] = Operation(RemoteOperationStatus.Queued, kind: RemoteOperationKinds.AgentStart);
        viewModel.SelectedProfile = EditAndTest;
        viewModel.AgentPrompt = "Rode os testes e corrija o que falhar";
        await viewModel.SendPromptCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.Should().BeNull();
        viewModel.AgentPrompt.Should().BeEmpty();
        viewModel.OperationTitle.Should().Be("Claude em Notebook · Validar antimicrobianos");

        _runner.Enqueue<GetRemoteOperationProgressHandler>(
            new RemoteOperationProgress(
                Operation(RemoteOperationStatus.Running, kind: RemoteOperationKinds.AgentStart),
                [
                    Event(1, RemoteEventKind.StepStarted, "Claude em feature/ECO-12 · perfil Editar e testar.", Conversation(conversation, true)),
                    Event(2, RemoteEventKind.AgentMessage, "Vou rodar os testes."),
                    Event(3, RemoteEventKind.ToolUse, "Bash: dotnet test"),
                ],
                3));
        await viewModel.TickAsync(Ct);

        viewModel.Output.Should().Be("» Claude em feature/ECO-12 · perfil Editar e testar.\nClaude: Vou rodar os testes.\n» usou Bash: dotnet test\n");
        viewModel.Conversation.Should().Be(conversation, "já dá para parar o primeiro prompt");
        viewModel.PromptButtonText.Should().Be("Mandar na conversa");
        viewModel.HasNoConversation.Should().BeFalse("o perfil é o da conversa");
    }

    [AvaloniaFact]
    public async Task AConversationThatCannotContinue_IsForgotten_AndStopForgetsItToo()
    {
        var conversation = Guid.CreateVersion7();
        var viewModel = ViewModel();
        await viewModel.OpenAsync(Notebook, Ct);
        _runner.ResultsByHandler[typeof(SendRemoteOperationHandler)] = Operation(RemoteOperationStatus.Queued, kind: RemoteOperationKinds.AgentStart);
        viewModel.AgentPrompt = "oi";
        await viewModel.SendPromptCommand.ExecuteAsync(null);

        _runner.Enqueue<GetRemoteOperationProgressHandler>(
            new RemoteOperationProgress(
                Operation(RemoteOperationStatus.Running, kind: RemoteOperationKinds.AgentStart),
                [Event(1, RemoteEventKind.StepStarted, "Claude…", Conversation(conversation, true))],
                1),
            new RemoteOperationProgress(
                Operation(RemoteOperationStatus.Failed, "O Claude não abriu.", RemoteOperationKinds.AgentStart),
                [Event(2, RemoteEventKind.Result, "O Claude não abriu.", Conversation(conversation, false))],
                2));
        await viewModel.TickAsync(Ct);
        viewModel.HasConversation.Should().BeTrue();
        await viewModel.TickAsync(Ct);
        viewModel.HasConversation.Should().BeFalse("o próximo prompt abre outra");

        _runner.Enqueue<GetRemoteOperationProgressHandler>(
            new RemoteOperationProgress(
                Operation(RemoteOperationStatus.Succeeded, "Respondeu.", RemoteOperationKinds.AgentStart),
                [Event(3, RemoteEventKind.Result, "Respondeu.", Conversation(conversation, true))],
                3));
        viewModel.AgentPrompt = "de novo";
        await viewModel.SendPromptCommand.ExecuteAsync(null);
        await viewModel.TickAsync(Ct);
        viewModel.HasConversation.Should().BeTrue();

        await viewModel.StopAgentCommand.ExecuteAsync(null);
        viewModel.StatusMessage.Should().Be("Pedido para parar o Claude enviado.");
        viewModel.HasConversation.Should().BeFalse();
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

    private static RemoteOperationView Operation(RemoteOperationStatus status, string? summary = null, string kind = RemoteOperationKinds.CommandRun) =>
        new(OperationId, Guid.CreateVersion7(), Notebook.DeviceId, Guid.CreateVersion7(), kind, Environment.TaskId, status, Now, Now.AddMinutes(5), Now, null, null, summary, 0);

    private static RemoteOperationEvent Event(long seq, RemoteEventKind kind, string text, System.Text.Json.JsonElement? data = null) =>
        new(OperationId, seq, Now, kind, "stdout", Text: text, Data: data);

    /// <summary>"Permitir nesta conversa": só com a oferta do agente, mostrando o que fica permitido.</summary>
    [AvaloniaFact]
    public async Task AllowingForTheConversation_ShowsWhatItCovers_AndOnlyWhenTheAgentOffersIt()
    {
        var offered = Approval(ApprovalKind.ToolUse, "O Claude quer usar Bash: dotnet test", new { canRemember = true, rememberAs = "Bash: dotnet test" });
        var old = Approval(ApprovalKind.ToolUse, "O Claude quer usar Edit: README.md", new { tool = "Edit" });
        _runner.ResultsByHandler[typeof(GetPendingApprovalsHandler)] = (IReadOnlyList<ApprovalView>)[offered, old];
        _runner.ResultsByHandler[typeof(AnswerApprovalHandler)] = offered;
        var viewModel = ViewModel();
        await viewModel.OpenAsync(Notebook, Ct);

        viewModel.Approvals[0].CanRemember.Should().BeTrue();
        viewModel.Approvals[0].RememberLabel.Should().Be("Até o fim da conversa, sem perguntar de novo: Bash: dotnet test");
        viewModel.Approvals[1].CanRemember.Should().BeFalse("o agente antigo não oferece");

        await viewModel.AllowForConversationCommand.ExecuteAsync(viewModel.Approvals[0]);

        _runner.Invoked.Should().Contain(typeof(AnswerApprovalHandler));
        viewModel.StatusMessage.Should().Be("Permitido nesta conversa.");
    }

    /// <summary>
    /// A pergunta do Claude (ADR-093): as opções marcáveis, uma só quando a
    /// pergunta pede uma, e "Responder" assina uma resposta por pergunta.
    /// Sem resposta, nada sai; marcar sobrevive ao relógio.
    /// </summary>
    [AvaloniaFact]
    public async Task TheQuestionOfClaude_IsAnsweredByMarkingTheOptions()
    {
        var question = Approval(
            ApprovalKind.ToolUse,
            "O Claude pergunta: Qual banco?",
            new
            {
                kind = "question",
                canRemember = false,
                questions = new object[]
                {
                    new { question = "Qual banco?", header = "Banco", multiSelect = false, options = new object[] { new { label = "Local", description = "O SQLite" }, new { label = "Docker" } } },
                    new { question = "Quais testes?", multiSelect = true, options = new object[] { new { label = "Unidade" }, new { label = "Integração" } } },
                },
            });
        question = question with { Request = ApprovalHash.Seal(question.Request) };
        SignedEnvelope? sent = null;
        var remote = Substitute.For<IRemoteOperationsApi>();
        remote.GetApprovalAsync(question.Request.ApprovalId, Arg.Any<CancellationToken>()).Returns(question);
        remote.RespondApprovalAsync(question.Request.ApprovalId, Arg.Do<SignedEnvelope>(envelope => sent = envelope), Arg.Any<CancellationToken>()).Returns(question);
        var signer = Substitute.For<IDeviceSigner>();
        signer.GetPublicKeyAsync(Arg.Any<CancellationToken>()).Returns(new DevicePublicKey(DevicePublicKey.Es256, "c3BraQ", "impressao"));
        signer.SignAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns("assinatura");
        _runner.Handlers[typeof(AnswerApprovalHandler)] = new AnswerApprovalHandler(remote, signer, TimeProvider.System);
        _runner.ResultsByHandler[typeof(GetPendingApprovalsHandler)] = (IReadOnlyList<ApprovalView>)[question];
        var viewModel = ViewModel();
        await viewModel.OpenAsync(Notebook, Ct);

        var item = viewModel.Approvals.Should().ContainSingle().Subject;
        (item.IsQuestion, item.AllowLabel, item.DenyLabel).Should().Be((true, "Responder", "Deixar o Claude decidir"));
        item.Questions[0].Text.Should().Be("Banco: Qual banco?");

        await viewModel.AllowCommand.ExecuteAsync(item);
        viewModel.ErrorMessage.Should().Be("Marque uma opção, ou escreva a resposta, em cada pergunta.");
        sent.Should().BeNull();

        item.Questions[0].Options[0].IsChosen = true;
        item.Questions[0].Options[1].IsChosen = true;
        item.Questions[0].Options[0].IsChosen.Should().BeFalse("a pergunta pede uma só");
        item.Questions[1].Options[0].IsChosen = true;
        item.Questions[1].Options[1].IsChosen = true;
        await viewModel.TickAsync(Ct);
        viewModel.Approvals.Single().Should().BeSameAs(item, "o relógio não apaga o que foi marcado");

        await viewModel.AllowCommand.ExecuteAsync(item);

        var response = System.Text.Json.JsonSerializer.Deserialize<ApprovalResponse>(
            System.Buffers.Text.Base64Url.DecodeFromChars(sent!.Jws.Split('.')[1]), RemoteJson.Options)!;
        response.Decision.Should().Be(ApprovalDecision.Allow);
        HeadlessQuestions.ReadAnswers(response.TypedConfirmation, 2).Should().Equal("Docker", "Unidade, Integração");
        viewModel.StatusMessage.Should().Be("Respondido.");
    }

    /// <summary>
    /// As conversas da máquina vêm do servidor: a lista com o estado, a linha
    /// do tempo da escolhida, o próximo prompt e o parar, os mesmos handlers do
    /// celular.
    /// </summary>
    [AvaloniaFact]
    public async Task TheConversations_AreListedWithTheirState_AndTheChosenOneShowsItsTimeline()
    {
        var waiting = Session(RemoteAgentSessionStatus.Idle, "Esperando o próximo prompt");
        var answering = Session(RemoteAgentSessionStatus.Running, "Respondendo");
        _runner.ResultsByHandler[typeof(ListRemoteSessionsHandler)] = new RemoteSessionList([waiting, answering]);
        _runner.Enqueue<GetRemoteSessionTimelineHandler>(
            Timeline(waiting, ["Você: Leia o README", "Claude: Li.", "— Respondeu em 1 turno."], live: false),
            Timeline(waiting, [], live: false));
        var viewModel = ViewModel();
        await viewModel.OpenAsync(Notebook, Ct);

        var sessions = viewModel.Sessions;
        sessions.Items.Select(line => line.Details).Should().Equal("feature/ECO-12 · Esperando o próximo prompt", "feature/ECO-12 · Respondendo");
        sessions.HasSelection.Should().BeFalse();

        sessions.Selected = sessions.Items[0];
        await WaitForAsync(() => sessions.Timeline.Length > 0);

        sessions.Timeline.Should().Be("Você: Leia o README\nClaude: Li.\n— Respondeu em 1 turno.\n");
        sessions.CanPrompt.Should().BeTrue();
        sessions.CanStop.Should().BeTrue();

        sessions.Prompt = "E os testes?";
        await sessions.SendPromptCommand.ExecuteAsync(null);
        sessions.Prompt.Should().BeEmpty();
        _runner.Invoked.Should().Contain(typeof(SendRemoteOperationHandler));

        _runner.Enqueue<GetRemoteSessionTimelineHandler>(Timeline(answering, ["Você: Rode tudo"], live: true));
        sessions.Selected = sessions.Items[1];
        sessions.CanPrompt.Should().BeTrue("respondendo, o próximo prompt entra na fila da conversa (ADR-095)");
        await sessions.StopCommand.ExecuteAsync(null);
        _runner.Invoked.Count(type => type == typeof(SendRemoteOperationHandler)).Should().Be(2);
    }

    /// <summary>Fechar e abrir a janela não perde a conversa: ela vem do servidor.</summary>
    [AvaloniaFact]
    public async Task AConversationWaitingInTheEnvironment_IsResumed_UntilANewOneIsAskedFor()
    {
        var waiting = Session(RemoteAgentSessionStatus.Idle, "Esperando o próximo prompt");
        _runner.ResultsByHandler[typeof(ListRemoteSessionsHandler)] = new RemoteSessionList([waiting]);
        var viewModel = ViewModel();

        await viewModel.OpenAsync(Notebook, Ct);

        viewModel.Conversation.Should().Be(waiting.Session.SessionId);
        viewModel.PromptButtonText.Should().Be("Mandar na conversa");

        viewModel.NewConversation();
        viewModel.Conversation.Should().BeNull("a conversa deixada de lado não volta pela lista");
        await viewModel.LoadCommand.ExecuteAsync(null);
        viewModel.Conversation.Should().BeNull();
    }

    /// <summary>
    /// Sem confirmação, o pedido parecia ter sumido: a tela diz que mandou, e
    /// depois onde a conversa abriu e onde continuar.
    /// </summary>
    [AvaloniaFact]
    public async Task OpeningAConversationThere_SaysItWasSent_AndThenWhereItOpened()
    {
        var conversation = Guid.CreateVersion7();
        var viewModel = ViewModel();
        await viewModel.OpenAsync(Notebook, Ct);
        _runner.ResultsByHandler[typeof(SendRemoteOperationHandler)] = Operation(RemoteOperationStatus.Queued, kind: RemoteOperationKinds.AgentStart);
        viewModel.AgentPrompt = "Leia o README";

        await viewModel.SendPromptCommand.ExecuteAsync(null);

        viewModel.ConversationNotice.Should().Be("Pedido enviado a Notebook. A conversa abre quando o agente de lá pegar o pedido.");

        _runner.Enqueue<GetRemoteOperationProgressHandler>(
            new RemoteOperationProgress(
                Operation(RemoteOperationStatus.Running, kind: RemoteOperationKinds.AgentStart),
                [Event(1, RemoteEventKind.StepStarted, "Claude…", Conversation(conversation, true))],
                1));
        await viewModel.TickAsync(Ct);

        viewModel.ConversationNotice.Should().StartWith("Conversa aberta em Notebook · Validar antimicrobianos (feature/ECO-12).");
        viewModel.HasConversationNotice.Should().BeTrue();

        viewModel.NewConversation();
        viewModel.HasConversationNotice.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task AConversationThatNeverOpened_DropsTheSentNotice()
    {
        var viewModel = ViewModel();
        await viewModel.OpenAsync(Notebook, Ct);
        _runner.ResultsByHandler[typeof(SendRemoteOperationHandler)] = Operation(RemoteOperationStatus.Queued, kind: RemoteOperationKinds.AgentStart);
        viewModel.AgentPrompt = "Leia o README";
        await viewModel.SendPromptCommand.ExecuteAsync(null);

        _runner.Enqueue<GetRemoteOperationProgressHandler>(
            new RemoteOperationProgress(Operation(RemoteOperationStatus.Expired, kind: RemoteOperationKinds.AgentStart), [], 0));
        await viewModel.TickAsync(Ct);

        viewModel.ConversationNotice.Should().BeNull();
    }

    /// <summary>"Na fila" sozinho não explicava nada: com o agente de lá parado, a tela diz.</summary>
    [AvaloniaFact]
    public async Task AQueuedRequest_SaysTheAgentThereIsStopped()
    {
        _runner.ResultsByHandler[typeof(ListRemoteSessionsHandler)] = new RemoteSessionList(
            [],
            null,
            new Dictionary<Guid, string> { [Notebook.DeviceId] = "agente parado desde 28/10 09:12: os pedidos esperam na fila" })
        {
            StoppedAgents = new HashSet<Guid> { Notebook.DeviceId },
        };
        var viewModel = ViewModel();
        await viewModel.OpenAsync(Notebook, Ct);

        await viewModel.InspectCommand.ExecuteAsync(null);

        viewModel.OperationState.Should().Be(
            "Na fila, esperando a outra máquina — Notebook: agente parado desde 28/10 09:12: os pedidos esperam na fila");
    }

    /// <summary>O "Abrir conversa" do card e do aviso abre já na conversa, mesmo com o Claude respondendo.</summary>
    [AvaloniaFact]
    public async Task OpeningOnAConversation_ChoosesIt_AndThePromptGoesInIt()
    {
        var answering = Session(RemoteAgentSessionStatus.Running, "Respondendo");
        _runner.ResultsByHandler[typeof(ListRemoteSessionsHandler)] = new RemoteSessionList([answering]);
        _runner.ResultsByHandler[typeof(GetRemoteSessionTimelineHandler)] = Timeline(answering, ["Você: Rode tudo"], live: true);
        var viewModel = ViewModel();

        await viewModel.OpenAsync(Notebook, Ct, Environment.DevelopmentId, answering.Session.SessionId);

        viewModel.Sessions.Selected!.SessionId.Should().Be(answering.Session.SessionId);
        viewModel.Conversation.Should().Be(answering.Session.SessionId);
        viewModel.PromptButtonText.Should().Be("Mandar na conversa");
    }

    [AvaloniaFact]
    public async Task AnOldServer_SaysToUpdateIt_AndTheRestOfTheWindowWorks()
    {
        _runner.ResultsByHandler[typeof(ListRemoteSessionsHandler)] = new RemoteSessionList([], RemoteSessionTexts.ServerTooOld);
        var viewModel = ViewModel();

        await viewModel.OpenAsync(Notebook, Ct);

        viewModel.Sessions.Message.Should().Be(RemoteSessionTexts.ServerTooOld);
        viewModel.Sessions.HasItems.Should().BeFalse();
        viewModel.Environments.Should().ContainSingle();
        viewModel.ErrorMessage.Should().BeNull();
    }

    private static RemoteSessionItem Session(RemoteAgentSessionStatus status, string state) => new(
        new RemoteAgentSessionView(
            Guid.CreateVersion7(), Notebook.DeviceId, 1, Environment.TaskId, Environment.DevelopmentId, "edit-test", null,
            status, RemoteAgentActivity.Working, null, null, Now, null, Now, Now),
        new RemoteAgentPresence(Notebook.DeviceId, Now),
        Environment.TaskTitle!,
        Environment.Branch,
        state);

    private static RemoteSessionTimeline Timeline(RemoteSessionItem session, IReadOnlyList<string> lines, bool live) => new(
        session,
        [Operation(live ? RemoteOperationStatus.Running : RemoteOperationStatus.Succeeded, kind: RemoteOperationKinds.AgentStart)],
        lines,
        live ? RemoteSessionCursor.Start : new RemoteSessionCursor(new Dictionary<Guid, long>(), new HashSet<Guid> { OperationId }));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10, Ct);
        }
    }

    private static System.Text.Json.JsonElement Conversation(Guid id, bool canContinue) =>
        System.Text.Json.JsonSerializer.SerializeToElement(new { agentSessionId = id, canContinue });

    private static ApprovalView Approval(string? hint) =>
        Approval(ApprovalKind.ProductionDatabase, "Copiar o banco de produção bd_producao", new { }, hint);

    private static ApprovalView Approval(ApprovalKind kind, string summary, object details, string? hint = null) =>
        new(
            new ApprovalRequest(
                Guid.CreateVersion7(),
                OperationId,
                kind,
                summary,
                System.Text.Json.JsonSerializer.SerializeToElement(details),
                hint,
                Now.AddMinutes(5),
                new string('a', 64)),
            Notebook.DeviceId,
            ApprovalState.Pending,
            null,
            null,
            null);
}

/// <summary>O hub de mentira: o teste diz se está ligado e manda os avisos.</summary>
internal sealed class FakeRemoteWatch : IRemoteWatch
{
    public event Action<RemoteNotice>? Noticed;

    public bool IsConnected { get; set; }

    public int Watchers { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Watchers++;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Watchers--;
        return Task.CompletedTask;
    }

    public void Notify(RemoteNoticeKind kind, Guid id) => Noticed?.Invoke(new RemoteNotice(kind, id));
}
