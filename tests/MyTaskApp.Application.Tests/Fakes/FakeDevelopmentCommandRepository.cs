using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>Comandos globais em memória, como <see cref="FakeTagRepository"/>.</summary>
internal sealed class FakeDevelopmentCommandRepository : IDevelopmentCommandRepository, IUnitOfWork
{
    private readonly Dictionary<Guid, DevelopmentCommand> _commands = [];

    public IReadOnlyCollection<DevelopmentCommand> Commands => _commands.Values;

    public int SaveCount { get; private set; }

    /// <summary>Em quantos diretórios cada comando é botão (ADR-051).</summary>
    public Dictionary<Guid, int> Bindings { get; } = [];

    public DevelopmentCommand Seed(
        string alias,
        string command,
        string? description = null,
        DevelopmentCommandSettings? settings = null)
    {
        var created = DevelopmentCommand.Create(
            alias,
            command,
            description,
            settings ?? DevelopmentCommandSettings.Default,
            FakeCommandExecutor.Started);
        _commands[created.Id] = created;
        return created;
    }

    public Task AddAsync(DevelopmentCommand command, CancellationToken cancellationToken = default)
    {
        _commands[command.Id] = command;
        return Task.CompletedTask;
    }

    public Task<DevelopmentCommand?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_commands.GetValueOrDefault(id));

    /// <summary>Um comando só do diretório (ADR-054).</summary>
    public DevelopmentCommand SeedForDirectory(
        Guid directoryId,
        string name,
        string command,
        DevelopmentCommandSettings? settings = null)
    {
        var created = DevelopmentCommand.CreateForDirectory(
            directoryId,
            command,
            null,
            (settings ?? DevelopmentCommandSettings.Default) with { Name = name },
            FakeCommandExecutor.Started);
        _commands[created.Id] = created;
        return created;
    }

    public Task<IReadOnlyList<DevelopmentCommand>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DevelopmentCommand>>(
            [.. _commands.Values.Where(command => command.IsGlobal).OrderBy(command => command.Alias, StringComparer.OrdinalIgnoreCase)]);

    public Task<IReadOnlyList<DevelopmentCommand>> ListForDirectoriesAsync(
        IReadOnlyCollection<Guid> directoryIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DevelopmentCommand>>(
            [.. _commands.Values.Where(command => command.TagDirectoryId is { } owner && directoryIds.Contains(owner))]);

    /// <summary>Sem diferenciar maiúsculas, como a coluna NOCASE do banco.</summary>
    public Task<bool> AliasExistsAsync(
        string alias,
        Guid? exceptId = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_commands.Values.Any(command =>
            string.Equals(command.Alias, alias, StringComparison.OrdinalIgnoreCase)
            && command.Id != exceptId));

    public void Remove(DevelopmentCommand command) => _commands.Remove(command.Id);

    public Task<IReadOnlyDictionary<Guid, int>> CountBindingsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, int>>(new Dictionary<Guid, int>(Bindings));

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}
