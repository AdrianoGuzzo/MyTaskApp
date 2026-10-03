using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Widget;

namespace MyTaskApp.Desktop.Tests.Widget;

/// <summary>
/// A máquina de estados da janela (ADR-047) e o que ela <b>não</b> controla.
/// O pino antigo era um booleano que ligava três coisas por baixo; aqui cada
/// conceito anda sozinho, e é isso que estes testes cobram.
/// </summary>
public class WindowModeTests
{
    [Fact]
    public void ItStartsAsTheNormalWindow()
    {
        var chrome = new WidgetChromeViewModel();

        chrome.WindowMode.Should().Be(WindowMode.Normal);
        chrome.IsNormal.Should().BeTrue();
        chrome.IsHud.Should().BeFalse();
    }

    [Fact]
    public void Normal_To_Hud()
    {
        var chrome = new WidgetChromeViewModel();

        chrome.EnterHud();

        chrome.WindowMode.Should().Be(WindowMode.Hud);
        chrome.IsHudExpanded.Should().BeTrue();
        chrome.IsHeaderVisible.Should().BeFalse();
        chrome.AreGripsVisible.Should().BeFalse();
    }

    [Fact]
    public void Hud_To_Normal()
    {
        var chrome = new WidgetChromeViewModel();
        chrome.EnterHud();

        chrome.ExitHud();

        chrome.WindowMode.Should().Be(WindowMode.Normal);
        chrome.IsHeaderVisible.Should().BeTrue();
    }

    [Fact]
    public void Hud_To_HudCollapsed_And_Back()
    {
        var chrome = new WidgetChromeViewModel();
        chrome.EnterHud();

        chrome.CollapseHud();

        chrome.WindowMode.Should().Be(WindowMode.HudCollapsed);
        chrome.IsPanelVisible.Should().BeFalse();

        chrome.ExpandHud();

        chrome.WindowMode.Should().Be(WindowMode.Hud);
        chrome.IsPanelVisible.Should().BeTrue();
    }

    [Fact]
    public void HudCollapsed_To_Normal_IsOneClick()
    {
        // O usuário nunca fica preso: da pílula também se sai direto.
        var chrome = new WidgetChromeViewModel();
        chrome.EnterHud();
        chrome.CollapseHud();

        chrome.ExitHud();

        chrome.WindowMode.Should().Be(WindowMode.Normal);
    }

    [Fact]
    public void CollapsingAndExpanding_MeanNothingOutsideTheHud()
    {
        var chrome = new WidgetChromeViewModel();

        chrome.CollapseHud();
        chrome.ExpandHud();

        chrome.WindowMode.Should().Be(WindowMode.Normal);
    }

    [Fact]
    public void WithTheCollapsedHudOn_EnteringLandsOnThePill()
    {
        var chrome = new WidgetChromeViewModel { HudUseCollapsed = true };

        chrome.EnterHud();

        chrome.WindowMode.Should().Be(WindowMode.HudCollapsed);
    }

    [Fact]
    public void Toggling_GoesBothWays()
    {
        // É o que o atalho global e o item da bandeja chamam.
        var chrome = new WidgetChromeViewModel();

        chrome.ToggleHud();
        chrome.IsHud.Should().BeTrue();

        chrome.ToggleHud();
        chrome.IsNormal.Should().BeTrue();
    }

    [Fact]
    public void LeavingTheHud_ClosesTheCaptureItOpened()
    {
        var chrome = new WidgetChromeViewModel();
        chrome.EnterHud();
        chrome.ToggleCapture();

        chrome.ExitHud();

        chrome.IsCaptureOpen.Should().BeFalse();
    }

    [Fact]
    public void Collapsing_ClosesTheCapture_SoNothingTypedHidesBehindThePill()
    {
        var chrome = new WidgetChromeViewModel();
        chrome.EnterHud();
        chrome.ToggleCapture();

        chrome.CollapseHud();

        chrome.IsCaptureOpen.Should().BeFalse();
    }

    [Fact]
    public void Hud_IsNot_AlwaysOnTop()
    {
        // "Manter o HUD sempre visível" desligado: HUD, mas não no topo.
        var chrome = new WidgetChromeViewModel { HudAlwaysOnTop = false };

        chrome.EnterHud();

        chrome.IsHud.Should().BeTrue();
        chrome.IsWindowTopmost.Should().BeFalse();
    }

