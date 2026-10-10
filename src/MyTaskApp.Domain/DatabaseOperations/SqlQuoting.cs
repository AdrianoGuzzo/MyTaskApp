namespace MyTaskApp.Domain.DatabaseOperations;

/// <summary>
/// Nomes e textos que vão dentro de um comando SQL: o identificador entre
/// aspas duplas, com aspa dobrada; o texto entre aspas simples, com aspa
/// simples dobrada. Uma coluna chamada <c>x"; drop table y; --</c> vira só um
/// nome esquisito.
/// </summary>
public static class SqlQuoting
{
    public static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static string QuoteLiteral(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
