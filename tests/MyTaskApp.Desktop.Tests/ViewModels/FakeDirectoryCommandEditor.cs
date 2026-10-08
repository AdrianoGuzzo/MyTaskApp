using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// Responde o diálogo do comando só do diretório sem janela (ADR-055): grava o
/// que o "usuário" preencheu, ou cancela. Guarda o que foi pedido.
/// </summary>
internal sealed class FakeDirectoryCommandEditor : IDirectoryCommandEditor
{
    /// <summary>O que o usuário preenche, a partir do pedido; <c>null</c> = cancelou.</summary>
    public Func<DirectoryCommandEditorRequest, DirectoryCommandDraft>? Answer { get; set; }

    public List<DirectoryCommandEditorRequest> Asked { get; } = [];

    /// <summary>A recusa que o caso de uso devolveu ao diálogo, que ficaria aberto mostrando-a.</summary>
    public string? LastError { get; private set; }

    public async Task<bool> EditAsync(DirectoryCommandEditorRequest request)
    {
        Asked.Add(request);

        if (Answer is null)
        {
            return false;
        }

        LastError = await request.SaveAsync(Answer(request), CancellationToken.None);

        return LastError is null;
    }
}
