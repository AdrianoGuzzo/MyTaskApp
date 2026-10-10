using System.Globalization;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Infrastructure.PostgreSql;

/// <summary>
/// O SQL da cópia mascarada (ADR-058): cada máscara do catálogo vira uma
/// expressão com funções nativas do PostgreSQL — <c>md5</c>, <c>left</c>,
/// <c>repeat</c>, <c>CASE</c> —, sem extensão nenhuma na origem.
/// </summary>
/// <remarks>
/// <para>
/// Nada aqui vem do usuário como SQL: nomes vão por
/// <see cref="SqlQuoting.QuoteIdentifier"/>, texto fixo por
/// <see cref="SqlQuoting.QuoteLiteral"/>, número relido como número. O tipo
/// do <c>CAST</c> vem do <c>format_type</c> do próprio servidor, já citado por ele.
/// </para>
/// <para>
/// As máscaras variáveis são <b>determinísticas</b>: o mesmo valor vira sempre
/// o mesmo mascarado (pelo md5 dele). Um e-mail que aparece em duas tabelas
/// continua batendo, e um índice único continua único no hash e no e-mail falso.
/// </para>
/// </remarks>
internal static class MaskedSelectBuilder
{
    private static readonly string[] FirstNames =
    [
        "Ana", "Bruno", "Carla", "Daniel", "Elisa", "Felipe", "Gabriela", "Henrique", "Isabela", "João",
        "Larissa", "Marcos", "Natália", "Otávio", "Paula", "Rafael", "Sofia", "Thiago", "Vanessa", "Yuri",
    ];

    private static readonly string[] LastNames =
    [
        "Almeida", "Barbosa", "Cardoso", "Dias", "Esteves", "Ferreira", "Gomes", "Hora", "Lima", "Martins",
        "Nogueira", "Oliveira", "Pereira", "Queiroz", "Ribeiro", "Santos", "Teixeira", "Vieira",
    ];

    /// <summary>A tabela inteira, mascarada no servidor: <c>COPY (SELECT … FROM ONLY t) TO STDOUT</c>.</summary>
    public static string CopyOut(MaskedTablePlan table) =>
        $"COPY (SELECT {string.Join(", ", table.Columns.Select(Selected))} FROM ONLY {Table(table)}) TO STDOUT";

    /// <summary>A gravação no destino, nas mesmas colunas e na mesma ordem.</summary>
    public static string CopyIn(MaskedTablePlan table) =>
        $"COPY {Table(table)} ({string.Join(", ", table.Columns.Select(column => SqlQuoting.QuoteIdentifier(column.Name)))}) FROM STDIN";

