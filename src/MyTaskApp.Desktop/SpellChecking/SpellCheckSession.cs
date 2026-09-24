namespace MyTaskApp.Desktop.SpellChecking;

/// <summary>
/// As palavras erradas de uma caixa de texto, sem a caixa (ADR-032). É onde
/// mora o cache e a regra da palavra em digitação — o que dá para errar sem
/// ninguém ver, e por isso fica fora do controle, testável.
/// </summary>
public sealed class SpellCheckSession(ISpellChecker checker)
{
    private readonly Dictionary<string, bool> _cache = new(StringComparer.Ordinal);

    public IReadOnlyList<WordSpan> Misspelled { get; private set; } = [];

    /// <summary>
    /// Verifica o texto e guarda o resultado em <see cref="Misspelled"/>.
    /// </summary>
    /// <param name="text">O texto da caixa.</param>
    /// <param name="editingAt">
    /// Onde está o cursor, se o usuário está digitando. A palavra sob ele não
    /// é marcada: "aplicativ" está errado até a próxima tecla, e um sublinhado
    /// piscando a cada letra é ruído.
    /// </param>
    /// <param name="cachedOnly">
    /// Só responde com o que já está no cache; palavra nova conta como certa.
    /// É o que roda a cada tecla, para os sublinhados acompanharem o texto sem
    /// chamar o corretor do sistema — a verificação completa vem depois do
    /// intervalo.
    /// </param>
    public IReadOnlyList<WordSpan> Check(string? text, int? editingAt = null, bool cachedOnly = false)
    {
        var misspelled = new List<WordSpan>();

        foreach (var word in SpellingTokenizer.Words(text))
        {
            if (editingAt is { } caret && word.Touches(caret))
            {
                continue;
            }

            if (!IsCorrect(word.Text, cachedOnly))
            {
                misspelled.Add(word);
            }
        }

        Misspelled = misspelled;
        return misspelled;
    }

    /// <summary>A palavra errada sob o índice, para o menu do clique direito.</summary>
    public WordSpan? MisspelledAt(int index)
    {
        foreach (var word in Misspelled)
        {
            if (word.Touches(index))
            {
                return word;
            }
        }

        return null;
    }

    public IReadOnlyList<string> Suggest(WordSpan word) => checker.Suggest(word.Text);

    /// <summary>
    /// O dicionário mudou (aqui ou em outra caixa): esquece o que sabia. Só
    /// limpar as palavras afetadas não basta — o Windows pode aceitar
    /// variações da palavra adicionada.
    /// </summary>
    public void Invalidate() => _cache.Clear();

    private bool IsCorrect(string word, bool cachedOnly)
    {
        if (_cache.TryGetValue(word, out var correct))
        {
            return correct;
        }

        if (cachedOnly)
        {
            return true;
        }

        correct = DeveloperTerms.Contains(word) || checker.IsCorrect(word);
        _cache[word] = correct;
        return correct;
    }
}
