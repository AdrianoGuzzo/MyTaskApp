namespace MyTaskApp.Desktop.Composition;

/// <summary>
/// As cópias de banco que rodam fora da janela Bancos de Dados — as da aba
/// Desenvolvimento (ADR-060). Fechando o app, todas param, e a limpeza do
/// diretório temporário roda no caminho de volta de cada uma.
/// </summary>
public sealed class DatabaseCopyRuns
{
    private readonly Lock _gate = new();

    private readonly HashSet<CancellationTokenSource> _runs = [];

    /// <summary>Acompanha <paramref name="run"/> até o retorno ser descartado.</summary>
    public IDisposable Track(CancellationTokenSource run)
    {
        lock (_gate)
        {
            _runs.Add(run);
        }

        return new Release(this, run);
    }

    public void CancelAll()
    {
        CancellationTokenSource[] runs;

        lock (_gate)
        {
            runs = [.. _runs];
        }

        foreach (var run in runs)
        {
            try
            {
                run.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Terminou entre a cópia da lista e o cancelamento.
            }
        }
    }

    private sealed class Release(DatabaseCopyRuns owner, CancellationTokenSource run) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._runs.Remove(run);
            }
        }
    }
}
