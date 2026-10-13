namespace MyTaskApp.Application.Sync;

/// <summary>
/// A identidade desta instalação para a sincronização (ADR-060): um id
/// estável, que não é o nome da máquina, guardado fora do banco.
/// </summary>
/// <remarks>
/// Fora do banco de propósito: um banco copiado para outro computador (backup
/// restaurado, pasta sincronizada) não pode levar junto a identidade, senão
/// dois computadores falariam em nome do mesmo dispositivo.
/// </remarks>
public interface IDeviceIdentity
{
    Guid DeviceId { get; }

    /// <summary>O apelido sugerido no primeiro registro: o nome da máquina.</summary>
    string DefaultNickname { get; }

    /// <summary>
    /// Troca o id. Um dispositivo revogado não volta: reconectar esta
    /// instalação a registra como um dispositivo novo.
    /// </summary>
    void Rotate();
}

/// <summary>
/// "Há algo para sincronizar": a gravação avisa, o agendador espera. Vários
/// avisos antes de o agendador acordar valem um só.
/// </summary>
public interface ISyncTrigger
{
    void Signal();

    Task WaitAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="ISyncTrigger"/>
public sealed class SyncTrigger : ISyncTrigger
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Já havia um aviso pendente: o próximo ciclo pega os dois.
        }
    }

    public Task WaitAsync(CancellationToken cancellationToken) => _signal.WaitAsync(cancellationToken);
}
