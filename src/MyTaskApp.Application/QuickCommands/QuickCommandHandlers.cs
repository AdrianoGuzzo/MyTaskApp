using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Tags;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.QuickCommands;

/// <summary>O que a seção "⚡ Comandos" do ambiente desenha (ADR-051).</summary>
/// <param name="Commands">Os botões, na ordem.</param>
/// <param name="Globals">Todos os comandos globais, para "+ Executar comando…".</param>
/// <param name="Recent">As execuções do ambiente, da mais nova para a mais antiga.</param>
public sealed record QuickCommandsView(
    IReadOnlyList<QuickCommandEntry> Commands,
    IReadOnlyList<DevelopmentCommandRow> Globals,
    IReadOnlyList<CommandExecutionView> Recent)
{
    public static readonly QuickCommandsView Empty = new([], [], []);
}

/// <summary>Tudo o que a tela precisa para perguntar e mostrar a linha final antes de rodar.</summary>
/// <param name="WorkingDirectory">A pasta absoluta onde o comando vai rodar, já conferida.</param>
public sealed record QuickCommandPlan(QuickCommandEntry Entry, string WorkingDirectory, CommandContext Context)
{
    /// <summary>Há parâmetro a preencher ou confirmação a pedir: a tela abre o diálogo.</summary>
    public bool NeedsPrompt => Entry.Parameters.Count > 0 || Entry.RequiresConfirmation;

    /// <summary>A linha com estes valores — a mesma conta que a execução refaz.</summary>
    public QuickCommandLine Build(IReadOnlyDictionary<string, string>? values) =>
        QuickCommandLine.Build(Entry.Template, Entry.Parameters, values, Context);
}

/// <summary>O caminho comum: o ambiente pronto, os comandos dele e a pasta de cada um.</summary>
internal static class QuickCommandTargets
{
    public static async Task<(TaskItem Task, TaskDevelopment Development)> ReadyAsync(
        ITaskItemRepository tasks,
        IDirectoryProbe directories,
        Guid taskId,
        Guid developmentId,
        CancellationToken cancellationToken)
    {
        var task = await tasks.GetByIdAsync(taskId, cancellationToken);
        var development = task.GetDevelopment(developmentId);

        if (development.Status != TaskDevelopmentStatus.Ready)
        {
            throw new DomainException("Os comandos só rodam com o worktree pronto.");
        }

        if (!await directories.ExistsAsync(development.WorktreePath, cancellationToken))
        {
            throw new DomainException(
                $"A pasta do worktree ({development.WorktreePath}) não existe mais. Nenhum comando foi executado.");
        }

        return (task, development);
    }

    /// <returns>
    /// Os diretórios que casaram; os comandos que podem virar botão — os globais
    /// e os só daqueles diretórios (ADR-054); e só os globais, para o avulso.
    /// </returns>
    public static async Task<(
        IReadOnlyList<CommandDirectoryMatch> Matches,
        IReadOnlyList<DevelopmentCommandRow> Commands,
        IReadOnlyList<DevelopmentCommandRow> Globals)>
        CatalogAsync(
            ITagQuery tagQuery,
            IDevelopmentCommandRepository commands,
            TaskItem task,
            TaskDevelopment development,
            CancellationToken cancellationToken)
    {
        var directories = await tagQuery.ListCommandDirectoriesAsync(cancellationToken);
        var globals = (await commands.ListAsync(cancellationToken)).Select(command => DevelopmentCommandRow.From(command)).ToList();
        var taskTags = task.Tags.Select(link => link.TagId).ToHashSet();
        var matches = QuickCommandCatalog.Match(development.RepositoryPath, taskTags, directories);

        if (matches.Count == 0)
        {
            return (matches, globals, globals);
        }

        var own = await commands.ListForDirectoriesAsync(
            [.. matches.Select(match => match.Directory.Id)],
            cancellationToken);

        return (matches, [.. globals, .. own.Select(command => DevelopmentCommandRow.From(command))], globals);
    }

