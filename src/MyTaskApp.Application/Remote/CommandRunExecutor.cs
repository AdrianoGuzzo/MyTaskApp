using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>O comando pedido de longe, resolvido nesta máquina: o botão do ambiente e os valores conferidos.</summary>
public sealed record RemoteCommandPlan(Guid TaskId, Guid DevelopmentId, QuickCommandEntry Entry, IReadOnlyDictionary<string, string> Values)
{
    /// <summary>Como a aprovação e o resultado chamam o comando: o nome e os valores, sem a linha (que tem a pasta).</summary>
    public string Describe() => Values.Count == 0
        ? $"\"{Entry.Name}\""
        : $"\"{Entry.Name}\" ({string.Join(", ", Values.Select(pair => $"{pair.Key} = {pair.Value}"))})";
}

/// <summary>
/// Confere um <c>command.run</c> contra o que está nesta máquina agora
/// (ADR-088): o comando liberado aqui (<c>RemoteAllowed</c>), o worktree
/// pronto, o botão sem o texto ou a pasta trocados no diretório, e os
/// valores. Recusa com a mensagem que a tela de quem pediu mostra.
/// </summary>
/// <remarks>
/// A liberação é do comando, e não do botão: um botão com <c>CommandOverride</c>
/// ou pasta própria roda outra coisa que a liberada, e de longe não roda.
/// </remarks>
public sealed class RemoteCommandPlanner(
    IDevelopmentCommandRepository commands,
    GetQuickCommandsHandler quickCommands,
    PrepareQuickCommandHandler prepare)
{
    /// <summary>
    /// O que um valor de texto pedido de longe não pode ter: o que o shell
    /// lê como outro comando, redirecionamento, variável ou aspas.
    /// </summary>
    public static readonly char[] UnsafeCharacters = [';', '&', '|', '<', '>', '^', '%', '$', '`', '"', '\'', '!', '(', ')', '{', '}'];

    public async Task<RemoteCommandPlan> PlanAsync(Guid? taskId, WorktreeExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (taskId is not { } task)
        {
            throw new DomainException("O pedido não diz de qual tarefa é o ambiente.");
        }

        if (await commands.FindByIdAsync(request.CommandId, cancellationToken) is not { RemoteAllowed: true })
        {
            throw new DomainException("Este comando não está liberado para rodar de longe nesta máquina.");
        }

        var buttons = await quickCommands.HandleAsync(new GetQuickCommands(task, request.DevelopmentId), cancellationToken);
        var button = buttons.Commands.FirstOrDefault(entry => entry.CommandId == request.CommandId && !entry.IsCustomized)
            ?? buttons.Commands.FirstOrDefault(entry => entry.CommandId == request.CommandId);

        if (button is { IsCustomized: true })
        {
            throw new DomainException(
                $"\"{button.Name}\" tem o texto ou a pasta trocados no diretório da etiqueta: de longe, só roda como foi liberado.");
        }

        var plan = await prepare.HandleAsync(
            new PrepareQuickCommand(task, request.DevelopmentId, request.CommandId, button?.BindingId),
            cancellationToken);
        var values = request.Parameters ?? new Dictionary<string, string>();

        foreach (var name in values.Keys)
        {
            if (!plan.Entry.Parameters.Any(parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DomainException($"\"{plan.Entry.Name}\" não tem o parâmetro {name}.");
            }
        }

        foreach (var parameter in plan.Entry.Parameters.Where(parameter => parameter.Type == CommandParameterType.Text))
        {
            var value = values.FirstOrDefault(pair => string.Equals(pair.Key, parameter.Name, StringComparison.OrdinalIgnoreCase)).Value;

            if (value is not null && (value.IndexOfAny(UnsafeCharacters) >= 0 || value.Any(char.IsControl)))
            {
                throw new DomainException(
                    $"{parameter.DisplayName} não pode ter {string.Join(" ", UnsafeCharacters)} quando o pedido vem de longe.");
            }
        }

        var line = plan.Build(values);

        if (!line.IsReady)
        {
            throw new DomainException(line.Problem ?? "O comando não pôde ser montado.");
        }

        return new RemoteCommandPlan(task, request.DevelopmentId, plan.Entry, values);
    }
}

/// <summary>
/// <c>command.run</c> (E3): um comando rápido do ambiente (ADR-051), rodando
/// escondido, com a saída ao vivo para quem pediu. "Pedir confirmação" vira
/// uma aprovação remota: só depois dela o caso de uso recebe <c>Confirmed</c>.
/// </summary>
/// <remarks>
/// <para>
/// A saída vai mascarada (<see cref="SensitiveText"/>) e em lotes: as linhas
/// que chegam juntas viram um evento só, e o despachante junta os eventos de
/// 250 ms num envio. Acima de <see cref="MaxOutputBytes"/>, o resto fica só no
/// histórico desta máquina.
/// </para>
/// <para>
/// Cancelar vem pelo token: o executor de shell mata a árvore. A queda do
/// agente leva o processo junto pelo Job Object do agente.
/// </para>
/// </remarks>
public sealed class CommandRunExecutor(IUseCaseRunner runner) : IRemoteOperationExecutor
{
    /// <summary>O teto do <c>Timeout</c> pedido: o padrão do executor de shell.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(30);

    /// <summary>O que vai ao servidor de uma operação; o histórico daqui guarda tudo.</summary>
    public const int MaxOutputBytes = 1024 * 1024;

    /// <summary>Um evento de saída junta linhas até aqui.</summary>
    public const int MaxChunkLength = 4096;

    public const string StandardOutput = "stdout";

    public const string StandardError = "stderr";

    public string Kind => RemoteOperationKinds.CommandRun;

    public async Task<RemoteRefusal?> CheckAsync(RemoteOperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await PlanAsync(request, cancellationToken);
            return null;
        }
        catch (DomainException exception)
        {
            return new RemoteRefusal(RemoteErrorCodes.PolicyRefused, exception.Message);
        }
    }

    public async Task<RemoteOperationResult> ExecuteAsync(RemoteExecution execution, CancellationToken cancellationToken)
    {
        // Lido de novo: entre o aceite e agora, alguém pode ter desligado a liberação.
        var plan = await PlanAsync(execution.Request, cancellationToken);
        var what = plan.Describe();

        if (plan.Entry.RequiresConfirmation)
        {
            var approval = await execution.RequestApprovalAsync(
                new RemoteApprovalAsk(
                    ApprovalKind.CommandConfirmation,
                    $"Rodar {what}, a pedido de {execution.ControllerNickname}.",
                    JsonSerializer.SerializeToElement(
                        new Dictionary<string, object>
                        {
                            ["commandId"] = plan.Entry.CommandId,
                            ["developmentId"] = plan.DevelopmentId,
                            ["parameters"] = plan.Values,
                        },
                        RemoteJson.Options)),
                cancellationToken);

            if (!approval.IsAllowed)
            {
                return approval.AsFailure($"Rodar {what}");
            }
        }

        var asked = execution.Payload<WorktreeExecutionRequest>().Timeout;
        var timeout = asked is { } wanted && wanted > TimeSpan.Zero && wanted < MaxTimeout ? wanted : MaxTimeout;
        var output = new RemoteOutputStream(execution);

        CommandExecutionView view;

        try
        {
            await execution.EmitAsync(RemoteEventKind.StepStarted, text: what, step: "comando", cancellationToken: cancellationToken);
            view = await runner.RunAsync<RunQuickCommandHandler, CommandExecutionView>(
                (handler, token) => handler.HandleAsync(
                    new RunQuickCommand(
                        plan.TaskId,
                        plan.DevelopmentId,
                        plan.Entry.CommandId,
                        plan.Entry.BindingId,
                        plan.Values,
                        Confirmed: true,
                        Hidden: true,
                        Timeout: timeout),
                    output,
                    token),
                cancellationToken);
        }
        finally
        {
            await output.CompleteAsync();
        }

        // O caso de uso grava "parado" e volta sem erro; quem pediu o cancelamento é o despachante.
        cancellationToken.ThrowIfCancellationRequested();

        var data = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["exitCode"] = view.ExitCode,
                ["executionId"] = view.Id,
                ["durationMs"] = view.FinishedAt is { } end ? (long)(end - view.StartedAt).TotalMilliseconds : null,
                ["outputTruncated"] = output.Truncated,
            },
            RemoteJson.Options);

        return view.Status switch
        {
            CommandExecutionStatus.Completed => RemoteOperationResult.Success($"{what} terminou bem.", data),
            CommandExecutionStatus.Stopped => RemoteOperationResult.Failure(RemoteErrorCodes.ExecutionFailed, $"{what} foi parado nesta máquina."),
            _ => RemoteOperationResult.Failure(
                RemoteErrorCodes.ExecutionFailed,
                view.FailureReason is { } reason
                    ? $"{what}: {SensitiveText.Mask(reason)}"
                    : $"{what} saiu com código {view.ExitCode}."),
        };
    }

    private Task<RemoteCommandPlan> PlanAsync(RemoteOperationRequest request, CancellationToken cancellationToken) =>
        runner.RunAsync<RemoteCommandPlanner, RemoteCommandPlan>(
            (planner, token) => planner.PlanAsync(request.TaskId, RemoteOperationRules.PayloadOf<WorktreeExecutionRequest>(request), token),
            cancellationToken);
}

