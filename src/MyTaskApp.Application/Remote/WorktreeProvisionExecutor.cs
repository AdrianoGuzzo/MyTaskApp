using System.Text.Json;
using System.Threading.Channels;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.External;
using MyTaskApp.Application.Tags;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>O <c>worktree.provision</c> resolvido nesta máquina: a pasta, a origem e a branch, como a tela escolheria.</summary>
/// <param name="Label">Como a pasta aparece para quem pediu: a etiqueta e o nome, nunca o caminho.</param>
public sealed record RemoteProvisionPlan(ProvisionEnvironment Command, string Label, string Source, bool RunCommands)
{
    public string Describe() => $"{Command.NewBranch} em {Label}, a partir de {Source}";
}

/// <summary>
/// Confere um <c>worktree.provision</c> contra esta máquina (ADR-088): o
/// diretório da etiqueta mapeado aqui, a origem que existe no repositório, e
/// a branch. O que não vem no pedido é escolhido como a aba Desenvolvimento
/// escolheria: a tentativa anterior, a padrão do diretório (ADR-035), os outros
/// ambientes da tarefa (ADR-031), a convenção do Jira (ADR-045), o título.
/// </summary>
public sealed class RemoteProvisionPlanner(
    ITaskItemRepository tasks,
    ITagQuery tags,
    ListBranchesHandler branches,
    GetTaskExternalContextHandler external)
{
    public async Task<RemoteProvisionPlan> PlanAsync(Guid? taskId, EnvironmentProvisioningRequest request, CancellationToken cancellationToken = default)
    {
        if (taskId != request.TaskId)
        {
            throw new DomainException("O pedido diz uma tarefa e o ambiente, outra.");
        }

        if (request.Database is not null || request.StartAgent is not null)
        {
            throw new DomainException("Esta versão do agente prepara o worktree e roda os comandos; o banco e o Claude vêm em pedidos próprios.");
        }

        if (request.SourceBranch is not null && request.SourceTag is not null)
        {
            throw new DomainException("Escolha uma branch ou uma tag de origem, não as duas.");
        }

        if (await tags.FindDirectoryAsync(request.TagDirectoryId, cancellationToken) is not { Path: { } path } directory)
        {
            throw new DomainException("O repositório pedido não está mapeado nesta máquina.");
        }

        var task = await tasks.GetByIdAsync(request.TaskId, cancellationToken);
        var label = $"{directory.TagName} · {directory.Name ?? directory.Alias}";
        var previous = task.Developments.FirstOrDefault(development => WorktreePathPlanner.SamePath(development.RepositoryPath, path)
            && development.Status is not TaskDevelopmentStatus.NotCreatedHere);
        var sibling = task.Developments
            .Where(development => development != previous)
            .OrderByDescending(development => development.Status == TaskDevelopmentStatus.Ready)
            .ThenByDescending(development => development.CreatedAt)
            .FirstOrDefault();

        if (previous is { Status: TaskDevelopmentStatus.Ready })
        {
            throw new DomainException($"A tarefa já tem um worktree pronto em {label} nesta máquina.");
        }

        var list = await branches.HandleAsync(new ListBranches(path), cancellationToken);
        var source = request.SourceBranch is { } wanted
            ? ListBranchesHandler.FindConfigured(list.Branches, wanted)
                ?? throw new DomainException($"A branch {wanted} não existe em {label} nesta máquina.")
            : ListBranchesHandler.ChooseSource(
                list.Branches,
                null,
                previous?.SourceBranch,
                ListBranchesHandler.FindConfigured(list.Branches, directory.DefaultBranch),
                sibling?.SourceBranch,
                list.Suggested)
                ?? throw new DomainException($"{label} não tem nenhuma branch ainda.");

        if (request.SourceTag is { } tag && list.Tags?.Any(existing => existing.Name == tag) is not true)
        {
            throw new DomainException($"A tag {tag} não existe em {label} nesta máquina.");
        }

        var branch = request.BranchName?.Trim() is { Length: > 0 } named ? named
            : previous?.Branch is { Length: > 0 } retried ? retried
            : sibling?.Branch is { Length: > 0 } shared ? shared
            : (await external.HandleAsync(new GetTaskExternalContext(task.Id), cancellationToken)).SuggestedBranch
                ?? GitBranchName.Suggest(task.Title);

        if (GitBranchName.Validate(branch) is { } problem)
        {
            throw new DomainException(problem);
        }

        return new RemoteProvisionPlan(
            new ProvisionEnvironment(
                task.Id,
                path,
                source.FullRef,
                branch,
                request.OnConflict,
                previous?.Id,
                request.SourceTag,
                Commands: null,
                RunCommands: request.RunPostWorktreeCommands),
            label,
            request.SourceTag ?? source.ShortName,
            request.RunPostWorktreeCommands);
    }
}

