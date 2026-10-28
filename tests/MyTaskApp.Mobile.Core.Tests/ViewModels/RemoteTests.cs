using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Mobile.Core.Composition;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Core.Remote;
using MyTaskApp.Mobile.Core.Tests.Fakes;
using MyTaskApp.Mobile.Core.ViewModels;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Mobile.Core.Tests.ViewModels;

/// <summary>
/// O controle remoto do celular (E7): a composição de verdade, os handlers de
/// verdade e um servidor de mentira que confere as assinaturas com a chave que
/// o celular registrou. O celular só controla: pede e aprova.
/// </summary>
public sealed class RemoteTests : IAsyncLifetime
{
    private static readonly Guid Notebook = Guid.CreateVersion7();

    private static readonly Guid Repository = Guid.CreateVersion7();

    private static readonly Guid CopyProfile = Guid.CreateVersion7();

    private MobileHarness _app = null!;

    private FakeRemoteServer Server => _app.Get<FakeRemoteServer>();

    public async ValueTask InitializeAsync()
    {
        _app = await MobileHarness.CreateAsync(configure: services =>
        {
            services.AddSingleton(provider => new FakeRemoteServer(provider.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IRemoteApi>(provider => provider.GetRequiredService<FakeRemoteServer>());
            services.AddSingleton<IRemoteOperationsApi>(provider => provider.GetRequiredService<FakeRemoteServer>());
            services.AddSingleton<IRemoteSessionsApi>(provider => provider.GetRequiredService<FakeRemoteServer>());
        });

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var device = scope.ServiceProvider.GetRequiredService<IDeviceIdentity>().DeviceId;
            await scope.ServiceProvider.GetRequiredService<ISyncLocalStore>().LinkAsync(
                new SyncAccountLink(Guid.CreateVersion7(), "Ana", null, Guid.CreateVersion7(), device, new Uri("https://sync.exemplo.com/")),
                TestContext.Current.CancellationToken);
        }

        Server.Devices.Add(FakeRemoteServer.Executor(Notebook, "Notebook", MobileHarness.Now));
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task WithTheRemoteOff_TheTabOffersToTurnItOn_AndRegistersNothing()
    {
        var remote = _app.Get<RemoteViewModel>();

        await remote.LoadAsync();

        remote.IsOff.Should().BeTrue();
        remote.HasNoMachines.Should().BeFalse("desligado, a tela oferece ligar, e não fala de máquinas");
        Server.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task TurningItOn_RegistersThePhoneAsControllerOnly_AndListsTheMachinesThatAcceptControl()
    {
        Server.Devices.Add(FakeRemoteServer.Executor(Guid.CreateVersion7(), "Outro celular", null) with { Roles = DeviceRoles.Controller });
        var remote = _app.Get<RemoteViewModel>();

        await remote.EnableAsync();

        var registration = Server.Registrations.Should().ContainSingle().Subject;
        registration.Roles.Should().Be(DeviceRoles.Controller);
        registration.Capabilities.Operations.Should().BeEmpty("o celular não executa nada");
        remote.IsReady.Should().BeTrue();
        var machine = remote.Machines.Should().ContainSingle().Subject;
        machine.Name.Should().Be("Notebook");
        machine.Detail.Should().Be("ligada · Git 2.47");
        _app.Unlock.Asked.Should().Be(0, "registrar mostra a chave pública, e não assina nada");
    }

    [Fact]
    public async Task Pairing_ShowsTheCodeToTypeThere_AndSaysWhenTheOtherMachineConfirms()
    {
        var remote = await ReadyAsync();

        await remote.PairAsync(remote.Machines[0]);

        var pairing = Server.Pairings.Values.Should().ContainSingle().Subject;
        remote.PairingCode.Should().Be(PairingCode.Derive(Server.Registrations[^1].Key.Fingerprint, "fp-Notebook", FakeRemoteServer.Nonce));
        remote.PairingStatus.Should().Contain("Notebook");

        Server.Pairings[pairing.PairingId] = pairing with { Status = PairingStatus.Approved };
        await AdvanceUntilAsync(() => remote.PairingCode is null, RemoteViewModel.PairingPoll);

        remote.PairingStatus.Should().Be("Pronto: Notebook confia neste celular.");
    }

    [Fact]
    public async Task APendingApproval_ShowsUpWithinTwoSeconds_AndOneThatExpiredDisappears()
    {
        var remote = await ReadyAsync();
        var watcher = _app.Get<ApprovalWatcher>();
        watcher.Start();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Server.Ask(Guid.CreateVersion7(), Notebook, ApprovalKind.ToolUse, "Bash: npm test", lifetime: TimeSpan.FromMinutes(1));
        var waited = await AdvanceUntilAsync(() => remote.Approvals.Count == 1, TimeSpan.FromMilliseconds(250));

        waited.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(2));
        remote.Approvals[0].Title.Should().Be("O Claude quer usar uma ferramenta");
        remote.Approvals[0].Summary.Should().Be("Bash: npm test");
        remote.Approvals[0].Detail.Should().StartWith("Notebook · sem resposta até as");
        remote.TabTitle.Should().Be("Remoto (1)");
        _app.Dialogs.Toasts.Should().Contain(toast => toast.StartsWith("Aprovação pedida", StringComparison.Ordinal));

        // O servidor ainda diz "pendente"; vencida, lá ela já vale negar.
        await AdvanceUntilAsync(() => remote.Approvals.Count == 0, TimeSpan.FromSeconds(30));
        remote.TabTitle.Should().Be("Remoto");
        watcher.Stop();
    }

    [Fact]
    public async Task Answering_SignsOnlyAfterUnlocking_AndGoingToTheBackgroundLocksAgain()
    {
        var remote = await ReadyAsync();
        await AskAsync(ApprovalKind.CommandConfirmation, "Rodar Migrar banco");

        _app.Unlock.Answer = UnlockResult.Canceled;
        await remote.AllowAsync(remote.Approvals[0]);

        _app.Dialogs.Alerts.Should().ContainSingle().Which.Message.Should().Be("Nada foi enviado: o celular não foi destravado.");
        Server.Responses.Should().BeEmpty();
        remote.Approvals.Should().ContainSingle("nada foi respondido");

        _app.Unlock.Answer = UnlockResult.Unlocked;
        await remote.AllowAsync(remote.Approvals[0]);

        Server.Responses.Should().ContainSingle().Which.Decision.Should().Be(ApprovalDecision.Allow);
        remote.Approvals.Should().BeEmpty();

        // Logo em seguida, o mesmo destravamento vale.
        await AskAsync(ApprovalKind.ToolUse, "Edit: src/app.ts");
        await remote.DenyAsync(remote.Approvals[0]);
        _app.Unlock.Asked.Should().Be(2);

        await _app.Get<MobileLifecycle>().SleepAsync();
        await AskAsync(ApprovalKind.ToolUse, "Write: README.md");
        await remote.DenyAsync(remote.Approvals[0]);

        _app.Unlock.Asked.Should().Be(3, "ir para segundo plano tranca a chave");
        Server.Responses.Select(response => response.Decision).Should().Equal(ApprovalDecision.Allow, ApprovalDecision.Deny, ApprovalDecision.Deny);
    }

    [Fact]
    public async Task APhoneWithoutScreenLock_CannotSign()
    {
        var remote = await ReadyAsync();
        await AskAsync(ApprovalKind.ToolUse, "Bash: ls");
        _app.Unlock.Answer = UnlockResult.NotAvailable;

        await remote.AllowAsync(remote.Approvals[0]);

        _app.Dialogs.Alerts.Should().ContainSingle().Which.Message.Should().Contain("bloqueio de tela");
        Server.Responses.Should().BeEmpty();
    }

    /// <summary>"Permitir nesta conversa" só aparece quando o agente diz o que fica permitido, e vai assinado na resposta.</summary>
    [Fact]
    public async Task AllowingForTheConversation_GoesInTheSignedAnswer_OnlyWhenTheAgentOffersIt()
    {
        var remote = await ReadyAsync();
        Server.Ask(Guid.CreateVersion7(), Notebook, ApprovalKind.ToolUse, "O Claude quer usar Bash: dotnet test", details: new { canRemember = true, rememberAs = "Bash: dotnet test" });
        Server.Ask(Guid.CreateVersion7(), Notebook, ApprovalKind.ToolUse, "O Claude quer usar Bash: ls | wc", details: new { canRemember = false });
        await _app.Get<ApprovalWatcher>().RefreshAsync(TestContext.Current.CancellationToken);

        var offered = remote.Approvals.Single(line => line.Summary.EndsWith("dotnet test", StringComparison.Ordinal));
        var compound = remote.Approvals.Single(line => line.Summary.EndsWith("wc", StringComparison.Ordinal));
        offered.CanRemember.Should().BeTrue();
        offered.RememberLabel.Should().Be("Até o fim da conversa, sem perguntar de novo: Bash: dotnet test");
        compound.CanRemember.Should().BeFalse();

        await remote.AllowForConversationAsync(offered);
        await remote.AllowForConversationAsync(compound);

        Server.Responses.Select(response => response.RememberForThisSession).Should().Equal(true, false);
    }

    [Fact]
    public async Task CriticalProduction_AsksForTheName_AndAWrongNameAnswersNothing()
    {
        var remote = await ReadyAsync();
        await AskAsync(ApprovalKind.ProductionDatabase, "Copiar clientes_prod para dev", typed: "clientes_prod");
        remote.Approvals[0].NeedsTyping.Should().BeTrue();

        _app.Dialogs.PromptAnswer = "clientes";
        await remote.AllowAsync(remote.Approvals[0]);

        _app.Dialogs.Alerts.Should().ContainSingle().Which.Title.Should().Be("O nome não confere");
        Server.Responses.Should().BeEmpty();
        _app.Unlock.Asked.Should().Be(0);

        _app.Dialogs.PromptAnswer = "clientes_prod";
        await remote.AllowAsync(remote.Approvals[0]);

        Server.Responses.Should().ContainSingle().Which.TypedConfirmation.Should().Be("clientes_prod");
    }

    [Fact]
    public async Task TheWholeFlow_TaskMachineWorktreeCommandsAndClaude_WithoutTouchingAComputer()
    {
        await ReadyAsync();
        var task = await _app.CreateTaskAsync("Corrigir o login");
        Server.Catalogs[Notebook] = Catalog();

        // Tarefa → "Preparar em…".
        var detail = _app.Get<TaskDetailViewModel>();
        await detail.LoadAsync(task.TaskId);
        detail.CanPrepareRemotely.Should().BeTrue();
        await detail.PrepareRemotelyAsync();
        _app.Navigator.Last.Should().Be($"prepare/{task.TaskId}");

        // Máquina → worktree, banco e Claude de uma vez.
        var prepare = _app.Get<RemotePrepareViewModel>();
        await prepare.LoadAsync(task.TaskId);
        prepare.SelectedMachine!.Name.Should().Be("Notebook");
        await EventuallyAsync(() => prepare.Repositories.Count == 1);
        prepare.SelectedRepository!.Name.Should().Be("app");
        prepare.SelectedCopyProfile!.Name.Should().Be("Homologação (app_prod → app_dev, produção: pede aprovação)");
        prepare.CopyDatabase = true;
        prepare.StartAgent = true;

        await prepare.PrepareAsync();
        prepare.ErrorMessage.Should().Be("Escreva o primeiro prompt do Claude.");
        Server.Requests.Should().BeEmpty();

        prepare.AgentPrompt = "Implemente o login com o Google";
        await prepare.PrepareAsync();

        var provision = Server.Requests.Should().ContainSingle().Subject;
        provision.Kind.Should().Be(RemoteOperationKinds.WorktreeProvision);
        provision.TaskId.Should().Be(task.TaskId);
        var request = provision.Payload.Deserialize<EnvironmentProvisioningRequest>(RemoteJson.Options)!;
        request.TagDirectoryId.Should().Be(Repository);
        request.RunPostWorktreeCommands.Should().BeTrue();
        request.Database!.CopyProfileId.Should().Be(CopyProfile);
        request.StartAgent!.ProfileId.Should().Be("padrao");
        _app.Navigator.Last.Should().Be($"operation/{provision.OperationId}");
        _app.Get<SentOperations>().List()[0].Title.Should().Be("Preparar tudo · Corrigir o login");

        // A operação ao vivo, com a aprovação da produção na mesma tela.
        var operation = _app.Get<RemoteOperationViewModel>();
        await operation.OpenAsync(provision.OperationId, Notebook);
        operation.Status.Should().Be("Na fila, esperando a outra máquina");

        Server.Ask(provision.OperationId, Notebook, ApprovalKind.ProductionDatabase, "Copiar app_prod para app_dev");
        await _app.Get<ApprovalWatcher>().RefreshAsync(TestContext.Current.CancellationToken);
        await operation.AllowAsync(operation.Approvals.Should().ContainSingle().Subject);
        Server.Responses.Should().ContainSingle().Which.OperationId.Should().Be(provision.OperationId);

        var conversation = Guid.CreateVersion7();
        Finish(provision.OperationId, ("npm install: ok", RemoteEventKind.Output, null), ("Pronto, o login está feito.", RemoteEventKind.AgentMessage, null), (null, RemoteEventKind.Result, conversation));
        await AdvanceUntilAsync(() => operation.IsFinished, RemoteOperationViewModel.Interval);

        operation.Status.Should().Be("Terminou bem");
        operation.Output.Should().Be("npm install: ok\nClaude: Pronto, o login está feito.\n");
        operation.HasConversation.Should().BeTrue();

        // Mais um prompt na mesma conversa.
        _app.Dialogs.PromptAnswer = "Agora os testes";
        await operation.ContinueAsync();
        operation.Close();

        var prompt = Server.Requests[^1];
        prompt.Kind.Should().Be(RemoteOperationKinds.AgentPrompt);
        prompt.Payload.Deserialize<PromptRequest>(RemoteJson.Options).Should().Be(new PromptRequest(conversation, "Agora os testes"));

        // Os comandos do worktree, da tela da máquina.
        Server.Catalogs[Notebook] = Catalog() with { Environments = [new CatalogEnvironment(task.TaskId, Guid.CreateVersion7(), [Testes.CommandId])] };
        var machine = _app.Get<RemoteMachineViewModel>();
        await machine.LoadAsync(Notebook, "Notebook");
        var environment = machine.Environments.Should().ContainSingle().Subject;
        environment.Title.Should().Be("Corrigir o login");

        _app.Dialogs.ChoiceAnswer = 0;
        _app.Dialogs.PromptAnswer = "unit";
        await machine.RunCommandAsync(environment);

        var run = Server.Requests[^1];
        run.Kind.Should().Be(RemoteOperationKinds.CommandRun);
        run.Payload.Deserialize<WorktreeExecutionRequest>(RemoteJson.Options)!.Parameters.Should().Equal(new Dictionary<string, string> { ["filtro"] = "unit" });
        _app.Unlock.Asked.Should().Be(1, "pedidos e aprovação em seguida, com um destravamento só");
    }

    [Fact]
    public async Task TheOperationScreen_StopsTheConversation()
    {
        await ReadyAsync();
        var conversation = Guid.CreateVersion7();
        var operationId = await SendAsync();
        Finish(operationId, (null, RemoteEventKind.Result, conversation));
        var operation = _app.Get<RemoteOperationViewModel>();

        await operation.OpenAsync(operationId, Notebook);
        await operation.StopAgentAsync();
        operation.Close();

        Server.Requests[^1].Kind.Should().Be(RemoteOperationKinds.AgentStop);
        Server.Requests[^1].Payload.Deserialize<AgentStopRequest>(RemoteJson.Options).Should().Be(new AgentStopRequest(conversation));
    }

    /// <summary>A saída passou das páginas de uma leitura e o estado já é final: a tela lê até a última linha.</summary>
    [Fact]
    public async Task TheOperationScreen_ReadsTheLastEventsAfterTheEnd()
    {
        await ReadyAsync();
        var operationId = await SendAsync();
        Server.EventPageSize = 1;
        Finish(operationId, [.. Enumerable.Range(1, 10).Select(index => ((string?)$"linha {index}", RemoteEventKind.Output, (Guid?)null))]);
        var operation = _app.Get<RemoteOperationViewModel>();

        await operation.OpenAsync(operationId, Notebook);
        await AdvanceUntilAsync(() => operation.Output.Contains("linha 10", StringComparison.Ordinal), RemoteOperationViewModel.Interval);
        operation.Close();

        operation.Output.Should().StartWith("linha 1\n").And.EndWith("linha 10\n");
    }

    [Fact]
    public async Task TheSentList_ShowsTheStateFromTheServer()
    {
        var remote = await ReadyAsync();
        var operationId = await SendAsync();

        await remote.LoadAsync();

        var line = remote.Recent.Should().ContainSingle().Subject;
        line.Operation.OperationId.Should().Be(operationId);
        line.Status.Should().Be("Na fila, esperando a outra máquina");
    }

    [Fact]
    public void ACorruptedSentList_IsAnEmptyList_AndOnlyTheLastOnesStay()
    {
        var preferences = new MemoryPreferences();
        preferences.Set(SentOperations.PreferenceKey, "{não é json");
        var sent = new SentOperations(preferences);

        sent.List().Should().BeEmpty();

        for (var i = 0; i < SentOperations.Max + 5; i++)
        {
            sent.Add(new SentOperation(Guid.CreateVersion7(), Notebook, $"Pedido {i}", MobileHarness.Now));
        }

        sent.List().Should().HaveCount(SentOperations.Max);
        sent.List()[0].Title.Should().Be($"Pedido {SentOperations.Max + 4}");
    }

    private static readonly CatalogCommand Testes = new(
        Guid.CreateVersion7(), "Testes", CommandScope.PostWorktree, Repository, false, [new CatalogParameter("filtro", false, null)]);

    private static AgentCatalog Catalog()
    {
        var production = Guid.CreateVersion7();
        var development = Guid.CreateVersion7();

        return new AgentCatalog(
            "hash",
            [new CatalogDirectory(Repository, Guid.CreateVersion7(), "app", true, true)],
            [Testes],
            [new CatalogDatabase(production, "app_prod", "Production", true, false), new CatalogDatabase(development, "app_dev", "Development", false, true)],
            [new CatalogAgentProfile("padrao", "Padrão", "Lê e edita o repositório")])
        {
            CopyProfiles = [new CatalogCopyProfile(CopyProfile, "Homologação", production, development, true)],
        };
    }

    /// <summary>
    /// A aba lista as conversas das máquinas, de qualquer tela que as abriu, e
    /// diz quando o agente de lá parou: ligada não é rodando os pedidos.
    /// </summary>
    [Fact]
    public async Task TheTab_ListsTheConversations_AndSaysWhenTheAgentThereStopped()
    {
        var session = Session(RemoteAgentSessionStatus.Idle);
        Server.AgentSeenAt[Notebook] = MobileHarness.Now.AddHours(-1);
        var remote = await ReadyAsync();

        await remote.LoadAsync();

        remote.Machines.Should().ContainSingle().Which.Detail.Should().StartWith("ligada, agente parado desde");
        var line = remote.Sessions.Should().ContainSingle().Subject;
        line.Title.Should().Be("Tarefa que ainda não chegou aqui");
        line.Detail.Should().Be("Notebook · Esperando o próximo prompt");

        await remote.OpenSessionCommand.ExecuteAsync(line);
        _app.Navigator.Last.Should().Be($"session/{session.SessionId}");
    }

    [Fact]
    public async Task AnOldServer_SaysToUpdateIt_AndTheMachinesStillShow()
    {
        Server.WithoutSessions = true;
        var remote = await ReadyAsync();

        await remote.LoadAsync();

        remote.SessionsProblem.Should().Be(RemoteSessionTexts.ServerTooOld);
        remote.Machines.Should().ContainSingle().Which.Detail.Should().StartWith("ligada");
        remote.Problem.Should().BeNull();
    }

    /// <summary>
    /// A tela da conversa mostra os prompts de todas as telas e manda o
    /// próximo nela mesma, sem trocar de tela: a linha do tempo pega a operação nova.
    /// </summary>
    [Fact]
    public async Task TheConversationScreen_ShowsEveryPrompt_AndTheNextOneStaysInIt()
    {
        var remote = await ReadyAsync();
        var first = await SendAsync();
        var session = Session(RemoteAgentSessionStatus.Idle);
        Server.Link(session.SessionId, first);
        Server.Events[first].Add(new RemoteOperationEvent(
            first, 1, MobileHarness.Now, RemoteEventKind.StepStarted, Text: "Claude em feature/x · perfil Padrão.",
            Data: JsonSerializer.SerializeToElement(new { agentSessionId = session.SessionId, canContinue = true, prompt = "Olá" })));
        Finish(first, ("Oi! Em que posso ajudar?", RemoteEventKind.AgentMessage, null));
        var visits = _app.Navigator.Visits.Count;
        var screen = _app.Get<RemoteSessionViewModel>();

        await screen.OpenAsync(session.SessionId, Notebook);

        screen.Output.Should().Be("Você: Olá\nClaude: Oi! Em que posso ajudar?\n— Pronto.\n");
        screen.CanPrompt.Should().BeTrue();

        _app.Dialogs.PromptAnswer = "Agora os testes";
        await screen.ContinueCommand.ExecuteAsync(null);
        screen.Close();

        var asked = Server.Requests[^1];
        asked.Kind.Should().Be(RemoteOperationKinds.AgentPrompt);
        asked.Payload.Deserialize<PromptRequest>(RemoteJson.Options)!.AgentSessionId.Should().Be(session.SessionId);
        Server.SessionOperations[session.SessionId].Should().HaveCount(2, "o prompt entra na conversa pelo payload");
        _app.Navigator.Visits.Should().HaveCount(visits, "o prompt fica na tela da conversa");
        remote.Recent.Should().BeEmpty("a lista só relê ao abrir a aba");
    }

    /// <summary>
    /// Com o hub ligado (ADR-094), o prompt mandado de outra tela aparece na
    /// conversa no aviso, sem esperar o relógio, e a aprovação nova também.
    /// </summary>
    [Fact]
    public async Task WithTheHub_APromptFromAnotherScreen_AndANewApproval_ShowUpOnTheNotice()
    {
        var remote = await ReadyAsync();
        _app.Watch.IsConnected = true;
        var first = await SendAsync();
        var session = Session(RemoteAgentSessionStatus.Idle);
        Server.Link(session.SessionId, first);
        Finish(first, ("Oi!", RemoteEventKind.AgentMessage, null));
        var screen = _app.Get<RemoteSessionViewModel>();
        await screen.OpenAsync(session.SessionId, Notebook);
        screen.Output.Should().Contain("Claude: Oi!");

        // O desktop mandou o próximo prompt; o celular só sabe pelo aviso da conversa.
        var second = await SendAsync();
        Server.Link(session.SessionId, second);
        Finish(second, ("Os testes passaram.", RemoteEventKind.AgentMessage, null));
        _app.Watch.Notify(RemoteNoticeKind.Session, session.SessionId);

        await EventuallyAsync(() => screen.Output.Contains("Claude: Os testes passaram.", StringComparison.Ordinal));
        screen.Close();

        var watcher = _app.Get<ApprovalWatcher>();
        watcher.Start();
        await EventuallyAsync(() => _app.Watch.Watchers > 0);
        Server.Ask(Guid.CreateVersion7(), Notebook, ApprovalKind.ToolUse, "Bash: npm test", lifetime: TimeSpan.FromMinutes(1));
        _app.Watch.Notify(RemoteNoticeKind.Approval, Guid.CreateVersion7());

        await EventuallyAsync(() => remote.Approvals.Count == 1);
        watcher.Stop();
        await EventuallyAsync(() => _app.Watch.Watchers == 0);
    }

    private RemoteAgentSessionView Session(RemoteAgentSessionStatus status)
    {
        var session = new RemoteAgentSessionView(
            Guid.CreateVersion7(), Notebook, 1, Guid.CreateVersion7(), Guid.CreateVersion7(), "padrao", null, status,
            RemoteAgentActivity.WaitingReview, null, null, MobileHarness.Now, null, MobileHarness.Now, MobileHarness.Now);
        Server.Sessions.Add(session);
        return session;
    }

    private async Task<RemoteViewModel> ReadyAsync()
    {
        var remote = _app.Get<RemoteViewModel>();
        await remote.EnableAsync();
        remote.IsReady.Should().BeTrue();
        return remote;
    }

    private async Task AskAsync(ApprovalKind kind, string summary, string? typed = null)
    {
        Server.Ask(Guid.CreateVersion7(), Notebook, kind, summary, typed);
        await _app.Get<ApprovalWatcher>().RefreshAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> SendAsync()
    {
        var environment = new RemoteEnvironmentView(Guid.CreateVersion7(), Guid.CreateVersion7(), "Tarefa", null, []);
        var view = await _app.Get<RemoteSender>().SendAsync(RemoteRequests.StartAgent(Notebook, environment, "padrao", "Olá"), "Claude · Tarefa");
        return view.OperationId;
    }

    /// <summary>A máquina de lá terminou: os eventos e o estado final, como o agente manda.</summary>
    private void Finish(Guid operationId, params (string? Text, RemoteEventKind Kind, Guid? Conversation)[] events)
    {
        var list = Server.Events[operationId];

        foreach (var (text, kind, conversation) in events)
        {
            JsonElement? data = conversation is { } id
                ? JsonSerializer.SerializeToElement(new { agentSessionId = id, canContinue = true })
                : null;
            list.Add(new RemoteOperationEvent(operationId, list.Count + 1, MobileHarness.Now, kind, Text: text, Data: data));
        }

        Server.Operations[operationId] = Server.Operations[operationId] with { Status = RemoteOperationStatus.Succeeded, ResultSummary = "Pronto.", LastContiguousSeq = list.Count };
    }

    /// <summary>Avança o relógio de pouco em pouco até a tela mostrar; devolve quanto tempo passou.</summary>
    private async Task<TimeSpan> AdvanceUntilAsync(Func<bool> condition, TimeSpan step)
    {
        var waited = TimeSpan.Zero;

        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            _app.Clock.Advance(step);
            waited += step;

            for (var spin = 0; spin < 10 && !condition(); spin++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

        condition().Should().BeTrue();
        return waited;
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        condition().Should().BeTrue();
    }
}
