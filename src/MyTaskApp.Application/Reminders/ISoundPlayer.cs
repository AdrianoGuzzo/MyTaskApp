namespace MyTaskApp.Application.Reminders;

/// <summary>
/// O canal de som da escada. Dispara e esquece, e <b>nunca lança</b>: falhar em
/// tocar um som não pode derrubar o aviso que realmente importa.
/// </summary>
public interface ISoundPlayer
{
    void PlayAlert();
}
