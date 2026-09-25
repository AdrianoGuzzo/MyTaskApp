using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace MyTaskApp.Desktop.SpellChecking;

/// <summary>
/// O corretor do Windows (Windows Spell Checking API), em português e inglês
/// ao mesmo tempo (ADR-032).
/// </summary>
/// <remarks>
/// <para>
/// Uma palavra só está errada se estiver errada nos <b>dois</b> idiomas: as
/// anotações misturam "branch", "merge" e "deploy" com português, e marcar
/// cada termo técnico ensinaria o usuário a ignorar o sublinhado. Cada idioma
/// só entra se o Windows tiver o dicionário dele — o inglês costuma faltar numa
/// máquina só em português.
/// </para>
/// <para>
/// Só é chamado da thread de UI (a caixa verifica depois de um intervalo e com
/// cache), então não há pergunta de apartment COM a responder.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsSpellChecker : ISpellChecker
{
    public static readonly string[] Languages = ["pt-BR", "en-US"];

    private const int MaxSuggestions = 5;

    private readonly IReadOnlyList<ISpellCheckerNative> _checkers;

    private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);

    private readonly ILogger _logger;

    private bool _failureLogged;

    private WindowsSpellChecker(IReadOnlyList<ISpellCheckerNative> checkers, ILogger logger)
    {
        _checkers = checkers;
        _logger = logger;
    }

    public event EventHandler? DictionaryChanged;

    public bool IsAvailable => true;

    /// <summary>
    /// O corretor com os idiomas que o Windows tiver, na ordem de
    /// <see cref="Languages"/>; nulo se não tiver nenhum ou se a API falhar —
    /// aí a composição usa o <see cref="NoSpellChecker"/>.
    /// </summary>
    public static WindowsSpellChecker? TryCreate(ILogger logger)
    {
        try
        {
            var factory = WindowsSpellCheckingInterop.CreateFactory();
            var installed = Languages.Where(factory.IsSupported).ToList();

            if (installed.Count == 0)
            {
                logger.LogWarning(
                    "SpellCheckerUnavailable: nenhum dicionario instalado para {Languages}",
                    string.Join(", ", Languages));
                return null;
            }

            // O dicionário de um idioma só existe se o idioma estiver
            // instalado no Windows; sem o inglês, o corretor segue só em
            // português (e os DeveloperTerms cobrem o jargão).
            logger.LogInformation("SpellCheckerReady {Languages}", string.Join(", ", installed));

            return new WindowsSpellChecker(installed.Select(factory.CreateSpellChecker).ToList(), logger);
        }
        catch (Exception exception) when (
            exception is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException)
        {
            // Sem corretor o app funciona igual; não pode é deixar de abrir.
            logger.LogWarning(exception, "SpellCheckerUnavailable");
            return null;
        }
    }

    public bool IsCorrect(string word)
    {
        if (_ignored.Contains(word))
        {
            return true;
        }

        try
        {
            return _checkers.Any(checker => !WindowsSpellCheckingInterop.HasAnyError(checker.Check(word)));
        }
        catch (COMException exception)
        {
            // Na dúvida, não sublinha: um falso "errado" é pior que um erro
            // não apontado.
            LogFailure(exception);
            return true;
        }
    }

    public IReadOnlyList<string> Suggest(string word)
    {
        try
        {
            return _checkers
                .SelectMany(checker => WindowsSpellCheckingInterop.ReadAll(checker.Suggest(word)))
                .Distinct(StringComparer.Ordinal)
                .Take(MaxSuggestions)
                .ToList();
        }
        catch (COMException exception)
        {
            LogFailure(exception);
            return [];
        }
    }

    /// <summary>
    /// Vai para o dicionário do primeiro idioma (português): é o do usuário, e
    /// o Windows o guarda entre execuções — e para os outros apps também.
    /// </summary>
    public void AddToDictionary(string word)
    {
        try
        {
            _checkers[0].Add(word);
        }
        catch (COMException exception)
        {
            // Não persistiu, mas o usuário pediu para parar de marcar.
            LogFailure(exception);
            _ignored.Add(word);
        }

        DictionaryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Ignore(string word)
    {
        _ignored.Add(word);
        DictionaryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LogFailure(COMException exception)
    {
        if (_failureLogged)
        {
            return;
        }

        _failureLogged = true;
        _logger.LogWarning(exception, "SpellCheckFailed");
    }
}
