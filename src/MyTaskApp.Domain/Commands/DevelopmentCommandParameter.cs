namespace MyTaskApp.Domain.Commands;

/// <summary>
/// A definição de um <c>{nome}</c> de um comando global (ADR-051): como a tela
/// pergunta o valor antes de executar. Faz parte do agregado
/// <see cref="DevelopmentCommand"/>.
/// </summary>
public sealed class DevelopmentCommandParameter
{
    /// <summary>Separa as opções na coluna: uma opção nunca tem quebra de linha.</summary>
    private const char OptionSeparator = '\n';

    private DevelopmentCommandParameter(
        Guid id,
        Guid developmentCommandId,
        string name,
        string? label,
        CommandParameterType type,
        string? defaultValue,
        bool isRequired,
        string? options,
        int order)
    {
        Id = id;
        DevelopmentCommandId = developmentCommandId;
        Name = name;
        Label = label;
        Type = type;
        DefaultValue = defaultValue;
        IsRequired = isRequired;
        Options = options;
        Order = order;
    }

    public Guid Id { get; }

    public Guid DevelopmentCommandId { get; }

    /// <summary>O nome entre chaves, sem elas.</summary>
    public string Name { get; private set; }

    public string? Label { get; private set; }

    public CommandParameterType Type { get; private set; }

    public string? DefaultValue { get; private set; }

    public bool IsRequired { get; private set; }

    /// <summary>As opções do combo, uma por linha. Só num <see cref="CommandParameterType.Choice"/>.</summary>
    public string? Options { get; private set; }

    /// <summary>A posição no formulário, a partir de 0.</summary>
    public int Order { get; private set; }

    public CommandParameterSpec ToSpec() =>
        new(Name, Label, Type, DefaultValue, IsRequired, Options?.Split(OptionSeparator));

    /// <param name="spec">Já normalizada (<see cref="CommandParameterSpec.Normalized"/>).</param>
    internal static DevelopmentCommandParameter Create(
        Guid developmentCommandId,
        CommandParameterSpec spec,
        int order,
        DateTimeOffset at)
    {
        var parameter = new DevelopmentCommandParameter(
            Guid.CreateVersion7(at),
            developmentCommandId,
            spec.Name,
            null,
            spec.Type,
            null,
            spec.IsRequired,
            null,
            order);

        parameter.Place(spec, order);

        return parameter;
    }

    /// <param name="spec">Já normalizada (<see cref="CommandParameterSpec.Normalized"/>).</param>
    internal void Place(CommandParameterSpec spec, int order)
    {
        Name = spec.Name;
        Label = spec.Label;
        Type = spec.Type;
        DefaultValue = spec.DefaultValue;
        IsRequired = spec.IsRequired;
        Options = spec.Options is { Count: > 0 } options ? string.Join(OptionSeparator, options) : null;
        Order = order;
    }
}
