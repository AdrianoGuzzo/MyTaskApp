using System.Globalization;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.DatabaseOperations;

// Editar um perfil de anonimização por partes (ADR-059). A tela salva o perfil
// inteiro de uma vez; quem conversa com uma IA quer "troque a máscara desta
// coluna" — e isto faz exatamente isso, sobre o mesmo agregado e as mesmas
// validações do domínio, com pré-visualização e diff antes de gravar.

/// <summary>Uma coluna, como a regra a identifica.</summary>
public sealed record AnonymizationColumnKey(string Schema, string Table, string Column)
{
    public string Key => $"{Schema}.{Table}.{Column}";
}

/// <summary>Um perfil pelo id, com regras e tabelas sem dados.</summary>
public sealed record GetAnonymizationProfile(Guid Id);

public sealed class GetAnonymizationProfileHandler(IAnonymizationProfileRepository profiles)
{
    public async Task<AnonymizationProfileRow> HandleAsync(
        GetAnonymizationProfile query,
        CancellationToken cancellationToken = default) =>
        GetAnonymizationProfilesHandler.ToRow(await profiles.GetByIdAsync(query.Id, cancellationToken));
}

/// <summary>
/// O que mudar num perfil. Só o que vier preenchido muda; o resto do perfil —
/// inclusive todas as regras não citadas — fica exatamente como está.
/// </summary>
/// <remarks>
/// <see cref="AddRules"/> recusa coluna que já tem regra, e
/// <see cref="UpdateRules"/> e <see cref="RemoveRules"/> recusam coluna que não
/// tem: quem pediu para trocar uma máscara não pode criar uma regra por engano.
/// </remarks>
public sealed record ChangeAnonymizationProfile(Guid ProfileId)
{
    /// <summary>
    /// O <c>UpdatedAt</c> lido antes de propor a mudança. Obrigatório para
    /// gravar; se o perfil mudou desde então — na tela, por outra conversa —,
    /// nada é gravado.
    /// </summary>
    public DateTimeOffset? ExpectedUpdatedAt { get; init; }

    public string? Name { get; init; }

    public Change<string?>? Description { get; init; }

    public Guid? ConnectionId { get; init; }

    public bool? Enabled { get; init; }

    public IReadOnlyList<AnonymizationRuleRow> AddRules { get; init; } = [];

    public IReadOnlyList<AnonymizationRuleRow> UpdateRules { get; init; } = [];

    public IReadOnlyList<AnonymizationColumnKey> RemoveRules { get; init; } = [];

    public IReadOnlyList<SkippedTableRow> AddSkippedTables { get; init; } = [];

    public IReadOnlyList<SkippedTableRow> RemoveSkippedTables { get; init; } = [];

    /// <summary><c>false</c> = só pré-visualiza: monta o diff, valida e não grava nada.</summary>
    public bool Apply { get; init; }

    /// <summary>
    /// Também confere a proposta contra as colunas reais do banco (só leitura,
    /// política <c>InspectDatabase</c>). <see cref="Database"/>: o banco, quando
    /// a conexão é só o servidor.
    /// </summary>
    public bool ValidateAgainstDatabase { get; init; }

    public string? Database { get; init; }
}

public enum ChangeKind
{
    Added = 0,
    Changed = 1,
    Removed = 2,
}

public sealed record AnonymizationRuleChange(
    string Column,
    ChangeKind Kind,
    AnonymizationRuleRow? Before,
    AnonymizationRuleRow? After);

public sealed record SkippedTableChange(string Table, ChangeKind Kind);

public sealed record FieldChange(string Field, string? Before, string? After);

/// <param name="Applied">Gravado. <c>false</c> numa pré-visualização ou quando havia problema.</param>
/// <param name="Before">O perfil como estava gravado.</param>
/// <param name="After">Aplicado: o perfil como ficou gravado. Pré-visualização: como ficaria.</param>
/// <param name="Problems">O que impede gravar. Com algum, nada foi gravado.</param>
/// <param name="DatabaseValidation">A proposta contra o banco, quando pedida.</param>
public sealed record AnonymizationProfileChangeResult(
    bool Applied,
    AnonymizationProfileRow Before,
    AnonymizationProfileRow After,
    IReadOnlyList<FieldChange> Fields,
    IReadOnlyList<AnonymizationRuleChange> Rules,
    IReadOnlyList<SkippedTableChange> SkippedTables,
    IReadOnlyList<string> Problems,
    AnonymizationProfileValidation? DatabaseValidation)
{
    public bool HasChanges => Fields.Count > 0 || Rules.Count > 0 || SkippedTables.Count > 0;
}