/// <summary>
/// As linhas do shell, na ordem em que chegam, para eventos de saída. O
/// <c>Report</c> não espera a rede: só enfileira; um leitor junta o que
/// já chegou e emite. De <c>command.run</c> e dos comandos pós-Worktree.
/// </summary>
internal sealed class RemoteOutputStream : IProgress<CommandOutputLine>
{
    private readonly Channel<CommandOutputLine> _lines = Channel.CreateUnbounded<CommandOutputLine>(new UnboundedChannelOptions { SingleReader = true });

    private readonly RemoteExecution _execution;

    private readonly Task _reader;

    private long _sent;

    public RemoteOutputStream(RemoteExecution execution)
    {
        _execution = execution;
        _reader = Task.Run(ReadAsync);
    }

    public bool Truncated { get; private set; }

    public void Report(CommandOutputLine value) => _lines.Writer.TryWrite(value);

    public async Task CompleteAsync()
    {
        _lines.Writer.TryComplete();
        await _reader;
    }

    private async Task ReadAsync()
    {
        var chunk = new StringBuilder();
        bool? isError = null;

        while (await _lines.Reader.WaitToReadAsync())
        {
            while (_lines.Reader.TryRead(out var line))
            {
                if (isError is { } current && (current != line.IsError || chunk.Length + line.Text.Length > CommandRunExecutor.MaxChunkLength))
                {
                    await SendAsync(chunk, current);
                }

                isError = line.IsError;
                chunk.Append(chunk.Length == 0 ? string.Empty : "\n").Append(line.Text);
            }

            if (isError is { } stream)
            {
                await SendAsync(chunk, stream);
                isError = null;
            }
        }
    }

    private async Task SendAsync(StringBuilder chunk, bool isError)
    {
        var text = SensitiveText.Mask(chunk.ToString());
        chunk.Clear();

        if (Truncated)
        {
            return;
        }

        if (_sent + text.Length > CommandRunExecutor.MaxOutputBytes)
        {
            Truncated = true;
            await EmitAsync(RemoteEventKind.Warning, $"A saída passou de {CommandRunExecutor.MaxOutputBytes / 1024 / 1024} MiB: o resto fica só no histórico desta máquina.", null);
            return;
        }

        _sent += text.Length;
        await EmitAsync(RemoteEventKind.Output, text, isError ? CommandRunExecutor.StandardError : CommandRunExecutor.StandardOutput);
    }

    private async Task EmitAsync(RemoteEventKind kind, string text, string? step)
    {
        try
        {
            // Sem o token da operação: a saída que já veio é contada mesmo com o cancelamento a caminho.
            await _execution.EmitAsync(kind, text: text, step: step, cancellationToken: CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // O diário que não gravou uma linha não para o comando; o histórico daqui tem tudo.
        }
    }
}
