using System.Text;
using MyTaskApp.Domain;

namespace MyTaskApp.Application.Agents;

/// <summary>
/// Os parâmetros que o usuário digita para o agente (ex.:
/// <c>--dangerously-skip-permissions</c>), do texto à lista de argumentos.
/// </summary>
/// <remarks>
/// Separa por espaço, e aspas duplas juntam um argumento com espaço
/// (<c>--append-system-prompt "seja breve"</c> são dois). Nada passa por
/// shell: a lista vai direto para o processo, então <c>&amp;</c> ou <c>|</c>
/// chegam ao agente como texto, e não viram outro comando.
/// </remarks>
public static class AgentArguments
{
    public const int MaxLength = 2000;

    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        if (text.Length > MaxLength)
        {
            throw new DomainException($"Os parâmetros do agente passam de {MaxLength} caracteres.");
        }

        var arguments = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var started = false;

        foreach (var character in text)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                started = true;
            }
            else if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (started)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(character);
                started = true;
            }
        }

        if (inQuotes)
        {
            throw new DomainException("Aspas sem fechar nos parâmetros do agente.");
        }

        if (started)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }
}
