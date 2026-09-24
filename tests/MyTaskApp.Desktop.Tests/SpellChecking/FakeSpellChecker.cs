using MyTaskApp.Desktop.SpellChecking;

namespace MyTaskApp.Desktop.Tests.SpellChecking;

/// <summary>
/// Um corretor com dicionário fixo: errada é a palavra da lista. Conta as
/// perguntas, para o teste do cache.
/// </summary>
internal sealed class FakeSpellChecker(params string[] misspelled) : ISpellChecker
{
    private readonly HashSet<string> _misspelled = new(misspelled, StringComparer.Ordinal);

    public Dictionary<string, string[]> Suggestions { get; } = new(StringComparer.Ordinal);

    public List<string> Asked { get; } = [];

    public List<string> Added { get; } = [];

    public bool IsAvailable => true;

    public event EventHandler? DictionaryChanged;

    public bool IsCorrect(string word)
    {
        Asked.Add(word);
        return !_misspelled.Contains(word);
    }

    public IReadOnlyList<string> Suggest(string word) =>
        Suggestions.TryGetValue(word, out var suggestions) ? suggestions : [];

    public void AddToDictionary(string word)
    {
        Added.Add(word);
        Ignore(word);
    }

    public void Ignore(string word)
    {
        _misspelled.Remove(word);
        DictionaryChanged?.Invoke(this, EventArgs.Empty);
    }
}
