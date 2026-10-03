using MyTaskApp.Desktop.Widget;

namespace MyTaskApp.Desktop.Tests.Widget;

/// <summary>
/// O que o X faz (ADR-047). Regra pura: a janela só executa o que sai daqui,
/// então cada combinação de escolha, modo e bandeja cabe num teste sem display.
/// </summary>
public class CloseRoutingTests
{
    [Theory]
    [InlineData(WindowMode.Normal)]
    [InlineData(WindowMode.Hud)]
    [InlineData(WindowMode.HudCollapsed)]
    public void Exit_AlwaysExits(WindowMode mode)
    {
        CloseRouting.Decide(CloseBehavior.Exit, mode, trayAvailable: true)
            .Should().Be(CloseAction.Exit);
    }

    [Theory]
    [InlineData(WindowMode.Normal)]
    [InlineData(WindowMode.Hud)]
    [InlineData(WindowMode.HudCollapsed)]
    public void Tray_HidesToTheTray_FromAnyMode(WindowMode mode)
    {
        CloseRouting.Decide(CloseBehavior.Tray, mode, trayAvailable: true)
            .Should().Be(CloseAction.HideToTray);
    }

    [Fact]
    public void Tray_WithoutATrayIcon_Exits()
    {
        // Esconder sem ícone na bandeja deixaria o app vivo e inalcançável.
        CloseRouting.Decide(CloseBehavior.Tray, WindowMode.Normal, trayAvailable: false)
            .Should().Be(CloseAction.Exit);
    }

    [Fact]
    public void Hud_FromTheNormalWindow_EntersTheHud()
    {
        CloseRouting.Decide(CloseBehavior.Hud, WindowMode.Normal, trayAvailable: true)
            .Should().Be(CloseAction.EnterHud);
    }

    [Theory]
    [InlineData(WindowMode.Hud, true)]
    [InlineData(WindowMode.HudCollapsed, true)]
    [InlineData(WindowMode.Hud, false)]
    public void Hud_AlreadyInTheHud_AsksInsteadOfIgnoringTheClick(WindowMode mode, bool tray)
    {
        // Entrar no HUD estando no HUD não faria nada: o clique pareceria
        // perdido. A janela pergunta — ocultar, sair, ou voltar.
        CloseRouting.Decide(CloseBehavior.Hud, mode, tray)
            .Should().Be(CloseAction.AskInHud);
    }

    [Fact]
    public void AValueOutsideTheCatalog_FallsBackToTheTray()
    {
        CloseRouting.Decide((CloseBehavior)42, WindowMode.Normal, trayAvailable: true)
            .Should().Be(CloseAction.HideToTray);
    }
}
