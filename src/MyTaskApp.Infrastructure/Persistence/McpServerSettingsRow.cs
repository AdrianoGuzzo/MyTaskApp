namespace MyTaskApp.Infrastructure.Persistence;

/// <summary>
/// As configurações do servidor MCP (ADR-059), em linha única. POCO pela mesma
/// razão do <see cref="DataRetentionSettingsRow"/>: o <c>McpServerSettings</c>
/// valida a porta no construtor, e uma linha corrompida precisa poder ser lida
/// antes de ser recusada.
/// </summary>
internal sealed class McpServerSettingsRow
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public bool Enabled { get; set; }

    public int Port { get; set; }

    public bool StartWithApp { get; set; }

    public bool ReadOnly { get; set; }
}
