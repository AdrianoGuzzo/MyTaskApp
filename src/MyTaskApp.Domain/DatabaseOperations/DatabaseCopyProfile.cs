namespace MyTaskApp.Domain.DatabaseOperations;

/// <summary>As escolhas de um perfil de cópia, de uma vez — evita oito booleanos soltos na assinatura.</summary>
public sealed record DatabaseCopyOptions(
    bool RequireAnonymization,
    bool IncludeSchema,
    bool IncludeData,
    bool RecreateDestination,
    bool VerifyAfterRestore,
    bool KeepAnonymizedArtifact)
{
    public static DatabaseCopyOptions Default { get; } = new(
        RequireAnonymization: true,
        IncludeSchema: true,
        IncludeData: true,
        RecreateDestination: true,
        VerifyAfterRestore: true,
        KeepAnonymizedArtifact: false);
}

/// <summary>
/// Uma cópia que se repete (ADR-056): de onde, para onde, com que
/// anonimização e como. "ECO Produção → ECO Desenvolvimento" é cadastrada uma
/// vez e executada quantas vezes for preciso.
/// </summary>
/// <remarks>
/// Aqui ficam só as regras do próprio perfil. Se a origem pode ser origem, se
/// o destino é produção, se a anonimização está mesmo ativa no servidor — isso
/// é da <see cref="IDatabaseSecurityPolicy"/>, julgado com as conexões lidas do
/// banco a cada execução: um perfil válido ontem pode ser recusado hoje.
/// </remarks>
public sealed class DatabaseCopyProfile
{
    public const int MaxNameLength = 80;

    private DatabaseCopyProfile(Guid id, DateTimeOffset createdAt)
    {
        Id = id;
        Name = string.Empty;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; }

    public string Name { get; private set; }

    public Guid SourceConnectionId { get; private set; }

    public Guid DestinationConnectionId { get; private set; }

    public Guid? AnonymizationProfileId { get; private set; }

    public bool RequireAnonymization { get; private set; }

    public bool IncludeSchema { get; private set; }

    public bool IncludeData { get; private set; }

    /// <summary>Apagar e criar o banco de destino antes do restore. Sem isso, o restore falha no primeiro conflito.</summary>
    public bool RecreateDestination { get; private set; }

    public bool VerifyAfterRestore { get; private set; }

    /// <summary>Guardar o dump anônimo depois da cópia. O bruto nunca é guardado.</summary>
    public bool KeepAnonymizedArtifact { get; private set; }

    public bool IsEnabled { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DatabaseCopyOptions Options => new(
        RequireAnonymization,
        IncludeSchema,
        IncludeData,
        RecreateDestination,
        VerifyAfterRestore,
        KeepAnonymizedArtifact);

    public static DatabaseCopyProfile Create(
        string name,
        Guid sourceConnectionId,
        Guid destinationConnectionId,
        Guid? anonymizationProfileId,
        DatabaseCopyOptions options,
        DateTimeOffset createdAt)
    {
        var profile = new DatabaseCopyProfile(Guid.CreateVersion7(createdAt), createdAt) { IsEnabled = true };
        profile.Apply(name, sourceConnectionId, destinationConnectionId, anonymizationProfileId, options);
        return profile;
    }

    public void Update(
        string name,
        Guid sourceConnectionId,
        Guid destinationConnectionId,
        Guid? anonymizationProfileId,
        DatabaseCopyOptions options,
        DateTimeOffset at)
    {
        Apply(name, sourceConnectionId, destinationConnectionId, anonymizationProfileId, options);
        UpdatedAt = at;
    }

    public void SetEnabled(bool enabled, DateTimeOffset at)
    {
        if (IsEnabled == enabled)
        {
            return;
        }

        IsEnabled = enabled;
        UpdatedAt = at;
    }

    private void Apply(
        string name,
        Guid sourceConnectionId,
        Guid destinationConnectionId,
        Guid? anonymizationProfileId,
        DatabaseCopyOptions options)
    {
        var normalizedName = name?.Trim();

        if (string.IsNullOrEmpty(normalizedName))
        {
            throw new DomainException("Informe o nome do perfil de cópia.");
        }

        if (normalizedName.Length > MaxNameLength)
        {
            throw new DomainException($"O nome do perfil passa de {MaxNameLength} caracteres.");
        }

        if (sourceConnectionId == Guid.Empty || destinationConnectionId == Guid.Empty)
        {
            throw new DomainException("Escolha a origem e o destino.");
        }

        if (sourceConnectionId == destinationConnectionId)
        {
            throw new DomainException("A origem e o destino precisam ser conexões diferentes.");
        }

        if (!options.IncludeSchema && !options.IncludeData)
        {
            throw new DomainException("Escolha copiar a estrutura, os dados ou os dois.");
        }

        if (options.RequireAnonymization && anonymizationProfileId is null)
        {
            throw new DomainException("A cópia exige anonimização: escolha um perfil de anonimização.");
        }

        Name = normalizedName;
        SourceConnectionId = sourceConnectionId;
        DestinationConnectionId = destinationConnectionId;
        AnonymizationProfileId = anonymizationProfileId;
        RequireAnonymization = options.RequireAnonymization;
        IncludeSchema = options.IncludeSchema;
        IncludeData = options.IncludeData;
        RecreateDestination = options.RecreateDestination;
        VerifyAfterRestore = options.VerifyAfterRestore;
        KeepAnonymizedArtifact = options.KeepAnonymizedArtifact;
    }
}