/// <summary>
/// <c>worktree.provision</c> (E4): o "Iniciar implementação" da aba
/// Desenvolvimento, na máquina de longe, sem a tela dela aberta. Cada etapa
/// (<c>prepare</c>, <c>worktree</c>, <c>commands</c>) vira um evento antes de
/// começar: o agente que cair no meio deixa dito onde parou, e a retomada com
/// <see cref="WorktreeConflictPolicy.AdoptExisting"/> adota o worktree que ficou.
/// </summary>
/// <remarks>
/// Os comandos pós-Worktree são a lista gravada no ambiente, definida nesta
/// máquina ou conferida aqui ao chegar pelo sync (ADR-061); quem pede de longe
/// só escolhe rodar ou não. O caminho das pastas não sai daqui.
/// </remarks>
public sealed class WorktreeProvisionExecutor(IUseCaseRunner runner, ProvisionEnvironmentHandler provision) : IRemoteOperationExecutor
{
    public const string PrepareStep = "prepare";

    public const string WorktreeStep = "worktree";

    public const string CommandsStep = "commands";

    public string Kind => RemoteOperationKinds.WorktreeProvision;

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
        var plan = await PlanAsync(execution.Request, cancellationToken);
        var events = new ProvisionEvents(execution);
        ProvisionResult result;

        try
        {
            result = await provision.HandleAsync(plan.Command, events, cancellationToken);
        }
        catch (DevelopmentStepException exception)
        {
            return RemoteOperationResult.Failure(
                RemoteErrorCodes.ExecutionFailed,
                $"{StepName(exception.Step)}: {SensitiveText.Mask(exception.Message)}");
        }
        catch (DomainException exception)
        {
            return RemoteOperationResult.Failure(RemoteErrorCodes.ExecutionFailed, SensitiveText.Mask(exception.Message));
        }
        finally
        {
            await events.CompleteAsync();
        }

