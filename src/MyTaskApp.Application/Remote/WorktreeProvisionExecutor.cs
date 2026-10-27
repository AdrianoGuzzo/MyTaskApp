using System.Text.Json;
using System.Threading.Channels;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
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

        if (request.StartAgent is { } agent)
        {
            if (AgentToolProfiles.Find(agent.ProfileId) is null)
            {
                throw new DomainException($"Perfil desconhecido nesta máquina: {agent.ProfileId}.");
            }

            if (agent.Prompt.AgentSessionId is not null)
            {
                throw new DomainException("A preparação abre uma conversa nova; para continuar outra, mande um prompt nela.");
            }
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
/// <c>worktree.provision</c> (E4, E6): o "Iniciar implementação" da aba
/// Desenvolvimento, na máquina de longe, sem a tela dela aberta; com banco e
/// Claude, o "Preparar tudo". Cada etapa (<c>prepare</c>, <c>database</c>,
/// <c>worktree</c>, <c>commands</c>, <c>agent</c>) vira um evento antes de
/// começar: o agente que cair no meio deixa dito onde parou, a falha diz em
/// qual foi, e a retomada com <see cref="WorktreeConflictPolicy.AdoptExisting"/>
/// adota o worktree que ficou.
/// </summary>
/// <remarks>
/// <para>
/// A ordem é a do ADR-060: a cópia é validada, e a produção aprovada, antes do
/// Git (negar não cria nada); o worktree; a cópia; os comandos, já com o
/// <c>{database}</c>; o Claude por último. O que já foi feito fica quando uma
/// etapa falha, como na tela.
/// </para>
/// <para>
/// Os comandos pós-Worktree são a lista gravada no ambiente, definida nesta
/// máquina ou conferida aqui ao chegar pelo sync (ADR-061); quem pede de longe
/// só escolhe rodar ou não. O caminho das pastas não sai daqui.
/// </para>
/// </remarks>
public sealed class WorktreeProvisionExecutor(
    IUseCaseRunner runner,
    ProvisionEnvironmentHandler provision,
    RemoteDatabaseCopy copies,
    HeadlessPromptRunner prompts) : IRemoteOperationExecutor
{
    public const string PrepareStep = "prepare";

    public const string WorktreeStep = "worktree";

    public const string DatabaseStep = "database";

    public const string CommandsStep = "commands";

    /// <summary>A etapa do Claude: o evento é o do <see cref="HeadlessPromptRunner"/>.</summary>
    public const string AgentStep = "agent";

    public string Kind => RemoteOperationKinds.WorktreeProvision;

    public async Task<RemoteRefusal?> CheckAsync(RemoteOperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await PlanAsync(request, cancellationToken);

            if (RemoteOperationRules.PayloadOf<EnvironmentProvisioningRequest>(request).Database is { } database)
            {
                await copies.PlanAsync(request.TaskId, database with { DevelopmentId = null }, cancellationToken);
            }
        }
        catch (DomainException exception)
        {
            return new RemoteRefusal(RemoteErrorCodes.PolicyRefused, exception.Message);
        }

        return RemoteOperationRules.PayloadOf<EnvironmentProvisioningRequest>(request).StartAgent is null
            ? null
            : (await prompts.DetectAsync(cancellationToken)).Refusal;
    }

    public async Task<RemoteOperationResult> ExecuteAsync(RemoteExecution execution, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(execution.Request, cancellationToken);
        var payload = execution.Payload<EnvironmentProvisioningRequest>();

        // Tudo o que pode recusar vem antes do Git: o Claude desta máquina, e a cópia com a aprovação dela.
        if (payload.StartAgent is not null && (await prompts.DetectAsync(cancellationToken)).Refusal is { } unavailable)
        {
            return RemoteOperationResult.Failure(unavailable.Code, $"Claude: {unavailable.Summary} Nada foi criado.");
        }

        RemoteCopyTarget? copy = null;
        RemoteCopyClearance? clearance = null;

        if (payload.Database is { } database)
        {
            copy = await copies.PlanAsync(execution.Request.TaskId, database with { DevelopmentId = null }, cancellationToken);
            (clearance, var refused) = await copies.ClearAsync(execution, copy, DatabaseStep, cancellationToken);

            if (refused is not null)
            {
                return refused with { Summary = $"Banco: {refused.Summary} Nada foi criado." };
            }
        }

        var events = new ProvisionEvents(execution);
        ProvisionResult result;

        try
        {
            // Com banco, os comandos esperam a cópia: precisam do {database}.
            result = await provision.HandleAsync(plan.Command with { RunCommands = plan.RunCommands && copy is null }, events, cancellationToken);
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

        var data = new Dictionary<string, object?>
        {
            ["developmentId"] = result.Development.Id,
            ["branch"] = result.Development.Branch,
            ["adopted"] = result.Adopted,
        };
        var worktree = result.Adopted ? "Worktree adotado" : "Worktree criado";
        var commands = result.Commands;
        string? databaseName = null;

        if (copy is not null)
        {
            try
            {
                var copied = await copies.CopyAsync(
                    execution,
                    copy with { TaskId = result.Development.TaskId, DevelopmentId = result.Development.Id },
                    clearance!,
                    DatabaseStep,
                    cancellationToken);
                data["databaseAuditId"] = copied.AuditId;

                if (!copied.Succeeded)
                {
                    return Failed($"Banco: {RemoteDatabaseCopy.Error(copied)} {worktree}; copie o banco de novo e rode os comandos.", data);
                }

                databaseName = copied.DestinationDatabase;
                data["database"] = databaseName;
            }
            catch (DomainException exception)
            {
                return Failed($"Banco: {SensitiveText.Mask(exception.Message)} {worktree}; copie o banco de novo e rode os comandos.", data);
            }

            if (plan.RunCommands)
            {
                var later = new ProvisionEvents(execution);

                try
                {
                    commands = await provision.RunCommandsAsync(result.Development, later, cancellationToken);
                }
                catch (DomainException exception)
                {
                    return Failed($"Comandos: {SensitiveText.Mask(exception.Message)}", data);
                }
                finally
                {
                    await later.CompleteAsync();
                }
            }
        }

        data["commands"] = commands.Steps.Count;
        data["commandsSucceeded"] = commands.Succeeded;

        if (commands.Steps.Count > 0 && !commands.Succeeded)
        {
            var failed = commands.Steps.FirstOrDefault(step => step.State is CommandStepState.Failed or CommandStepState.Canceled);
            cancellationToken.ThrowIfCancellationRequested();

            return Failed(
                $"{worktree}, mas um comando pós-Worktree falhou{(failed is null ? string.Empty : $" ({SensitiveText.Mask(failed.Command)})")}. O worktree fica.",
                data);
        }

        var summary = commands.Steps.Count == 0
            ? $"{worktree}: {plan.Describe()}."
            : $"{worktree} e {Count(commands.Steps.Count)}: {plan.Describe()}.";

        if (databaseName is not null)
        {
            summary += $" Banco: {databaseName}.";
        }

        if (payload.StartAgent is not { } agent)
        {
            return RemoteOperationResult.Success(summary, Json(data));
        }

        // O evento da etapa (agent) é o do HeadlessPromptRunner, já com o id da conversa.
        RemoteOperationResult answer;

        try
        {
            answer = await AgentStartExecutor.StartAsync(
                runner,
                prompts,
                execution,
                agent with { DevelopmentId = result.Development.Id },
                cancellationToken);
        }
        catch (DomainException exception)
        {
            return Failed($"{summary} Claude: {SensitiveText.Mask(exception.Message)}", data);
        }

        // O id da conversa vai junto: quem pediu já pode mandar o próximo prompt.
        if (answer.Data is { ValueKind: JsonValueKind.Object } agentData)
        {
            foreach (var property in agentData.EnumerateObject())
            {
                data[property.Name] = property.Value;
            }
        }

        return new RemoteOperationResult(answer.Succeeded, $"{summary} Claude: {answer.Summary}", answer.Code, Json(data));
    }

    private static RemoteOperationResult Failed(string summary, Dictionary<string, object?> data) =>
        new(false, summary, RemoteErrorCodes.ExecutionFailed, Json(data));

    private static JsonElement Json(Dictionary<string, object?> data) => JsonSerializer.SerializeToElement(data, RemoteJson.Options);

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
