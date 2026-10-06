using System.Globalization;

namespace MyTaskApp.Domain.Commands;

/// <summary>
/// Como perguntar um parâmetro <c>{nome}</c> de comando rápido (ADR-051): rótulo,
/// tipo, valor padrão, se é obrigatório e, num combo, as opções.
/// </summary>
/// <remarks>
/// Um <c>{nome}</c> sem definição vale <see cref="Plain"/>: texto obrigatório,
/// que é como o parâmetro sempre funcionou nos comandos pós-Worktree (ADR-028).
/// </remarks>
public sealed record CommandParameterSpec(
    string Name,
    string? Label = null,
    CommandParameterType Type = CommandParameterType.Text,
    string? DefaultValue = null,
    bool IsRequired = true,
    IReadOnlyList<string>? Options = null)
{
    public const int MaxNameLength = 64;

    public const int MaxLabelLength = 80;

    public const int MaxValueLength = 500;

    public const int MaxOptions = 50;

    /// <summary>O parâmetro sem definição: texto obrigatório.</summary>
    public static CommandParameterSpec Plain(string name) => new(name);

    /// <summary>O que a tela mostra: o rótulo, ou o próprio nome.</summary>
    public string DisplayName => Label ?? Name;

    public IReadOnlyList<string> Choices => Options ?? [];

    /// <summary>
    /// Apara e valida tudo. Num combo, o padrão precisa ser uma das opções; num
    /// número, precisa ser número. Fora do combo, as opções são descartadas.
    /// </summary>
    public CommandParameterSpec Normalized()
    {
        var name = Name?.Trim() ?? string.Empty;

        if (!CommandParameters.IsName(name) || name.Length > MaxNameLength)
        {
            throw new DomainException($"\"{name}\" não é um nome de parâmetro válido.");
        }

        if (!Enum.IsDefined(Type))
        {
            throw new DomainException($"O tipo do parâmetro {name} não é válido.");
        }

        var label = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim();

        if (label?.Length > MaxLabelLength)
        {
            throw new DomainException($"O rótulo de {name} não pode passar de {MaxLabelLength} caracteres.");
        }

        var options = Type == CommandParameterType.Choice ? NormalizeOptions(name, Options) : null;
        var defaultValue = string.IsNullOrWhiteSpace(DefaultValue) ? null : DefaultValue.Trim();

        if (defaultValue is not null)
        {
            var spec = new CommandParameterSpec(name, label, Type, null, IsRequired, options);

            if (spec.Check(defaultValue) is { } error)
            {
                throw new DomainException($"O valor padrão de {name} não serve: {error}");
            }
        }

        return new CommandParameterSpec(name, label, Type, defaultValue, IsRequired, options);
    }

    /// <summary>
    /// O valor que vai para o comando. Em branco vale o padrão; sem padrão, um
    /// opcional vira texto vazio e um obrigatório fica faltando.
    /// </summary>
    public ParameterEvaluation Evaluate(string? input)
    {
        var value = string.IsNullOrWhiteSpace(input) ? DefaultValue : input.Trim();

        if (value is null)
        {
            return IsRequired
                ? new ParameterEvaluation(string.Empty, $"Informe {DisplayName}.", IsMissing: true)
                : new ParameterEvaluation(string.Empty, null, IsMissing: false);
        }

        return Check(value) is { } error
            ? new ParameterEvaluation(value, error, IsMissing: false)
            : new ParameterEvaluation(value, null, IsMissing: false);
    }

    private string? Check(string value)
    {
        if (value.Length > MaxValueLength)
        {
            return $"{DisplayName} não pode passar de {MaxValueLength} caracteres.";
        }

        if (value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
        {
            return $"{DisplayName} ocupa uma linha só.";
        }

        return Type switch
        {
            CommandParameterType.Number when !double.TryParse(
                value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) =>
                $"{DisplayName} precisa ser um número (use ponto para decimais).",
            CommandParameterType.Choice when !Choices.Contains(value, StringComparer.Ordinal) =>
                $"{DisplayName} precisa ser uma das opções: {string.Join(", ", Choices)}.",
            _ => null,
        };
    }

    private static List<string> NormalizeOptions(string name, IReadOnlyList<string>? options)
    {
        var normalized = (options ?? [])
            .Select(option => option?.Trim() ?? string.Empty)
            .Where(option => option.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalized.Count == 0)
        {
            throw new DomainException($"O parâmetro {name} é uma lista: cadastre pelo menos uma opção.");
        }

        if (normalized.Count > MaxOptions)
        {
            throw new DomainException($"O parâmetro {name} aceita até {MaxOptions} opções.");
        }

        if (normalized.Exists(option => option.Length > MaxValueLength))
        {
            throw new DomainException($"Uma opção de {name} não pode passar de {MaxValueLength} caracteres.");
        }

        if (normalized.Exists(option => option.Contains('\n', StringComparison.Ordinal)
                                        || option.Contains('\r', StringComparison.Ordinal)))
        {
            throw new DomainException($"Cada opção de {name} ocupa uma linha só.");
        }

        return normalized;
    }
}

/// <summary>O valor de um parâmetro pronto para o comando, ou o motivo de não estar.</summary>
public sealed record ParameterEvaluation(string Value, string? Error, bool IsMissing)
{
    public bool IsValid => Error is null;
}
