using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Application.Tags;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>O histórico dos comandos rápidos em memória (ADR-051).</summary>
internal sealed class FakeCommandExecutionRepository : ICommandExecutionRepository, IUnitOfWork
{
    private readonly List<CommandExecution> _executions = [];

    public IReadOnlyList<CommandExecution> Executions => _executions;

    public int SaveCount { get; private set; }

    /// <summary>O estado de cada gravação, na ordem: prova o "grava antes de abrir".</summary>
    public List<CommandExecutionStatus[]> Saved { get; } = [];

    public void Seed(params CommandExecution[] executions) => _executions.AddRange(executions);

    public Task AddAsync(CommandExecution execution, CancellationToken cancellationToken = default)
    {
        _executions.Add(execution);
        return Task.CompletedTask;
    }

    public Task<CommandExecution?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_executions.Find(execution => execution.Id == id));

    public Task<IReadOnlyList<CommandExecution>> ListActiveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CommandExecution>>([.. _executions.Where(execution => execution.IsActive)]);

    public Task<IReadOnlyList<CommandExecution>> ListForDevelopmentAsync(
        Guid developmentId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CommandExecution>>(
        [
            .. _executions
                .Where(execution => execution.TaskDevelopmentId == developmentId)
                .OrderByDescending(execution => execution.StartedAt)
                .ThenByDescending(execution => execution.Id),
        ]);

    public void Remove(CommandExecution execution) => _executions.Remove(execution);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;
        Saved.Add([.. _executions.Select(execution => execution.Status)]);
        return Task.CompletedTask;
    }
}

/// <summary>Abre "terminais" sem abrir nada; o processo fica vivo no tracker.</summary>
internal sealed class FakeTerminalCommandLauncher(FakeAgentProcessTracker processes, DateTimeOffset now)
    : ITerminalCommandLauncher
{
    private int _nextProcessId = 24680;

    public List<TerminalCommandRequest> Launched { get; } = [];

    public string? FailWith { get; set; }

    public bool ExitsImmediately { get; set; }

    public int LastProcessId { get; private set; }

    public Task<TerminalLaunchResult> LaunchAsync(
        TerminalCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        Launched.Add(request);

        if (FailWith is not null)
        {
            return Task.FromResult(TerminalLaunchResult.Failed(FailWith));
        }

        LastProcessId = _nextProcessId++;

        if (!ExitsImmediately)
        {
            processes.Run(LastProcessId, now);
        }

        return Task.FromResult(new TerminalLaunchResult
        {
            Started = true,
            ProcessId = LastProcessId,
            ProcessStartedAt = now,
        });
    }
}

/// <summary>Registra o que os casos de uso avisam, sem vigiar nada de verdade.</summary>
internal sealed class FakeCommandExecutionWatcher : ICommandExecutionWatcher
{
    private readonly Dictionary<Guid, CancellationTokenSource> _inProcess = [];

    public List<CommandExecutionWatch> Watched { get; } = [];

    public List<Guid> Notified { get; } = [];

    /// <summary>Quais execuções estavam "neste processo" quando algo as consultou.</summary>
    public List<Guid> SeenInProcess { get; } = [];

    public void Watch(CommandExecutionWatch watch) => Watched.Add(watch);

    public void NotifyChanged(Guid taskId) => Notified.Add(taskId);

    public IDisposable TrackInProcess(Guid executionId, CancellationTokenSource cancellation)
    {
        _inProcess[executionId] = cancellation;
        return new Registration(this, executionId);
    }

    public bool IsInProcess(Guid executionId)
    {
        var inProcess = _inProcess.ContainsKey(executionId);

        if (inProcess)
        {
            SeenInProcess.Add(executionId);
        }

        return inProcess;
    }

    public bool Cancel(Guid executionId)
    {
        if (!_inProcess.TryGetValue(executionId, out var cancellation))
        {
            return false;
        }

        cancellation.Cancel();
        return true;
    }

    private sealed class Registration(FakeCommandExecutionWatcher owner, Guid executionId) : IDisposable
    {
        public void Dispose() => owner._inProcess.Remove(executionId);
    }
}

/// <summary>As consultas de etiqueta, com os diretórios que oferecem comando semeados à mão.</summary>
internal sealed class FakeTagQuery : ITagQuery
{
    public List<CommandDirectoryRow> CommandDirectories { get; } = [];

    public Task<IReadOnlyList<TagRow>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TagRow>>([]);

    public Task<IReadOnlyList<TagDirectoryRow>> ListDirectoriesAsync(
        Guid tagId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TagDirectoryRow>>([]);

    public Task<IReadOnlyList<TagDirectoryRow>> ListDirectoriesForTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TagDirectoryRow>>([]);

    public Task<IReadOnlyList<TagDirectoryCommandRow>> ListDirectoryCommandsAsync(
        Guid directoryId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TagDirectoryCommandRow>>(
            [.. CommandDirectories.Where(directory => directory.Id == directoryId).SelectMany(directory => directory.Commands)]);

    public Task<IReadOnlyList<CommandDirectoryRow>> ListCommandDirectoriesAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CommandDirectoryRow>>([.. CommandDirectories]);
}