    /// <summary>
    /// O botão do diretório, ou — sem associação, ou com uma que já não está
    /// aqui — o comando global como avulso. O comando só do diretório não tem
    /// avulso: fora do botão, ele não existe.
    /// </summary>
    public static QuickCommandEntry Find(
        IReadOnlyList<CommandDirectoryMatch> matches,
        IReadOnlyList<DevelopmentCommandRow> commands,
        IReadOnlyList<DevelopmentCommandRow> globals,
        Guid commandId,
        Guid? bindingId)
    {
        if (bindingId is not null
            && QuickCommandCatalog.Entries(matches, commands)
                .FirstOrDefault(entry => entry.BindingId == bindingId && entry.CommandId == commandId) is { } bound)
        {
            return bound;
        }

        var global = globals.FirstOrDefault(row => row.Id == commandId)
            ?? throw new DomainException("Este comando não está mais disponível. Ele pode ter sido excluído, ou desligado no diretório da etiqueta.");

        return QuickCommandCatalog.AdHoc(global, matches);
    }

    /// <summary>A pasta absoluta, dentro do worktree, conferida agora.</summary>
    public static async Task<string> WorkingDirectoryAsync(
        IDirectoryProbe directories,
        TaskDevelopment development,
        QuickCommandEntry entry,
        CancellationToken cancellationToken)
    {
        var root = development.WorktreePath;

        if (entry.WorkingDirectory is null)
        {
            return root;
        }

        var path = Path.GetFullPath(Path.Combine(root, entry.WorkingDirectory.Replace('/', Path.DirectorySeparatorChar)));

        if (QuickCommandCatalog.OffsetOf(root, path) is null)
        {
            throw new DomainException("A pasta do comando precisa ficar dentro do worktree.");
        }

        if (!await directories.ExistsAsync(path, cancellationToken))
        {
            throw new DomainException(
                $"A pasta {entry.WorkingDirectory} não existe neste worktree ({path}). "
                + "Confira a pasta do comando em Comandos globais ou no diretório da etiqueta.");
        }

        return path;
    }

    public static CommandContext ContextFor(TaskItem task, TaskDevelopment development, QuickCommandEntry entry) =>
        CommandContext.For(task, development, entry.TagName);
}

/// <summary>Os botões do ambiente e a última execução de cada um (ADR-051).</summary>
public sealed record GetQuickCommands(Guid TaskId, Guid DevelopmentId);

