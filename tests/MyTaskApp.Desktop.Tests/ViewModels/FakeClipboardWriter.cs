using MyTaskApp.Desktop.Composition;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A área de transferência sem o sistema operacional. Guarda o que foi escrito
/// e sabe recusar: no Windows a área de transferência falha de verdade quando
/// outro processo a está segurando, e a tela precisa ter o que dizer quando
/// isso acontece.
/// </summary>
internal sealed class FakeClipboardWriter : IClipboardWriter
{
    /// <summary>Liga a recusa — o caminho que o stub headless não consegue simular.</summary>
    public bool Refuses { get; set; }

    public List<string> Written { get; } = [];

    public string? LastWritten => Written.Count == 0 ? null : Written[^1];

    public Task WriteAsync(string text)
    {
        if (Refuses)
        {
            throw new InvalidOperationException("CLIPBRD_E_CANT_OPEN");
        }

        Written.Add(text);
        return Task.CompletedTask;
    }
}
