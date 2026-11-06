using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>
/// O agente no relay do terminal (ADR-099): publica a saída e o estado de uma
/// conexão, e ouve a entrada que chega para ela. O que chega é só repassado
/// pelo servidor; quem confere é o <see cref="RemoteTerminalBridge"/>.
/// </summary>
public interface IRemoteTerminalLink
{
    /// <summary>Um lote de entrada para a conexão (attachId, envelope), ainda sem conferir.</summary>
    event Action<Guid, SignedEnvelope>? InputReceived;

    /// <summary>Quem seguia a conexão saiu.</summary>
    event Action<Guid>? Detached;

    /// <summary>A ligação com o servidor caiu: para ele, toda conexão de terminal acabou.</summary>
    event Action? Lost;

    Task PublishAsync(RemoteTerminalFrame frame, CancellationToken cancellationToken);

    Task CloseAsync(Guid attachId, string reason, CancellationToken cancellationToken);
}

/// <summary>Sem hub (desktop, celular, testes): nada é publicado.</summary>
public sealed class NoRemoteTerminalLink : IRemoteTerminalLink
{
    public static readonly NoRemoteTerminalLink Instance = new();

    public event Action<Guid, SignedEnvelope>? InputReceived
    {
        add { }
        remove { }
    }

    public event Action<Guid>? Detached
    {
        add { }
        remove { }
    }

    public event Action? Lost
    {
        add { }
        remove { }
    }

    public Task PublishAsync(RemoteTerminalFrame frame, CancellationToken cancellationToken) =>
        throw new DomainException("Esta máquina não está ligada ao servidor para o terminal remoto.");