    [Fact]
    public void TheHudsTopmost_DoesNotLeakIntoTheNormalWindow()
    {
        // O problema que o pino antigo tinha: sair do "modo fixado" deixava a
        // janela grande presa na frente de tudo.
        var chrome = new WidgetChromeViewModel();

        chrome.EnterHud();
        chrome.IsWindowTopmost.Should().BeTrue();

        chrome.ExitHud();

        chrome.IsTopmost.Should().BeFalse();
        chrome.IsWindowTopmost.Should().BeFalse();
    }

    [Fact]
    public void TheNormalWindowsTopmost_SurvivesARoundTripThroughTheHud()
    {
        var chrome = new WidgetChromeViewModel { IsTopmost = true, HudAlwaysOnTop = false };

        chrome.EnterHud();
        chrome.IsWindowTopmost.Should().BeFalse();

        chrome.ExitHud();
        chrome.IsWindowTopmost.Should().BeTrue();
    }

    [Fact]
    public void Hud_IsNot_Transparency()
    {
        // Mexer na opacidade não entra nem sai do HUD, nem muda a ordem Z.
        var chrome = new WidgetChromeViewModel();
        var changed = new List<string?>();
        chrome.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        chrome.HudOpacity = 0.8;

        chrome.IsNormal.Should().BeTrue();
        changed.Should().BeEquivalentTo(new string?[]
        {
            nameof(WidgetChromeViewModel.HudOpacity),
            nameof(WidgetChromeViewModel.HudOpacityLabel),
        });
    }

    [Fact]
    public void EnteringTheHud_DoesNotTouchTheOpacity()
    {
        var chrome = new WidgetChromeViewModel { HudOpacity = 0.85 };

        chrome.EnterHud();
        chrome.ExitHud();

        chrome.HudOpacity.Should().Be(0.85);
    }

    [Theory]
    [InlineData(0.2, HudSettings.MinOpacity)]
    [InlineData(1.7, 1)]
    [InlineData(double.NaN, HudSettings.DefaultOpacity)]
    public void TheOpacityHasAFloor_SoTheHudNeverDisappears(double asked, double expected)
    {
        var chrome = new WidgetChromeViewModel { HudOpacity = asked };

        chrome.HudOpacity.Should().Be(expected);
    }

    [Fact]
    public void TheCloseChoice_IsReadFromTheRadioButtons()
    {
        var chrome = new WidgetChromeViewModel();

        chrome.CloseHides.Should().BeTrue("esconder na bandeja continua sendo o padrão");

        chrome.CloseEntersHud = true;

        chrome.CloseBehavior.Should().Be(CloseBehavior.Hud);
        chrome.CloseHides.Should().BeFalse();

        // Desmarcar um rádio não escolhe nada: só marcar outro.
        chrome.CloseEntersHud = false;
        chrome.CloseBehavior.Should().Be(CloseBehavior.Hud);

        chrome.CloseExits = true;
        chrome.CloseBehavior.Should().Be(CloseBehavior.Exit);
    }

    [Fact]
    public void TheCloseDecision_FollowsModeAndTray()
    {
        var chrome = new WidgetChromeViewModel { CloseBehavior = CloseBehavior.Hud, TrayAvailable = true };

        chrome.DecideClose().Should().Be(CloseAction.EnterHud);

        chrome.EnterHud();

        chrome.DecideClose().Should().Be(CloseAction.AskInHud);
    }

    [Fact]
    public void DraggingTheHud_MakesThePositionCustom()
    {
        var chrome = new WidgetChromeViewModel();

        chrome.RememberHudPoint(640, 200);

        chrome.HudPosition.Should().Be(HudPosition.Custom);
        chrome.HudCustomPoint.Should().Be(new PixelPointValue(640, 200));

        // E escolher um canto de novo volta a ancorar.
        chrome.UseHudPosition("BottomRight");
        chrome.HudPosition.Should().Be(HudPosition.BottomRight);
    }

