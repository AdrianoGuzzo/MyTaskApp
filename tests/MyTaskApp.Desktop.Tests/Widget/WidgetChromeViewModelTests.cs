using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Widget;

namespace MyTaskApp.Desktop.Tests.Widget;

/// <summary>
/// A moldura do painel: qual modo está ligado e o que cada modo mostra. Sem
/// janela — o que se testa aqui é a decisão, não o pixel.
/// </summary>
public class WidgetChromeViewModelTests
{
    [Fact]
    public void ItOpensAsTheFullPanel()
    {
        var chrome = new WidgetChromeViewModel();

        chrome.Mode.Should().Be(WidgetMode.Expanded);
        chrome.IsPanelVisible.Should().BeTrue();
        chrome.IsExpanded.Should().BeTrue();
        chrome.IsTopmost.Should().BeFalse();
    }

    [Fact]
    public void TheCompactMode_KeepsTheListAndDropsTheCaptureBox()
    {
        var chrome = new WidgetChromeViewModel();

        chrome.UseMode("compact");

        chrome.IsCompact.Should().BeTrue();
        chrome.IsPanelVisible.Should().BeTrue();
        chrome.IsExpanded.Should().BeFalse();
    }

    [Fact]
    public void TheCollapsedMode_LeavesOnlyThePill()
    {
        var chrome = new WidgetChromeViewModel();

        chrome.UseMode("collapsed");

        chrome.IsCollapsed.Should().BeTrue();
        chrome.IsPanelVisible.Should().BeFalse();
    }

    [Fact]
    public void AnUnknownModeName_FallsBackToTheFullPanel()
    {
        var chrome = new WidgetChromeViewModel { Mode = WidgetMode.Collapsed };

        chrome.UseMode("whatever");

        chrome.Mode.Should().Be(WidgetMode.Expanded);
    }

    [Fact]
    public void CollapsingTwice_GoesBackToTheFullPanel()
    {
        // É o mesmo botão: recolher e voltar não podem exigir dois controles.
        var chrome = new WidgetChromeViewModel();

        chrome.ToggleCollapsed();
        chrome.ToggleCollapsed();

        chrome.Mode.Should().Be(WidgetMode.Expanded);
    }

    [Fact]
    public void TheCollapseGlyphFollowsTheMode()
    {
        var chrome = new WidgetChromeViewModel();

        // Glifos da fonte de ícones do Windows: menos e mais.
        chrome.CollapseGlyph.Should().Be("\uE738");

        chrome.ToggleCollapsed();

        chrome.CollapseGlyph.Should().Be("\uE710");
    }

    [Fact]
    public void ChangingTheModeNotifiesEverythingTheViewBindsTo()
    {
        // Sem estas notificações o XAML liga uma vez e nunca mais atualiza.
        var chrome = new WidgetChromeViewModel();
        var changed = new List<string?>();
        chrome.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        chrome.UseMode("collapsed");

        changed.Should().Contain(nameof(WidgetChromeViewModel.IsPanelVisible))
            .And.Contain(nameof(WidgetChromeViewModel.CollapseGlyph));
    }

    [Fact]
    public void TheTopmostToggle_FlipsAndDescribesItself()
    {
        var chrome = new WidgetChromeViewModel();

        chrome.ToggleTopmost();

        chrome.IsTopmost.Should().BeTrue();
        chrome.TopmostTip.Should().Be("Sempre no topo: ligado");
        chrome.TopmostGlyph.Should().Be("\uE840");
    }

    [Fact]
    public void HidingAsksTheWindow_BecauseTheViewModelCannotHideItself()
    {
        var chrome = new WidgetChromeViewModel();
        var asked = 0;
        chrome.HideRequested += () => asked++;

        chrome.Hide();

        asked.Should().Be(1);
    }

    [Fact]
    public void ItRestoresWhatWasSavedAndGivesItBackUnchanged()
    {
        var saved = WidgetState.Default with
        {
            Mode = WidgetMode.Compact,
            Topmost = true,
            StartHidden = true,
            X = 100,
            Y = 200,
            Width = 380,
        };

        var chrome = new WidgetChromeViewModel();
        chrome.Restore(saved);

        chrome.Mode.Should().Be(WidgetMode.Compact);
        chrome.IsTopmost.Should().BeTrue();
        chrome.StartHidden.Should().BeTrue();

        // Carimbar as preferências não pode mexer na geometria.
        chrome.CaptureInto(saved).Should().Be(saved);
    }

    [Fact]
    public void TheTopmostToggle_NotifiesNothingButItself()
    {
        // Item 8 da especificação: o pino controla só "ficar sobre as outras
        // janelas". Se ele notificasse Mode — ou qualquer coisa derivada dela —
        // a janela rodaria ApplyMode(), que redimensiona e reposiciona o painel.
        var chrome = new WidgetChromeViewModel();
        var changed = new List<string?>();
        chrome.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        chrome.ToggleTopmost();

        // O pino leva o modo discreto junto, e nada mais: nenhuma dessas
        // propriedades passa por ApplyMode().
        changed.Should().BeEquivalentTo(new string?[]
        {
            nameof(WidgetChromeViewModel.IsTopmost),
            nameof(WidgetChromeViewModel.TopmostTip),
            nameof(WidgetChromeViewModel.TopmostGlyph),
            nameof(WidgetChromeViewModel.GhostPinTip),
            nameof(WidgetChromeViewModel.IsGhost),
            nameof(WidgetChromeViewModel.IsGhostActive),
            nameof(WidgetChromeViewModel.IsHeaderVisible),
            nameof(WidgetChromeViewModel.GhostTip),
        });
    }

    [Fact]
    public void ChangingTheMode_SaysNothingAboutThePin()
    {
        // E o contrário também: fixar não é um modo de exibição, então trocar de
        // modo não pode soltar o pino nem fingir que ele mudou.
        var chrome = new WidgetChromeViewModel { IsTopmost = true };
        var changed = new List<string?>();
        chrome.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        chrome.UseMode("compact");

        chrome.IsTopmost.Should().BeTrue();
        changed.Should().NotContain(nameof(WidgetChromeViewModel.IsTopmost));
    }
}
