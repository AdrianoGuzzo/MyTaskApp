namespace MyTaskApp.Domain.DatabaseOperations;

/// <summary>
/// Uma senha em trânsito (ADR-056). Existe porque <c>record</c> imprime todos
/// os membros: um <c>CreateDatabaseConnection(..., string Password)</c> poria a
/// senha em qualquer log que registrasse o comando. Aqui o texto só sai por
/// <see cref="Reveal"/>, chamado por quem a entrega ao cofre ou ao servidor.
/// </summary>
public sealed class SecretText
{
    private readonly string _value;

    public SecretText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new DomainException("Informe a senha.");
        }

        _value = value;
    }

    /// <summary><c>null</c> quando nada foi digitado — "manter a senha guardada".</summary>
    public static SecretText? FromOptional(string? value) => string.IsNullOrEmpty(value) ? null : new SecretText(value);

    public string Reveal() => _value;

    public override string ToString() => SensitiveText.Redacted;
}
