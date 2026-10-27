namespace MyTaskApp.Application.Abstractions;

/// <summary>Um processo, pelo PID e pelo início: o PID sozinho se repete depois que o processo sai.</summary>
public sealed record ProcessStamp(int ProcessId, DateTimeOffset StartedAt)
{
    /// <summary>O início lido de novo varia alguns milissegundos (o Linux o calcula a partir do boot).</summary>
    public static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

    public bool IsSameAs(ProcessStamp other) =>
        ProcessId == other.ProcessId && (StartedAt - other.StartedAt).Duration() < StartTolerance;
}

/// <summary>
/// Este processo e os outros deste computador (ADR-085): o desktop e o agente
/// abrem o mesmo banco, e o que um deixou "em andamento" pode ser do outro, vivo.
/// </summary>
public interface IProcessOwnership
{
    ProcessStamp Current { get; }

    /// <summary>Na dúvida (o sistema não deixa perguntar), vivo: quem pergunta espera, e não apaga.</summary>
    bool IsAlive(ProcessStamp process);
}
