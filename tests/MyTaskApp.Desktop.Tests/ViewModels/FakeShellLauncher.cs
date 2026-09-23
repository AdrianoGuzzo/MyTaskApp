using MyTaskApp.Desktop.Composition;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>Registra o que se pediu para abrir, sem abrir nada no sistema.</summary>
internal sealed class FakeShellLauncher : IShellLauncher
{
    public bool Succeeds { get; set; } = true;

    public List<string> OpenedFolders { get; } = [];

    public List<string> OpenedTerminals { get; } = [];

    public List<Uri> OpenedUris { get; } = [];

    public Task<bool> OpenFolderAsync(string path)
    {
        OpenedFolders.Add(path);
        return Task.FromResult(Succeeds);
    }

    public Task<bool> OpenUriAsync(Uri uri)
    {
        OpenedUris.Add(uri);
        return Task.FromResult(Succeeds);
    }

    public bool OpenTerminal(string path)
    {
        OpenedTerminals.Add(path);
        return Succeeds;
    }
}
