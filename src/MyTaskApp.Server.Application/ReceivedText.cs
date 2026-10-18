using System.Globalization;
using System.Text;

namespace MyTaskApp.Server.Application;

/// <summary>
/// Os textos curtos que o cliente manda fora dos campos (tipo de entidade,
/// plataforma, versão, apelido): nenhum caractere de controle. O NUL, além de
/// não ter sentido ali, o PostgreSQL nem grava.
/// </summary>
internal static class ReceivedText
{
    public static bool HasControl(string? value) => value is not null && value.Any(char.IsControl);

    /// <summary>
    /// Controle (<c>Cc</c>) ou formatação (<c>Cf</c>): o ESC das sequências
    /// ANSI e o U+202E que inverte o texto na tela (SEC-015).
    /// </summary>
    public static bool HasControlOrFormat(string? value) => value is not null && value.EnumerateRunes().Any(IsControlOrFormat);

    /// <summary>O texto sem os caracteres de controle e de formatação.</summary>
    public static string? Clean(string? value)
    {
        if (value is null || !HasControlOrFormat(value))
        {
            return value;
        }

        var clean = new StringBuilder(value.Length);

        foreach (var rune in value.EnumerateRunes().Where(rune => !IsControlOrFormat(rune)))
        {
            clean.Append(rune.ToString());
        }

        return clean.ToString();
    }

    private static bool IsControlOrFormat(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format;
}
