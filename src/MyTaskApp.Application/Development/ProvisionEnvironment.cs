using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Development;

/// <summary>
/// As etapas do ambiente preparado sem tela, na ordem. Cada uma é um
/// checkpoint: a retomada sabe onde parou, e a falha diz em qual foi.
/// </summary>
public enum ProvisionStage
{
    /// <summary>Validar, buscar o remoto, atualizar a origem e calcular a pasta. Não grava nada.</summary>
    Prepare,

    /// <summary>Criar (ou adotar) o worktree e gravar o ambiente na tarefa.</summary>
    Worktree,

    /// <summary>Os comandos pós-Worktree (ADR-028).</summary>
    Commands,
}

/// <summary>Um aviso do caminho: a etapa, e o detalhe dela quando há.</summary>
public sealed record ProvisionProgress(ProvisionStage Stage, DevelopmentProgress? Step = null, CommandStepProgress? Command = null);

/// <summary>
/// "Iniciar implementação" sem ninguém para perguntar (E4): as decisões que a
/// tela pergunta vêm como dados.
/// </summary>
/// <param name="SourceRef">A ref completa da origem, como em <see cref="PrepareDevelopment"/>.</param>
/// <param name="OnConflict">O que fazer se a pasta do worktree já existe; a tela pergunta.</param>
/// <param name="Commands">A lista pós-Worktree gravada no ambiente. Nula: mantém a que estava.</param>
/// <param name="RunCommands">Rodar a lista no fim, com o worktree pronto.</param>
public sealed record ProvisionEnvironment(
    Guid TaskId,
    string DirectoryPath,
    string SourceRef,
    string NewBranch,
    WorktreeConflictPolicy OnConflict,
    Guid? DevelopmentId = null,
    string? SourceTag = null,
    IReadOnlyList<string>? Commands = null,
    bool RunCommands = true);

/// <param name="Adopted">O worktree já estava lá e foi adotado: é como a retomada não refaz a etapa.</param>
public sealed record ProvisionResult(TaskDevelopmentView Development, bool Adopted, CommandRunSummary Commands);

/// <summary>
/// A sequência de "Iniciar implementação" (ADR-027, ADR-028, ADR-031), com os
/// mesmos casos de uso da tela, um escopo cada: preparar, criar o worktree e
/// rodar os comandos pós-Worktree. A tela orquestra os mesmos passos, porque
/// entre eles faz perguntas; este é o caminho do agente (E4).
/// </summary>
/// <remarks>
/// Não segura escopo entre as etapas: o <c>git fetch</c> e o
/// <c>git worktree add</c> demoram, e o banco é de mais de um processo (T-07).
/// O worktree criado fica, mesmo que os comandos falhem: é a regra da tela.
/// </remarks>
public sealed class ProvisionEnvironmentHandler(IUseCaseRunner runner)
{
    public async Task<ProvisionResult> HandleAsync(
        ProvisionEnvironment command,
        IProgress<ProvisionProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        // Um @alias que não existe é descoberto antes do Git, como na tela.
        if (command.Commands is [_, ..] entries)
        {
            await runner.RunAsync<ValidateCommandEntriesHandler, IReadOnlyList<ResolvedCommand>>(
                (handler, token) => handler.HandleAsync(new ValidateCommandEntries(entries), token),
                cancellationToken);
        }

        var steps = Forward(progress, ProvisionStage.Prepare);
        progress?.Report(new ProvisionProgress(ProvisionStage.Prepare));

        var plan = await runner.RunAsync<PrepareDevelopmentHandler, DevelopmentPlan>(
            (handler, token) => handler.HandleAsync(
                new PrepareDevelopment(
                    command.TaskId, command.DirectoryPath, command.SourceRef, command.NewBranch, command.DevelopmentId, command.SourceTag),
                steps,
                token),
            cancellationToken);

        var (path, adopt) = Resolve(plan, command.OnConflict);
        progress?.Report(new ProvisionProgress(ProvisionStage.Worktree));

        // Sem token: criar o worktree não se interrompe (ver StartDevelopmentHandler).
        var development = await runner.RunAsync<StartDevelopmentHandler, TaskDevelopmentView>(
            (handler, token) => handler.HandleAsync(
                new StartDevelopment(plan, path, adopt, command.Commands),
                Forward(progress, ProvisionStage.Worktree),
                token),
            CancellationToken.None);

        var summary = command.RunCommands
            ? await RunCommandsAsync(development, progress, cancellationToken)
            : CommandRunSummary.Empty;

        return new ProvisionResult(development, adopt, summary);
    }

    /// <summary>
    /// A lista pós-Worktree do ambiente. O "Preparar tudo" chama à parte,
    /// depois da cópia do banco, para os comandos já terem o <c>{database}</c> (ADR-060).
    /// </summary>
    public async Task<CommandRunSummary> RunCommandsAsync(
        TaskDevelopmentView development,
        IProgress<ProvisionProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (development.Commands is not [_, ..])
        {
            return CommandRunSummary.Empty;
        }

        progress?.Report(new ProvisionProgress(ProvisionStage.Commands));
        var commands = new InlineProgress<CommandStepProgress>(step => progress?.Report(new ProvisionProgress(ProvisionStage.Commands, Command: step)));

        return await runner.RunAsync<RunDevelopmentCommandsHandler, CommandRunSummary>(
            (handler, token) => handler.HandleAsync(new RunDevelopmentCommands(development.TaskId, development.Id), commands, token),
            cancellationToken);
    }

    /// <summary>
    /// A pergunta da tela, respondida pela política: falhar, adotar o worktree
    /// que já está lá (só se for da mesma branch), ou ir para a pasta sugerida.
    /// </summary>
    internal static (string Path, bool Adopt) Resolve(DevelopmentPlan plan, WorktreeConflictPolicy policy)
    {
        if (plan.Conflict is not { } conflict)
        {
            return (plan.WorktreePath, false);
        }

        switch (policy)
        {
            case WorktreeConflictPolicy.AdoptExisting when conflict.CanAdopt
                && string.Equals(conflict.Registered!.BranchName, plan.NewBranch, StringComparison.Ordinal):
                return (conflict.Path, true);

            case WorktreeConflictPolicy.AdoptExisting:
                throw new DevelopmentStepException(
                    DevelopmentStep.PlanWorktreePath,
                    conflict.Registered?.BranchName is { } other
                        ? $"A pasta do worktree ({conflict.Path}) já tem outro worktree, da branch {other}. Nada foi criado."
                        : $"A pasta do worktree ({conflict.Path}) já existe e não é um worktree deste repositório. Nada foi criado.");

            case WorktreeConflictPolicy.UseSuggestedPath:
                return (conflict.SuggestedPath, false);

            default:
                throw new DevelopmentStepException(
                    DevelopmentStep.PlanWorktreePath,
                    $"A pasta do worktree ({conflict.Path}) já existe. Nada foi criado.");
        }
    }

    private static InlineProgress<DevelopmentProgress>? Forward(IProgress<ProvisionProgress>? progress, ProvisionStage stage) =>
        progress is null ? null : new(step => progress.Report(new ProvisionProgress(stage, step)));

    /// <summary>Sem o contexto de sincronização do <see cref="Progress{T}"/>: o agente não tem tela.</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
