using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O diálogo de período sem janela (ADR-052). Com <see cref="Answer"/>, faz o que
/// o usuário faria: muda os campos e aperta o botão — que grava pelo mesmo
/// <see cref="TimeEntryEditorRequest.SaveAsync"/> do diálogo de verdade. Sem
/// resposta, cancela.
/// </summary>
internal sealed class FakeTimeEntryEditor : ITimeEntryEditor
{
    /// <summary>O período que o usuário confirma; <c>null</c> = cancelou.</summary>
    public Func<TimeEntryDraft, TimeEntryDraft>? Answer { get; set; }

    public List<TimeEntryEditorRequest> Asked { get; } = [];

    /// <summary>A recusa que o caso de uso devolveu ao diálogo, que ficaria aberto mostrando-a.</summary>
    public string? LastError { get; private set; }

    public async Task<bool> EditAsync(TimeEntryEditorRequest request)
    {
        Asked.Add(request);

        if (Answer is null)
        {
            return false;
        }

        LastError = await request.SaveAsync(Answer(request.Initial), CancellationToken.None);

        return LastError is null;
    }
}
