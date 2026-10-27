using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Application;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// Os casos de falha da seção 4.6 da arquitetura, de ponta a ponta (fase 2,
/// T-35): o servidor de verdade, o PostgreSQL de verdade e o notebook sem
/// tela, com a rede, o relógio e os processos dele mexidos pelo teste.
/// </summary>
public sealed partial class RemoteOperationsEndToEndTests
{
    /// <summary>4.6, mensagem duplicada: o mesmo pedido duas vezes e o mesmo lote de eventos de novo rodam e contam uma vez.</summary>
    [Fact]
    public async Task Chaos_DuplicatedRequestsAndEvents_RunAndCountOnce()
    {
        var (notebook, phone, phoneKey) = await PairedAsync();
        using var _ = phoneKey;
        var request = await DraftAsync(notebook, phone);
        var envelope = Sign(phoneKey, request, RemoteJws.OperationType);
        var first = await SendAsync<RemoteOperationView>(phone, HttpMethod.Post, RemoteRoutes.Operations, envelope);

        // O celular reenvia o mesmo envelope (a resposta se perdeu): volta a mesma operação.
        var again = await SendAsync<RemoteOperationView>(phone, HttpMethod.Post, RemoteRoutes.Operations, envelope);
        again.Should().BeEquivalentTo(first);

        // O hub tocou duas vezes: duas buscas.
        var dispatcher = notebook.Services.GetRequiredService<RemoteOperationDispatcher>();
        await dispatcher.PollAsync(Ct);
        await dispatcher.PollAsync(Ct);
        (await FinishedAsync(phone, request.OperationId)).Status.Should().Be(RemoteOperationStatus.Succeeded);

        var events = await EventsAsync(phone, request.OperationId);
        events.Events.Where(item => item.Kind == RemoteEventKind.Result).Should().ContainSingle("rodou uma vez só");

        // O agente reenvia um lote que o servidor já tinha (o ack se perdeu).
        var ack = await notebook.Services.GetRequiredService<IRemoteOperationsApi>()
            .AppendEventsAsync(new EventBatch(request.OperationId, [.. events.Events.Take(2)]), Ct);
        ack.LastContiguousSeq.Should().Be(events.LastContiguousSeq);
        (await EventsAsync(phone, request.OperationId)).Events.Should().HaveCount(events.Events.Count);
    }

    /// <summary>
    /// 4.6, eventos fora de ordem: o servidor entrega em ordem e só até o
    /// buraco. O controlador, que anda o "depois de" pelo que recebeu, não
    /// perde o que chegou atrasado.
    /// </summary>
    [Fact]
    public async Task Chaos_EventsOutOfOrder_ReachTheControllerInOrder_WithoutLosingTheLateOne()
    {
        var (notebook, phone, phoneKey) = await PairedAsync();
        using var _ = phoneKey;
        var request = await RequestAsync(notebook, phone, phoneKey);
        var api = notebook.Services.GetRequiredService<IRemoteOperationsApi>();
        await api.ListAgentOperationsAsync(Ct);
        await api.UpdateStatusAsync(request.OperationId, new OperationStatusUpdate(RemoteOperationStatus.Accepted), Ct);
        await api.UpdateStatusAsync(request.OperationId, new OperationStatusUpdate(RemoteOperationStatus.Running), Ct);
        var controller = new GetRemoteOperationProgressHandler(new PhoneOperations(phone));

        await api.AppendEventsAsync(new EventBatch(request.OperationId, [Output(request.OperationId, 1), Output(request.OperationId, 3)]), Ct);
        var first = await controller.HandleAsync(new GetRemoteOperationProgress(request.OperationId, 0), Ct);

        first.Events.Select(item => item.Seq).Should().Equal([1], "o 3 espera o 2");

        await api.AppendEventsAsync(new EventBatch(request.OperationId, [Output(request.OperationId, 2)]), Ct);
        var second = await controller.HandleAsync(new GetRemoteOperationProgress(request.OperationId, first.After), Ct);

        second.Events.Select(item => item.Seq).Should().Equal(2, 3);
        second.Events.Select(item => item.Text).Should().Equal("linha 2", "linha 3");
    }

