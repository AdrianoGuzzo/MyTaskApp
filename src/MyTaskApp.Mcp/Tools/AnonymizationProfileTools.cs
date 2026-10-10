using System.ComponentModel;
using ModelContextProtocol.Server;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Mcp.Hosting;

namespace MyTaskApp.Mcp.Tools;

/// <summary>
/// Perfis de anonimização (ADR-058, ADR-059): consultar, analisar, validar e
/// alterar por partes. Toda alteração começa como pré-visualização
/// (<c>apply=false</c>): o diff e os problemas voltam, e nada é gravado. Com
/// <c>apply=true</c>, grava pelos mesmos casos de uso da tela e relê o perfil
/// do banco para confirmar o que ficou.
/// </summary>
/// <remarks>
/// Nada aqui instala regra no banco, executa SQL ou copia dado: as máscaras
/// entram no SELECT de cada cópia, e a cópia não é oferecida pelo MCP.
/// </remarks>
[McpServerToolType]
public sealed class AnonymizationProfileTools(McpGateway gateway)
{
    private const string ApplyHelp =
        "false (padrão) = só pré-visualiza: devolve o diff e os problemas, e não grava. true = grava e relê o perfil gravado.";

    private const string ExpectedHelp =
        "O updatedAt lido do perfil antes de propor a mudança (anonymization_profile_get). Obrigatório com apply=true; " +
        "se o perfil mudou desde então, nada é gravado.";

