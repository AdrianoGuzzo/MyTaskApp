namespace MyTaskApp.Infrastructure.Persistence;

/// <summary>
/// O controle remoto nesta instalação (ADR-087, RS-10), em linha única. Só
/// deste computador: não há mapper de sync para a tabela.
/// </summary>
internal sealed class RemoteSettingsRow
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public bool Enabled { get; set; }

    public bool AcceptControl { get; set; }
}