    /// <summary>4.6, máquina desligada: o pedido espera na fila até o prazo e expira; ela não roda nada ao voltar.</summary>
    [Fact]
    public async Task Chaos_AMachineTurnedOff_LetsTheRequestExpire_AndDoesNotRunItWhenItComesBack()
    {
        var serverClock = await UseServerClockAsync();
        var (notebook, phone, phoneKey) = await PairedAsync();
        using var _ = phoneKey;
        notebook.Net.Offline = true;
        var request = await RequestAsync(notebook, phone, phoneKey);

        serverClock.Advance(RemoteLimits.DefaultAcceptanceWindow + TimeSpan.FromMinutes(1));
        await using (var scope = _api.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RemoteOperationUseCases>().SweepAsync(RemoteLimits.ReplayWindow, Ct);
        }

        (await SendAsync<RemoteOperationView>(phone, HttpMethod.Get, RemoteRoutes.Operation(request.OperationId), null))
            .Status.Should().Be(RemoteOperationStatus.Expired);
        (await SendAsync<List<RemoteDeviceInfo>>(phone, HttpMethod.Get, RemoteRoutes.Devices, null))
            .Single(device => device.DeviceId == notebook.Device.DeviceId)
            .IsOnlineAt(serverClock.GetUtcNow()).Should().BeFalse("o controlador vê que ela está desligada antes de pedir");

        // Ligou de novo: a fila não traz a expirada.
        notebook.Net.Offline = false;
        await notebook.Services.GetRequiredService<RemoteOperationDispatcher>().PollAsync(Ct);
        (await EventsAsync(phone, request.OperationId)).Events.Should().BeEmpty();
        (await SendAsync<RemoteOperationView>(phone, HttpMethod.Get, RemoteRoutes.Operation(request.OperationId), null))
            .Status.Should().Be(RemoteOperationStatus.Expired);
    }

    /// <summary>
    /// 4.6, servidor fora do ar e controlador sem conexão: a operação em
    /// curso segue no notebook, a saída fica no diário e chega inteira quando
    /// a rede volta; o controlador continua de onde parou.
    /// </summary>
    [Fact]
    public async Task Chaos_TheServerGoesAwayMidOperation_TheAgentFinishes_AndTellsEverythingLater()
    {
        var (notebook, phone, phoneKey) = await PairedAsync();
        using var _ = phoneKey;
        var slow = DevelopmentCommand.Create("@devagar", Sequence("echo antes-da-queda", Pause(7), "echo depois-da-queda"), null, DateTimeOffset.UtcNow);
        slow.AllowRemote(true);
        var (taskId, developmentId) = await ReadyEnvironmentAsync(notebook, slow);
        var dispatcher = notebook.Services.GetRequiredService<RemoteOperationDispatcher>();
        var finished = new TaskCompletionSource<RemoteOperationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Finished += (_, status) => finished.TrySetResult(status);

        var request = await RequestAsync(notebook, phone, phoneKey, RemoteOperationKinds.CommandRun, Run(developmentId, slow), taskId);
        await dispatcher.PollAsync(Ct);
        var before = await WaitForAsync(notebook, phone, request.OperationId, page => page.Events.Any(item => item.Text?.Contains("antes-da-queda") == true));

        notebook.Net.Offline = true;
        (await finished.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct)).Should().Be(RemoteOperationStatus.Succeeded);

        // O servidor não soube: para o controlador, ainda roda.
        var stale = await SendAsync<RemoteOperationView>(phone, HttpMethod.Get, RemoteRoutes.Operation(request.OperationId), null);
        RemoteOperationLifecycle.IsFinal(stale.Status).Should().BeFalse();

        notebook.Net.Offline = false;
        await dispatcher.FlushAsync(Ct);

