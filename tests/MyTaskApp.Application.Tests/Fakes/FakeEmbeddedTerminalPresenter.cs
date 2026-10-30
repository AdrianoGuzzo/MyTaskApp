using MyTaskApp.Application.Terminals;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>A vista dentro do app, de mentira: grava o que pediram para mostrar.</summary>
internal sealed class FakeEmbeddedTerminalPresenter : IEmbeddedTerminalPresenter
{
    public List<(Guid SessionId, string Title)> Shown { get; } = [];

    /// <summary>Desligado, é o processo sem tela.</summary>
    public bool Available { get; set; } = true;

    public bool Show(Guid sessionId, string title)
    {
        if (Available)
        {
            Shown.Add((sessionId, title));
        }

        return Available;
    }
}