public sealed class ChangeAnonymizationProfileHandler(
    IAnonymizationProfileRepository profiles,
    IDatabaseConnectionRepository connections,
    AnonymizationProfileValidator validator,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<ChangeAnonymizationProfileHandler> logger)
{
    public async Task<AnonymizationProfileChangeResult> HandleAsync(
        ChangeAnonymizationProfile command,
        CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetByIdAsync(command.ProfileId, cancellationToken);
        var before = GetAnonymizationProfilesHandler.ToRow(profile);
        var now = timeProvider.GetUtcNow();
        var problems = new List<string>();

        if (command.Apply && command.ExpectedUpdatedAt is null)
        {
            // Gravar exige ter lido: é o que impede uma proposta montada sobre o
            // perfil de ontem de apagar o que a tela mudou hoje.
            problems.Add(
                $"Para gravar, informe expectedUpdatedAt com o updatedAt lido do perfil ({profile.UpdatedAt:O}). " +
                "Consulte o perfil antes de alterar.");
        }

        if (command.ExpectedUpdatedAt is { } expected && expected.UtcTicks != profile.UpdatedAt.UtcTicks)
        {
            problems.Add(
                $"O perfil {profile.Name} mudou desde a leitura (gravado em {profile.UpdatedAt:O}). " +
                "Consulte de novo e refaça a proposta.");
        }

        var rules = before.Rules.ToDictionary(rule => rule.ColumnKey, StringComparer.Ordinal);
        var order = before.Rules.Select(rule => rule.ColumnKey).ToList();

        foreach (var rule in command.AddRules)
        {
            if (!rules.TryAdd(rule.ColumnKey, rule))
            {
                problems.Add($"{rule.ColumnKey} já tem regra: use a alteração de regra.");
                continue;
            }

            order.Add(rule.ColumnKey);
        }

        foreach (var rule in command.UpdateRules)
        {
            if (!rules.ContainsKey(rule.ColumnKey))
            {
                problems.Add($"{rule.ColumnKey} não tem regra para alterar: use a inclusão de regra.");
                continue;
            }

            rules[rule.ColumnKey] = rule;
        }

        foreach (var column in command.RemoveRules)
        {
            if (!rules.Remove(column.Key))
            {
                problems.Add($"{column.Key} não tem regra para remover.");
            }
        }

        var skipped = before.SkippedTables.ToDictionary(table => table.TableKey, StringComparer.Ordinal);

        foreach (var table in command.AddSkippedTables)
        {
            if (!skipped.TryAdd(table.TableKey, table))
            {
                problems.Add($"{table.TableKey} já está nas tabelas sem dados.");
            }
        }

        foreach (var table in command.RemoveSkippedTables)
        {
            if (!skipped.Remove(table.TableKey))
            {
                problems.Add($"{table.TableKey} não está nas tabelas sem dados.");
            }
        }

        var name = command.Name ?? profile.Name;
        var description = command.Description is { } changed ? changed.Value : profile.Description;
        var connectionId = command.ConnectionId ?? profile.ConnectionId;
        var enabled = command.Enabled ?? profile.IsEnabled;
        var proposedRules = order.Where(rules.ContainsKey).Select(key => rules[key]).ToList();
        var proposedSkipped = skipped.Values.ToList();

        if (command.ConnectionId is { } newConnection && await connections.FindByIdAsync(newConnection, cancellationToken) is null)
        {
            problems.Add("Conexão de banco não encontrada.");
        }

        if (command.Name is not null
            && await AnonymizationProfileNames.TakenAsync(profiles, name, profile.Id, cancellationToken))
        {
            problems.Add($"Já existe um perfil de anonimização chamado {name.Trim()}.");
        }

        // As mesmas validações de gravar, num rascunho: nome, argumentos de cada
        // máscara, colunas repetidas. O rascunho nunca chega ao repositório.
        AnonymizationProfile? draft = null;

        try
        {
            draft = AnonymizationProfile.Create(name, description, connectionId, now);
            draft.ReplaceRules(proposedRules.Select(rule => rule.ToSpec()), now);
            draft.ReplaceSkippedTables(proposedSkipped.Select(table => table.ToSpec()), now);
        }
        catch (DomainException exception)
        {
            problems.Add(exception.Message);
            draft = null;
        }

        var proposed = draft is null
            ? before with
            {
                Name = name,
                Description = description,
                ConnectionId = connectionId,
                IsEnabled = enabled,
                Rules = proposedRules,
                SkippedTables = proposedSkipped,
            }
            : GetAnonymizationProfilesHandler.ToRow(draft) with { Id = profile.Id, IsEnabled = enabled, UpdatedAt = profile.UpdatedAt };

        var fields = Diff.Fields(before, proposed);
        var ruleChanges = Diff.Rules(before.Rules, proposed.Rules);
        var tableChanges = Diff.SkippedTables(before.SkippedTables, proposed.SkippedTables);

        AnonymizationProfileValidation? databaseValidation = null;

        if (command.ValidateAgainstDatabase && draft is not null)
        {
            databaseValidation = await validator.ValidateAsync(draft, profile.Id, command.Database, cancellationToken);
        }

        if (!command.Apply || problems.Count > 0 || (fields.Count == 0 && ruleChanges.Count == 0 && tableChanges.Count == 0))
        {
            return new AnonymizationProfileChangeResult(
                Applied: false, before, proposed, fields, ruleChanges, tableChanges, problems, databaseValidation);
        }

        profile.Update(name, description, connectionId, now);
        profile.SetEnabled(enabled, now);

        if (ruleChanges.Count > 0)
        {
            profile.ReplaceRules(proposedRules.Select(rule => rule.ToSpec()), now);
        }

        if (tableChanges.Count > 0)
        {
            profile.ReplaceSkippedTables(proposedSkipped.Select(table => table.ToSpec()), now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "AnonymizationProfileChanged {ProfileId} {Fields} {RuleChanges} {SkippedTableChanges}",
            profile.Id,
            fields.Count,
            ruleChanges.Count,
            tableChanges.Count);

        return new AnonymizationProfileChangeResult(
            Applied: true,
            before,
            GetAnonymizationProfilesHandler.ToRow(profile),
            fields,
            ruleChanges,
            tableChanges,
            [],
            databaseValidation);
    }
}

/// <summary>Criar uma variante: as mesmas regras e tabelas sem dados, com outro nome.</summary>
public sealed record DuplicateAnonymizationProfile(Guid Id, string Name, Guid? ConnectionId = null);

public sealed class DuplicateAnonymizationProfileHandler(
    IAnonymizationProfileRepository profiles,
    SaveAnonymizationProfileHandler save)
{
    public async Task<Guid> HandleAsync(DuplicateAnonymizationProfile command, CancellationToken cancellationToken = default)
    {
        var source = GetAnonymizationProfilesHandler.ToRow(await profiles.GetByIdAsync(command.Id, cancellationToken));

        // Pelo caminho de criar da tela: nome único, conexão existente, regras válidas.
        return await save.HandleAsync(
            new SaveAnonymizationProfile(
                null,
                command.Name,
                source.Description,
                command.ConnectionId ?? source.ConnectionId,
                source.Rules)
            {
                SkippedTables = source.SkippedTables,
            },
            cancellationToken);
    }
}

/// <summary>As diferenças entre dois perfis.</summary>
public sealed record CompareAnonymizationProfiles(Guid FirstId, Guid SecondId);

/// <param name="Rules">Do primeiro para o segundo: "Added" só existe no segundo.</param>
public sealed record AnonymizationProfileComparison(
    AnonymizationProfileRow First,
    AnonymizationProfileRow Second,
    IReadOnlyList<FieldChange> Fields,
    IReadOnlyList<AnonymizationRuleChange> Rules,
    IReadOnlyList<SkippedTableChange> SkippedTables,
    int IdenticalRules);

public sealed class CompareAnonymizationProfilesHandler(IAnonymizationProfileRepository profiles)
{
    public async Task<AnonymizationProfileComparison> HandleAsync(
        CompareAnonymizationProfiles query,
        CancellationToken cancellationToken = default)
    {
        var first = GetAnonymizationProfilesHandler.ToRow(await profiles.GetByIdAsync(query.FirstId, cancellationToken));
        var second = GetAnonymizationProfilesHandler.ToRow(await profiles.GetByIdAsync(query.SecondId, cancellationToken));
        var rules = Diff.Rules(first.Rules, second.Rules);
        var secondKeys = second.Rules.Select(rule => rule.ColumnKey).ToHashSet(StringComparer.Ordinal);

        return new AnonymizationProfileComparison(
            first,
            second,
            Diff.Fields(first, second).Where(field => field.Field != "name").ToList(),
            rules,
            Diff.SkippedTables(first.SkippedTables, second.SkippedTables),
            first.Rules.Count(rule => secondKeys.Contains(rule.ColumnKey))
                - rules.Count(change => change.Kind is ChangeKind.Changed));
    }
}

/// <summary>Quem usa um perfil de anonimização: apelidos e perfis de cópia.</summary>
public sealed record GetAnonymizationProfileUsage(Guid Id);

public sealed record ProfileUsage(
    IReadOnlyList<SavedDatabaseRow> Aliases,
    IReadOnlyList<DatabaseCopyProfileRow> CopyProfiles)
{
    public bool IsUsed => Aliases.Count > 0 || CopyProfiles.Count > 0;
}

/// <summary>Quem usa uma conexão: perfis de anonimização, apelidos e perfis de cópia.</summary>
public sealed record GetDatabaseConnectionUsage(Guid Id);

public sealed record ConnectionUsage(
    IReadOnlyList<AnonymizationProfileRow> AnonymizationProfiles,
    IReadOnlyList<SavedDatabaseRow> Aliases,
    IReadOnlyList<DatabaseCopyProfileRow> CopyProfilesAsSource,
    IReadOnlyList<DatabaseCopyProfileRow> CopyProfilesAsDestination)
{
    /// <summary>Usada = a exclusão seria recusada.</summary>
    public bool IsUsed => AnonymizationProfiles.Count > 0 || Aliases.Count > 0
        || CopyProfilesAsSource.Count > 0 || CopyProfilesAsDestination.Count > 0;
}

public sealed class DatabaseUsageHandlers(
    IDatabaseConnectionRepository connections,
    IAnonymizationProfileRepository profiles,
    GetSavedDatabasesHandler aliases,
    GetDatabaseCopyProfilesHandler copyProfiles)
{
    public async Task<ProfileUsage> HandleAsync(GetAnonymizationProfileUsage query, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetByIdAsync(query.Id, cancellationToken);

        return new ProfileUsage(
            (await aliases.HandleAsync(new GetSavedDatabases(), cancellationToken))
                .Where(alias => alias.AnonymizationProfileId == profile.Id)
                .ToList(),
            (await copyProfiles.HandleAsync(new GetDatabaseCopyProfiles(), cancellationToken))
                .Where(copy => copy.AnonymizationProfileId == profile.Id)
                .ToList());
    }

    public async Task<ConnectionUsage> HandleAsync(GetDatabaseConnectionUsage query, CancellationToken cancellationToken = default)
    {
        var connection = await connections.GetByIdAsync(query.Id, cancellationToken);
        var copies = await copyProfiles.HandleAsync(new GetDatabaseCopyProfiles(), cancellationToken);

        return new ConnectionUsage(
            (await profiles.ListAsync(cancellationToken))
                .Where(profile => profile.ConnectionId == connection.Id)
                .Select(GetAnonymizationProfilesHandler.ToRow)
                .ToList(),
            (await aliases.HandleAsync(new GetSavedDatabases(), cancellationToken))
                .Where(alias => alias.ConnectionId == connection.Id)
                .ToList(),
            copies.Where(copy => copy.SourceConnectionId == connection.Id).ToList(),
            copies.Where(copy => copy.DestinationConnectionId == connection.Id).ToList());
    }
}

/// <summary>Copiar uma conexão para outra com outro nome — sem a senha, que não sai do cofre.</summary>
public sealed record DuplicateDatabaseConnection(Guid Id, string Name);

public sealed class DuplicateDatabaseConnectionHandler(
    IDatabaseConnectionRepository connections,
    SaveDatabaseConnectionHandler save)
{
    public async Task<Guid> HandleAsync(DuplicateDatabaseConnection command, CancellationToken cancellationToken = default)
    {
        var source = await connections.GetByIdAsync(command.Id, cancellationToken);

        // Senha nula: a cópia nasce sem senha, e quem quiser a define na tela.
        return await save.HandleAsync(
            new SaveDatabaseConnection(
                null,
                command.Name,
                source.Host,
                source.Port,
                source.Database,
                source.Username,
                source.Environment,
                source.SslMode,
                source.Description,
                source.Permissions,
                Password: null),
            cancellationToken);
    }
}

// --- Validação e análise contra o banco ------------------------------------------

/// <summary>
/// Validar um perfil contra as colunas reais do banco, sem copiar nada (ADR-059).
/// <see cref="Database"/>: o banco, quando a conexão é só o servidor; vazio, o
/// de um apelido que usa o perfil.
/// </summary>
public sealed record ValidateAnonymizationProfile(Guid Id, string? Database = null);

/// <param name="IsValid">Sem <see cref="Problems"/>: uma cópia com este perfil passaria pela validação das máscaras.</param>
/// <param name="Problems">Comprovados contra o catálogo: impedem a cópia.</param>
/// <param name="Warnings">Não impedem, mas mudam o resultado.</param>
/// <param name="UncoveredCandidates">Colunas que parecem dado pessoal e não têm regra — heurística por nome e comentário.</param>
public sealed record AnonymizationProfileValidation(
    Guid ProfileId,
    string ConnectionName,
    string Database,
    DateTimeOffset ValidatedAt,
    bool IsValid,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ColumnSuggestion> UncoveredCandidates,
    int Tables,
    int Columns,
    int MaskedColumns,
    int SkippedTables);

/// <summary>
/// Monta o catálogo e roda o <see cref="MaskingPlanner"/> — o mesmo julgamento
/// que a cópia faz antes de começar. Só leitura: a sessão do Npgsql é somente
/// leitura duas vezes (ADR-056), e nada aqui grava.
/// </summary>
public sealed class AnonymizationProfileValidator(
    IDatabaseConnectionRepository connections,
    ISavedDatabaseRepository savedDatabases,
    IPostgresMaskedCopier copier,
    IDatabaseSecurityPolicy policy,
    TimeProvider timeProvider)
{
    internal async Task<AnonymizationProfileValidation> ValidateAsync(
        AnonymizationProfile profile,
        Guid storedProfileId,
        string? database,
        CancellationToken cancellationToken)
    {
        var connection = (await connections.GetByIdAsync(profile.ConnectionId, cancellationToken)).Snapshot();

        if (string.IsNullOrWhiteSpace(database) && connection.Database is null)
        {
            // O banco do apelido que usa este perfil: é onde ele vai ser aplicado.
            database = (await savedDatabases.ListAsync(cancellationToken))
                .FirstOrDefault(alias => alias.AnonymizationProfileId == storedProfileId
                    && alias.ConnectionId == connection.Id)
                ?.DatabaseName;
        }

        var resolved = DatabaseChoice.Resolve(connection, database);
        policy.Demand(new DatabaseOperationRequest(DatabaseOperationType.InspectDatabase, resolved));

        var catalog = await copier.ReadCatalogAsync(resolved, cancellationToken);
        var validation = MaskingPlanner.Plan(profile, catalog);

        return new AnonymizationProfileValidation(
            storedProfileId,
            resolved.Name,
            resolved.Database ?? string.Empty,
            timeProvider.GetUtcNow(),
            validation.IsValid,
            validation.Problems,
            validation.Warnings,
            validation.UncoveredCandidates,
            catalog.Tables.Count,
            catalog.AllColumns.Count,
            validation.MaskedColumns,
            validation.Tables.Count(table => table.SkipData));
    }
}

public sealed class ValidateAnonymizationProfileHandler(
    IAnonymizationProfileRepository profiles,
    AnonymizationProfileValidator validator)
{
    public async Task<AnonymizationProfileValidation> HandleAsync(
        ValidateAnonymizationProfile query,
        CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetByIdAsync(query.Id, cancellationToken);

        return await validator.ValidateAsync(profile, profile.Id, query.Database, cancellationToken);
    }
}

/// <summary>A análise de um perfil: o que está comprovado, o que é risco e o que falta saber.</summary>
public sealed record AnalyzeAnonymizationProfile(Guid Id, string? Database = null);

/// <param name="Proven">Problemas comprovados pela validação contra o banco.</param>
/// <param name="StaticRisks">Riscos que a configuração mostra sozinha, sem olhar o banco.</param>
/// <param name="NeedsInformation">O que a análise não consegue afirmar sem mais informação.</param>
/// <param name="State">
/// Em que estado está o perfil: "configurado no MyTaskApp" sempre; "validado"
/// só quando <see cref="Validation"/> veio e não tem problema. Não existe
/// "aplicado ao banco": as máscaras entram no SELECT de cada cópia (ADR-058).
/// </param>
public sealed record AnonymizationProfileAnalysis(
    AnonymizationProfileRow Profile,
    string State,
    AnonymizationProfileValidation? Validation,
    IReadOnlyList<string> Proven,
    IReadOnlyList<string> StaticRisks,
    IReadOnlyList<string> NeedsInformation,
    ProfileUsage Usage,
    IReadOnlyDictionary<string, int> RulesByMethod,
    IReadOnlyList<string> TablesWithRules);

public sealed class AnalyzeAnonymizationProfileHandler(
    IAnonymizationProfileRepository profiles,
    AnonymizationProfileValidator validator,
    DatabaseUsageHandlers usage)
{
    /// <summary>
    /// Fixo de propósito: ter um perfil não é estar de acordo com a LGPD, e
    /// nenhuma análise automática daqui pode afirmar isso.
    /// </summary>
    public const string ComplianceNotice =
        "Ter um perfil de anonimização não garante conformidade com a LGPD: a detecção de dado pessoal é heurística " +
        "(nome e comentário da coluna), não lê o conteúdo, e texto livre, JSON e anexos podem carregar dado pessoal sem parecer.";

    public async Task<AnonymizationProfileAnalysis> HandleAsync(
        AnalyzeAnonymizationProfile query,
        CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetByIdAsync(query.Id, cancellationToken);
        var row = GetAnonymizationProfilesHandler.ToRow(profile);
        var needs = new List<string> { ComplianceNotice };
        AnonymizationProfileValidation? validation = null;

        try
        {
            validation = await validator.ValidateAsync(profile, profile.Id, query.Database, cancellationToken);
        }
        catch (DomainException exception)
        {
            // Sem banco, a análise segue só com o que a configuração mostra.
            needs.Add($"Não foi possível conferir contra o banco: {exception.Message}");
        }

        var risks = StaticRisks(row).ToList();

        if (validation is not null)
        {
            if (validation.UncoveredCandidates.Count > 0)
            {
                risks.Add(
                    $"{validation.UncoveredCandidates.Count} coluna(s) parecem dado pessoal e não têm regra " +
                    $"({validation.UncoveredCandidates.Count(candidate => candidate.Sensitivity == ColumnSensitivity.High)} de alta sensibilidade).");
            }

            needs.Add(
                $"{validation.Columns - validation.MaskedColumns} coluna(s) do banco vão sem máscara; " +
                "a lista de candidatas cobre só as que parecem dado pessoal pelo nome.");
        }
        else
        {
            needs.Add("Sem a validação, chaves, unicidade, tipos e NOT NULL das colunas não foram conferidos.");
        }

        var state = validation switch
        {
            null => "Configurado no MyTaskApp; não validado contra o banco.",
            { IsValid: true } => $"Configurado no MyTaskApp e validado contra {validation.ConnectionName}/{validation.Database} em {validation.ValidatedAt:O}.",
            _ => $"Configurado no MyTaskApp; a validação contra {validation.ConnectionName}/{validation.Database} encontrou problemas.",
        };

        return new AnonymizationProfileAnalysis(
            row,
            state + " As máscaras não são instaladas no banco: entram no SELECT de cada cópia (ADR-058).",
            validation,
            validation?.Problems ?? [],
            risks,
            needs,
            await usage.HandleAsync(new GetAnonymizationProfileUsage(profile.Id), cancellationToken),
            row.Rules
                .GroupBy(rule => rule.Method.ToString())
                .ToDictionary(group => group.Key, group => group.Count()),
            row.Rules
                .Select(rule => $"{rule.Schema}.{rule.Table}")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList());
    }

    /// <summary>O que a configuração, sozinha, já deixa ver.</summary>
    private static IEnumerable<string> StaticRisks(AnonymizationProfileRow profile)
    {
        if (!profile.IsEnabled)
        {
            yield return "O perfil está desabilitado: nenhuma cópia o usa até ser habilitado.";
        }

        if (profile.Rules.Count == 0)
        {
            yield return "O perfil não tem regra: uma cópia com ele levaria todos os dados como estão.";
        }

        foreach (var rule in profile.Rules)
        {
            var info = MaskingCatalog.Of(rule.Method);

            if (rule.Method == MaskingMethod.Partial && rule.Sensitivity == ColumnSensitivity.High)
            {
                var (start, end) = MaskingCatalog.PartialKeep(rule.Argument);
                yield return $"{rule.ColumnKey}: a máscara parcial mantém {start} caractere(s) do início e {end} do fim de um dado de alta sensibilidade.";
            }

            if (rule.Method is MaskingMethod.DateShift or MaskingMethod.NumberNoise && rule.Sensitivity == ColumnSensitivity.High)
            {
                yield return $"{rule.ColumnKey}: \"{info.Label}\" desloca o valor real, e não o esconde — o valor original fica próximo.";
            }

            if (!info.KeepsUniqueness && LooksLikeIdentifier(rule.Column))
            {
                yield return $"{rule.ColumnKey}: \"{info.Label}\" repete valores; se a coluna tiver índice único a cópia falha (a validação contra o banco confirma).";
            }
        }
    }

    private static bool LooksLikeIdentifier(string column)
    {
        var name = column.ToLowerInvariant();

        return name is "cpf" or "cnpj" or "email" or "login" or "username" or "documento"
            || name.EndsWith("_cpf", StringComparison.Ordinal)
            || name.EndsWith("_email", StringComparison.Ordinal);
    }
}

internal static class AnonymizationProfileNames
{
    /// <summary>
    /// O nome já é de outro perfil? O índice é NOCASE no banco; a pergunta vem
    /// antes, para a resposta ser uma frase e não uma violação de índice.
    /// </summary>
    public static async Task<bool> TakenAsync(
        IAnonymizationProfileRepository profiles,
        string name,
        Guid? exceptId,
        CancellationToken cancellationToken) =>
        (await profiles.ListAsync(cancellationToken)).Any(profile =>
            profile.Id != exceptId
            && string.Equals(profile.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
}

internal static class Diff
{
    public static IReadOnlyList<FieldChange> Fields(AnonymizationProfileRow before, AnonymizationProfileRow after)
    {
        var changes = new List<FieldChange>();

        void Compare(string field, string? left, string? right)
        {
            if (!string.Equals(left, right, StringComparison.Ordinal))
            {
                changes.Add(new FieldChange(field, left, right));
            }
        }

        Compare("name", before.Name, after.Name);
        Compare("description", before.Description, after.Description);
        Compare("connectionId", before.ConnectionId.ToString(), after.ConnectionId.ToString());
        Compare("enabled", before.IsEnabled.ToString(CultureInfo.InvariantCulture), after.IsEnabled.ToString(CultureInfo.InvariantCulture));

        return changes;
    }

    public static IReadOnlyList<AnonymizationRuleChange> Rules(
        IReadOnlyList<AnonymizationRuleRow> before,
        IReadOnlyList<AnonymizationRuleRow> after)
    {
        var left = before.ToDictionary(rule => rule.ColumnKey, StringComparer.Ordinal);
        var right = after.ToDictionary(rule => rule.ColumnKey, StringComparer.Ordinal);
        var changes = new List<AnonymizationRuleChange>();

        foreach (var (key, rule) in left)
        {
            if (!right.TryGetValue(key, out var other))
            {
                changes.Add(new AnonymizationRuleChange(key, ChangeKind.Removed, rule, null));
            }
            else if (rule.Method != other.Method
                || !string.Equals(rule.Argument, other.Argument, StringComparison.Ordinal)
                || rule.Sensitivity != other.Sensitivity)
            {
                changes.Add(new AnonymizationRuleChange(key, ChangeKind.Changed, rule, other));
            }
        }

        changes.AddRange(right
            .Where(entry => !left.ContainsKey(entry.Key))
            .Select(entry => new AnonymizationRuleChange(entry.Key, ChangeKind.Added, null, entry.Value)));

        return changes.OrderBy(change => change.Column, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<SkippedTableChange> SkippedTables(
        IReadOnlyList<SkippedTableRow> before,
        IReadOnlyList<SkippedTableRow> after)
    {
        var left = before.Select(table => table.TableKey).ToHashSet(StringComparer.Ordinal);
        var right = after.Select(table => table.TableKey).ToHashSet(StringComparer.Ordinal);

        return left.Except(right).Select(table => new SkippedTableChange(table, ChangeKind.Removed))
            .Concat(right.Except(left).Select(table => new SkippedTableChange(table, ChangeKind.Added)))
            .OrderBy(change => change.Table, StringComparer.Ordinal)
            .ToList();
    }
}
