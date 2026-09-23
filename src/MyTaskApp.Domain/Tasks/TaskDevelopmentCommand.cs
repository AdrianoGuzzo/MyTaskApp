namespace MyTaskApp.Domain.Tasks;

/// <summary>
/// Uma etapa da lista de comandos pós-Worktree da tarefa (ADR-028): um comando
/// literal (<c>dotnet restore</c>) ou um <c>@alias</c> de comando global,
/// guardado como foi digitado e resolvido só na execução.
/// </summary>
public sealed class TaskDevelopmentCommand
{
    public const int MaxCommandLength = 2000;

    private TaskDevelopmentCommand(Guid id, Guid taskDevelopmentId, string command, int order)
    {
        Id = id;
        TaskDevelopmentId = taskDevelopmentId;
        Command = command;
        Order = order;
    }

    public Guid Id { get; }

    public Guid TaskDevelopmentId { get; }

    public string Command { get; private set; }

    /// <summary>A posição na lista, a partir de 0. A execução segue esta ordem.</summary>
    public int Order { get; private set; }

    internal static TaskDevelopmentCommand Create(Guid taskDevelopmentId, string command, int order, DateTimeOffset at) =>
        new(Guid.CreateVersion7(at), taskDevelopmentId, command, order);

    internal void Place(string command, int order)
    {
        Command = command;
        Order = order;
    }

    /// <summary>
    /// Apara e descarta as linhas em branco; recusa o que não cabe. Devolve a
    /// lista na ordem recebida.
    /// </summary>
    public static IReadOnlyList<string> NormalizeList(IEnumerable<string?>? commands)
    {
        var normalized = new List<string>();

        foreach (var command in commands ?? [])
        {
            var text = command?.Trim();

            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (text.Length > MaxCommandLength)
            {
                throw new DomainException($"Um comando não pode passar de {MaxCommandLength} caracteres.");
            }

            if (text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal))
            {
                throw new DomainException("Cada comando ocupa uma linha só. Use um item por etapa.");
            }

            normalized.Add(text);
        }

        return normalized;
    }
}
