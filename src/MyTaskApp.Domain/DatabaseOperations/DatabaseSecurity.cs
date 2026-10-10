namespace MyTaskApp.Domain.DatabaseOperations;

/// <summary>Por que a política recusou. Cada código é uma regra do ADR-056 (e da ADR-058, para as máscaras).</summary>
public enum SecurityViolationCode
{
    MissingSource = 1,
    MissingDestination,
    SourceDisabled,
    DestinationDisabled,
    SourceNotAllowed,
    DestinationNotAllowed,
    MissingPermission,
    ProtectedTargetModification,
    DestinationIsProduction,
    ProductionToProduction,
    SameEndpoint,
    DestinationMatchesProtectedEndpoint,
    DestinationEnvironmentNotAllowed,
    AnonymizationRequired,
    PlainDumpFromProtectedSource,
    AnonymizationProfileMissing,
    AnonymizationProfileDisabled,
    AnonymizationProfileEmpty,

    /// <summary>Alguma regra não cabe no banco de origem: coluna que sumiu, tipo errado, chave, único (ADR-058).</summary>
    MaskingRulesInvalid,
    UncoveredHighCandidates,
    VerificationRequired,
    KeepArtifactForbidden,
    NothingToCopy,
    StaticMaskingForbidden,
    SqlExecutionForbidden,
    ProtectedSystemDatabase,
    UnsupportedOperation,

    /// <summary>Cópia de produção sem a confirmação explícita (ou, na crítica, sem o nome digitado).</summary>
    ConfirmationRequired,

    /// <summary>A conexão é só o servidor (ADR-057) e nenhum banco foi escolhido.</summary>
    DatabaseNotChosen,
}

public sealed record SecurityViolation(SecurityViolationCode Code, string Message);

/// <summary>O julgamento: permitido só sem nenhuma violação.</summary>
public sealed record SecurityDecision(IReadOnlyList<SecurityViolation> Violations)
{
    public static SecurityDecision Allowed { get; } = new([]);

    public bool IsAllowed => Violations.Count == 0;

    public bool Has(SecurityViolationCode code) => Violations.Any(violation => violation.Code == code);

    public string Describe() => string.Join(" ", Violations.Select(violation => violation.Message).Distinct());
}

/// <summary>
/// O que se sabe da anonimização de uma cópia. O julgamento das regras contra
/// as colunas da origem (<see cref="SourceChecked"/>) chega depois de ler o
/// catálogo; antes disso, a política julga só o cadastro, e julga de novo com os fatos.
/// </summary>
public sealed record AnonymizationFacts(
    bool ProfileExists,
    bool ProfileEnabled,
    int RuleCount,
    bool SourceChecked = false,
    IReadOnlyList<string>? RuleProblems = null,
    int UncoveredHighCandidates = 0)
{
    public static AnonymizationFacts None { get; } = new(false, false, 0);

    /// <summary>Os mesmos fatos do cadastro, agora com o que as colunas da origem disseram.</summary>
    public AnonymizationFacts WithSource(IReadOnlyList<string> ruleProblems, int uncoveredHigh) =>
        this with
        {
            SourceChecked = true,
            RuleProblems = ruleProblems,
            UncoveredHighCandidates = uncoveredHigh,
        };
}

/// <summary>
/// Uma operação a julgar. As conexões vêm do banco do app, lidas pelo handler
/// — nunca da tela. <see cref="ProtectedEndpoints"/> são as chaves de todo
/// banco cadastrado como produção: um destino "Desenvolvimento" que aponte
/// para um deles é produção com outro rótulo. <see cref="NewDestinationDatabase"/>
/// é a cópia para um banco novo, com nome gerado (ADR-057): só cria, nunca apaga.
/// </summary>
public sealed record DatabaseOperationRequest(
    DatabaseOperationType Operation,
    DatabaseConnectionSnapshot? Source = null,
    DatabaseConnectionSnapshot? Destination = null,
    AnonymizationFacts? Anonymization = null,
    DatabaseCopyOptions? Options = null,
    IReadOnlyCollection<string>? ProtectedEndpoints = null,
    bool NewDestinationDatabase = false);

