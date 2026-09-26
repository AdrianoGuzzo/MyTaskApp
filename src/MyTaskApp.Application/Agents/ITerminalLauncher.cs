namespace MyTaskApp.Application.Agents;

/// <summary>
/// Abre um terminal real do sistema executando um programa (ADR-030). O app não
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
/// <param name="Environment">
/// Variáveis acrescentadas ao ambiente herdado do app — é por elas que o
/// agente sabe de qual tarefa é (ADR-037). <c>null</c> = só o herdado.
/// </param>
public sealed record TerminalLaunchOptions(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null);

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

    /// <summary>
    /// A janela do terminal do processo é a que está em primeiro plano? Serve
    /// para não avisar quem já está olhando para o agente (ADR-037). Na dúvida,
    /// <c>false</c>: avisar à toa é melhor que calar quando precisava.
    /// </summary>
    Task<bool> IsInForegroundAsync(int processId);

    /// <summary>
    /// Faz a janela do terminal piscar na barra de tarefas até o usuário ir até
    /// ela (ADR-037). O aviso no canto some quando dispensado; a pendência
    /// continua visível onde ela de fato está. <c>false</c> quando não há janela.
    /// </summary>
    Task<bool> FlashAsync(int processId);

    /// <summary>Para de piscar: o agente voltou a trabalhar antes de o usuário olhar.</summary>
    Task StopFlashingAsync(int processId);
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
