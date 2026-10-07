using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.Tests.ViewModels;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Desktop.Widget;

namespace MyTaskApp.Desktop.Tests.StickyNotes;

/// <summary>
/// O atalho global de "Novo post-it" (ADR-054): Ctrl+Alt+N ligado por padrão,
/// trocável, e honesto quando outro programa já é dono da combinação.
/// </summary>
public class StickyNoteHotkeyTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "MyTaskApp.NoteHotkey." + Guid.CreateVersion7().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // A combinação e o arquivo
    // ------------------------------------------------------------------

    [Fact]
    public void TheDefault_IsCtrlAltN()
    {
        WidgetState.Default.NoteHotkey.Should().Be("ctrl-alt-n");
        HotkeyGesture.FindNewNote("ctrl-alt-n")!.Label.Should().Be("Ctrl+Alt+N");
        HotkeyGesture.FindNewNote("ctrl-shift-n")!.Label.Should().Be("Ctrl+Shift+N");
    }

    [Theory]
    [InlineData("ctrl-alt-n", "ctrl-alt-n")]
    [InlineData("ctrl-shift-n", "ctrl-shift-n")]
    [InlineData("off", "off")]
    [InlineData("ctrl-alt-del", "ctrl-alt-n")]
    [InlineData(null, "ctrl-alt-n")]
    public void AnUnknownChoice_FallsBackToTheDefault(string? stored, string expected)
    {
        HotkeyGesture.NormalizeNewNote(stored).Should().Be(expected);
        (WidgetState.Default with { NoteHotkey = stored! }).Sanitized().NoteHotkey.Should().Be(expected);
    }

    /// <summary>O <c>WM_HOTKEY</c> diz qual id disparou: dois atalhos, dois ids.</summary>
    [Fact]
    public void TheTwoShortcuts_NeverShareARegistrationId()
    {
        HotkeyGesture.NewNoteChoices.Should().OnlyContain(
            gesture => gesture.RegistrationId != HotkeyGesture.ToggleHud.RegistrationId);
        HotkeyGesture.NewNoteCtrlAltN.Modifiers.Should().Be(HotkeyModifiers.Control | HotkeyModifiers.Alt);
    }

    [Fact]
    public void AFileFromBeforePostIts_GetsTheShortcutOn()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "widget.json"), """{ "Width": 360, "Height": 560 }""");

        var state = new WidgetStateStore(NullLogger<WidgetStateStore>.Instance, _directory).Load();

        state.NoteHotkey.Should().Be("ctrl-alt-n");
    }

    [Fact]
    public void TheChoice_IsSavedAndComesBack()
    {
        var store = new WidgetStateStore(NullLogger<WidgetStateStore>.Instance, _directory);

        store.Save(WidgetState.Default with { NoteHotkey = "off" });

        store.Load().NoteHotkey.Should().Be("off");
    }

    [Fact]
    public void TheChrome_RestoresAndCapturesTheChoice()
    {
        var chrome = new WidgetChromeViewModel();

        chrome.Restore(WidgetState.Default with { NoteHotkey = "ctrl-shift-n" });

        chrome.NoteHotkey.Should().Be("ctrl-shift-n");
        chrome.CaptureInto(WidgetState.Default).NoteHotkey.Should().Be("ctrl-shift-n");
        chrome.NoteHotkeyChoices.Select(choice => choice.Label).Should().Equal("Ctrl+Alt+N", "Ctrl+Shift+N", "Desligado");
    }

    // ------------------------------------------------------------------
    // A janela é a dona do registro
    // ------------------------------------------------------------------

    private static MainWindow Show(RecordingHotkeys hotkeys, string choice = "ctrl-alt-n")
    {
        var viewModel = new TodayViewModel(
            new FakeUseCaseRunner { Result = new TodayBoard(new DateOnly(2026, 10, 7), [], [], [], [], []) },
            new FakeConfirmationDialog(),
            new FakeClipboardWriter(),
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);

        var window = new MainWindow { DataContext = viewModel };
        window.Attach(new WidgetHudTests.RecordingStore(WidgetState.Default with { NoteHotkey = choice }), noteHotkeys: hotkeys);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    [AvaloniaFact]
    public void OnceThePanelIsPlaced_TheDefaultShortcutIsRegistered()
    {
        var hotkeys = new RecordingHotkeys();

        var window = Show(hotkeys);

        hotkeys.Registered.Should().Equal("ctrl-alt-n");
        window.Chrome.CanUseNoteHotkey.Should().BeTrue();
        window.Chrome.NoteHotkeyMessage.Should().BeNull();
    }

    [AvaloniaFact]
    public void PressingIt_AsksForANewNote()
    {
        var hotkeys = new RecordingHotkeys();
        var window = Show(hotkeys);
        var asked = 0;
        window.NewNoteHotkeyPressed += () => asked++;

        hotkeys.Press();
        Dispatcher.UIThread.RunJobs();

        asked.Should().Be(1);
    }

    [AvaloniaFact]
    public void ChangingTheChoice_SwapsTheRegistration()
    {
        var hotkeys = new RecordingHotkeys();
        var window = Show(hotkeys);

        window.Chrome.NoteHotkey = "ctrl-shift-n";
        window.Chrome.NoteHotkey = "off";

        hotkeys.Registered.Should().Equal("ctrl-alt-n", "ctrl-shift-n");
        hotkeys.Unregistered.Should().Equal("ctrl-alt-n", "ctrl-shift-n");
    }

    [AvaloniaFact]
    public void AShortcutOwnedByAnotherProgram_TurnsOffAndSaysWhy()
    {
        var hotkeys = new RecordingHotkeys { Taken = { "ctrl-alt-n" } };

        var window = Show(hotkeys);

        window.Chrome.NoteHotkey.Should().Be("off");
        window.Chrome.NoteHotkeyMessage.Should().Be("Ctrl+Alt+N já está em uso por outro programa.");

        window.Chrome.NoteHotkey = "ctrl-shift-n";

        hotkeys.Registered.Should().Equal("ctrl-shift-n");
        window.Chrome.NoteHotkeyMessage.Should().BeNull();
    }

    [AvaloniaFact]
    public void ClosingThePanel_ReleasesTheShortcut()
    {
        var hotkeys = new RecordingHotkeys();
        var window = Show(hotkeys);

        window.ExitNow();

        hotkeys.Unregistered.Should().Equal("ctrl-alt-n");
    }

    [AvaloniaFact]
    public void WithoutSupport_TheOptionIsHidden()
    {
        var viewModel = new TodayViewModel(
            new FakeUseCaseRunner { Result = new TodayBoard(new DateOnly(2026, 10, 7), [], [], [], [], []) },
            new FakeConfirmationDialog(),
            new FakeClipboardWriter(),
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);
        var window = new MainWindow { DataContext = viewModel };

        window.Attach(new WidgetHudTests.RecordingStore(WidgetState.Default));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.Chrome.CanUseNoteHotkey.Should().BeFalse();
        UnsupportedGlobalHotkeyFactory.Instance.Create(HotkeyGesture.NewNoteCtrlAltN).Register(window, () => { })
            .Should().BeFalse();
    }

    /// <summary>Registra e solta de mentira — o de verdade é nativo e não roda no headless.</summary>
    private sealed class RecordingHotkeys : IGlobalHotkeyFactory
    {
        private Action? _pressed;

        public HashSet<string> Taken { get; } = [];

        public List<string> Registered { get; } = [];

        public List<string> Unregistered { get; } = [];

        public bool IsSupported => true;

        public IGlobalHotkeyService Create(HotkeyGesture gesture) => new Hotkey(this, gesture);

        public void Press() => _pressed?.Invoke();

        private sealed class Hotkey(RecordingHotkeys owner, HotkeyGesture gesture) : IGlobalHotkeyService
        {
            public bool IsSupported => true;

            public string GestureLabel => gesture.Label;

            public bool Register(Window window, Action pressed)
            {
                if (owner.Taken.Contains(gesture.Id))
                {
                    return false;
                }

                owner.Registered.Add(gesture.Id);
                owner._pressed = pressed;
                return true;
            }

            public void Unregister() => owner.Unregistered.Add(gesture.Id);
        }
    }
}