/// <summary>Recusa da política de segurança. É um <see cref="DomainException"/>: a mensagem é para o usuário.</summary>
public sealed class DatabaseSecurityException(SecurityDecision decision) : DomainException(
    "Operação bloqueada pela política de segurança. " + decision.Describe())
{
    public SecurityDecision Decision { get; } = decision;
}

/// <summary>
/// A política central de segurança das operações de banco (ADR-056). Julga
/// origem, destino, tipo da operação, ambiente, permissões e anonimização.
/// </summary>
/// <remarks>
/// Fica no Domain porque é regra pura, e é chamada em três lugares: pelo
/// handler antes de tudo, pelo orquestrador antes de cada passo destrutivo e
/// pela Infrastructure logo antes de iniciar um processo. Esconder um botão
/// não protege nada; isto protege.
/// </remarks>
public interface IDatabaseSecurityPolicy
{
    /// <summary>Todas as violações, para a tela mostrar de uma vez.</summary>
    SecurityDecision Evaluate(DatabaseOperationRequest request);

    /// <summary>Lança <see cref="DatabaseSecurityException"/> se houver qualquer violação.</summary>
    void Demand(DatabaseOperationRequest request);
}

/// <inheritdoc />
public sealed class DatabaseSecurityPolicy : IDatabaseSecurityPolicy
{
    /// <summary>Bancos do próprio servidor: apagar ou recriar um deles derruba tudo.</summary>
    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "postgres",
        "template0",
        "template1",
    };

    public void Demand(DatabaseOperationRequest request)
    {
        var decision = Evaluate(request);

        if (!decision.IsAllowed)
        {
            throw new DatabaseSecurityException(decision);
        }
    }

    public SecurityDecision Evaluate(DatabaseOperationRequest request)
    {
        var violations = new Violations();

        switch (request.Operation)
        {
            case DatabaseOperationType.TestConnection:
                RequireTarget(request, violations);
                break;

            case DatabaseOperationType.InspectDatabase:
            case DatabaseOperationType.Diagnose:
                if (RequireTarget(request, violations) is { } inspected)
                {
                    RequireEnabled(inspected, isSource: true, violations);
                    RequirePermission(inspected, ConnectionPermission.Read, "ler", violations);
                }

                break;

            case DatabaseOperationType.Verify:
                ReadableSide(request.Source, isSource: true, violations);
                ReadableSide(request.Destination, isSource: false, violations);
                break;

            case DatabaseOperationType.Dump:
                if (CheckSource(request.Source, violations) is { } dumped
                    && dumped.Permissions.RequireAnonymization)
                {
                    violations.Add(
                        SecurityViolationCode.PlainDumpFromProtectedSource,
                        $"{dumped.Name} exige anonimização: os dados só saem mascarados.");
                }

                break;

            // Só a estrutura: nenhuma linha sai, então vale mesmo para quem exige anonimização.
            case DatabaseOperationType.SchemaDump:
                CheckSource(request.Source, violations);
                break;

            // Os dados de uma tabela, com as máscaras no SELECT: lê a origem, grava no destino.
            case DatabaseOperationType.MaskedDataCopy:
                if (CheckSource(request.Source, violations) is { } masked)
                {
                    CheckAnonymization(request, masked, violations);
                }

                CheckWritableTarget(request, ConnectionPermission.Restore, "receber os dados", violations);
                break;

            case DatabaseOperationType.AnonymousDump:
                violations.Add(
                    SecurityViolationCode.UnsupportedOperation,
                    "O dump pelo PostgreSQL Anonymizer não existe mais: a cópia mascara os dados na consulta.");
                break;

            case DatabaseOperationType.Restore:
                CheckWritableTarget(request, ConnectionPermission.Restore, "receber restore", violations);
                break;

            case DatabaseOperationType.CreateDatabase:
                CheckWritableTarget(request, ConnectionPermission.CreateDatabase, "criar o banco", violations);
                break;

            case DatabaseOperationType.DropDatabase:
                CheckWritableTarget(request, ConnectionPermission.DropDatabase, "apagar o banco", violations);
                break;

            case DatabaseOperationType.Copy:
            case DatabaseOperationType.CopyAndAnonymize:
                CheckCopy(request, violations);
                break;

            case DatabaseOperationType.StaticMasking:
                CheckInPlaceChange(
                    request,
                    SecurityViolationCode.StaticMaskingForbidden,
                    "Mascaramento estático reescreve os dados do próprio banco e é proibido em produção.",
                    violations);
                break;

            case DatabaseOperationType.ExecuteSql:
                CheckInPlaceChange(
                    request,
                    SecurityViolationCode.SqlExecutionForbidden,
                    "SQL livre é proibido em conexões de produção.",
                    violations);
                break;

            default:
                violations.Add(SecurityViolationCode.UnsupportedOperation, "Operação de banco desconhecida.");
                break;
        }

        return violations.Count == 0 ? SecurityDecision.Allowed : new SecurityDecision(violations.ToList());
    }

    private static void CheckCopy(DatabaseOperationRequest request, Violations violations)
    {
        var options = request.Options ?? DatabaseCopyOptions.Default;
        var source = CheckSource(request.Source, violations);
        var destination = CheckDestination(request, violations, ConnectionPermission.Restore, "receber restore");

        if (!options.IncludeSchema && !options.IncludeData)
        {
            violations.Add(SecurityViolationCode.NothingToCopy, "Escolha copiar a estrutura, os dados ou os dois.");
        }

        if (destination is not null && request.NewDestinationDatabase)
        {
            RequirePermission(destination, ConnectionPermission.CreateDatabase, "criar o banco", violations);
        }
        else if (destination is not null && options.RecreateDestination)
        {
            RequirePermission(destination, ConnectionPermission.DropDatabase, "apagar o banco para recriá-lo", violations);
            RequirePermission(destination, ConnectionPermission.CreateDatabase, "criar o banco", violations);
        }

        if (source is null || destination is null)
        {
            return;
        }

        // Até aqui, uma conexão sem banco ainda pode ser julgada (permissões,
        // ambiente). Para copiar, os dois lados precisam do banco resolvido.
        RequireDatabase(source, "origem", violations);
        RequireDatabase(destination, "destino", violations);

        var sourceRules = EnvironmentPolicy.For(source.Environment);

        if (source.Id == destination.Id || source.EndpointKey == destination.EndpointKey)
        {
            violations.Add(SecurityViolationCode.SameEndpoint, "A origem e o destino são o mesmo banco.");
        }

        if (source.IsProtected && destination.IsProtected)
        {
            violations.Add(SecurityViolationCode.ProductionToProduction, "Cópia de produção para produção é proibida.");
        }
        else if (!destination.IsProtected && !sourceRules.AllowedCopyDestinations.Contains(destination.Environment))
        {
            violations.Add(
                SecurityViolationCode.DestinationEnvironmentNotAllowed,
                $"Dados de {Label(source.Environment)} não podem ir para {Label(destination.Environment)}.");
        }

        var anonymizes = request.Operation == DatabaseOperationType.CopyAndAnonymize;
        var mustAnonymize = source.Permissions.RequireAnonymization || options.RequireAnonymization;

        if (mustAnonymize && !anonymizes)
        {
            violations.Add(
                SecurityViolationCode.AnonymizationRequired,
                $"{source.Name} exige anonimização: use Copiar + Anonimizar.");
        }

        if (anonymizes)
        {
            CheckAnonymization(request, source, violations);
        }

        if (sourceRules.RequiresVerification && !options.VerifyAfterRestore)
        {
            violations.Add(
                SecurityViolationCode.VerificationRequired,
                $"{source.Name} é produção crítica: a verificação depois do restore é obrigatória.");
        }

        if (!sourceRules.AllowsKeepingArtifact && options.KeepAnonymizedArtifact)
        {
            violations.Add(
                SecurityViolationCode.KeepArtifactForbidden,
                $"{source.Name} é produção crítica: o dump não pode ser guardado depois da cópia.");
        }
    }

    private static void CheckAnonymization(
        DatabaseOperationRequest request,
        DatabaseConnectionSnapshot source,
        Violations violations)
    {
        var facts = request.Anonymization ?? AnonymizationFacts.None;

        if (!facts.ProfileExists)
        {
            violations.Add(SecurityViolationCode.AnonymizationProfileMissing, "Escolha um perfil de anonimização.");
            return;
        }

        if (!facts.ProfileEnabled)
        {
            violations.Add(SecurityViolationCode.AnonymizationProfileDisabled, "O perfil de anonimização está desativado.");
        }

        if (facts.RuleCount == 0)
        {
            violations.Add(
                SecurityViolationCode.AnonymizationProfileEmpty,
                "O perfil de anonimização não tem nenhuma regra confirmada.");
        }

        if (!facts.SourceChecked)
        {
            return;
        }

        if (facts.RuleProblems is { Count: > 0 } problems)
        {
            violations.Add(
                SecurityViolationCode.MaskingRulesInvalid,
                "As máscaras não cabem no banco de origem: " + string.Join(" ", problems));
        }

        if (facts.UncoveredHighCandidates > 0 && EnvironmentPolicy.For(source.Environment).BlocksOnUncoveredHighCandidates)
        {
            violations.Add(
                SecurityViolationCode.UncoveredHighCandidates,
                $"{facts.UncoveredHighCandidates} coluna(s) com alta probabilidade de dado pessoal estão sem regra.");
        }
    }

    private static DatabaseConnectionSnapshot? CheckSource(DatabaseConnectionSnapshot? source, Violations violations)
    {
        if (source is null)
        {
            violations.Add(SecurityViolationCode.MissingSource, "Escolha a origem.");
            return null;
        }

        RequireEnabled(source, isSource: true, violations);

        if (!source.Permissions.AllowAsSource)
        {
            violations.Add(SecurityViolationCode.SourceNotAllowed, $"{source.Name} não pode ser usada como origem.");
        }

        RequirePermission(source, ConnectionPermission.Dump, "fazer dump", violations);
        return source;
    }

    private static DatabaseConnectionSnapshot? CheckDestination(
        DatabaseOperationRequest request,
        Violations violations,
        ConnectionPermission permission,
        string action)
    {
        if (request.Destination is not { } destination)
        {
            violations.Add(SecurityViolationCode.MissingDestination, "Escolha o destino.");
            return null;
        }

        RequireEnabled(destination, isSource: false, violations);

        if (destination.IsProtected)
        {
            violations.Add(
                SecurityViolationCode.DestinationIsProduction,
                $"{destination.Name} é produção: nunca pode ser destino nem ser alterada.");
        }

        if (!destination.Permissions.AllowAsDestination)
        {
            violations.Add(SecurityViolationCode.DestinationNotAllowed, $"{destination.Name} não pode ser usada como destino.");
        }

        RequirePermission(destination, permission, action, violations);
        CheckProtectedEndpoint(request, destination, violations);
        return destination;
    }

    private static void CheckWritableTarget(
        DatabaseOperationRequest request,
        ConnectionPermission permission,
        string action,
        Violations violations)
    {
        if (CheckDestination(request, violations, permission, action) is not { } target)
        {
            return;
        }

        if (target.Database is not { } database)
        {
            RequireDatabase(target, "destino", violations);
        }
        else if (SystemDatabases.Contains(database))
        {
            violations.Add(
                SecurityViolationCode.ProtectedSystemDatabase,
                $"{database} é um banco do próprio servidor e não pode ser alterado.");
        }
    }

    private static void RequireDatabase(DatabaseConnectionSnapshot connection, string side, Violations violations)
    {
        if (!connection.HasDatabase)
        {
            violations.Add(
                SecurityViolationCode.DatabaseNotChosen,
                $"{connection.Name} é só o servidor: escolha o banco de {side}.");
        }
    }

    /// <summary>Alteração no próprio banco — mascaramento estático, SQL livre.</summary>
    private static void CheckInPlaceChange(
        DatabaseOperationRequest request,
        SecurityViolationCode code,
        string protectedMessage,
        Violations violations)
    {
        if (RequireTarget(request, violations) is not { } target)
        {
            return;
        }

        if (target.IsProtected || IsProtectedEndpoint(request, target))
        {
            violations.Add(code, protectedMessage);
            violations.Add(
                SecurityViolationCode.ProtectedTargetModification,
                $"{target.Name} é produção: nenhuma alteração é permitida.");
            return;
        }

        RequireEnabled(target, isSource: false, violations);
        RequirePermission(target, ConnectionPermission.Modify, "ser alterada", violations);
        RequirePermission(target, ConnectionPermission.ExecuteSql, "executar SQL", violations);
    }

    private static void CheckProtectedEndpoint(
        DatabaseOperationRequest request,
        DatabaseConnectionSnapshot destination,
        Violations violations)
    {
        if (!destination.IsProtected && IsProtectedEndpoint(request, destination))
        {
            violations.Add(
                SecurityViolationCode.DestinationMatchesProtectedEndpoint,
                $"{destination.Name} aponta para o mesmo banco (ou servidor) de uma conexão de produção.");
        }
    }

    private static bool IsProtectedEndpoint(DatabaseOperationRequest request, DatabaseConnectionSnapshot target) =>
        target.IsAmong(request.ProtectedEndpoints);

    private static DatabaseConnectionSnapshot? RequireTarget(DatabaseOperationRequest request, Violations violations)
    {
        var target = request.Source ?? request.Destination;

        if (target is null)
        {
            violations.Add(SecurityViolationCode.MissingSource, "Escolha a conexão.");
        }

        return target;
    }

    private static void ReadableSide(DatabaseConnectionSnapshot? connection, bool isSource, Violations violations)
    {
        if (connection is null)
        {
            violations.Add(
                isSource ? SecurityViolationCode.MissingSource : SecurityViolationCode.MissingDestination,
                isSource ? "Escolha a origem." : "Escolha o destino.");
            return;
        }

        RequireEnabled(connection, isSource, violations);
        RequirePermission(connection, ConnectionPermission.Read, "ler", violations);
    }

    private static void RequireEnabled(DatabaseConnectionSnapshot connection, bool isSource, Violations violations)
    {
        if (!connection.IsEnabled)
        {
            violations.Add(
                isSource ? SecurityViolationCode.SourceDisabled : SecurityViolationCode.DestinationDisabled,
                $"A conexão {connection.Name} está desativada.");
        }
    }

    private static void RequirePermission(
        DatabaseConnectionSnapshot connection,
        ConnectionPermission permission,
        string action,
        Violations violations)
    {
        if (!connection.Permissions.Has(permission))
        {
            violations.Add(SecurityViolationCode.MissingPermission, $"{connection.Name} não tem permissão para {action}.");
        }
    }

    private static string Label(DatabaseEnvironment environment) => environment switch
    {
        DatabaseEnvironment.Development => "Desenvolvimento",
        DatabaseEnvironment.Test => "Teste",
        DatabaseEnvironment.Staging => "Homologação",
        DatabaseEnvironment.Production => "Produção",
        DatabaseEnvironment.CriticalProduction => "Produção crítica",
        _ => environment.ToString(),
    };

    private sealed class Violations : List<SecurityViolation>
    {
        public void Add(SecurityViolationCode code, string message) => Add(new SecurityViolation(code, message));
    }
}
