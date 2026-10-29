using System.Text;

namespace MyTaskApp.Infrastructure.Terminals.Windows;

/// <summary>
/// Monta a linha de comando do <c>CreateProcess</c> com as mesmas regras do
/// <c>ProcessStartInfo.ArgumentList</c> (as do CRT da Microsoft): cada item
/// volta inteiro no <c>argv</c> do programa, com espaço, aspas e barra
/// invertida (ADR-098). É a regra que o terminal direto já usa, agora sem o
/// <c>Process</c> no meio.
/// </summary>
internal static class WindowsCommandLine
{
    public static string Build(string executable, IReadOnlyList<string> arguments)
    {
        var line = new StringBuilder();
        Append(line, executable);

        foreach (var argument in arguments)
        {
            line.Append(' ');
            Append(line, argument);
        }

        return line.ToString();
    }

    private static void Append(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            line.Append(argument);
            return;
        }

        line.Append('"');
        var backslashes = 0;

        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                // Cada barra antes de uma aspa dobra, e a aspa ganha a sua.
                line.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                line.Append('\\', backslashes);
            }

            backslashes = 0;
            line.Append(c);
        }

        // Antes da aspa que fecha, as barras dobram para não escaparem a aspa.
        line.Append('\\', backslashes * 2);
        line.Append('"');
    }

    /// <summary>
    /// O bloco de ambiente do <c>CreateProcess</c> em UTF-16: o herdado com os
    /// acréscimos por cima, em ordem alfabética sem caixa, como o Windows pede.
    /// </summary>
    public static string EnvironmentBlock(
        IReadOnlyDictionary<string, string>? overrides,
        IReadOnlyCollection<string>? removed = null)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                variables[name] = value;
            }
        }

        foreach (var name in removed ?? [])
        {
            variables.Remove(name);
        }

        foreach (var (name, value) in overrides ?? new Dictionary<string, string>())
        {
            variables[name] = value;
        }

        var block = new StringBuilder();

        foreach (var (name, value) in variables)
        {
            block.Append(name).Append('=').Append(value).Append('\0');
        }

        return block.Append('\0').ToString();
    }
}