        (await SendAsync<RemoteOperationView>(phone, HttpMethod.Get, RemoteRoutes.Operation(request.OperationId), null))
            .Status.Should().Be(RemoteOperationStatus.Succeeded);
        var rest = await EventsAsync(phone, request.OperationId, before.LastContiguousSeq);
        rest.Events.Should().Contain(item => item.Text != null && item.Text.Contains("depois-da-queda"));
        rest.Events.Should().NotContain(item => item.Seq <= before.LastContiguousSeq, "o controlador continua de onde parou");
        (await EventsAsync(phone, request.OperationId)).Events.Select(item => item.Seq).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    /// <summary>
    /// 4.6, agente reiniciado (4.5): o que rodava quando ele caiu não tem
    /// como continuar a saída, e vira "Interrompido", com a etapa. Nada se
    /// repete sozinho.
    /// </summary>
    [Fact]
    public async Task Chaos_AnAgentThatFellMidOperation_EndsItAsInterrupted_WhenItComesBack()
    {
        var (notebook, phone, phoneKey) = await PairedAsync();
        using var _ = phoneKey;
        var slow = DevelopmentCommand.Create("@demorado", Sequence("echo comecou", Pause(60)), null, DateTimeOffset.UtcNow);
        slow.AllowRemote(true);
        var (taskId, developmentId) = await ReadyEnvironmentAsync(notebook, slow);
        var request = await RequestAsync(notebook, phone, phoneKey, RemoteOperationKinds.CommandRun, Run(developmentId, slow), taskId);
        await notebook.Services.GetRequiredService<RemoteOperationDispatcher>().PollAsync(Ct);
        await WaitForAsync(notebook, phone, request.OperationId, page => page.Events.Any(item => item.Text?.Contains("comecou") == true));

        // O processo caiu sem fechar nada: outro agente sobe sobre o mesmo banco e o mesmo cofre.
        var reborn = await SyncedDesktop.CreateAsync(_api, "Notebook", Ct, notebook.Folder, notebook.Device, notebook.Secrets, new Network());
        _desktops.Insert(0, reborn);
        await reborn.Services.GetRequiredService<RemoteOperationDispatcher>().RecoverAsync(Ct);

        var operation = await SendAsync<RemoteOperationView>(phone, HttpMethod.Get, RemoteRoutes.Operation(request.OperationId), null);
        operation.Status.Should().Be(RemoteOperationStatus.Interrupted);
        operation.ResultCode.Should().Be(RemoteErrorCodes.Interrupted);
        operation.ResultSummary.Should().StartWith("O agente parou");

        await reborn.Services.GetRequiredService<RemoteOperationDispatcher>().PollAsync(Ct);
        (await SendAsync<RemoteOperationView>(phone, HttpMethod.Get, RemoteRoutes.Operation(request.OperationId), null))
            .Status.Should().Be(RemoteOperationStatus.Interrupted, "repetir é outra operação, pedida pela pessoa");
    }