    public Task CloseAsync(Guid attachId, string reason, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>As sessões do terminal no servidor: o agente relata, os controladores leem.</summary>
public interface IRemoteTerminalsApi
{
    Task<RemoteTerminalSessionAck> ReportTerminalsAsync(RemoteTerminalSessionBatch batch, CancellationToken cancellationToken);

    Task<RemoteTerminalSessionList> ListTerminalsAsync(CancellationToken cancellationToken);
}

/// <summary>O terminal do agente, entre o que o host conhece e o que vai pelo fio.</summary>
public static class RemoteTerminalMapping
{
    /// <summary>O estado para uma conexão de longe: sem a pasta, com os textos mascarados e curtos.</summary>
    public static RemoteTerminalState ToRemote(TerminalSessionStatus status, Guid viewerId) => new(
        status.SessionId,
        status.TaskId,
        status.DevelopmentId,
        status.ProcessId,
        status.StartedAt,
        status.Size.Columns,
        status.Size.Rows,
        ProcessOf(status.Process),
        status.ExitCode,
        ActivityOf(status.Activity),
        Clip(status.ActivityMessage, RemoteLimits.MaxSessionActivityLength),
        status.Dialog is { } dialog
            ? new RemoteTerminalDialog(dialog.Id, DialogKindOf(dialog.Kind), Clip(dialog.Message, RemoteLimits.MaxResultSummaryLength) ?? string.Empty, dialog.OpenedAt)
            : null,
        status.Lease?.ClientId,
        [.. status.Clients.Select(client => new RemoteTerminalViewer(client.Id, ViewerKindOf(client.Kind), Clip(client.Label, AgentSession.MaxRemoteOriginLength) ?? string.Empty, client.AttachedAt))],
        status.Monitored,
        status.PermissionsBypassed,
        Clip(status.PermissionMode, 40),
        viewerId);

    /// <summary>Do fio para a vista do terminal: o mesmo estado que a vista local recebe, sem a pasta.</summary>
    public static TerminalSessionStatus FromRemote(RemoteTerminalState state)
    {
        var viewers = state.Viewers.Select(viewer => new TerminalClientView(viewer.Id, ClientKindOf(viewer.Kind), viewer.Label, viewer.AttachedAt)).ToList();
        var holder = state.ControlledBy is { } controlledBy ? viewers.FirstOrDefault(viewer => viewer.Id == controlledBy) : null;

        return new TerminalSessionStatus(
            state.SessionId,
            state.TaskId,
            state.DevelopmentId,
            WorkingDirectory: string.Empty,
            state.ProcessId,
            state.StartedAt,
            new TerminalSize(state.Columns, state.Rows).Clamp(),
            state.Process switch
            {
                RemoteTerminalProcess.Running => TerminalProcessState.Running,
                RemoteTerminalProcess.Exited => TerminalProcessState.Exited,
                _ => TerminalProcessState.Crashed,
            },
            state.ExitCode,
            state.Activity switch
            {
                RemoteAgentActivity.Working => AgentActivity.Working,
                RemoteAgentActivity.WaitingForUser => AgentActivity.WaitingForUser,
                RemoteAgentActivity.WaitingReview => AgentActivity.WaitingReview,
                RemoteAgentActivity.Failed => AgentActivity.Failed,
                _ => AgentActivity.Unknown,
            },
            state.ActivityMessage,
            state.Dialog is { } dialog
                ? new TerminalDialog(dialog.Id, dialog.Kind switch
                {
                    "permission" => TerminalDialogKind.Permission,
                    "question" => TerminalDialogKind.Question,
                    "plan" => TerminalDialogKind.Plan,
                    _ => TerminalDialogKind.Input,
                }, dialog.Message, dialog.OpenedAt)
                : null,
            holder is null ? null : new TerminalLease(holder.Id, holder.Kind, holder.Label, holder.AttachedAt),
            viewers,
            state.Monitored,
            state.PermissionsBypassed,
            state.PermissionMode,
            LastOutputAt: null,
            state.YourViewerId);
    }

    /// <param name="withContent">
    /// Com "Aceitar o terminal remoto": vai também o texto do Claude. Sem ele,
    /// só o estado (ADR-103): quem vê de longe sabe que o Claude espera, não o quê.
    /// </param>
    public static RemoteTerminalSessionReport ToReport(TerminalSessionStatus status, DateTimeOffset updatedAt, bool withContent = true) => new(
        status.SessionId,
        status.TaskId,
        status.DevelopmentId,
        ProcessOf(status.Process),
        status.ExitCode,
        ActivityOf(status.Activity),
        withContent ? Clip(status.ActivityMessage, RemoteLimits.MaxSessionActivityLength) : null,
        HasDialog: status.Dialog is not null,
        status.Monitored,
        status.PermissionsBypassed,
        RemotelyControlled: withContent && status.Lease?.Kind == TerminalClientKind.Remote,
        status.StartedAt,
        updatedAt);

    private static RemoteTerminalProcess ProcessOf(TerminalProcessState process) => process switch
    {
        TerminalProcessState.Running => RemoteTerminalProcess.Running,
        TerminalProcessState.Exited => RemoteTerminalProcess.Exited,
        _ => RemoteTerminalProcess.Crashed,
    };

    private static RemoteAgentActivity ActivityOf(AgentActivity activity) => activity switch
    {
        AgentActivity.Working => RemoteAgentActivity.Working,
        AgentActivity.WaitingForUser => RemoteAgentActivity.WaitingForUser,
        AgentActivity.WaitingReview => RemoteAgentActivity.WaitingReview,
        AgentActivity.Failed => RemoteAgentActivity.Failed,
        _ => RemoteAgentActivity.Unknown,
    };

    private static string DialogKindOf(TerminalDialogKind kind) => kind switch
    {
        TerminalDialogKind.Permission => "permission",
        TerminalDialogKind.Question => "question",
        TerminalDialogKind.Plan => "plan",
        _ => "input",
    };

    private static string ViewerKindOf(TerminalClientKind kind) => kind switch
    {
        TerminalClientKind.NativeWindow => "window",
        TerminalClientKind.Embedded => "app",
        _ => "remote",
    };

    private static TerminalClientKind ClientKindOf(string kind) => kind switch
    {
        "window" => TerminalClientKind.NativeWindow,
        "app" => TerminalClientKind.Embedded,
        _ => TerminalClientKind.Remote,
    };

    /// <summary>Sem segredo à vista (o comando do diálogo pode trazer um), e curto.</summary>
    private static string? Clip(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var masked = new string([.. SensitiveText.Mask(text).Where(character => !char.IsControl(character))]);
        return masked.Length > max ? $"{masked[..(max - 1)]}…" : masked;
    }
}

/// <summary>Quem pode usar o terminal de longe agora: a chave ligada e o controlador ainda pareado.</summary>
public sealed record CheckRemoteTerminalAccess(Guid ControllerDeviceId);

public sealed class CheckRemoteTerminalAccessHandler(IRemoteSettingsStore settings, RemoteTrust trust)
{
    public const string ControlOffMessage = "Esta máquina parou de aceitar controle remoto.";

    public const string TerminalOffMessage = "Esta máquina não aceita o terminal remoto. Ligue \"Aceitar o terminal remoto\" em Máquinas, nela.";

    public const string NotPairedMessage = "Este controlador não está mais pareado com esta máquina.";

    /// <summary>O motivo da recusa, ou <c>null</c>.</summary>
    public async Task<string?> HandleAsync(CheckRemoteTerminalAccess query, CancellationToken cancellationToken = default)
    {
        var current = await settings.GetAsync(cancellationToken);

        if (!current.AcceptControl)
        {
            return ControlOffMessage;
        }

        if (!current.AcceptTerminal)
        {
            return TerminalOffMessage;
        }

        return (await trust.ListAsync(cancellationToken)).Any(controller => controller.ControllerDeviceId == query.ControllerDeviceId)
            ? null
            : NotPairedMessage;
    }
}

/// <summary>
/// As conexões de longe às sessões do terminal do agente (ADR-099). Cada uma
/// é um cliente <see cref="TerminalClientKind.Remote"/> do host, como a janela
/// nativa e a vista do app são: vê a mesma tela e segue a mesma vez de digitar.
/// </summary>
/// <remarks>
/// <para>
/// <b>A entrada é conferida aqui</b>, lote a lote, na ordem: a assinatura com a
/// chave da conexão (<see cref="RemoteTerminalInputVerifier"/>) e, de novo a
/// cada lote, "Aceitar controle", "Aceitar o terminal remoto" e o pareamento.
/// Desligar a chave no meio recusa o próximo lote e encerra a conexão.
/// </para>
/// <para>
/// <b>A saída vai em pedaços</b> de até <see cref="RemoteLimits.MaxTerminalFrameData"/>,
/// juntando o que chega em <see cref="BatchWindow"/>: uma tela que repinta não
/// vira centenas de mensagens. O retrato de entrada leva pouco histórico
/// (<see cref="SnapshotScrollback"/>), para caber no teto do servidor.
/// </para>
/// </remarks>
public sealed partial class RemoteTerminalBridge : IDisposable
{
    /// <summary>Quantas conexões de longe ao mesmo tempo, somando as sessões.</summary>
    public const int MaxConnections = 8;

    /// <summary>As linhas de histórico do retrato que vai de longe.</summary>
    public const int SnapshotScrollback = 300;

    /// <summary>Quanto a saída espera para juntar mais antes de sair.</summary>
    public static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(15);

    /// <summary>Quanto vale a conferência da chave e do pareamento entre um lote e outro.</summary>
    public static readonly TimeSpan AccessLifetime = TimeSpan.FromSeconds(1);

    private const int MaxBatchBytes = 256 * 1024;

    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();

    private readonly ITerminalSessions _terminals;

    private readonly IRemoteTerminalLink _link;

    private readonly IUseCaseRunner _runner;

    private readonly TimeProvider _time;

    private readonly ILogger<RemoteTerminalBridge> _logger;

    public RemoteTerminalBridge(ITerminalSessions terminals, IRemoteTerminalLink link, IUseCaseRunner runner, TimeProvider time, ILogger<RemoteTerminalBridge> logger)
    {
        _terminals = terminals;
        _link = link;
        _runner = runner;
        _time = time;
        _logger = logger;
        _link.InputReceived += OnInput;
        _link.Detached += OnDetached;
        _link.Lost += OnLost;
    }

    public int Count => _connections.Count;

    /// <summary>
    /// Liga a conexão pedida em <c>terminal.attach</c>. A operação termina na
    /// hora; a conexão segue até um dos lados sair, ou a sessão acabar.
    /// </summary>
    public async Task<RemoteOperationResult> OpenAsync(RemoteExecution execution, CancellationToken cancellationToken)
    {
        var request = execution.Payload<RemoteTerminalAttachRequest>();

        if (_terminals.Find(request.SessionId) is not { IsRunning: true } host || host.Info.DevelopmentId != request.DevelopmentId)
        {
            return RemoteOperationResult.Failure(RemoteErrorCodes.PolicyRefused, "Esta sessão não está rodando no terminal do agente desta máquina.");
        }

        if (!request.ViewerKey.IsWellFormedEs256())
        {
            return RemoteOperationResult.Failure(RemoteErrorCodes.InvalidPayload, "A chave da conexão não é válida.");
        }

        if (_connections.Count >= MaxConnections)
        {
            return RemoteOperationResult.Failure(RemoteErrorCodes.TooManyOperations, $"Já há {MaxConnections} conexões de longe ao terminal desta máquina.");
        }

        var attachId = execution.Request.OperationId;
        var label = execution.ControllerNickname == RemoteTrust.OwnNickname ? "Este computador, pelo servidor" : execution.ControllerNickname;
        var client = host.Attach(TerminalClientKind.Remote, label, SnapshotScrollback);
        var connection = new Connection(
            new RemoteTerminalExpectation(attachId, execution.Request.TargetDeviceId, request.SessionId, request.ViewerKey),
            execution.Request.RequestedByDeviceId,
            client);

        if (!_connections.TryAdd(attachId, connection))
        {
            client.Dispose();
            return RemoteOperationResult.Failure(RemoteErrorCodes.AlreadySeen, "Esta conexão já está ligada.");
        }

        if (new TerminalSize(request.Columns, request.Rows) is { IsValid: true } size)
        {
            try
            {
                await client.SendAsync(new TerminalResize(size), cancellationToken);
            }
            catch (DomainException)
            {
                // Outro dita o tamanho: a vista de longe acompanha o dele.
            }
        }

        connection.Pump = Task.Run(() => PumpAsync(connection), CancellationToken.None);
        LogOpened(attachId, request.SessionId);

        return RemoteOperationResult.Success(
            "Conectado ao terminal.",
            data: JsonSerializer.SerializeToElement(new { sessionId = request.SessionId, attachId }));
    }

    /// <summary>Encerra todas as conexões (o agente fechando, a chave desligada).</summary>
    public async Task CloseAllAsync(string reason)
    {
        foreach (var attachId in _connections.Keys)
        {
            await CloseAsync(attachId, reason, notify: true);
        }
    }

    public void Dispose()
    {
        _link.InputReceived -= OnInput;
        _link.Detached -= OnDetached;
        _link.Lost -= OnLost;

        foreach (var connection in _connections.Values)
        {
            connection.Client.Dispose();
            connection.Gate.Dispose();
        }

        _connections.Clear();
    }

    /// <summary>Os testes esperam a entrada chegar ao host.</summary>
    internal Task HandleInputAsync(Guid attachId, SignedEnvelope envelope) =>
        _connections.TryGetValue(attachId, out var connection) ? InputAsync(connection, envelope) : Task.CompletedTask;

    private void OnInput(Guid attachId, SignedEnvelope envelope) => _ = HandleInputAsync(attachId, envelope);

    private void OnDetached(Guid attachId) => _ = CloseAsync(attachId, reason: null, notify: false);

    private void OnLost()
    {
        foreach (var attachId in _connections.Keys)
        {
            _ = CloseAsync(attachId, reason: null, notify: false);
        }
    }

    private async Task InputAsync(Connection connection, SignedEnvelope envelope)
    {
        var attachId = connection.Expectation.AttachId;

        try
        {
            await connection.Gate.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (!_connections.ContainsKey(attachId))
            {
                return;
            }

            if (await AccessAsync(connection) is { } denied)
            {
                LogRefused(attachId, "access");
                await RefuseAsync(connection, denied);
                await CloseAsync(attachId, denied, notify: true);
                return;
            }

            var (input, sequence, problem) = RemoteTerminalInputVerifier.Verify(envelope, connection.Expectation, connection.LastSequence, _time.GetUtcNow());

            if (sequence != long.MinValue)
            {
                connection.LastSequence = sequence;
            }

            if (input is null)
            {
                LogRefused(attachId, "input");
                await RefuseAsync(connection, problem!);
                return;
            }

            try
            {
                await connection.Client.SendAsync(input);
            }
            catch (DomainException exception)
            {
                await RefuseAsync(connection, exception.Message);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // O conteúdo nunca vai para o log: só o tipo.
            LogInputFailed(attachId, exception.GetType().Name);
        }
        finally
        {
            try
            {
                connection.Gate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task<string?> AccessAsync(Connection connection)
    {
        var now = _time.GetUtcNow();

        if (connection.AccessCheckedAt is { } at && now - at < AccessLifetime)
        {
            return null;
        }

        var denied = await _runner.RunAsync<CheckRemoteTerminalAccessHandler, string?>(
            (handler, token) => handler.HandleAsync(new CheckRemoteTerminalAccess(connection.ControllerDeviceId), token),
            CancellationToken.None);

        connection.AccessCheckedAt = denied is null ? now : null;
        return denied;
    }

    private async Task RefuseAsync(Connection connection, string problem)
    {
        try
        {
            await _link.PublishAsync(new RemoteTerminalFrame(connection.Expectation.AttachId, RemoteTerminalFrameKinds.Refusal, Text: problem), CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogPublishFailed(connection.Expectation.AttachId, exception.GetType().Name);
        }
    }

    private async Task CloseAsync(Guid attachId, string? reason, bool notify)
    {
        if (!_connections.TryRemove(attachId, out var connection))
        {
            return;
        }

        connection.Client.Dispose();
        LogClosed(attachId, notify ? "agent" : "follower");

        if (notify)
        {
            try
            {
                await _link.CloseAsync(attachId, reason ?? "A conexão com o terminal foi encerrada.", CancellationToken.None);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogPublishFailed(attachId, exception.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Do host para o fio: junta a saída que chega junto, corta no teto do
    /// quadro e manda o estado como ele está. Acabou a sessão, ou o fio caiu,
    /// a conexão acaba.
    /// </summary>
    private async Task PumpAsync(Connection connection)
    {
        var attachId = connection.Expectation.AttachId;
        var client = connection.Client;
        var reason = "A sessão do terminal terminou.";
        TerminalMessage? next = null;

        try
        {
            while ((next ?? await client.ReadAsync()) is { } message)
            {
                next = null;

                switch (message)
                {
                    case TerminalOutput output:
                        (var batch, next) = await GatherAsync(client, output);
                        await PublishOutputAsync(attachId, batch);
                        break;
                    case TerminalStateMessage state:
                        await _link.PublishAsync(
                            new RemoteTerminalFrame(attachId, RemoteTerminalFrameKinds.State, State: RemoteTerminalMapping.ToRemote(state.Status, client.Id)),
                            CancellationToken.None);
                        break;
                    case TerminalNotice notice:
                        await _link.PublishAsync(new RemoteTerminalFrame(attachId, RemoteTerminalFrameKinds.Notice, Text: notice.Text), CancellationToken.None);
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            reason = "O agente perdeu a ligação com o servidor.";
            LogPublishFailed(attachId, exception.GetType().Name);
        }

        await CloseAsync(attachId, reason, notify: true);
    }

    /// <summary>A saída contígua que chega dentro da janela, até o teto; o que não cabe volta como próxima mensagem.</summary>
    private async Task<(TerminalOutput Batch, TerminalMessage? Next)> GatherAsync(TerminalClient client, TerminalOutput first)
    {
        // O retrato vai sozinho: o fim dele é o deslocamento dele, e juntar saída nova mudaria isso.
        if (first.Reset)
        {
            return (first, null);
        }

        using var buffer = new MemoryStream();
        buffer.Write(first.Data);
        using var window = new CancellationTokenSource(BatchWindow, _time);

        while (buffer.Length < MaxBatchBytes)
        {
            TerminalMessage? more;

            try
            {
                more = await client.ReadAsync(window.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (more is TerminalOutput { Reset: false } output && output.Offset == first.Offset + buffer.Length)
            {
                buffer.Write(output.Data);
                continue;
            }

            return (first with { Data = buffer.ToArray() }, more);
        }

        return (first with { Data = buffer.ToArray() }, null);
    }

    private async Task PublishOutputAsync(Guid attachId, TerminalOutput output)
    {
        var data = output.Data;
        var start = 0;

        // O retrato vazio também vai: é ele que limpa a tela de quem segue.
        do
        {
            var length = Math.Min(RemoteLimits.MaxTerminalFrameData, data.Length - start);

            await _link.PublishAsync(
                new RemoteTerminalFrame(
                    attachId,
                    RemoteTerminalFrameKinds.Output,
                    // Os pedaços do retrato levam o deslocamento dele; os da saída, cada um o seu.
                    output.Reset ? output.Offset : output.Offset + start,
                    data.AsSpan(start, length).ToArray(),
                    Reset: output.Reset && start == 0,
                    More: start + length < data.Length),
                CancellationToken.None);

            start += length;
        }
        while (start < data.Length);
    }

    [LoggerMessage(EventId = 9861, Level = LogLevel.Information, Message = "RemoteTerminalOpened {AttachId} {SessionId}")]
    private partial void LogOpened(Guid attachId, Guid sessionId);

    [LoggerMessage(EventId = 9862, Level = LogLevel.Information, Message = "RemoteTerminalClosed {AttachId} {By}")]
    private partial void LogClosed(Guid attachId, string by);

    [LoggerMessage(EventId = 9863, Level = LogLevel.Warning, Message = "RemoteTerminalRefused {AttachId} {Check}")]
    private partial void LogRefused(Guid attachId, string check);

    [LoggerMessage(EventId = 9864, Level = LogLevel.Warning, Message = "RemoteTerminalPublishFailed {AttachId} {Error}")]
    private partial void LogPublishFailed(Guid attachId, string error);

    [LoggerMessage(EventId = 9865, Level = LogLevel.Warning, Message = "RemoteTerminalInputFailed {AttachId} {Error}")]
    private partial void LogInputFailed(Guid attachId, string error);

    private sealed class Connection(RemoteTerminalExpectation expectation, Guid controllerDeviceId, TerminalClient client)
    {
        public RemoteTerminalExpectation Expectation { get; } = expectation;

        public Guid ControllerDeviceId { get; } = controllerDeviceId;

        public TerminalClient Client { get; } = client;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public long LastSequence { get; set; }

        public DateTimeOffset? AccessCheckedAt { get; set; }

        public Task? Pump { get; set; }
    }
}

/// <summary>
/// O que os três executores do terminal conferem antes de tudo: a chave
/// "Aceitar o terminal remoto" ligada e o terminal do agente nesta máquina.
/// </summary>
internal static class RemoteTerminalPolicy
{
    public static async Task<RemoteRefusal?> CheckAsync(IUseCaseRunner runner, ITerminalSessions terminals, CancellationToken cancellationToken)
    {
        if (!terminals.IsHost || !terminals.Support.IsSupported)
        {
            return new RemoteRefusal(RemoteErrorCodes.TerminalNotAccepted, terminals.Support.Reason ?? "Esta máquina não tem o terminal do agente.");
        }

        var settings = await runner.RunAsync<IRemoteSettingsStore, RemoteSettings>((store, token) => store.GetAsync(token), cancellationToken);

        return settings.AcceptTerminal
            ? null
            : new RemoteRefusal(RemoteErrorCodes.TerminalNotAccepted, CheckRemoteTerminalAccessHandler.TerminalOffMessage);
    }
}

/// <summary>
/// <c>terminal.start</c>: abre o Claude no terminal do agente, no ambiente
/// pedido, com os parâmetros, o modelo e o acompanhamento salvos nesta
/// máquina. De longe não vem texto de argumento nem prompt (ADR-099).
/// </summary>
public sealed class TerminalStartExecutor(IUseCaseRunner runner, ITerminalSessions terminals) : IRemoteOperationExecutor
{
    public string Kind => RemoteOperationKinds.TerminalStart;

    public async Task<RemoteRefusal?> CheckAsync(RemoteOperationRequest request, CancellationToken cancellationToken) =>
        await RemoteTerminalPolicy.CheckAsync(runner, terminals, cancellationToken)
        ?? (request.TaskId is null ? new RemoteRefusal(RemoteErrorCodes.InvalidPayload, "Diga a tarefa do ambiente.") : null);

    public async Task<RemoteOperationResult> ExecuteAsync(RemoteExecution execution, CancellationToken cancellationToken)
    {
        var request = execution.Payload<RemoteTerminalStartRequest>();

        try
        {
            var view = await runner.RunAsync<StartAgentSessionHandler, AgentSessionView>(
                (handler, token) => handler.HandleAsync(
                    new StartAgentSession(
                        execution.Request.TaskId!.Value,
                        request.DevelopmentId,
                        Hosting: AgentHosting.AgentWindow,
                        RemoteOrigin: execution.ControllerNickname == RemoteTrust.OwnNickname ? null : execution.ControllerNickname,
                        OmitPrompt: true),
                    token),
                cancellationToken);

            return view.Status == AgentSessionStatus.Failed
                ? RemoteOperationResult.Failure(RemoteErrorCodes.ExecutionFailed, view.FailureReason ?? "O Claude não abriu no terminal do agente.")
                : RemoteOperationResult.Success(
                    view.MonitoringNote is { } note ? $"O Claude abriu no terminal do agente. {note}" : "O Claude abriu no terminal do agente.",
                    data: JsonSerializer.SerializeToElement(new { sessionId = view.SessionId }));
        }
        catch (DomainException exception)
        {
            return RemoteOperationResult.Failure(RemoteErrorCodes.PolicyRefused, exception.Message);
        }
    }
}

/// <summary><c>terminal.attach</c>: liga a conexão de longe e termina; a conexão vive no <see cref="RemoteTerminalBridge"/>.</summary>
public sealed class TerminalAttachExecutor(IUseCaseRunner runner, ITerminalSessions terminals, RemoteTerminalBridge bridge) : IRemoteOperationExecutor
{
    public string Kind => RemoteOperationKinds.TerminalAttach;

    public Task<RemoteRefusal?> CheckAsync(RemoteOperationRequest request, CancellationToken cancellationToken) =>
        RemoteTerminalPolicy.CheckAsync(runner, terminals, cancellationToken);

    public Task<RemoteOperationResult> ExecuteAsync(RemoteExecution execution, CancellationToken cancellationToken) =>
        bridge.OpenAsync(execution, cancellationToken);
}

/// <summary>
/// <c>terminal.stop</c>: fechar pede ao Claude que saia (como fechar a
/// janela); forçar encerra a árvore de processos da sessão, e só ela.
/// </summary>
public sealed class TerminalStopExecutor(IUseCaseRunner runner, ITerminalSessions terminals) : IRemoteOperationExecutor
{
    public string Kind => RemoteOperationKinds.TerminalStop;

    public Task<RemoteRefusal?> CheckAsync(RemoteOperationRequest request, CancellationToken cancellationToken) =>
        RemoteTerminalPolicy.CheckAsync(runner, terminals, cancellationToken);

    public Task<RemoteOperationResult> ExecuteAsync(RemoteExecution execution, CancellationToken cancellationToken)
    {
        var request = execution.Payload<RemoteTerminalStopRequest>();

        if (terminals.Find(request.SessionId) is not { IsRunning: true } host)
        {
            return Task.FromResult(RemoteOperationResult.Success("A sessão já tinha terminado."));
        }

        var force = request.Mode == RemoteTerminalStopMode.Force;
        host.Stop(force);

        return Task.FromResult(RemoteOperationResult.Success(force ? "O Claude e o que ele abriu foram encerrados." : "O Claude recebeu o pedido para fechar."));
    }
}

/// <summary>
/// O relato das sessões do terminal desta máquina ao servidor (ADR-099), a
/// cada volta do <see cref="RemoteOperationScheduler"/> e logo depois de uma
/// mudança. Vai sempre o lote inteiro: o servidor guarda só em memória, e a
/// sessão que some do lote acabou. Basta "Aceitar controle" para o lote ir
/// (ADR-103): as outras máquinas e o celular mostram o estado na tarefa. Sem
/// "Aceitar o terminal remoto", vai só o estado, sem o texto do Claude, e
/// ninguém segue a tela.
/// </summary>
public sealed partial class RemoteTerminalReporter : IDisposable
{
    public static readonly TimeSpan OldServerBackoff = TimeSpan.FromHours(1);

    /// <summary>Quanto espera depois de uma mudança, para juntar as que vêm juntas.</summary>
    public static readonly TimeSpan ChangeDelay = TimeSpan.FromMilliseconds(500);

    private readonly IRemoteTerminalsApi _api;

    private readonly ITerminalSessions _terminals;

    private readonly IUseCaseRunner _runner;

    private readonly TimeProvider _time;

    private readonly ILogger<RemoteTerminalReporter> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private ITimer? _soon;

    private bool _active;

    private DateTimeOffset _quietUntil = DateTimeOffset.MinValue;

    public RemoteTerminalReporter(IRemoteTerminalsApi api, ITerminalSessions terminals, IUseCaseRunner runner, TimeProvider time, ILogger<RemoteTerminalReporter> logger)
    {
        _api = api;
        _terminals = terminals;
        _runner = runner;
        _time = time;
        _logger = logger;
        _terminals.Changed += OnChanged;
    }

    /// <summary>Quantos relatos o servidor aceitou (os testes esperam por eles).</summary>
    public int Reports { get; private set; }

    /// <summary>A volta do agendador: o agente aceita controle, então relata.</summary>
    public async Task PublishAsync(CancellationToken cancellationToken)
    {
        _active = true;

        if (!_terminals.IsHost || _time.GetUtcNow() < _quietUntil)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            var settings = await _runner.RunAsync<IRemoteSettingsStore, RemoteSettings>((store, token) => store.GetAsync(token), cancellationToken);

            if (!settings.AcceptControl)
            {
                _active = false;
                return;
            }

            var now = _time.GetUtcNow();
            IReadOnlyList<RemoteTerminalSessionReport> sessions = [.. _terminals.List()
                .Select(host => host.Status)
                .OrderByDescending(status => status.IsRunning)
                .ThenByDescending(status => status.StartedAt)
                .Take(RemoteLimits.MaxTerminalSessionsPerBatch)
                .Select(status => RemoteTerminalMapping.ToReport(status, status.LastOutputAt ?? status.StartedAt, withContent: settings.AcceptTerminal))];

            await _api.ReportTerminalsAsync(new RemoteTerminalSessionBatch(sessions), cancellationToken);
            Reports++;
        }
        catch (SyncApiException exception) when (exception is { Kind: SyncFailureKind.NotFound, Code: null })
        {
            // Servidor de antes do terminal remoto: tenta de novo daqui a pouco, sem encher o log.
            _quietUntil = _time.GetUtcNow() + OldServerBackoff;
            LogOldServer();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _terminals.Changed -= OnChanged;
        _soon?.Dispose();
        _gate.Dispose();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (!_active)
        {
            return;
        }

        _soon?.Dispose();
        _soon = _time.CreateTimer(_ => _ = PublishQuietlyAsync(), null, ChangeDelay, Timeout.InfiniteTimeSpan);
    }

    private async Task PublishQuietlyAsync()
    {
        try
        {
            await PublishAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogReportFailed(exception.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 9866, Level = LogLevel.Information, Message = "RemoteTerminalReportOldServer")]
    private partial void LogOldServer();

    [LoggerMessage(EventId = 9867, Level = LogLevel.Warning, Message = "RemoteTerminalReportFailed {Error}")]
    private partial void LogReportFailed(string error);
}