public sealed class GetQuickCommandsHandler(
    ITaskItemRepository tasks,
    ITagQuery tagQuery,
    IDevelopmentCommandRepository commands,
    ICommandExecutionRepository executions,
    IUnitOfWork unitOfWork,
    IAgentProcessTracker processes,
    ICommandExecutionWatcher watcher,
    TimeProvider timeProvider)
{
    public async Task<QuickCommandsView> HandleAsync(GetQuickCommands query, CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(query.TaskId, cancellationToken);
        var development = task.GetDevelopment(query.DevelopmentId);

        if (development.Status != TaskDevelopmentStatus.Ready)
        {
            return QuickCommandsView.Empty;
        }

        var (matches, available, globals) = await QuickCommandTargets.CatalogAsync(
            tagQuery, commands, task, development, cancellationToken);

        var recent = await executions.ListForDevelopmentAsync(development.Id, cancellationToken);
        var now = timeProvider.GetUtcNow();

        // O banco não é a verdade, o processo é: o terminal que fechou com o app
        // fechado termina aqui, antes de virar "Executando" na tela.
        if (recent.Count(execution => CommandExecutionReconciler.EndIfGone(execution, processes, watcher, now)) > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return new QuickCommandsView(
            QuickCommandCatalog.Entries(matches, available),
            globals,
            [.. recent.Select(CommandExecutionView.From)]);
    }
}

/// <summary>
/// Antes de rodar: o comando efetivo, a pasta conferida e o que perguntar
/// (ADR-051). Não grava nada.
/// </summary>
public sealed record PrepareQuickCommand(Guid TaskId, Guid DevelopmentId, Guid CommandId, Guid? BindingId);

public sealed class PrepareQuickCommandHandler(
    ITaskItemRepository tasks,
    ITagQuery tagQuery,
    IDevelopmentCommandRepository commands,
    IDirectoryProbe directories)
{
    public async Task<QuickCommandPlan> HandleAsync(PrepareQuickCommand query, CancellationToken cancellationToken = default)
    {
        var (task, development) = await QuickCommandTargets.ReadyAsync(
            tasks, directories, query.TaskId, query.DevelopmentId, cancellationToken);

        var (matches, available, globals) = await QuickCommandTargets.CatalogAsync(
            tagQuery, commands, task, development, cancellationToken);

        var entry = QuickCommandTargets.Find(matches, available, globals, query.CommandId, query.BindingId);
        var folder = await QuickCommandTargets.WorkingDirectoryAsync(directories, development, entry, cancellationToken);

        return new QuickCommandPlan(entry, folder, QuickCommandTargets.ContextFor(task, development, entry));
    }
}

/// <summary>
/// Roda um comando rápido no ambiente (ADR-051). Os valores são os dos
/// parâmetros; o resto — pasta, worktree, variáveis — o app resolve.
/// </summary>
public sealed record RunQuickCommand(
    Guid TaskId,
    Guid DevelopmentId,
    Guid CommandId,
    Guid? BindingId,
    IReadOnlyDictionary<string, string>? Values = null);

/// <remarks>
/// <para>
/// <b>Monta a linha de novo,</b> com o comando e o ambiente lidos agora: a
/// prévia da tela é uma conveniência, e não a fonte da verdade.
/// </para>
/// <para>
/// <b>Grava antes de abrir</b>, como a sessão do agente: uma queda no meio deixa
/// uma execução sem processo, que a reconciliação encerra — e não um processo
/// sem registro.
/// </para>
/// <para>
/// <b>Escondido</b> roda aqui, com o output chegando ao vivo por
/// <c>output</c>, e volta já terminado. <b>Terminal</b> volta assim que a janela
/// abre; quem encerra a execução é o vigia do processo.
/// </para>
/// <para>
/// O output vai para o histórico, que é dado do usuário, e <b>não</b> para o
/// log: pode ter segredo (ADR-028). O log leva o comando e o exit code.
/// </para>
/// </remarks>
public sealed class RunQuickCommandHandler(
    ITaskItemRepository tasks,
    ITagQuery tagQuery,
    IDevelopmentCommandRepository commands,
    ICommandExecutionRepository executions,
    IUnitOfWork unitOfWork,
    ICommandExecutor executor,
    ITerminalCommandLauncher terminals,
    IAgentProcessTracker processes,
    ICommandExecutionWatcher watcher,
    IDirectoryProbe directories,
    TimeProvider timeProvider,
    ILogger<RunQuickCommandHandler> logger)
{
    /// <summary>O histórico guarda as últimas execuções de cada ambiente; as mais antigas saem.</summary>
    public const int HistoryLimit = 20;

    public async Task<CommandExecutionView> HandleAsync(
        RunQuickCommand command,
        IProgress<CommandOutputLine>? output,
        CancellationToken cancellationToken = default)
    {
        var (task, development) = await QuickCommandTargets.ReadyAsync(
            tasks, directories, command.TaskId, command.DevelopmentId, cancellationToken);

        var (matches, available, globals) = await QuickCommandTargets.CatalogAsync(
            tagQuery, commands, task, development, cancellationToken);

        var entry = QuickCommandTargets.Find(matches, available, globals, command.CommandId, command.BindingId);
        var folder = await QuickCommandTargets.WorkingDirectoryAsync(directories, development, entry, cancellationToken);
        var line = QuickCommandLine.Build(
            entry.Template,
            entry.Parameters,
            command.Values,
            QuickCommandTargets.ContextFor(task, development, entry));

        if (line.Line is not { } commandLine)
        {
            throw new DomainException(line.Problem ?? "O comando não pôde ser montado.");
        }

        var history = await executions.ListForDevelopmentAsync(development.Id, cancellationToken);

        await EnsureNotRunningAsync(entry, history, cancellationToken);

        var execution = CommandExecution.Create(
            task.Id,
            development.Id,
            entry.CommandId,
            entry.BindingId,
            entry.Name,
            commandLine,
            folder,
            entry.Mode,
            entry.KeepTerminalOpen,
            timeProvider.GetUtcNow());

        await executions.AddAsync(execution, cancellationToken);

        foreach (var old in history.Where(old => !old.IsActive).Skip(HistoryLimit - 1))
        {
            executions.Remove(old);
        }

        logger.LogInformation(
            "QuickCommandStarted {TaskId} {ExecutionId} {CommandId} {Mode} {Command} {WorkingDirectory}",
            task.Id,
            execution.Id,
            entry.CommandId,
            entry.Mode,
            commandLine,
            folder);

        return entry.Mode == CommandMode.Terminal
            ? await OpenTerminalAsync(execution, cancellationToken)
            : await ExecuteAsync(execution, output, cancellationToken);
    }

    /// <summary>
    /// Um <c>dotnet run</c> por vez em cada ambiente: o segundo brigaria pela
    /// porta. O terminal que já fechou não conta.
    /// </summary>
    private async Task EnsureNotRunningAsync(
        QuickCommandEntry entry,
        IReadOnlyList<CommandExecution> history,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var running = history.Where(old => old.IsActive && old.DevelopmentCommandId == entry.CommandId).ToList();
        var ended = running.Where(old => CommandExecutionReconciler.EndIfGone(old, processes, watcher, now)).ToList();

        if (ended.Count > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        if (running.Except(ended).Any())
        {
            throw new DomainException(
                entry.Mode == CommandMode.Terminal
                    ? $"{entry.Name} já está rodando neste ambiente. Use \"Mostrar terminal\" para ir até ele."
                    : $"{entry.Name} já está rodando neste ambiente.");
        }
    }

    private async Task<CommandExecutionView> ExecuteAsync(
        CommandExecution execution,
        IProgress<CommandOutputLine>? output,
        CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var registration = watcher.TrackInProcess(execution.Id, cancellation);

        execution.MarkRunning();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        watcher.NotifyChanged(execution.TaskItemId);

        var now = timeProvider.GetUtcNow;

        try
        {
            var result = await executor.ExecuteAsync(
                new CommandExecutionRequest(execution.CommandLine, execution.WorkingDirectory),
                output,
                cancellation.Token);

            if (result.Canceled)
            {
                execution.Stop(result.StandardOutput, result.StandardError, now());
            }
            else if (result.TimedOut)
            {
                execution.Fail(CommandSequence.TimeoutMessage, now(), result.StandardOutput, result.StandardError);
            }
            else
            {
                execution.Finish(result.ExitCode, result.StandardOutput, result.StandardError, now());
            }
        }
        catch (CommandStartException exception)
        {
            execution.Fail(exception.Message, now());
        }
        catch (OperationCanceledException)
        {
            execution.Stop(null, null, now());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Gravar o fim é o que importa: sem isto, a execução ficaria
            // "Executando" até a próxima abertura do app.
            execution.Fail("O comando não pôde ser executado.", now());
            logger.LogError(exception, "QuickCommandFailed {ExecutionId}", execution.Id);
        }

        // Cancelado ou não, o fim é gravado: o token de quem pediu pode já ter caído.
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        logger.LogInformation(
            "QuickCommandFinished {TaskId} {ExecutionId} {Status} {ExitCode}",
            execution.TaskItemId,
            execution.Id,
            execution.Status,
            execution.ExitCode);

        watcher.NotifyChanged(execution.TaskItemId);

        return CommandExecutionView.From(execution);
    }

    private async Task<CommandExecutionView> OpenTerminalAsync(
        CommandExecution execution,
        CancellationToken cancellationToken)
    {
        // Gravada antes de abrir: ver o remarks.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        TerminalLaunchResult result;

        try
        {
            result = await terminals.LaunchAsync(
                new TerminalCommandRequest(execution.CommandLine, execution.WorkingDirectory, execution.KeepTerminalOpen),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "QuickCommandTerminalFailed {ExecutionId}", execution.Id);
            result = TerminalLaunchResult.Failed("Não foi possível abrir o terminal.");
        }

        var now = timeProvider.GetUtcNow();

        if (!result.Started)
        {
            execution.Fail(result.Error ?? "Não foi possível abrir o terminal.", now);
        }
        else
        {
            execution.MarkRunning(result.ProcessId, result.ProcessStartedAt);

            // Saiu antes de olharmos (um /c que terminou na hora): não há o que vigiar.
            if (!processes.IsAlive(result.ProcessId, result.ProcessStartedAt))
            {
                execution.Finish(null, null, null, now);
            }
        }

        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        logger.LogInformation(
            "QuickCommandTerminalOpened {TaskId} {ExecutionId} {Status} {ProcessId}",
            execution.TaskItemId,
            execution.Id,
            execution.Status,
            execution.ProcessId);

        watcher.NotifyChanged(execution.TaskItemId);

        if (CommandExecutionWatch.For(execution) is { } watch)
        {
            watcher.Watch(watch);
        }

        return CommandExecutionView.From(execution);
    }
}

/// <summary>"Cancelar" num comando escondido que roda neste processo (ADR-051).</summary>
public sealed record CancelQuickCommand(Guid ExecutionId);

public sealed class CancelQuickCommandHandler(ICommandExecutionWatcher watcher)
{
    /// <returns><c>false</c> quando a execução não está rodando aqui.</returns>
    public Task<bool> HandleAsync(CancelQuickCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(watcher.Cancel(command.ExecutionId));
}