    [Fact]
    public void TheMenuRadios_FollowTheChoice()
    {
        var chrome = new WidgetChromeViewModel();

        chrome.HudSizeOptions.Single(option => option.IsSelected).Label.Should().Be("Compacto");
        chrome.HudPositionOptions.Single(option => option.IsSelected).Label.Should().Be("Superior esquerdo");

        chrome.HudSizeOptions.Single(option => option.Label == "Expandido").SelectCommand.Execute(null);
        chrome.HudPositionOptions.Single(option => option.Label == "Inferior direito").SelectCommand.Execute(null);

        chrome.HudSize.Should().Be(HudSize.Expanded);
        chrome.HudPosition.Should().Be(HudPosition.BottomRight);
        chrome.HudSizeOptions.Single(option => option.IsSelected).Label.Should().Be("Expandido");
    }

    [Fact]
    public void TheComboIndexes_MapToTheEnums()
    {
        var chrome = new WidgetChromeViewModel { HudPositionIndex = (int)HudPosition.CenterRight, HudSizeIndex = 1 };

        chrome.HudPosition.Should().Be(HudPosition.CenterRight);
        chrome.HudSize.Should().Be(HudSize.Normal);

        // Um índice fora da lista (ComboBox sem seleção = -1) não muda nada.
        chrome.HudPositionIndex = -1;
        chrome.HudPosition.Should().Be(HudPosition.CenterRight);
    }

    [Fact]
    public void EverythingTravelsToTheDiskAndBack()
    {
        var chrome = new WidgetChromeViewModel
        {
            CloseBehavior = CloseBehavior.Hud,
            StartInHud = true,
            HudAlwaysOnTop = false,
            HudSize = HudSize.Expanded,
            HudOpacity = 0.8,
            HudUseCollapsed = true,
            HudIntroSeen = true,
            UseGlobalHotkey = true,
        };

        chrome.RememberHudPoint(300, 400);
        chrome.EnterHud();

        var saved = chrome.CaptureInto(WidgetState.Default);

        saved.WindowMode.Should().Be(WindowMode.HudCollapsed);
        saved.CloseBehavior.Should().Be(CloseBehavior.Hud);
        saved.StartInHud.Should().BeTrue();
        saved.GlobalHotkey.Should().BeTrue();
        saved.Hud.Should().Be(new HudSettings
        {
            Position = HudPosition.Custom,
            Size = HudSize.Expanded,
            Opacity = 0.8,
            AlwaysOnTop = false,
            UseCollapsed = true,
            X = 300,
            Y = 400,
            IntroSeen = true,
        });

        var restored = new WidgetChromeViewModel();
        restored.Restore(saved);

        restored.CaptureInto(WidgetState.Default).Should().Be(saved);
    }

    [Fact]
    public void StartInHud_WinsOverHowTheWindowWasLeft()
    {
        var chrome = new WidgetChromeViewModel();

        chrome.Restore(WidgetState.Default with { StartInHud = true, WindowMode = WindowMode.Normal });

        chrome.WindowMode.Should().Be(WindowMode.Hud);
    }

    [Fact]
    public void WithoutStartInHud_ItReopensTheWayItWasLeft()
    {
        var chrome = new WidgetChromeViewModel();

        chrome.Restore(WidgetState.Default with { WindowMode = WindowMode.Hud });

        chrome.WindowMode.Should().Be(WindowMode.Hud);

        chrome.Restore(WidgetState.Default);

        chrome.WindowMode.Should().Be(WindowMode.Normal);
    }

    [Fact]
    public void TheHotkeyOption_ShowsOnlyWhereThePlatformHasIt()
    {
        var chrome = new WidgetChromeViewModel();

        chrome.CanUseGlobalHotkey.Should().BeFalse();

        chrome.UseHotkeySupport(true);

        chrome.CanUseGlobalHotkey.Should().BeTrue();
    }

    [Fact]
    public void TheIntro_ShowsOnceAndOnlyInTheOpenHud()
    {
        var chrome = new WidgetChromeViewModel();

        chrome.IsHudIntroVisible.Should().BeFalse();

        chrome.EnterHud();
        chrome.IsHudIntroVisible.Should().BeTrue();

        chrome.DismissHudIntro();
        chrome.IsHudIntroVisible.Should().BeFalse();

        chrome.ExitHud();
        chrome.EnterHud();
        chrome.IsHudIntroVisible.Should().BeFalse();
    }
}
