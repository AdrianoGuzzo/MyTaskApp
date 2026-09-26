using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.SpellChecking;
using MyTaskApp.Desktop.Tests.ViewModels;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;

namespace MyTaskApp.Desktop.Tests.SpellChecking;

/// <summary>
/// A caixa de verdade, headless: o que só quebra na tela é o sublinhado não
/// aparecer (adorner sem camada, presenter não achado) e a troca pela
/// sugestão não chegar ao binding.
/// </summary>
public partial class SpellCheckRenderingTests
{
    private sealed partial class Model : ObservableObject
    {
        [ObservableProperty]
        private string _text = string.Empty;
    }

    private static (Window Window, TextBox Box, Model Model, SpellCheckBinder Binder) Show(
        FakeSpellChecker checker,
        string text)
    {
        var previous = SpellCheck.Checker;
        SpellCheck.Checker = checker;

        try
        {
            var model = new Model { Text = text };
            var box = new TextBox { Width = 300, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            box.Bind(TextBox.TextProperty, new Avalonia.Data.Binding(nameof(Model.Text)) { Mode = Avalonia.Data.BindingMode.TwoWay });
            SpellCheck.SetIsEnabled(box, true);

            var window = new Window { Content = box, DataContext = model, Width = 400, Height = 300 };
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            return (window, box, model, SpellCheck.GetBinder(box)!);
        }
        finally
        {
            SpellCheck.Checker = previous;
        }
    }

    [AvaloniaFact]
    public void MisspelledWord_GetsAnUnderline_InTheAdornerLayer()
    {
        var (window, _, _, binder) = Show(new FakeSpellChecker("aplicativoo"), "Estou testando este aplicativoo");

        binder.CheckNow();

        binder.Squiggles.Should().NotBeNull();
        binder.Squiggles!.GetVisualParent().Should().NotBeNull("sem camada de adorner, o sublinhado não aparece");
        binder.Squiggles.Underlines().Should().ContainSingle()
            .Which.Width.Should().BePositive();

        window.Close();
    }

    [AvaloniaFact]
    public void CorrectText_HasNoUnderline()
    {
        var (window, _, _, binder) = Show(new FakeSpellChecker("aplicativoo"), "Estou testando este aplicativo");

        binder.CheckNow();

        binder.Squiggles!.Underlines().Should().BeEmpty();

        window.Close();
    }

    [AvaloniaFact]
    public void ChoosingASuggestion_ReplacesTheWord_ThroughTheBinding()
    {
        var checker = new FakeSpellChecker("aplicativoo");
        checker.Suggestions["aplicativoo"] = ["aplicativo"];
        var (window, _, model, binder) = Show(checker, "Estou testando este aplicativoo agora");
        binder.CheckNow();

        var word = binder.Session.MisspelledAt(22)!.Value;
        var menu = binder.BuildMenu(word);
        menu.Items.OfType<MenuItem>().First().Header.Should().Be("aplicativo");

        binder.Replace(word, "aplicativo");

        model.Text.Should().Be("Estou testando este aplicativo agora");
        binder.Squiggles!.Underlines().Should().BeEmpty();

        window.Close();
    }

    /// <summary>
    /// O clique direito fora da primeira linha: o <c>HitTestPoint</c> do
    /// layout diz que o ponto está fora do texto, e o menu das sugestões não
    /// abria na anotação — só o Recortar/Copiar/Colar padrão.
    /// </summary>
    [AvaloniaFact]
    public void RightClick_OnAMisspelledWord_BelowTheFirstLine_OpensTheSuggestions()
    {
        const string text = "Linha um\nLinha dois\n\nEstou testando este aplicativoo agora";
        var (window, box, _, binder) = Show(new FakeSpellChecker("aplicativoo"), text);
        binder.CheckNow();

        var presenter = box.GetVisualDescendants().OfType<TextPresenter>().Single();
        var word = presenter.TextLayout.HitTestTextPosition(text.IndexOf("aplicativoo", StringComparison.Ordinal) + 3);

        // No túnel, um passo abaixo da caixa: só o binder marca o pedido como
        // tratado até ali. O flyout padrão vem depois, na volta (bubble).
        var handledBySpellCheck = false;
        presenter.AddHandler(
            Control.ContextRequestedEvent,
            (_, e) => handledBySpellCheck = e.Handled,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        var point = presenter.TranslatePoint(word.Center, window)!.Value;
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        handledBySpellCheck.Should().BeTrue("o clique caiu numa palavra errada, e o menu é o das sugestões");

        window.Close();
    }

    [AvaloniaFact]
    public void RightClick_OnACorrectWord_KeepsTheDefaultMenu()
    {
        var (window, box, _, binder) = Show(new FakeSpellChecker("aplicativoo"), "Linha um\nEstou testando este aplicativoo");
        binder.CheckNow();

        var presenter = box.GetVisualDescendants().OfType<TextPresenter>().Single();

        binder.WordAt(presenter.TextLayout.HitTestTextPosition(12).Center).Should().BeNull();

        window.Close();
    }

    [AvaloniaFact]
    public void Ignoring_ClearsTheUnderline()
    {
        var checker = new FakeSpellChecker("aplicativoo");
        var (window, _, _, binder) = Show(checker, "este aplicativoo");
        binder.CheckNow();

        checker.Ignore("aplicativoo");

        binder.Squiggles!.Underlines().Should().BeEmpty();

        window.Close();
    }

    /// <summary>
    /// A janela de anotação de verdade: o XAML liga a anotação e o título, e
    /// deixa de fora o campo Diretório (path não é prosa).
    /// </summary>
    [AvaloniaFact]
    public async Task TheNotesWindow_ChecksTheNoteAndTitle_ButNotTheDirectory()
    {
        var previous = SpellCheck.Checker;
        SpellCheck.Checker = new FakeSpellChecker("aplicativoo");

        try
        {
            var runner = new FakeUseCaseRunner();
            runner.ResultsByHandler[typeof(GetTaskDirectoriesHandler)] = TaskNotesAliasTests.Directories;
            var viewModel = new TaskNotesViewModel(
                runner,
                new FakeDirectoryProbe(),
                TestDevelopment.For(runner),
                NullLogger<TaskNotesViewModel>.Instance);
            viewModel.Load(TaskNotesAliasTests.Row());

            var window = new TaskNotesWindow(viewModel, new FakeConfirmationDialog());
            window.Show();
            await viewModel.LoadAliasesAsync(CancellationToken.None);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            SpellCheck.GetIsEnabled(window.FindControl<TextBox>("TitleEditor")!).Should().BeTrue();
            SpellCheck.GetIsEnabled(window.FindControl<TextBox>("DirectoryBox")!).Should().BeFalse();

            var editor = window.FindControl<TextBox>("Editor")!;
            editor.Text = "Estou testando este aplicativoo";
            var binder = SpellCheck.GetBinder(editor)!;
            binder.CheckNow();

            binder.Squiggles!.Underlines().Should().ContainSingle();

            window.Close();
        }
        finally
        {
            SpellCheck.Checker = previous;
        }
    }

    [AvaloniaFact]
    public void WithoutAChecker_NothingIsAttached()
    {
        var box = new TextBox();
        SpellCheck.SetIsEnabled(box, true);

        var window = new Window { Content = box };
        window.Show();

        SpellCheck.GetBinder(box)!.Squiggles.Should().BeNull();

        window.Close();
    }
}
