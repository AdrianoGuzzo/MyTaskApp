using MyTaskApp.Application.Commands;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Application.QuickCommands;

/// <summary>
/// A linha que vai ao shell, ou por que ainda não dá para montá-la (ADR-051).
/// </summary>
/// <param name="Line"><c>null</c> enquanto faltar valor ou houver erro.</param>
/// <param name="ParameterErrors">Nome do parâmetro → o que está errado com ele.</param>
/// <param name="Error">O que não é de um parâmetro: uma variável perigosa, ou que o ambiente não fornece.</param>
public sealed record QuickCommandLine(
    string? Line,
    IReadOnlyDictionary<string, string> ParameterErrors,
    string? Error)
{
    public bool IsReady => Line is not null;

    /// <summary>Todos os problemas, numa mensagem para a tela.</summary>
    public string? Problem =>
        IsReady
            ? null
            : string.Join(Environment.NewLine, ParameterErrors.Values.Append(Error).OfType<string>());

    /// <summary>
    /// Preenche o texto do comando com os valores digitados e as variáveis do
    /// ambiente, numa passada só. É a mesma regra para a prévia na tela e para a
    /// execução — que monta de novo, do lado do caso de uso.
    /// </summary>
    public static QuickCommandLine Build(
        string template,
        IReadOnlyList<CommandParameterSpec> parameters,
        IReadOnlyDictionary<string, string>? values,
        CommandContext context)
    {
        var filled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in parameters)
        {
            var typed = values?.FirstOrDefault(pair => string.Equals(pair.Key, parameter.Name, StringComparison.OrdinalIgnoreCase)).Value;
            var evaluation = parameter.Evaluate(typed);

            if (evaluation.IsValid)
            {
                filled[parameter.Name] = evaluation.Value;
            }
            else
            {
                errors[parameter.Name] = evaluation.Error!;
            }
        }

        if (errors.Count > 0)
        {
            return new QuickCommandLine(null, errors, null);
        }

        var expansion = CommandAliasResolver.Fill(template, filled, context);

        if (expansion.Error is { } error)
        {
            return new QuickCommandLine(null, errors, char.ToUpperInvariant(error[0]) + error[1..]);
        }

        if (!expansion.IsComplete)
        {
            // Só sobra o que nenhum parâmetro cobre: uma variável que este
            // ambiente não sabe dizer — o {tag} de um comando sem diretório.
            return new QuickCommandLine(
                null,
                errors,
                $"Este comando usa {string.Join(", ", expansion.Missing.Select(name => $"{{{name}}}"))}, "
                + "que este ambiente não sabe preencher.");
        }

        var line = expansion.Command!.Trim();

        if (line.Length == 0)
        {
            return new QuickCommandLine(null, errors, "O comando ficou vazio.");
        }

        if (line.Length > CommandExecution.MaxCommandLineLength)
        {
            return new QuickCommandLine(
                null,
                errors,
                $"O comando, preenchido, passa de {CommandExecution.MaxCommandLineLength} caracteres.");
        }

        return new QuickCommandLine(line, errors, null);
    }
}
