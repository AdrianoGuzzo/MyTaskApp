using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O autocomplete de <c>@alias</c> sozinho, fora da anotação — é o mesmo que
/// serve ao campo Diretório da aba Desenvolvimento (ADR-027).
/// </summary>
public class AliasCompletionViewModelTests
{
    private static AliasCompletionViewModel Loaded()
    {
        var completion = new AliasCompletionViewModel();
        completion.SetDirectories(TaskNotesAliasTests.Directories, new Dictionary<Guid, bool>());
        return completion;
    }

    /// <summary>
    /// A regressão do Popup: o <c>IsOpen</c> é amarrado nos dois sentidos, e
    /// fechar dispara o <c>Closed</c> na hora. Se o token ainda estivesse lá, o
    /// handler de "fechou por fora" dispensaria um <c>@</c> que só estava sendo
    /// filtrado.
    /// </summary>
    [Fact]
    public void ClosingTheList_ClearsTheTokenBeforeTheListCloses()
    {
        var completion = Loaded();
        completion.UpdateCompletion("@eco", 4);

        object? tokenWhenClosed = "não fechou";
        completion.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AliasCompletionViewModel.IsCompletionOpen) && !completion.IsCompletionOpen)
            {
                tokenWhenClosed = completion.CompletionToken;
            }
        };

        completion.UpdateCompletion("@ecx", 4);

        tokenWhenClosed.Should().BeNull();
    }

    [Fact]
    public void NoMatch_ThenBackspace_ReopensTheList()
    {
        var completion = Loaded();

        // O que a janela faz quando o Popup fecha: dispensa só se ainda houver token.
        completion.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AliasCompletionViewModel.IsCompletionOpen)
                && !completion.IsCompletionOpen
                && completion.CompletionToken is not null)
            {
                completion.DismissCompletion();
            }
        };

        completion.UpdateCompletion("@ec", 3);
        completion.UpdateCompletion("@ecx", 4);
        completion.IsCompletionOpen.Should().BeFalse();

        completion.UpdateCompletion("@ec", 3);

        completion.IsCompletionOpen.Should().BeTrue();
    }

    [Fact]
    public void Disabled_NeverOpens()
    {
        var completion = Loaded();
        completion.IsEnabled = false;

        completion.UpdateCompletion("@", 1);

        completion.IsCompletionOpen.Should().BeFalse();
    }

    [Fact]
    public void Reset_ForgetsTheDirectories()
    {
        var completion = Loaded();
        completion.UpdateCompletion("@", 1);

        completion.Reset();

        completion.HasDirectories.Should().BeFalse();
        completion.IsCompletionOpen.Should().BeFalse();
        completion.Suggestions.Should().BeEmpty();
    }
}
