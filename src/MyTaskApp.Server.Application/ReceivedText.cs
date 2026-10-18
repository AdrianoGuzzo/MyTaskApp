namespace MyTaskApp.Server.Application;

/// <summary>
/// Os textos curtos que o cliente manda fora dos campos (tipo de entidade,
/// plataforma, versão): nenhum caractere de controle. O NUL, além de não ter
/// sentido ali, o PostgreSQL nem grava.
/// </summary>
internal static class ReceivedText
{
    public static bool HasControl(string? value) => value is not null && value.Any(char.IsControl);
}
