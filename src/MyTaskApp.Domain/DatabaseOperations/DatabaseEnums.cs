namespace MyTaskApp.Domain.DatabaseOperations;

/// <summary>
/// O ambiente de uma conexão (ADR-056). Decide o teto das permissões e para
/// onde os dados podem ir — ver <see cref="EnvironmentPolicy"/>. Os valores vão
/// para o banco: não reordene.
/// </summary>
public enum DatabaseEnvironment
{
    Development = 1,

    Test = 2,

    Staging = 3,

    /// <summary>Só leitura e dump anônimo; nunca destino.</summary>
    Production = 4,

    /// <summary>Produção, e ainda: só para Test/Staging, verificação obrigatória, nome digitado para confirmar.</summary>
    CriticalProduction = 5,
}

/// <summary>
/// O que uma operação faz com o banco. A política de segurança decide por
/// este tipo; os valores vão para a auditoria: não reordene.
/// </summary>
public enum DatabaseOperationType
{
    TestConnection = 1,

    InspectDatabase = 2,

    /// <summary>Dump com os dados como estão. Proibido para quem exige anonimização.</summary>
    Dump = 3,

    /// <summary>Dump feito por uma role mascarada: o dado sai anonimizado do servidor.</summary>
    AnonymousDump = 4,

    Restore = 5,

    CreateDatabase = 6,

    DropDatabase = 7,

    Copy = 8,

    CopyAndAnonymize = 9,

    Verify = 10,

    Diagnose = 11,

    /// <summary>
    /// <c>anon.anonymize_database()</c> e afins: reescreve os dados no próprio
    /// banco. O app não oferece; existe para a política recusar com nome.
    /// </summary>
    StaticMasking = 12,

    /// <summary>SQL livre. O app não tem console; existe para a política recusar com nome.</summary>
    ExecuteSql = 13,
}

/// <summary>Como terminou uma operação de banco. Os valores vão para o banco: não reordene.</summary>
public enum DatabaseOperationStatus
{
    /// <summary>Gravada antes do primeiro processo, para uma queda não deixar operação sem rastro.</summary>
    Running = 1,

    Succeeded = 2,

    Failed = 3,

    Canceled = 4,

    /// <summary>Recusada pela política de segurança antes de qualquer comando.</summary>
    Blocked = 5,

    /// <summary>O app fechou ou caiu no meio; achada como "em andamento" na volta.</summary>
    Interrupted = 6,
}

/// <summary>O <c>sslmode</c> da conexão. Os valores vão para o banco: não reordene.</summary>
public enum DatabaseSslMode
{
    /// <summary>Usa SSL se o servidor oferecer — o padrão do próprio PostgreSQL.</summary>
    Prefer = 1,

    Require = 2,

    /// <summary>SSL com certificado e nome do host conferidos.</summary>
    VerifyFull = 3,

    Disable = 4,
}

/// <summary>Como uma coluna é mascarada pelo PostgreSQL Anonymizer. Os valores vão para o banco: não reordene.</summary>
public enum MaskingKind
{
    /// <summary><c>MASKED WITH FUNCTION anon.xxx(...)</c>.</summary>
    Function = 1,

    /// <summary><c>MASKED WITH VALUE ...</c>: um literal ou <c>NULL</c>.</summary>
    Value = 2,
}

/// <summary>
/// O quanto uma coluna parece dado pessoal. Só sugestão: quem confirma é o
/// usuário. Os valores vão para o banco: não reordene.
/// </summary>
public enum ColumnSensitivity
{
    Low = 1,

    Medium = 2,

    High = 3,
}
