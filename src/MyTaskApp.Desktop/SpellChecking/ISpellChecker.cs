namespace MyTaskApp.Desktop.SpellChecking;

/// <summary>
/// O corretor ortográfico do sistema operacional (ADR-032). A UI só conhece
/// esta porta: no Windows responde a Windows Spell Checking API; fora dele, ou
/// quando o dicionário não está instalado, o <see cref="NoSpellChecker"/>.
/// </summary>
/// <remarks>
/// Recebe palavras soltas, e não o texto inteiro: decidir o que é palavra — e
/// o que é código, path, <c>@alias</c> ou <c>#tag</c> — é do
/// <see cref="SpellingTokenizer"/>, que é o mesmo em qualquer sistema.
/// </remarks>
public interface ISpellChecker
{
    /// <summary>Se há dicionário por trás. Falso no objeto nulo.</summary>
    bool IsAvailable { get; }

    bool IsCorrect(string word);

    IReadOnlyList<string> Suggest(string word);

    /// <summary>Persiste no dicionário do usuário do sistema.</summary>
    void AddToDictionary(string word);

    /// <summary>Aceita a palavra até o app fechar.</summary>
    void Ignore(string word);

    /// <summary>
    /// Uma palavra foi adicionada ou ignorada: quem guarda resposta em cache
    /// tem de perguntar de novo — inclusive as outras caixas abertas.
    /// </summary>
    event EventHandler? DictionaryChanged;
}

/// <summary>
/// Sem corretor: tudo está certo e não há sugestão. É o que roda no Linux, nos
/// testes headless e quando o Windows não tem nenhum dos dicionários.
/// </summary>
public sealed class NoSpellChecker : ISpellChecker
{
    public static NoSpellChecker Instance { get; } = new();

    public bool IsAvailable => false;

    public bool IsCorrect(string word) => true;

    public IReadOnlyList<string> Suggest(string word) => [];

    public void AddToDictionary(string word)
    {
    }

    public void Ignore(string word)
    {
    }

    public event EventHandler? DictionaryChanged
    {
        add { }
        remove { }
    }
}
