using MyTaskApp.Application.Commands;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.QuickCommands;

/// <summary>
/// O banco do ambiente num comando rápido (ADR-060): direto no texto, no valor
/// digitado de um parâmetro, ou como valor padrão dele.
/// </summary>
public class QuickCommandLineDatabaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    private static CommandContext Context(bool withDatabase)
    {
        var task = TaskItem.Create("Implementar validação", Now);
        var development = task.BeginDevelopment(null, @"C:\Projects\eco", "origin/develop", "feature/x", @"C:\Projects\eco-feature-x", Now);
        task.MarkDevelopmentReady(development.Id, Now);

        if (withDatabase)
        {
            task.SetDevelopmentDatabase(development.Id, TaskDevelopmentDatabase.Create(
                Guid.CreateVersion7(), "Local", "localhost", 5432, "dev", "eco_core_20261013_101500", null, Now));
        }

        return CommandContext.For(task, development, "ECO", windows: true);
    }

    [Fact]
    public void ADefaultValueOfDatabase_GoesWithTheCopiedName()
    {
        var parameters = new[] { new CommandParameterSpec("nomebanco", DefaultValue: "{database}") };

        var line = QuickCommandLine.Build("configura-ambiente {nomebanco}", parameters, null, Context(withDatabase: true));

        line.Line.Should().Be("configura-ambiente eco_core_20261013_101500");
    }

    [Fact]
    public void ATypedValueOfDatabase_GoesWithTheCopiedName()
    {
        var parameters = new[] { CommandParameterSpec.Plain("nomebanco") };

        var line = QuickCommandLine.Build(
            "configura-ambiente {nomebanco}",
            parameters,
            new Dictionary<string, string> { ["nomebanco"] = "{database}_teste" },
            Context(withDatabase: true));

        line.Line.Should().Be("configura-ambiente eco_core_20261013_101500_teste");
    }

    [Fact]
    public void WithoutACopiedDatabase_TheLineSaysWhy()
    {
        var line = QuickCommandLine.Build("psql -h {database.host} {database}", [], null, Context(withDatabase: false));

        line.IsReady.Should().BeFalse();
        line.Error.Should().StartWith("A variável {database.host} não tem valor: este ambiente não tem banco copiado");
    }
}
