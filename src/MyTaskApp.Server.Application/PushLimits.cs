namespace MyTaskApp.Server.Application;

/// <summary>
/// Os tetos de armazenamento do push (ADR-072), em <c>Sync:Push</c>. Zero
/// desliga o teto.
/// </summary>
/// <remarks>
/// O campo de texto longo (a descrição da tarefa) não tem teto próprio: quem o
/// segura é o tamanho da entidade inteira, já mesclada.
/// </remarks>
public sealed class PushLimits
{
    /// <summary>
    /// O JSON de uma entidade, em bytes UTF-8. O padrão é o maior que ainda cabe
    /// no corpo de 1 MiB do push, já que a criação manda tudo junto.
    /// </summary>
    public int MaxEntityBytes { get; set; } = 768 * 1024;

    /// <summary>
    /// Entidades vivas do workspace: excluir libera. O tombstone fica de fora,
    /// porque nada o apaga; ele pesa só nos bytes.
    /// </summary>
    public long MaxWorkspaceEntities { get; set; } = 100_000;

    /// <summary>A soma do JSON das entidades do workspace, em bytes UTF-8.</summary>
    public long MaxWorkspaceBytes { get; set; } = 256L * 1024 * 1024;
}