    /// <summary>4.6, processo morto de fora: a operação falha com o motivo, e a árvore não fica rodando.</summary>
    [Fact]
    public async Task Chaos_ACommandKilledFromOutside_FailsWithTheReason()
    {
        var (notebook, phone, phoneKey) = await PairedAsync();
        using var _ = phoneKey;

        // O processo que espera grava o próprio PID: no sh, o exec faz o sleep herdar o PID do shell.
        var victim = DevelopmentCommand.Create(
            "@vitima",
            OperatingSystem.IsWindows()
                ? "powershell -NoProfile -Command \"$PID | Out-File -Encoding ascii pid.txt; Start-Sleep -Seconds 120\""
                : "echo $$ > pid.txt; exec sleep 120",
            null,
            DateTimeOffset.UtcNow);
        victim.AllowRemote(true);
        var (taskId, developmentId) = await ReadyEnvironmentAsync(notebook, victim);
        var worktree = (await DevelopmentAsync(notebook, taskId)).WorktreePath!;
        var request = await RequestAsync(notebook, phone, phoneKey, RemoteOperationKinds.CommandRun, Run(developmentId, victim), taskId);
        await notebook.Services.GetRequiredService<RemoteOperationDispatcher>().PollAsync(Ct);

        var pidFile = Path.Combine(worktree, "pid.txt");
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (!File.Exists(pidFile) || (await File.ReadAllTextAsync(pidFile, Ct)).Trim().Length == 0)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "o comando devia ter começado");
            await Task.Delay(200, Ct);
        }

        using (var process = Process.GetProcessById(int.Parse((await File.ReadAllTextAsync(pidFile, Ct)).Trim(), System.Globalization.CultureInfo.InvariantCulture)))
        {
            process.Kill();
        }

        var operation = await FinishedAsync(phone, request.OperationId);
        operation.Status.Should().Be(RemoteOperationStatus.Failed);
        operation.ResultSummary.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>4.6, aprovação sem resposta: vence no prazo e vale negar; o comando não roda.</summary>
    [Fact]
    public async Task Chaos_AnApprovalNobodyAnswers_IsDeniedAtTheDeadline()
    {
        var (notebook, phone, phoneKey) = await PairedAsync();
        using var _ = phoneKey;
        var confirm = DevelopmentCommand.Create(
            "@confirmar", "echo nao-devia-rodar", null, new DevelopmentCommandSettings("Confirmar", RequiresConfirmation: true), DateTimeOffset.UtcNow);
        confirm.AllowRemote(true);
        var (taskId, developmentId) = await ReadyEnvironmentAsync(notebook, confirm);
        var request = await RequestAsync(notebook, phone, phoneKey, RemoteOperationKinds.CommandRun, Run(developmentId, confirm), taskId);
        await notebook.Services.GetRequiredService<RemoteOperationDispatcher>().PollAsync(Ct);
        await PendingApprovalAsync(phone, request.OperationId);

        // Ninguém responde; o prazo passa no relógio do notebook, que é quem decide.
        var clock = (FakeTimeProvider)notebook.Services.GetRequiredService<TimeProvider>();
        clock.Advance(RemoteLimits.DefaultApprovalLifetime);
        var operation = await FinishedAsync(phone, request.OperationId, clock);

        operation.Status.Should().Be(RemoteOperationStatus.Failed);
        operation.ResultCode.Should().Be(RemoteErrorCodes.ApprovalExpired);
        (await EventsAsync(phone, request.OperationId)).Events.Should().NotContain(item => item.Text != null && item.Text.Contains("nao-devia-rodar"));
    }

    /// <summary>4.6, dispositivo revogado: o agente para de aceitar, encerra o que roda e avisa na sincronização desta máquina.</summary>
    [Fact]
    public async Task Chaos_ARevokedMachine_StopsWhatRuns_AndStopsAccepting()
    {
        var (notebook, phone, phoneKey) = await PairedAsync();
        using var _ = phoneKey;
        var slow = DevelopmentCommand.Create("@demorado", Sequence("echo comecou", Pause(60), "echo nao-devia-chegar"), null, DateTimeOffset.UtcNow);
        slow.AllowRemote(true);
        var (taskId, developmentId) = await ReadyEnvironmentAsync(notebook, slow);
        var dispatcher = notebook.Services.GetRequiredService<RemoteOperationDispatcher>();
        var finished = new TaskCompletionSource<RemoteOperationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Finished += (_, status) => finished.TrySetResult(status);
        var scheduler = notebook.Services.GetRequiredService<RemoteOperationScheduler>();

        var request = await RequestAsync(notebook, phone, phoneKey, RemoteOperationKinds.CommandRun, Run(developmentId, slow), taskId);
        await scheduler.TickAsync();
        await WaitForAsync(notebook, phone, request.OperationId, page => page.Events.Any(item => item.Text?.Contains("comecou") == true));

        // Na conta, pelo celular: "revogar o Notebook".
        await phone.SendAsync<object, DeviceInfo>(HttpMethod.Post, ApiRoutes.RevokeDevice(notebook.Device.DeviceId), new { });
        await scheduler.TickAsync();

        (await finished.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct)).Should().NotBe(RemoteOperationStatus.Succeeded);
        dispatcher.RunningCount.Should().Be(0);
        (await notebook.StateAsync(Ct)).State.Should().Be(SyncLinkState.DeviceRevoked);

        // Revogado, ele não busca mais nada.
        var calls = notebook.Net.Calls.Count;
        await scheduler.TickAsync();
        notebook.Net.Calls.Should().HaveCount(calls);
    }

    /// <summary>4.6, duas máquinas com o mesmo banco: a cópia não aceita pedidos, nem os da máquina de onde veio.</summary>
    [Fact]
    public async Task Chaos_ADatabaseCopiedToAnotherComputer_DoesNotTakeTheOperations()
    {
        var (notebook, phone, phoneKey) = await PairedAsync();
        using var _ = phoneKey;
        var request = await RequestAsync(notebook, phone, phoneKey);
        await notebook.DisposeAsync();

        var folder = Path.Combine(Path.GetTempPath(), $"mytaskapp-e2e-{Guid.NewGuid():N}");
        CopyDirectory(notebook.Folder, folder);
        var copy = await SyncedDesktop.CreateAsync(_api, "Outro computador", Ct, folder, new TestDevice("Outro computador"), notebook.Secrets);
        _desktops.Add(copy);

        await copy.Services.GetRequiredService<RemoteOperationScheduler>().TickAsync();

        (await SendAsync<RemoteOperationView>(phone, HttpMethod.Get, RemoteRoutes.Operation(request.OperationId), null))
            .Status.Should().Be(RemoteOperationStatus.Queued, "a cópia não pega o pedido do notebook");
        (await copy.StateAsync(Ct)).LastErrorCode.Should().Be(RemoteOperationScheduler.DeviceChangedCode);
        copy.Net.Calls.Should().BeEmpty("não fala com o servidor em nome do notebook");
    }

    /// <summary>Relógio adiantado no notebook: o pedido parece vencido lá, e é recusado em vez de rodar.</summary>
    [Fact]
    public async Task Chaos_AMachineWithItsClockAhead_RefusesInsteadOfRunning()
    {
        var (notebook, phone, phoneKey) = await PairedAsync();
        using var _ = phoneKey;
        var request = await RequestAsync(notebook, phone, phoneKey);
        ((FakeTimeProvider)notebook.Services.GetRequiredService<TimeProvider>()).Advance(RemoteLimits.DefaultAcceptanceWindow + TimeSpan.FromMinutes(1));

        await notebook.Services.GetRequiredService<RemoteOperationDispatcher>().PollAsync(Ct);
        var operation = await FinishedAsync(phone, request.OperationId);

        operation.Status.Should().Be(RemoteOperationStatus.Rejected);
        operation.ResultCode.Should().Be(RemoteErrorCodes.AlreadyExpired);
        (await EventsAsync(phone, request.OperationId)).Events.Should().NotContain(item => item.Kind == RemoteEventKind.Result);
    }

    /// <summary>O servidor com relógio de mentira, para o teste fazer o prazo passar.</summary>
    private async Task<FakeTimeProvider> UseServerClockAsync()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await _api.DisposeAsync();
        _api = new ApiFactory(await postgres.CreateDatabaseAsync(Ct), testServices: services => services.AddSingleton<TimeProvider>(clock));
        return clock;
    }

    private static Task<RemoteEventPage> EventsAsync(DesktopSession phone, Guid operationId, long after = 0) =>
        SendAsync<RemoteEventPage>(phone, HttpMethod.Get, $"{RemoteRoutes.OperationEvents(operationId)}?after={after}", null);

    /// <summary>Espera a saída chegar ao servidor; o envio do notebook junta os eventos no relógio dele, que o teste anda.</summary>
    private static async Task<RemoteEventPage> WaitForAsync(SyncedDesktop notebook, DesktopSession phone, Guid operationId, Func<RemoteEventPage, bool> condition)
    {
        var clock = (FakeTimeProvider)notebook.Services.GetRequiredService<TimeProvider>();
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (true)
        {
            clock.Advance(RemoteOperationDispatcher.MinEventSendInterval);

            var page = await EventsAsync(phone, operationId);

            if (condition(page))
            {
                return page;
            }

            DateTime.UtcNow.Should().BeBefore(deadline, "a saída devia ter chegado");
            await Task.Delay(200, Ct);
        }
    }

    /// <summary>
    /// Os passos um depois do outro, no shell da máquina: o <c>cmd</c> no
    /// Windows e o <c>/bin/sh</c> no Linux (o CI). No <c>sh</c>, o <c>&amp;</c>
    /// do <c>cmd</c> manda o passo para o fundo, e tudo roda junto.
    /// </summary>
    private static string Sequence(params string[] steps) => string.Join(OperatingSystem.IsWindows() ? " & " : "; ", steps);

    /// <summary>Espera, sem ler a entrada: o <c>timeout</c> do Windows recusa a entrada redirecionada, e o <c>ping</c> do Linux sem <c>-c</c> não para.</summary>
    private static string Pause(int seconds) =>
        OperatingSystem.IsWindows()
            ? $"ping -n {seconds + 1} 127.0.0.1 >nul"
            : $"sleep {seconds}";

    private static RemoteOperationEvent Output(Guid operationId, long seq) =>
        new(operationId, seq, DateTimeOffset.UtcNow, RemoteEventKind.Output, Text: $"linha {seq}");

    private static void CopyDirectory(string from, string to)
    {
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(from, to, StringComparison.Ordinal));
        }

        Directory.CreateDirectory(to);

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal));
        }
    }

    /// <summary>O lado do celular de <see cref="IRemoteOperationsApi"/>, pela sessão dele: o mesmo caso de uso das telas lê os eventos.</summary>
    private sealed class PhoneOperations(DesktopSession phone) : IRemoteOperationsApi
    {
        public Task<RemoteOperationView> GetAsync(Guid operationId, CancellationToken cancellationToken) =>
            SendAsync<RemoteOperationView>(phone, HttpMethod.Get, RemoteRoutes.Operation(operationId), null);

        public Task<RemoteEventPage> ListEventsAsync(Guid operationId, long after, int? limit, CancellationToken cancellationToken) =>
            EventsAsync(phone, operationId, after);

        public Task<IReadOnlyList<AgentOperation>> ListAgentOperationsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RemoteOperationView> UpdateStatusAsync(Guid operationId, OperationStatusUpdate update, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EventBatchAck> AppendEventsAsync(EventBatch batch, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RemoteOperationView> CreateAsync(SignedEnvelope request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RemoteOperationView> CancelAsync(Guid operationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AgentCatalog> PublishCatalogAsync(AgentCatalog catalog, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AgentCatalog> GetCatalogAsync(Guid deviceId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ApprovalView> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ApprovalView> GetApprovalAsync(Guid approvalId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ApprovalView>> ListPendingApprovalsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ApprovalView> RespondApprovalAsync(Guid approvalId, SignedEnvelope response, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