    [McpServerTool(Name = "anonymization_profile_list", Title = "Listar perfis de anonimização", ReadOnly = true, Idempotent = true)]
    [Description("Os perfis de anonimização: nome, conexão de onde as colunas são lidas, se está habilitado, quantas regras e tabelas sem dados.")]
    public Task<IReadOnlyList<ProfileSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "anonymization_profile_list",
            async (runner, token) =>
            {
                var connections = await DatabaseConnectionTools.Connections(runner, token);

                return (IReadOnlyList<ProfileSummary>)(await Profiles(runner, token))
                    .Select(profile => ProfileSummary.Of(profile, connections))
                    .ToList();
            },
            cancellationToken);

    [McpServerTool(Name = "anonymization_profile_get", Title = "Consultar perfil de anonimização", ReadOnly = true, Idempotent = true)]
    [Description("O resumo de um perfil: nome, descrição, conexão, habilitado, contagens e updatedAt (use-o em expectedUpdatedAt ao alterar).")]
    public Task<ProfileSummary> GetAsync(
        [Description("O id do perfil.")] string profileId,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "anonymization_profile_get",
            async (runner, token) => ProfileSummary.Of(
                await Profile(runner, McpInput.Id(profileId, "o id do perfil"), token),
                await DatabaseConnectionTools.Connections(runner, token)),
            cancellationToken);

    [McpServerTool(Name = "anonymization_profile_get_details", Title = "Configuração do perfil", ReadOnly = true, Idempotent = true)]
    [Description(
        "A configuração completa e efetiva de um perfil: todas as regras (schema, tabela, coluna, máscara, argumento, sensibilidade " +
        "e o que a máscara faz), as tabelas que vão sem dados, a conexão e quem usa o perfil. Nenhum campo é omitido.")]
    public Task<ProfileDetail> GetDetailsAsync(
        [Description("O id do perfil.")] string profileId,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "anonymization_profile_get_details",
            (runner, token) => Detail(runner, McpInput.Id(profileId, "o id do perfil"), token),
            cancellationToken);

    [McpServerTool(Name = "anonymization_profile_get_rules", Title = "Regras do perfil", ReadOnly = true, Idempotent = true)]
    [Description("As regras de um perfil, opcionalmente só de um schema ou de uma tabela.")]
    public Task<IReadOnlyList<RuleInfo>> GetRulesAsync(
        [Description("O id do perfil.")] string profileId,
        [Description("Só deste schema.")] string? schema = null,
        [Description("Só desta tabela.")] string? table = null,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "anonymization_profile_get_rules",
            async (runner, token) => (IReadOnlyList<RuleInfo>)(await Profile(runner, McpInput.Id(profileId, "o id do perfil"), token)).Rules
                .Where(rule => schema is null || string.Equals(rule.Schema, schema, StringComparison.Ordinal))
                .Where(rule => table is null || string.Equals(rule.Table, table, StringComparison.Ordinal))
                .Select(RuleInfo.Of)
                .ToList(),
            cancellationToken);

    [McpServerTool(Name = "anonymization_profile_get_schema", Title = "Máscaras suportadas", ReadOnly = true, Idempotent = true)]
    [Description(
        "Tudo o que um perfil aceita: as máscaras do catálogo (o que cada uma faz, os tipos de coluna que aceita, se mantém valores " +
        "únicos e o argumento), as sensibilidades e os limites. Não há SQL livre: só estas máscaras.")]
    public Task<ProfileSchema> GetSchemaAsync(CancellationToken cancellationToken = default) =>
        gateway.ReadAsync("anonymization_profile_get_schema", (_, _) => Task.FromResult(ProfileSchema.Current), cancellationToken);

    [McpServerTool(Name = "anonymization_profile_validate", Title = "Validar perfil", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description(
        "Valida o perfil gravado contra as colunas reais do banco (só leitura do catálogo): coluna inexistente, máscara incompatível " +
        "com o tipo, chave PK/FK mascarada, índice único com máscara que repete, NULL em NOT NULL, FK para tabela sem dados, e as " +
        "colunas que parecem dado pessoal e não têm regra. O resultado não fica gravado: valide de novo depois de mudar.")]
    public Task<AnonymizationProfileValidation> ValidateAsync(
        [Description("O id do perfil.")] string profileId,
        [Description("O banco, quando a conexão é só o servidor. Vazio = o do apelido que usa o perfil.")] string? database = null,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "anonymization_profile_validate",
            (runner, token) => runner.RunAsync<ValidateAnonymizationProfileHandler, AnonymizationProfileValidation>(
                (handler, cancel) => handler.HandleAsync(
                    new ValidateAnonymizationProfile(McpInput.Id(profileId, "o id do perfil"), database), cancel),
                token),
            cancellationToken);

    [McpServerTool(Name = "anonymization_profile_analyze", Title = "Analisar perfil", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description(
        "Analisa o perfil e separa: proven (problemas comprovados pela validação contra o banco), staticRisks (riscos da " +
        "configuração) e needsInformation (o que não dá para afirmar). Traz cobertura, dependências e o estado do perfil. " +
        "Não afirma conformidade com a LGPD.")]
    public Task<AnonymizationProfileAnalysis> AnalyzeAsync(
        [Description("O id do perfil.")] string profileId,
        [Description("O banco, quando a conexão é só o servidor.")] string? database = null,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "anonymization_profile_analyze",
            (runner, token) => runner.RunAsync<AnalyzeAnonymizationProfileHandler, AnonymizationProfileAnalysis>(
                (handler, cancel) => handler.HandleAsync(
                    new AnalyzeAnonymizationProfile(McpInput.Id(profileId, "o id do perfil"), database), cancel),
                token),
            cancellationToken);

    [McpServerTool(Name = "anonymization_profile_compare", Title = "Comparar perfis", ReadOnly = true, Idempotent = true)]
    [Description("As diferenças entre dois perfis: regras só de um, regras com máscara diferente, tabelas sem dados e campos gerais.")]
    public Task<AnonymizationProfileComparison> CompareAsync(
        [Description("O id do primeiro perfil.")] string firstProfileId,
        [Description("O id do segundo perfil.")] string secondProfileId,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "anonymization_profile_compare",
            (runner, token) => runner.RunAsync<CompareAnonymizationProfilesHandler, AnonymizationProfileComparison>(
                (handler, cancel) => handler.HandleAsync(
                    new CompareAnonymizationProfiles(
                        McpInput.Id(firstProfileId, "o id do primeiro perfil"),
                        McpInput.Id(secondProfileId, "o id do segundo perfil")),
                    cancel),
                token),
            cancellationToken);

    [McpServerTool(Name = "anonymization_profile_get_usage", Title = "Uso do perfil", ReadOnly = true, Idempotent = true)]
    [Description("Os apelidos de banco e os perfis de cópia que usam o perfil — onde uma mudança nele vale na próxima cópia.")]
    public Task<ProfileUsage> GetUsageAsync(
        [Description("O id do perfil.")] string profileId,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "anonymization_profile_get_usage",
            (runner, token) => Usage(runner, McpInput.Id(profileId, "o id do perfil"), token),
            cancellationToken);

    [McpServerTool(Name = "anonymization_profile_suggest_columns", Title = "Sugerir colunas sensíveis", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("As colunas do banco que parecem dado pessoal (pelo nome e comentário), com a máscara sugerida. Só leitura do catálogo.")]
    public Task<IReadOnlyList<ColumnSuggestion>> SuggestColumnsAsync(
        [Description("O id da conexão.")] string connectionId,
        [Description("O banco, quando a conexão é só o servidor.")] string? database = null,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "anonymization_profile_suggest_columns",
            (runner, token) => runner.RunAsync<SuggestSensitiveColumnsHandler, IReadOnlyList<ColumnSuggestion>>(
                (handler, cancel) => handler.HandleAsync(
                    new SuggestSensitiveColumns(McpInput.Id(connectionId, "o id da conexão"), database), cancel),
                token),
            cancellationToken);

    [McpServerTool(Name = "anonymization_profile_list_tables", Title = "Tabelas do banco", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("As tabelas do banco de uma conexão, com linhas estimadas e tamanho — para escolher as que vão sem dados. Só catálogo.")]
    public Task<IReadOnlyList<SourceTableRow>> ListTablesAsync(
        [Description("O id da conexão.")] string connectionId,
        [Description("O banco, quando a conexão é só o servidor.")] string? database = null,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "anonymization_profile_list_tables",
            (runner, token) => runner.RunAsync<GetSourceTablesHandler, IReadOnlyList<SourceTableRow>>(
                (handler, cancel) => handler.HandleAsync(new GetSourceTables(McpInput.Id(connectionId, "o id da conexão"), database), cancel),
                token),
            cancellationToken);

    [McpServerTool(Name = "anonymization_profile_preview_masking", Title = "Pré-visualizar máscaras", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description(
        "Algumas linhas de cada coluna mascarada, já mascaradas pelo próprio servidor numa sessão somente leitura. " +
        "Só o valor mascarado volta, nunca o real. Também lista os problemas das regras contra o banco.")]
    public Task<MaskingPreviewResult> PreviewMaskingAsync(
        [Description("O id do perfil.")] string profileId,
        [Description("O banco, quando a conexão é só o servidor.")] string? database = null,
        [Description("Linhas por coluna, de 1 a 20.")] int rows = 5,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "anonymization_profile_preview_masking",
            async (runner, token) =>
            {
                var profile = await Profile(runner, McpInput.Id(profileId, "o id do perfil"), token);

                return await runner.RunAsync<PreviewMaskingHandler, MaskingPreviewResult>(
                    (handler, cancel) => handler.HandleAsync(
                        new PreviewMasking(profile.ConnectionId, database, profile.Rules, rows) { SkippedTables = profile.SkippedTables },
                        cancel),
                    token);
            },
            cancellationToken);

    [McpServerTool(Name = "anonymization_profile_create", Title = "Criar perfil de anonimização", Idempotent = false, Destructive = false)]
    [Description(
        "Cria um perfil, com regras e tabelas sem dados opcionais. Passa pelas mesmas validações da tela: nome único, conexão " +
        "existente, argumento de cada máscara, coluna repetida. Valide contra o banco depois com anonymization_profile_validate.")]
    public Task<ProfileDetail> CreateAsync(
        [Description("Nome único (até 80 caracteres).")] string name,
        [Description("A conexão de onde as colunas são lidas (database_profile_list).")] string connectionId,
        [Description("Descrição (até 500 caracteres).")] string? description = null,
        [Description("As regras iniciais.")] RuleInput[]? rules = null,
        [Description("As tabelas que vão sem dados.")] TableInput[]? skippedTables = null,
        CancellationToken cancellationToken = default) =>
        gateway.WriteAsync(
            "anonymization_profile_create",
            DataArea.Databases,
            async (runner, token) =>
            {
                var id = await runner.RunAsync<SaveAnonymizationProfileHandler, Guid>(
                    (handler, cancel) => handler.HandleAsync(
                        new SaveAnonymizationProfile(
                            null,
                            name,
                            description,
                            McpInput.Id(connectionId, "o id da conexão"),
                            (rules ?? []).Select(rule => rule.ToRow()).ToList())
                        {
                            SkippedTables = (skippedTables ?? []).Select(table => table.ToRow()).ToList(),
                        },
                        cancel),
                    token);

                return await Detail(runner, id, token);
            },
            cancellationToken);

    [McpServerTool(Name = "anonymization_profile_update", Title = "Alterar perfil de anonimização", Destructive = true, Idempotent = false)]
    [Description(
        "Altera por partes: nome, descrição, conexão, habilitado, e inclui, altera ou remove regras e tabelas sem dados. O que não for " +
        "citado fica como está. Sempre devolve o antes, o depois e o diff de cada regra. " + ApplyHelp)]
    public Task<ProfileChangeResult> UpdateAsync(
        [Description("O id do perfil.")] string profileId,
        [Description(ApplyHelp)] bool apply = false,
        [Description(ExpectedHelp)] DateTimeOffset? expectedUpdatedAt = null,
        [Description("Novo nome.")] string? name = null,
        [Description("Nova descrição.")] string? description = null,
        [Description("Apaga a descrição.")] bool clearDescription = false,
        [Description("Nova conexão de onde ler as colunas.")] string? connectionId = null,
        [Description("Habilitar ou desabilitar.")] bool? enabled = null,
        [Description("Regras novas (a coluna não pode ter regra).")] RuleInput[]? addRules = null,
        [Description("Regras a trocar (a coluna precisa ter regra); o conjunto inteiro da regra.")] RuleInput[]? updateRules = null,
        [Description("Colunas cuja regra sai.")] ColumnInput[]? removeRules = null,
        [Description("Tabelas que passam a ir sem dados.")] TableInput[]? addSkippedTables = null,
        [Description("Tabelas que voltam a ir com dados.")] TableInput[]? removeSkippedTables = null,
        [Description("Também conferir a proposta contra o banco (só leitura).")] bool validateAgainstDatabase = false,
        [Description("O banco, quando a conexão é só o servidor.")] string? database = null,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(
            "anonymization_profile_update",
            profileId,
            apply,
            async (_, _) => new ChangeAnonymizationProfile(McpInput.Id(profileId, "o id do perfil"))
            {
                ExpectedUpdatedAt = expectedUpdatedAt,
                Name = name,
                Description = clearDescription ? new Change<string?>(null) : description is null ? null : new Change<string?>(description),
                ConnectionId = McpInput.OptionalId(connectionId, "o id da conexão"),
                Enabled = enabled,
                AddRules = (addRules ?? []).Select(rule => rule.ToRow()).ToList(),
                UpdateRules = (updateRules ?? []).Select(rule => rule.ToRow()).ToList(),
                RemoveRules = (removeRules ?? []).Select(column => column.ToKey()).ToList(),
                AddSkippedTables = (addSkippedTables ?? []).Select(table => table.ToRow()).ToList(),
                RemoveSkippedTables = (removeSkippedTables ?? []).Select(table => table.ToRow()).ToList(),
                ValidateAgainstDatabase = validateAgainstDatabase,
                Database = database,
            },
            cancellationToken);

    [McpServerTool(Name = "anonymization_profile_rule_create", Title = "Incluir regra", Idempotent = false, Destructive = false)]
    [Description("Inclui uma regra numa coluna sem regra. " + ApplyHelp)]
    public Task<ProfileChangeResult> CreateRuleAsync(
        [Description("O id do perfil.")] string profileId,
        [Description("Schema da coluna (ex.: public).")] string schema,
        [Description("Tabela.")] string table,
        [Description("Coluna (sensível a maiúsculas, como no banco).")] string column,
        [Description("A máscara: veja anonymization_profile_get_schema.")] string method,
        [Description("Low, Medium ou High.")] string sensitivity,
        [Description("O argumento da máscara, quando ela tem um.")] string? argument = null,
        [Description(ApplyHelp)] bool apply = false,
        [Description(ExpectedHelp)] DateTimeOffset? expectedUpdatedAt = null,
        [Description("Também conferir contra o banco.")] bool validateAgainstDatabase = false,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(
            "anonymization_profile_rule_create",
            profileId,
            apply,
            (_, _) => Task.FromResult(new ChangeAnonymizationProfile(McpInput.Id(profileId, "o id do perfil"))
            {
                ExpectedUpdatedAt = expectedUpdatedAt,
                AddRules = [new RuleInput(schema, table, column, method, sensitivity, argument).ToRow()],
                ValidateAgainstDatabase = validateAgainstDatabase,
            }),
            cancellationToken);

    [McpServerTool(Name = "anonymization_profile_rule_update", Title = "Alterar regra", Destructive = true, Idempotent = true)]
    [Description(
        "Troca a máscara, o argumento ou a sensibilidade da regra de uma coluna; o que não for informado fica como está. " +
        "Trocar a máscara sem informar argumento usa o padrão da máscara nova. " + ApplyHelp)]
    public Task<ProfileChangeResult> UpdateRuleAsync(
        [Description("O id do perfil.")] string profileId,
        [Description("Schema da coluna.")] string schema,
        [Description("Tabela.")] string table,
        [Description("Coluna.")] string column,
        [Description("Nova máscara.")] string? method = null,
        [Description("Nova sensibilidade: Low, Medium ou High.")] string? sensitivity = null,
        [Description("Novo argumento.")] string? argument = null,
        [Description("Apaga o argumento (vale o padrão da máscara).")] bool clearArgument = false,
        [Description(ApplyHelp)] bool apply = false,
        [Description(ExpectedHelp)] DateTimeOffset? expectedUpdatedAt = null,
        [Description("Também conferir contra o banco.")] bool validateAgainstDatabase = false,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(
            "anonymization_profile_rule_update",
            profileId,
            apply,
            async (runner, token) =>
            {
                var id = McpInput.Id(profileId, "o id do perfil");
                var key = new ColumnInput(schema, table, column).ToKey();
                var current = (await Profile(runner, id, token)).Rules.FirstOrDefault(rule => rule.ColumnKey == key.Key)
                    ?? throw new DomainException($"{key.Key} não tem regra para alterar: use anonymization_profile_rule_create.");

                var newMethod = McpInput.OptionalEnum<MaskingMethod>(method, "a máscara") ?? current.Method;

                return new ChangeAnonymizationProfile(id)
                {
                    ExpectedUpdatedAt = expectedUpdatedAt,
                    UpdateRules =
                    [
                        current with
                        {
                            Method = newMethod,
                            // Máscara nova, argumento velho não serve: sem argumento, o padrão da nova.
                            Argument = clearArgument ? null : argument ?? (newMethod == current.Method ? current.Argument : null),
                            Sensitivity = McpInput.OptionalEnum<ColumnSensitivity>(sensitivity, "a sensibilidade") ?? current.Sensitivity,
                        },
                    ],
                    ValidateAgainstDatabase = validateAgainstDatabase,
                };
            },
            cancellationToken);

    [McpServerTool(Name = "anonymization_profile_rule_delete", Title = "Remover regra", Destructive = true, Idempotent = false)]
    [Description("Remove a regra de uma coluna: na próxima cópia, ela vai sem máscara. " + ApplyHelp)]
    public Task<ProfileChangeResult> DeleteRuleAsync(
        [Description("O id do perfil.")] string profileId,
        [Description("Schema da coluna.")] string schema,
        [Description("Tabela.")] string table,
        [Description("Coluna.")] string column,
        [Description(ApplyHelp)] bool apply = false,
        [Description(ExpectedHelp)] DateTimeOffset? expectedUpdatedAt = null,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(
            "anonymization_profile_rule_delete",
            profileId,
            apply,
            (_, _) => Task.FromResult(new ChangeAnonymizationProfile(McpInput.Id(profileId, "o id do perfil"))
            {
                ExpectedUpdatedAt = expectedUpdatedAt,
                RemoveRules = [new ColumnInput(schema, table, column).ToKey()],
            }),
            cancellationToken);

    [McpServerTool(Name = "anonymization_profile_skipped_table_add", Title = "Tabela sem dados", Idempotent = false, Destructive = false)]
    [Description("Marca uma tabela para ir sem dados (só a estrutura). Uma FK de outra tabela com dados para ela aparece como problema. " + ApplyHelp)]
    public Task<ProfileChangeResult> AddSkippedTableAsync(
        [Description("O id do perfil.")] string profileId,
        [Description("Schema.")] string schema,
        [Description("Tabela.")] string table,
        [Description(ApplyHelp)] bool apply = false,
        [Description(ExpectedHelp)] DateTimeOffset? expectedUpdatedAt = null,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(
            "anonymization_profile_skipped_table_add",
            profileId,
            apply,
            (_, _) => Task.FromResult(new ChangeAnonymizationProfile(McpInput.Id(profileId, "o id do perfil"))
            {
                ExpectedUpdatedAt = expectedUpdatedAt,
                AddSkippedTables = [new TableInput(schema, table).ToRow()],
            }),
            cancellationToken);

    [McpServerTool(Name = "anonymization_profile_skipped_table_remove", Title = "Tabela com dados", Destructive = true, Idempotent = false)]
    [Description("Faz uma tabela voltar a ir com dados (e com as máscaras das regras dela). " + ApplyHelp)]
    public Task<ProfileChangeResult> RemoveSkippedTableAsync(
        [Description("O id do perfil.")] string profileId,
        [Description("Schema.")] string schema,
        [Description("Tabela.")] string table,
        [Description(ApplyHelp)] bool apply = false,
        [Description(ExpectedHelp)] DateTimeOffset? expectedUpdatedAt = null,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(
            "anonymization_profile_skipped_table_remove",
            profileId,
            apply,
            (_, _) => Task.FromResult(new ChangeAnonymizationProfile(McpInput.Id(profileId, "o id do perfil"))
            {
                ExpectedUpdatedAt = expectedUpdatedAt,
                RemoveSkippedTables = [new TableInput(schema, table).ToRow()],
            }),
            cancellationToken);

    [McpServerTool(Name = "anonymization_profile_duplicate", Title = "Duplicar perfil", Idempotent = false, Destructive = false)]
    [Description("Cria uma variante com as mesmas regras e tabelas sem dados, com outro nome — e, se quiser, outra conexão.")]
    public Task<ProfileDetail> DuplicateAsync(
        [Description("O id do perfil de origem.")] string profileId,
        [Description("O nome da variante.")] string name,
        [Description("Outra conexão para a variante.")] string? connectionId = null,
        CancellationToken cancellationToken = default) =>
        gateway.WriteAsync(
            "anonymization_profile_duplicate",
            DataArea.Databases,
            async (runner, token) =>
            {
                var id = await runner.RunAsync<DuplicateAnonymizationProfileHandler, Guid>(
                    (handler, cancel) => handler.HandleAsync(
                        new DuplicateAnonymizationProfile(
                            McpInput.Id(profileId, "o id do perfil"),
                            name,
                            McpInput.OptionalId(connectionId, "o id da conexão")),
                        cancel),
                    token);

                return await Detail(runner, id, token);
            },
            cancellationToken);

    [McpServerTool(Name = "anonymization_profile_set_enabled", Title = "Habilitar perfil", Idempotent = true, Destructive = false)]
    [Description("Habilita ou desabilita um perfil. Desabilitado, nenhuma cópia o usa.")]
    public Task<ProfileDetail> SetEnabledAsync(
        [Description("O id do perfil.")] string profileId,
        [Description("true = habilitado.")] bool enabled,
        CancellationToken cancellationToken = default) =>
        gateway.WriteAsync(
            "anonymization_profile_set_enabled",
            DataArea.Databases,
            async (runner, token) =>
            {
                var id = McpInput.Id(profileId, "o id do perfil");

                await runner.RunAsync<SetAnonymizationProfileEnabledHandler>(
                    (handler, cancel) => handler.HandleAsync(new SetAnonymizationProfileEnabled(id, enabled), cancel), token);

                return await Detail(runner, id, token);
            },
            cancellationToken);

    /// <summary>
    /// O fluxo de toda alteração: propõe, e com <c>apply</c> grava; depois relê
    /// num escopo novo — o que volta é o que está no banco, não o objeto que
    /// acabou de ser salvo.
    /// </summary>
    private Task<ProfileChangeResult> ChangeAsync(
        string operation,
        string profileId,
        bool apply,
        Func<IUseCaseRunner, CancellationToken, Task<ChangeAnonymizationProfile>> build,
        CancellationToken cancellationToken)
    {
        async Task<ProfileChangeResult> Run(IUseCaseRunner runner, CancellationToken token)
        {
            var command = (await build(runner, token)) with { Apply = apply };

            var result = await runner.RunAsync<ChangeAnonymizationProfileHandler, AnonymizationProfileChangeResult>(
                (handler, cancel) => handler.HandleAsync(command, cancel), token);

            if (!result.Applied)
            {
                return new ProfileChangeResult(
                    result.Problems.Count > 0
                        ? "Nada foi gravado: há problemas na proposta."
                        : !result.HasChanges
                            ? "Nada a mudar: a proposta é igual ao que está gravado."
                            : "Pré-visualização: nada foi gravado. Repita com apply=true para gravar.",
                    result,
                    null,
                    null);
            }

            var persisted = await Profile(runner, command.ProfileId, token);
            var confirmed = Equivalent(persisted, result.After);

            return new ProfileChangeResult(
                confirmed
                    ? "Gravado e conferido: o perfil relido do banco é o proposto."
                    : "Gravado, mas o perfil relido do banco difere do proposto — consulte anonymization_profile_get_details.",
                result,
                persisted,
                confirmed);
        }

        return apply
            ? gateway.WriteAsync(operation, DataArea.Databases, Run, cancellationToken)
            : gateway.ReadAsync(operation, Run, cancellationToken);
    }

    private static bool Equivalent(AnonymizationProfileRow persisted, AnonymizationProfileRow expected) =>
        persisted.Name == expected.Name
        && persisted.Description == expected.Description
        && persisted.ConnectionId == expected.ConnectionId
        && persisted.IsEnabled == expected.IsEnabled
        && persisted.Rules.Select(rule => (rule.ColumnKey, rule.Method, rule.Argument, rule.Sensitivity)).Order()
            .SequenceEqual(expected.Rules.Select(rule => (rule.ColumnKey, rule.Method, rule.Argument, rule.Sensitivity)).Order())
        && persisted.SkippedTables.Select(table => table.TableKey).Order(StringComparer.Ordinal)
            .SequenceEqual(expected.SkippedTables.Select(table => table.TableKey).Order(StringComparer.Ordinal));

    private static Task<IReadOnlyList<AnonymizationProfileRow>> Profiles(IUseCaseRunner runner, CancellationToken cancellationToken) =>
        runner.RunAsync<GetAnonymizationProfilesHandler, IReadOnlyList<AnonymizationProfileRow>>(
            (handler, token) => handler.HandleAsync(new GetAnonymizationProfiles(), token),
            cancellationToken);

    private static Task<AnonymizationProfileRow> Profile(IUseCaseRunner runner, Guid id, CancellationToken cancellationToken) =>
        runner.RunAsync<GetAnonymizationProfileHandler, AnonymizationProfileRow>(
            (handler, token) => handler.HandleAsync(new GetAnonymizationProfile(id), token),
            cancellationToken);

    private static Task<ProfileUsage> Usage(IUseCaseRunner runner, Guid id, CancellationToken cancellationToken) =>
        runner.RunAsync<DatabaseUsageHandlers, ProfileUsage>(
            (handler, token) => handler.HandleAsync(new GetAnonymizationProfileUsage(id), token),
            cancellationToken);

    private static async Task<ProfileDetail> Detail(IUseCaseRunner runner, Guid id, CancellationToken cancellationToken)
    {
        var profile = await Profile(runner, id, cancellationToken);
        var connections = await DatabaseConnectionTools.Connections(runner, cancellationToken);
        var connection = connections.FirstOrDefault(row => row.Id == profile.ConnectionId);
        var usage = await Usage(runner, id, cancellationToken);

        return new ProfileDetail(
            ProfileSummary.Of(profile, connections),
            connection is null ? null : ConnectionInfo.Of(connection),
            profile.Rules.Select(RuleInfo.Of).ToList(),
            profile.SkippedTables.Select(table => table.TableKey).ToList(),
            usage,
            "Configurado no MyTaskApp. As máscaras não são instaladas no banco: entram no SELECT de cada cópia (ADR-058). " +
            "Para saber se o perfil serve ao banco de hoje, use anonymization_profile_validate.");
    }
}

/// <summary>Uma regra como argumento.</summary>
public sealed record RuleInput(
    [property: Description("Schema (ex.: public).")] string Schema,
    [property: Description("Tabela.")] string Table,
    [property: Description("Coluna, como no banco.")] string Column,
    [property: Description("A máscara: Hash, FakeEmail, Partial, FakeName, FixedText, FixedNumber, Null, DateShift, NumberNoise.")] string Method,
    [property: Description("Low, Medium ou High.")] string Sensitivity,
    [property: Description("O argumento da máscara, quando ela tem um.")] string? Argument = null)
{
    internal AnonymizationRuleRow ToRow() => new(
        Schema,
        Table,
        Column,
        McpInput.Enum<MaskingMethod>(Method, "a máscara"),
        Argument,
        McpInput.Enum<ColumnSensitivity>(Sensitivity, "a sensibilidade"));
}

public sealed record ColumnInput(string Schema, string Table, string Column)
{
    internal AnonymizationColumnKey ToKey() => new(Schema, Table, Column);
}

public sealed record TableInput(string Schema, string Table)
{
    internal SkippedTableRow ToRow() => new(Schema, Table);
}

public sealed record RuleInfo(
    string Column,
    string Schema,
    string Table,
    string ColumnName,
    string Method,
    string MethodLabel,
    string? Argument,
    string Sensitivity,
    bool KeepsUniqueness,
    string Effect)
{
    public static RuleInfo Of(AnonymizationRuleRow rule)
    {
        var info = MaskingCatalog.Of(rule.Method);

        return new RuleInfo(
            rule.ColumnKey,
            rule.Schema,
            rule.Table,
            rule.Column,
            rule.Method.ToString(),
            info.Label,
            rule.Argument,
            rule.Sensitivity.ToString(),
            info.KeepsUniqueness,
            info.Description);
    }
}

public sealed record ProfileSummary(
    Guid Id,
    string Name,
    string? Description,
    Guid ConnectionId,
    string? ConnectionName,
    bool IsEnabled,
    int Rules,
    int SkippedTables,
    DateTimeOffset UpdatedAt)
{
    public static ProfileSummary Of(AnonymizationProfileRow profile, IReadOnlyList<DatabaseConnectionRow> connections) => new(
        profile.Id,
        profile.Name,
        profile.Description,
        profile.ConnectionId,
        connections.FirstOrDefault(connection => connection.Id == profile.ConnectionId)?.Name,
        profile.IsEnabled,
        profile.Rules.Count,
        profile.SkippedTables.Count,
        profile.UpdatedAt);
}

public sealed record ProfileDetail(
    ProfileSummary Profile,
    ConnectionInfo? Connection,
    IReadOnlyList<RuleInfo> Rules,
    IReadOnlyList<string> SkippedTables,
    ProfileUsage UsedBy,
    string State);

/// <param name="Persisted">Com apply: o perfil relido do banco, num escopo novo.</param>
/// <param name="Confirmed">Com apply: o relido é exatamente o proposto.</param>
public sealed record ProfileChangeResult(
    string Message,
    AnonymizationProfileChangeResult Change,
    AnonymizationProfileRow? Persisted,
    bool? Confirmed);

public sealed record MaskInfo(
    string Method,
    string Label,
    string Description,
    IReadOnlyList<string> AcceptsTypes,
    bool KeepsUniqueness,
    string? ArgumentLabel,
    string? DefaultArgument);

public sealed record ProfileSchema(
    IReadOnlyList<MaskInfo> Masks,
    IReadOnlyList<string> Sensitivities,
    int MaxRules,
    int MaxSkippedTables,
    IReadOnlyList<string> Notes)
{
    public static ProfileSchema Current { get; } = new(
        MaskingCatalog.All
            .Select(info => new MaskInfo(
                info.Method.ToString(),
                info.Label,
                info.Description,
                info.Accepts.Select(type => type.ToString()).ToList(),
                info.KeepsUniqueness,
                info.ArgumentLabel,
                info.DefaultArgument))
            .ToList(),
        Enum.GetNames<ColumnSensitivity>(),
        AnonymizationProfile.MaxRules,
        AnonymizationProfile.MaxSkippedTables,
        [
            "Uma regra identifica a coluna por schema, tabela e coluna, sensíveis a maiúsculas como no PostgreSQL.",
            "Coluna PK ou FK não se mascara: quebraria as ligações. Índice único só aceita máscara que mantém valores únicos (Hash, FakeEmail).",
            "Tabela sem dados vai só com a estrutura; uma FK de tabela com dados para ela impede a cópia.",
            "O perfil não tem campos além destes: nada fica oculto ou sem interpretação.",
            "As máscaras não são instaladas no banco (sem PostgreSQL Anonymizer): entram no SELECT de cada cópia (ADR-058).",
        ]);
}
