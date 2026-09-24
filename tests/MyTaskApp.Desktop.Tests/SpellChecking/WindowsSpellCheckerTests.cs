using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Desktop.SpellChecking;

namespace MyTaskApp.Desktop.Tests.SpellChecking;

/// <summary>
/// O corretor de verdade do Windows. A vtable COM declarada à mão é o tipo de
/// coisa que compila, passa em todo teste com corretor falso e só quebra aqui:
/// um método fora de ordem chama outro método.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsSpellCheckerTests
{
    /// <summary>
    /// Pula se o Windows não tiver o dicionário: ele só existe para os idiomas
    /// instalados, e o CI e as máquinas só em português não têm os dois.
    /// </summary>
    private static WindowsSpellChecker Create(string language)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A Windows Spell Checking API só existe no Windows.");
        Assert.SkipUnless(
            WindowsSpellCheckingInterop.CreateFactory().IsSupported(language),
            $"O Windows desta máquina não tem o dicionário {language}.");

        return WindowsSpellChecker.TryCreate(NullLogger.Instance)!;
    }

    [Fact]
    public void AMisspelledPortugueseWord_IsWrong_AndTheFixIsSuggested()
    {
        var checker = Create("pt-BR");

        checker.IsCorrect("aplicativoo").Should().BeFalse();
        checker.Suggest("aplicativoo").Should().Contain("aplicativo");
    }

    [Theory]
    [InlineData("aplicativo")]
    [InlineData("revisão")]
    [InlineData("Estou")]
    public void RightInPortuguese_IsCorrect(string word)
    {
        Create("pt-BR").IsCorrect(word).Should().BeTrue();
    }

    [Theory]
    [InlineData("however")]
    [InlineData("throughput")]
    public void RightInEnglish_IsCorrect(string word)
    {
        Create("en-US").IsCorrect(word).Should().BeTrue();
    }

    [Fact]
    public void Ignore_AcceptsTheWord_AndTellsTheBoxes()
    {
        var checker = Create("pt-BR");
        var changed = 0;
        checker.DictionaryChanged += (_, _) => changed++;

        checker.Ignore("aplicativoo");

        checker.IsCorrect("aplicativoo").Should().BeTrue();
        changed.Should().Be(1);
    }
}
