namespace MyTaskApp.Domain.Commands;

/// <summary>O tipo de um parâmetro <c>{nome}</c> (ADR-051). Os valores vão para o banco.</summary>
public enum CommandParameterType
{
    Text = 0,

    /// <summary>Número com ponto decimal, na cultura invariante: vai para o shell como foi digitado.</summary>
    Number = 1,

    /// <summary>Uma das opções cadastradas, escolhida num combo.</summary>
    Choice = 2,
}
