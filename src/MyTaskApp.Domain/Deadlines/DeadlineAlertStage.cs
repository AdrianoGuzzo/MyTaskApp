namespace MyTaskApp.Domain.Deadlines;

/// <summary>
/// Os degraus do alerta de prazo. <c>[Flags]</c> porque o mesmo tipo responde
/// duas perguntas: "quais degraus estão ligados" (um conjunto) e "qual foi o
/// último avisado" (um valor só). O valor maior é sempre o mais grave, então
/// "chegou num degrau novo" é uma comparação de inteiros (ADR-050).
/// </summary>
/// <remarks>
/// Os números vão para o banco. Reordenar ou renumerar muda o significado do
/// que já está gravado.
/// </remarks>
[Flags]
public enum DeadlineAlertStage
{
    None = 0,
    SevenDays = 1,
    ThreeDays = 2,
    OneDay = 4,
    EightHours = 8,
    TwoHours = 16,
    Overdue = 32,
    All = SevenDays | ThreeDays | OneDay | EightHours | TwoHours | Overdue,
}
