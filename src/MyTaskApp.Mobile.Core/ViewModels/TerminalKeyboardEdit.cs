using System.Globalization;
using MyTaskApp.Application.Terminals;

namespace MyTaskApp.Mobile.Core.ViewModels;

public static class TerminalKeyboardEdit
{
    // O teclado consegue apagar o marcador mesmo quando não há texto local.
    public const string Marker = "\u2060";

    public static byte[] Encode(string? previous, string? current)
    {
        var before = Elements(previous);
        var after = Elements(current);
        var shared = 0;
        while (shared < before.Length && shared < after.Length && before[shared] == after[shared])
        {
            shared++;
        }

        var deleted = before.Length - shared;
        if (previous?.StartsWith(Marker, StringComparison.Ordinal) == true && string.IsNullOrEmpty(current))
        {
            deleted = Math.Max(1, deleted);
        }

        var backspace = TerminalKeyBytes.Encode(TerminalKey.Backspace)[0];
        var bytes = TerminalKeyBytes.Text(string.Concat(after.Skip(shared)));
        return [.. Enumerable.Repeat(backspace, deleted), .. bytes];
    }

    private static string[] Elements(string? value)
    {
        var text = value ?? string.Empty;
        if (text.StartsWith(Marker, StringComparison.Ordinal))
        {
            text = text[Marker.Length..];
        }

        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            elements.Add(enumerator.GetTextElement());
        }

        return [.. elements];
    }
}
