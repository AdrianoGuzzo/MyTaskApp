namespace MyTaskApp.Infrastructure.PostgreSql;

/// <summary>
/// Limites das operações de banco (ADR-056), na seção <c>DatabaseOperations</c>
/// do appsettings. São botões de implantação, não preferência do usuário (ADR-014).
/// </summary>
public sealed class DatabaseOperationsOptions
{
    public const string SectionName = "DatabaseOperations";

    /// <summary>O <c>--version</c> de cada ferramenta.</summary>
    public int VersionTimeoutSeconds { get; set; } = 10;

    public int CreateDropTimeoutSeconds { get; set; } = 120;

    /// <summary>Um banco de centenas de GB leva horas; o teto evita um processo esquecido para sempre.</summary>
    public int DumpTimeoutMinutes { get; set; } = 360;

    public int RestoreTimeoutMinutes { get; set; } = 360;

    public int ListTimeoutSeconds { get; set; } = 120;

    /// <summary>Conexão ao servidor (libpq e Npgsql).</summary>
    public int ConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>Cada consulta do diagnóstico e da verificação.</summary>
    public int QueryTimeoutSeconds { get; set; } = 120;

    /// <summary>Quanto o <c>pg_dump</c> espera por um lock antes de desistir: nunca fica na fila de um ALTER em produção.</summary>
    public int LockWaitTimeoutSeconds { get; set; } = 60;

    /// <summary>Outra pasta para os diretórios temporários; vazia usa a pasta local do usuário.</summary>
    public string? WorkspaceDirectory { get; set; }

    internal TimeSpan VersionTimeout => TimeSpan.FromSeconds(Math.Max(1, VersionTimeoutSeconds));

    internal TimeSpan CreateDropTimeout => TimeSpan.FromSeconds(Math.Max(5, CreateDropTimeoutSeconds));

    internal TimeSpan DumpTimeout => TimeSpan.FromMinutes(Math.Max(1, DumpTimeoutMinutes));

    internal TimeSpan RestoreTimeout => TimeSpan.FromMinutes(Math.Max(1, RestoreTimeoutMinutes));

    internal TimeSpan ListTimeout => TimeSpan.FromSeconds(Math.Max(5, ListTimeoutSeconds));
}
