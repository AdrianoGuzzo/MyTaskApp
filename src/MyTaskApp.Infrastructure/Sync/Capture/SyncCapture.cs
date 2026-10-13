namespace MyTaskApp.Infrastructure.Sync.Capture;

/// <summary>
/// Liga a captura quando a instalação está conectada a uma conta. Desligada,
/// o interceptor não lê nem grava nada: quem nunca conectou não paga nada.
/// </summary>
internal sealed class SyncCaptureSwitch
{
    private volatile bool _isLinked;

    public bool IsLinked => _isLinked;

    public void Set(bool isLinked) => _isLinked = isLinked;
}

/// <summary>
/// "O que estou gravando veio do servidor": dentro deste escopo, o
/// interceptor não gera operação de saída. É a segunda barreira — a
/// aplicação remota grava por SQL, que já não passa pelo <c>SaveChanges</c>.
/// </summary>
internal static class SyncSuppression
{
    private static readonly AsyncLocal<int> Depth = new();

    public static bool IsActive => Depth.Value > 0;

    public static IDisposable Enter()
    {
        Depth.Value++;
        return new Exit();
    }

    private sealed class Exit : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Depth.Value--;
            }
        }
    }
}