    /// <summary>Só as colunas mascaradas, como texto, para a pré-visualização: nunca o valor real.</summary>
    public static string Preview(MaskedTablePlan table, int rows)
    {
        var masked = table.Columns.Where(column => column.IsMasked).Select(column => $"({Masked(column)})::text").ToList();

        return masked.Count == 0
            ? throw new DomainException($"{table.QualifiedName} não tem coluna mascarada para pré-visualizar.")
            : $"SELECT {string.Join(", ", masked)} FROM ONLY {Table(table)} LIMIT {rows.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Quantas linhas de uma coluna com máscara fixa não têm o valor dela (ver <see cref="IPostgresServerInspector.CountNotMaskedAsync"/>).</summary>
    public static string CountNotMasked(ColumnReference column, MaskedColumnPlan mask) =>
        mask.Method is MaskingMethod.Null or MaskingMethod.FixedText or MaskingMethod.FixedNumber
            ? $"SELECT count(*) FROM {SqlQuoting.QuoteIdentifier(column.Schema)}.{SqlQuoting.QuoteIdentifier(column.Table)} " +
              $"WHERE {SqlQuoting.QuoteIdentifier(column.Column)} IS DISTINCT FROM {Masked(mask)}"
            : throw new DomainException("Só a máscara fixa tem um valor para conferir.");

    /// <summary>A expressão de uma coluna mascarada, já no tipo da coluna.</summary>
    public static string Masked(MaskedColumnPlan column)
    {
        var name = SqlQuoting.QuoteIdentifier(column.Name);
        var expression = column.Method switch
        {
            MaskingMethod.Hash => $"left(md5({name}::text), 16)",
            MaskingMethod.FakeEmail => $"'user_' || left(md5(lower({name}::text)), 12) || '@exemplo.invalid'",
            MaskingMethod.Partial => Partial(name, column.Argument),
            MaskingMethod.FakeName => FakeName(name),
            MaskingMethod.FixedText => SqlQuoting.QuoteLiteral(column.Argument ?? string.Empty),
            MaskingMethod.FixedNumber => Number(column.Argument),
            MaskingMethod.Null => "NULL",
            MaskingMethod.DateShift => DateShift(name, column.Argument),
            MaskingMethod.NumberNoise => Noise(name, column.Argument),
            _ => throw new DomainException("Máscara desconhecida."),
        };

        // Nulo continua nulo: a máscara não inventa valor onde não havia.
        return column.Method is MaskingMethod.Null or MaskingMethod.FixedText or MaskingMethod.FixedNumber
            ? $"CAST({expression} AS {column.DataType})"
            : $"CAST(CASE WHEN {name} IS NULL THEN NULL ELSE {expression} END AS {column.DataType})";
    }

    private static string Selected(MaskedColumnPlan column) =>
        column.IsMasked ? Masked(column) : SqlQuoting.QuoteIdentifier(column.Name);

    private static string Table(MaskedTablePlan table) =>
        $"{SqlQuoting.QuoteIdentifier(table.Schema)}.{SqlQuoting.QuoteIdentifier(table.Table)}";

    /// <summary>Um inteiro de 0 a 2^28-1 tirado do md5 do valor: o mesmo valor, o mesmo número.</summary>
    private static string Seed(string name) => $"(('x' || substr(md5({name}::text), 1, 7))::bit(28)::int)";

    private static string Partial(string name, string? argument)
    {
        var (start, end) = MaskingCatalog.PartialKeep(argument);
        var keep = (start + end).ToString(CultureInfo.InvariantCulture);
        var head = start.ToString(CultureInfo.InvariantCulture);
        var tail = end.ToString(CultureInfo.InvariantCulture);

        // Curto demais para manter início e fim: tudo vira *.
        return $"CASE WHEN length({name}::text) <= {keep} THEN repeat('*', length({name}::text)) " +
               $"ELSE left({name}::text, {head}) || repeat('*', length({name}::text) - {keep}) || right({name}::text, {tail}) END";
    }

    private static string FakeName(string name)
    {
        var first = string.Join(", ", FirstNames.Select(SqlQuoting.QuoteLiteral));
        var last = string.Join(", ", LastNames.Select(SqlQuoting.QuoteLiteral));
        var seed = Seed(name);

        return $"(ARRAY[{first}])[1 + {seed} % {FirstNames.Length.ToString(CultureInfo.InvariantCulture)}] || ' ' || " +
               $"(ARRAY[{last}])[1 + ({seed} / {FirstNames.Length.ToString(CultureInfo.InvariantCulture)}) % {LastNames.Length.ToString(CultureInfo.InvariantCulture)}]";
    }

    private static string DateShift(string name, string? argument)
    {
        var days = Integer(argument);
        var span = ((days * 2) + 1).ToString(CultureInfo.InvariantCulture);

        return $"{name} + (({Seed(name)} % {span}) - {days.ToString(CultureInfo.InvariantCulture)}) * interval '1 day'";
    }

    private static string Noise(string name, string? argument)
    {
        var percent = Integer(argument).ToString(CultureInfo.InvariantCulture);

        return $"round({name}::numeric * (1 + ((({Seed(name)} % 201) - 100) / 100.0) * {percent} / 100.0), 2)";
    }

    /// <summary>O argumento relido como número aqui também: nada chega ao SQL sem passar por um parser.</summary>
    private static string Number(string? argument) =>
        decimal.TryParse(argument, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value.ToString(CultureInfo.InvariantCulture)
            : throw new DomainException("Número fixo inválido.");

    private static int Integer(string? argument) =>
        int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new DomainException("Parâmetro de máscara inválido.");
}
