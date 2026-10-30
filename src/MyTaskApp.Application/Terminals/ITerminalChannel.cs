namespace MyTaskApp.Application.Terminals;

/// <summary>
/// Uma vista ligada a uma sessão de terminal (ADR-098, ADR-099): o canal local
/// desta máquina ou o relay do controle remoto. Quem desenha a tela não sabe
/// por qual dos dois os bytes chegam.
/// </summary>
public interface ITerminalChannel : IAsyncDisposable
{
    /// <summary>A próxima mensagem, em ordem; <c>null</c> quando o outro lado fechou.</summary>
    ValueTask<TerminalMessage?> ReadAsync(CancellationToken cancellationToken = default);

    Task SendAsync(TerminalInput input, CancellationToken cancellationToken = default);

    /// <summary>Encerrar a sessão: <paramref name="force"/> mata a árvore dela; sem, é como fechar a janela.</summary>
    Task StopAsync(bool force, CancellationToken cancellationToken = default);
}
