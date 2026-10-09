namespace MyTaskApp.Infrastructure.Secrets;

/// <summary>
/// O nome de um segredo: só letras, dígitos e hífen. Vira nome de arquivo no
/// Windows e atributo do chaveiro no Linux — nos dois, nada que precise escapar.
/// </summary>
internal static class SecretName
{
    public static string Validate(string name)
    {
        if (name.Length == 0 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            throw new ArgumentException("Nome de segredo inválido.", nameof(name));
        }

        return name;
    }
}
