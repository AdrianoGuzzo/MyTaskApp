using System.Globalization;
using System.Text;

namespace MyTaskApp.Application.Development;

/// <summary>
/// As regras de nome de branch do Git (<c>git check-ref-format --branch</c>),
/// em C#, para a tela avisar enquanto o usuário digita — sem abrir um processo
/// a cada tecla. A palavra final continua sendo do Git, no pipeline (ADR-027).
/// </summary>
public static class GitBranchName
{
    public const int MaxLength = 255;

    public const int MaxSlugLength = 50;

    public const string DefaultPrefix = "feature/";

    private const string ForbiddenChars = " ~^:?*[\\";

    /// <summary>O motivo da recusa, em pt-BR, ou <c>null</c> quando o nome serve.</summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Informe o nome da nova branch.";
        }

        if (name.Length > MaxLength)
        {
            return $"O nome da branch não pode passar de {MaxLength} caracteres.";
        }

        if (name is "@" or "HEAD")
        {
            return $"\"{name}\" é um nome reservado do Git.";
        }

        if (name[0] == '-')
        {
            return "O nome da branch não pode começar com hífen.";
        }

        if (name[0] == '/' || name[^1] == '/')
        {
            return "O nome da branch não pode começar nem terminar com barra.";
        }

        if (name[^1] == '.')
        {
            return "O nome da branch não pode terminar com ponto.";
        }

        if (name.Contains("..", StringComparison.Ordinal))
        {
            return "O nome da branch não pode ter \"..\".";
        }

        if (name.Contains("//", StringComparison.Ordinal))
        {
            return "O nome da branch não pode ter barras seguidas.";
        }

        if (name.Contains("@{", StringComparison.Ordinal))
        {
            return "O nome da branch não pode ter \"@{\".";
        }

        foreach (var c in name)
        {
            if (char.IsControl(c))
            {
                return "O nome da branch não pode ter caracteres de controle.";
            }

            if (ForbiddenChars.Contains(c, StringComparison.Ordinal))
            {
                return c == ' '
                    ? "O nome da branch não pode ter espaços."
                    : $"O nome da branch não pode ter \"{c}\".";
            }
        }

        foreach (var component in name.Split('/'))
        {
            if (component.StartsWith('.'))
            {
                return "Nenhuma parte do nome da branch pode começar com ponto.";
            }

            if (component.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            {
                return "Nenhuma parte do nome da branch pode terminar em \".lock\".";
            }
        }

        return null;
    }

    /// <summary>
    /// <c>feature/{slug-do-título}</c>. Só o título: a tarefa não tem número, e
    /// um pedaço de Guid no nome da branch ninguém lê. O usuário edita à vontade.
    /// </summary>
    public static string Suggest(string? title) => DefaultPrefix + Slug(title);

    /// <summary>
    /// "Corrigir cálculo de animais" vira "corrigir-calculo-de-animais": sem
    /// acento, minúsculo, e só letras, dígitos e hífen.
    /// </summary>
    public static string Slug(string? text)
    {
        var decomposed = (text ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(c);

            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                builder.Append(lower);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');

        if (slug.Length > MaxSlugLength)
        {
            slug = slug[..MaxSlugLength].TrimEnd('-');
        }

        return slug.Length == 0 ? "tarefa" : slug;
    }
}
