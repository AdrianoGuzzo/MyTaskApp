namespace MyTaskApp.Mcp.Hosting;

public enum McpActivityOutcome
{
    /// <summary>A ferramenta respondeu.</summary>
    Succeeded = 0,

    /// <summary>A regra de negócio recusou — a mensagem é a que o usuário leria na tela.</summary>
    Rejected = 1,

    /// <summary>Erro inesperado: o detalhe está no log do app, nunca na resposta.</summary>
    Failed = 2,

    /// <summary>Barrado antes de chegar à ferramenta: sem token, host ou origem errados, somente leitura.</summary>
    Denied = 3,

    /// <summary>Do próprio servidor: subiu, parou, falhou ao subir.</summary>
    Server = 4,
}

/// <param name="Operation">A ferramenta, o recurso, o prompt — ou "http" para o que foi barrado na porta.</param>
/// <param name="Detail">Nunca os argumentos: podem carregar o que não deve ir para a tela nem para o log.</param>
public sealed record McpActivityEntry(
    DateTimeOffset At,
    string Operation,
    McpActivityOutcome Outcome,
    TimeSpan Elapsed,
    string? Detail);

/// <summary>
/// As últimas operações do servidor, para a tela "Logs recentes" (ADR-059). Em
/// memória e limitado: o histórico de verdade é o arquivo de log do app.
/// </summary>
public sealed class McpActivityLog
{
    public const int Capacity = 200;

    private readonly Lock _gate = new();
    private readonly LinkedList<McpActivityEntry> _entries = new();

    /// <summary>Dispara na thread de quem registrou.</summary>
    public event Action? Changed;

    public void Add(McpActivityEntry entry)
    {
        lock (_gate)
        {
            _entries.AddFirst(entry);

            while (_entries.Count > Capacity)
            {
                _entries.RemoveLast();
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Da mais recente para a mais antiga.</summary>
    public IReadOnlyList<McpActivityEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToList();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }

        Changed?.Invoke();
    }
}
