namespace MyTaskApp.Application.Agents;

/// <summary>
/// Abre um terminal real do sistema executando um programa (ADR-029). O app não
/// vira terminal: o usuário continua conversando com o agente na janela que
/// abriu.
/// </summary>
/// <remarks>
/// A implementação é por sistema (hoje, só Windows). Trocar o terminal usado não
/// muda nada fora dela.
/// </remarks>
public interface ITerminalLauncher
{
    /// <summary>Nunca lança por falha ao abrir: ela volta em <see cref="TerminalLaunchResult.Error"/>.</summary>
    Task<TerminalLaunchResult> LaunchAsync(
        TerminalLaunchOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// O que executar. Programa e argumentos separados — nunca uma linha de shell
/// montada à mão, para caminho com espaço não virar dois argumentos.
/// </summary>
public sealed record TerminalLaunchOptions(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

public sealed record TerminalLaunchResult
{
    public bool Started { get; init; }

    /// <summary>O processo que representa a sessão: vive enquanto ela vive.</summary>
    public int ProcessId { get; init; }

    /// <summary>Quando o sistema diz que o processo começou — distingue PID reaproveitado.</summary>
    public DateTimeOffset ProcessStartedAt { get; init; }

    public string? Error { get; init; }

    public static TerminalLaunchResult Failed(string error) => new() { Error = error };
}

/// <summary>Traz para a frente a janela de terminal de um processo.</summary>
public interface ITerminalWindowManager
{
    /// <summary><c>false</c> quando o processo não tem janela que se ache.</summary>
    Task<bool> FocusAsync(int processId);
}

/// <summary>Os processos das sessões: vivos? E avisar quando acabarem.</summary>
public interface IAgentProcessTracker
{
    /// <summary>
    /// Vivo e o mesmo: o PID existe <b>e</b> o processo começou em
    /// <paramref name="startedAt"/>. PID igual com outro início é reaproveitamento.
    /// </summary>
    bool IsAlive(int processId, DateTimeOffset startedAt);

    /// <summary>
    /// Chama <paramref name="onExited"/> quando o processo terminar, sem polling.
    /// <c>null</c> se ele já não existe; descartar o retorno para de vigiar
    /// (e não encerra o processo).
    /// </summary>
    IDisposable? WatchExit(int processId, DateTimeOffset startedAt, Action onExited);
}
