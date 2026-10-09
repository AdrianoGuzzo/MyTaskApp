using System.Globalization;
using System.Text;

namespace MyTaskApp.Domain.DatabaseOperations;

/// <summary>
/// O script <c>SECURITY LABEL</c> de um perfil de anonimização, para o DBA
/// revisar e executar (ADR-056). O app gera; o app nunca executa — produção
/// não sofre alteração vinda daqui.
/// </summary>
/// <remarks>
/// Identificadores vão entre aspas duplas, com aspa dobrada; a expressão vai
/// dentro de um literal, com aspa simples dobrada. Uma coluna chamada
/// <c>x"; drop table y; --</c> vira só um nome esquisito.
/// </remarks>
public static class MaskingScriptBuilder
{
    public static string Build(
        AnonymizationProfile profile,
        string maskedRole,
        string databaseName,
        DateTimeOffset generatedAt)
    {
        var policy = profile.PolicyName;
        var script = new StringBuilder();

        script.AppendLine(CultureInfo.InvariantCulture, $"-- Perfil de anonimização: {Comment(profile.Name)}");
        script.AppendLine(CultureInfo.InvariantCulture, $"-- Gerado pelo MyTaskApp em {generatedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC.");
        script.AppendLine("-- Revise antes de executar. Rode como superusuário ou dono do banco, no banco de origem.");
        script.AppendLine("-- PostgreSQL Anonymizer 2.x, mascaramento dinâmico transparente: os dados NÃO são alterados.");
        script.AppendLine("-- NÃO use as funções de anonimização estática (anonymize_database, anonymize_table, anonymize_column):");
        script.AppendLine("-- elas reescrevem os dados do próprio banco, e isso é proibido em produção.");
        script.AppendLine();
        script.AppendLine("CREATE EXTENSION IF NOT EXISTS anon;");
        script.AppendLine();
        script.AppendLine("-- O usuário do dump anônimo: tudo o que ele ler sai mascarado.");
        script.AppendLine(CultureInfo.InvariantCulture, $"SECURITY LABEL FOR {policy} ON ROLE {QuoteIdentifier(maskedRole)} IS 'MASKED';");
        script.AppendLine(CultureInfo.InvariantCulture, $"-- GRANT pg_read_all_data TO {QuoteIdentifier(maskedRole)};");
        script.AppendLine(CultureInfo.InvariantCulture, $"-- ALTER DATABASE {QuoteIdentifier(databaseName)} SET anon.transparent_dynamic_masking TO true;");
        script.AppendLine();

        if (profile.Rules.Count == 0)
        {
            script.AppendLine("-- Nenhuma regra confirmada neste perfil.");
            return script.ToString();
        }

        script.AppendLine("-- Regras de mascaramento por coluna.");

        foreach (var rule in profile.Rules.OrderBy(rule => rule.Schema, StringComparer.Ordinal)
                     .ThenBy(rule => rule.Table, StringComparer.Ordinal)
                     .ThenBy(rule => rule.Column, StringComparer.Ordinal))
        {
            var label = rule.Kind == MaskingKind.Function
                ? "MASKED WITH FUNCTION " + rule.Expression
                : "MASKED WITH VALUE " + rule.Expression;

            script.AppendLine(CultureInfo.InvariantCulture,
                $"SECURITY LABEL FOR {policy} ON COLUMN {QuoteIdentifier(rule.Schema)}.{QuoteIdentifier(rule.Table)}.{QuoteIdentifier(rule.Column)} IS {QuoteLiteral(label)};");
        }

        return script.ToString();
    }

    public static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static string QuoteLiteral(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>Um nome dentro de comentário de linha: sem quebra, que encerraria o comentário.</summary>
    private static string Comment(string text) =>
        new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