        var data = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["developmentId"] = result.Development.Id,
                ["branch"] = result.Development.Branch,
                ["adopted"] = result.Adopted,
                ["commands"] = result.Commands.Steps.Count,
                ["commandsSucceeded"] = result.Commands.Succeeded,
            },
            RemoteJson.Options);

        var worktree = result.Adopted ? "Worktree adotado" : "Worktree criado";

        if (result.Commands.Steps.Count == 0)
        {
            return RemoteOperationResult.Success($"{worktree}: {plan.Describe()}.", data);
        }

        if (result.Commands.Succeeded)
        {
            return RemoteOperationResult.Success($"{worktree} e {Count(result.Commands.Steps.Count)}: {plan.Describe()}.", data);
        }

        var failed = result.Commands.Steps.FirstOrDefault(step => step.State is CommandStepState.Failed or CommandStepState.Canceled);
        cancellationToken.ThrowIfCancellationRequested();

        return RemoteOperationResult.Failure(
            RemoteErrorCodes.ExecutionFailed,
            $"{worktree}, mas um comando pós-Worktree falhou{(failed is null ? string.Empty : $" ({SensitiveText.Mask(failed.Command)})")}. O worktree fica.");
    }

    internal static string StepName(DevelopmentStep step) => step switch
    {
        DevelopmentStep.CheckGit => "Conferir o Git",
        DevelopmentStep.ValidateDirectory or DevelopmentStep.ValidateRepository => "Conferir o repositório",
        DevelopmentStep.Fetch => "Buscar o remoto",
        DevelopmentStep.ValidateSource or DevelopmentStep.CheckChanges or DevelopmentStep.UpdateSource => "Atualizar a origem",
        DevelopmentStep.ValidateBranchName => "Conferir a branch",
        DevelopmentStep.PlanWorktreePath => "A pasta do worktree",
        DevelopmentStep.CreateWorktree or DevelopmentStep.ValidateWorktree => "Criar o worktree",
        DevelopmentStep.SaveTask => "Gravar o ambiente",
        _ => step.ToString(),
    };

    private static string Count(int commands) => commands == 1 ? "1 comando" : $"{commands} comandos";

    private Task<RemoteProvisionPlan> PlanAsync(RemoteOperationRequest request, CancellationToken cancellationToken) =>
        runner.RunAsync<RemoteProvisionPlanner, RemoteProvisionPlan>(
            (planner, token) => planner.PlanAsync(request.TaskId, RemoteOperationRules.PayloadOf<EnvironmentProvisioningRequest>(request), token),
            cancellationToken);

    /// <summary>
    /// O andamento da preparação em eventos, na ordem: a etapa que começa, o
    /// que cada passo do Git disse, e a saída dos comandos em lotes, como no
    /// <c>command.run</c>. O <c>Report</c> só enfileira.
    /// </summary>
    private sealed class ProvisionEvents : IProgress<ProvisionProgress>
    {
        private readonly Channel<ProvisionProgress> _queue = Channel.CreateUnbounded<ProvisionProgress>(new UnboundedChannelOptions { SingleReader = true });

        private readonly RemoteExecution _execution;

        private readonly RemoteOutputStream _output;

        private readonly Task _reader;

        public ProvisionEvents(RemoteExecution execution)
        {
            _execution = execution;
            _output = new RemoteOutputStream(execution);
            _reader = Task.Run(ReadAsync);
        }

        public void Report(ProvisionProgress value) => _queue.Writer.TryWrite(value);

        public async Task CompleteAsync()
        {
            _queue.Writer.TryComplete();
            await _reader;
            await _output.CompleteAsync();
        }

        private async Task ReadAsync()
        {
            await foreach (var progress in _queue.Reader.ReadAllAsync())
            {
                switch (progress)
                {
                    case { Step: null, Command: null }:
                        await EmitAsync(RemoteEventKind.StepStarted, Started(progress.Stage), StepOf(progress.Stage));
                        break;

                    case { Step: { State: DevelopmentStepState.Done or DevelopmentStepState.Warning } step }:
                        await EmitAsync(
                            step.State == DevelopmentStepState.Warning ? RemoteEventKind.Warning : RemoteEventKind.StepCompleted,
                            step.Note is { Length: > 0 } note ? $"{StepName(step.Step)}: {SensitiveText.Mask(note)}" : StepName(step.Step),
                            StepOf(progress.Stage));
                        break;

                    case { Command: { Line: { } line } }:
                        _output.Report(line);
                        break;

                    // O número, e não o texto: a linha do comando pode ter o que não sai daqui.
                    case { Command: { State: CommandStepState.Running, Command: not null } started }:
                        _output.Report(new CommandOutputLine($"— comando {started.Index + 1} —", false));
                        break;
                }
            }
        }

        private static string StepOf(ProvisionStage stage) => stage switch
        {
            ProvisionStage.Prepare => PrepareStep,
            ProvisionStage.Worktree => WorktreeStep,
            _ => CommandsStep,
        };

        private static string Started(ProvisionStage stage) => stage switch
        {
            ProvisionStage.Prepare => "Conferindo o repositório e atualizando a origem.",
            ProvisionStage.Worktree => "Criando o worktree.",
            _ => "Rodando os comandos pós-Worktree.",
        };

        private async Task EmitAsync(RemoteEventKind kind, string text, string step)
        {
            try
            {
                await _execution.EmitAsync(kind, text: text, step: step, cancellationToken: CancellationToken.None);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // O diário que não gravou um aviso não para a preparação.
            }
        }
    }
}
