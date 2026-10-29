namespace MyTaskApp.Application.Terminals;

/// <summary>
/// Colunas e linhas de um terminal (ADR-098). Os limites cortam pedido absurdo
/// de longe antes de chegar ao sistema.
/// </summary>
public readonly record struct TerminalSize(int Columns, int Rows)
{
    public const int MinColumns = 20;

    public const int MaxColumns = 500;

    public const int MinRows = 5;

    public const int MaxRows = 300;

    /// <summary>O tamanho de partida, o mesmo do console clássico do Windows.</summary>
    public static TerminalSize Default { get; } = new(120, 30);

    public bool IsValid =>
        Columns is >= MinColumns and <= MaxColumns && Rows is >= MinRows and <= MaxRows;

    /// <summary>Encaixa nos limites: quem pede 10 colunas recebe 20, e não um erro.</summary>
    public TerminalSize Clamp() => new(
        Math.Clamp(Columns, MinColumns, MaxColumns),
        Math.Clamp(Rows, MinRows, MaxRows));

    public override string ToString() => $"{Columns}x{Rows}";
}

/// <summary>
/// O que iniciar num pseudoterminal. Programa e argumentos separados, como no
/// <c>TerminalLaunchOptions</c>: nunca uma linha de shell montada à mão.
/// </summary>
/// <param name="Environment">Por cima do ambiente herdado; <c>null</c> = só o herdado.</param>
/// <param name="RemovedVariables">
/// Tiradas do ambiente herdado, antes dos acréscimos: as marcas de uma sessão
/// do Claude que abriu o agente não passam para o Claude que o agente abre.
/// </param>
public sealed record PseudoTerminalStart(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment,
    TerminalSize Size,
    IReadOnlyCollection<string>? RemovedVariables = null);

/// <summary>Se este sistema tem pseudoterminal, e por que não quando não tem.</summary>
public sealed record PseudoTerminalSupport(bool IsSupported, string Platform, string? Reason)
{
    public static PseudoTerminalSupport Unsupported(string platform, string reason) => new(false, platform, reason);
}

/// <summary>
/// Abre processos num terminal de verdade (ConPTY no Windows, <c>openpty</c> no
/// Linux): o programa vê um TTY, desenha a própria tela e lê o teclado como
/// leria numa janela de terminal (ADR-098).
/// </summary>
public interface IPseudoTerminalFactory
{
    PseudoTerminalSupport Support { get; }

    /// <summary>
    /// Inicia o processo. A falha esperada (sem suporte, executável ou pasta
    /// inexistente, o sistema recusou) é <c>DomainException</c> com a mensagem
    /// que o usuário lê.
    /// </summary>
    IPseudoTerminal Start(PseudoTerminalStart start);
}

/// <summary>
/// Um processo num pseudoterminal. A saída é o fluxo de bytes que um terminal
/// desenharia (texto UTF-8 e sequências VT); a entrada é o que o teclado
/// mandaria.
/// </summary>
/// <remarks>
/// Três jeitos de acabar, e eles não se confundem:
/// <list type="bullet">
/// <item><see cref="Close"/>: fecha o terminal, como fechar a janela. O programa
/// recebe o aviso do sistema e tem a chance de sair direito.</item>
/// <item><see cref="Kill"/>: encerra à força o processo e o que ele abriu, e só
/// isso (o job da sessão no Windows, o grupo de processos no Linux).</item>
/// <item><see cref="IAsyncDisposable.DisposeAsync"/>: solta tudo; o que ainda
/// estiver vivo morre junto.</item>
/// </list>
/// </remarks>
public interface IPseudoTerminal : IAsyncDisposable
{
    int ProcessId { get; }

    /// <summary>O início segundo o sistema: é o que distingue PID reaproveitado (ADR-030).</summary>
    DateTimeOffset ProcessStartedAt { get; }

    TerminalSize Size { get; }

    /// <summary>Lê a saída. <c>0</c> quando o terminal fechou e não há mais nada.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>Escreve como se fosse o teclado.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    void Resize(TerminalSize size);

    /// <summary>Completa quando o processo acaba, com o exit code quando o sistema o informa.</summary>
    Task<int?> Exited { get; }

    void Close();

    void Kill();
}
