using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace MyTaskApp.Desktop.SpellChecking;

// As interfaces de spellcheck.h (Windows 8+), com COM gerado em tempo de
// compilação — o mesmo caminho do [LibraryImport] do resto do app. A ordem dos
// métodos é a vtable: os que o app não usa continuam declarados, com nome de
// marcador, só para segurar o lugar dos que vêm depois.

[SupportedOSPlatform("windows")]
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("8E018A9D-2415-4677-BF08-794EA61F94BB")]
internal partial interface ISpellCheckerFactoryNative
{
    void SupportedLanguagesSlot();

    [return: MarshalAs(UnmanagedType.Bool)]
    bool IsSupported(string languageTag);

    ISpellCheckerNative CreateSpellChecker(string languageTag);
}

[SupportedOSPlatform("windows")]
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B")]
internal partial interface ISpellCheckerNative
{
    void LanguageTagSlot();

    IEnumSpellingErrorNative Check(string text);

    IEnumStringNative Suggest(string word);

    void Add(string word);

    void Ignore(string word);
}

[SupportedOSPlatform("windows")]
[GeneratedComInterface]
[Guid("803E3BD4-2828-4410-8290-418D1D73C762")]
internal partial interface IEnumSpellingErrorNative
{
    /// <summary>S_OK com um erro; S_FALSE quando acabou.</summary>
    [PreserveSig]
    int Next(out nint error);
}

[SupportedOSPlatform("windows")]
[GeneratedComInterface]
[Guid("00000101-0000-0000-C000-000000000046")]
internal partial interface IEnumStringNative
{
    [PreserveSig]
    int Next(uint count, out nint value, out uint fetched);
}

[SupportedOSPlatform("windows")]
internal static partial class WindowsSpellCheckingInterop
{
    private static readonly Guid SpellCheckerFactoryClsid = new("7AB36653-1796-484B-BDFA-E74F1DB7C1DC");

    private const uint ClassContextInProcAndLocal = 0x1 | 0x4;

    private const uint CoInitMultithreaded = 0x0;

    public static ISpellCheckerFactoryNative CreateFactory()
    {
        // A thread de UI da Avalonia já é STA — aí isto devolve
        // RPC_E_CHANGED_MODE e não muda nada. Nos testes, a thread pode nunca
        // ter iniciado COM, e sem isto o CoCreateInstance falha.
        _ = CoInitializeEx(0, CoInitMultithreaded);

        var iid = typeof(ISpellCheckerFactoryNative).GUID;
        Marshal.ThrowExceptionForHR(
            CoCreateInstance(SpellCheckerFactoryClsid, 0, ClassContextInProcAndLocal, iid, out var pointer));

        try
        {
            return (ISpellCheckerFactoryNative)new StrategyBasedComWrappers()
                .GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    /// <summary>Se há pelo menos um erro — numa palavra só, é o que importa.</summary>
    public static bool HasAnyError(IEnumSpellingErrorNative errors)
    {
        var found = errors.Next(out var error) == 0 && error != 0;

        if (error != 0)
        {
            Marshal.Release(error);
        }

        return found;
    }

    public static IEnumerable<string> ReadAll(IEnumStringNative strings)
    {
        while (strings.Next(1, out var value, out var fetched) == 0 && fetched == 1)
        {
            try
            {
                if (Marshal.PtrToStringUni(value) is { } text)
                {
                    yield return text;
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(value);
            }
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid classId,
        nint outer,
        uint context,
        in Guid interfaceId,
        out nint instance);
}
