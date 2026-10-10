using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using MyTaskApp.Mcp.Hosting;

namespace MyTaskApp.Mcp.Tests;

/// <summary>
/// O que pode sair pelo MCP, provado por reflexão (ADR-059): nenhuma resposta
/// de ferramenta — nem nada alcançável dentro dela — tem lugar para senha,
/// segredo ou token. Uma propriedade nova com esse nome quebra aqui antes de
/// chegar a um cliente.
/// </summary>
public partial class ContractSafetyTests
{
    [Fact]
    public void NoToolResult_HasAPlaceForASecret()
    {
        var offending = new List<string>();
        var seen = new HashSet<Type>();

        foreach (var method in ToolMethods())
        {
            Walk(method.ReturnType, $"{method.Name} →", seen, offending);
        }

        offending.Should().BeEmpty();
    }

    [Fact]
    public void NoToolParameter_AcceptsAPassword()
    {
        ToolMethods()
            .SelectMany(method => method.GetParameters()
                .Where(parameter => parameter.ParameterType != typeof(CancellationToken))
                .Select(parameter => $"{method.Name}({parameter.Name})"))
            .Where(name => Secret().IsMatch(name))
            .Should().BeEmpty();
    }

    [Fact]
    public void EveryTool_IsDescribed_AndAnnotated()
    {
        McpCatalog.Tools.Should().OnlyContain(tool => tool.Description.Length > 20);
        McpCatalog.Tools.Select(tool => tool.Name).Should().OnlyHaveUniqueItems();
        McpCatalog.Tools.Should().OnlyContain(tool => NameRule().IsMatch(tool.Name));

        // As que excluem ou trocam dado de verdade dizem isso ao cliente.
        McpCatalog.Tools.Where(tool => tool.Name is "task_delete" or "time_entry_delete" or "database_profile_delete")
            .Should().OnlyContain(tool => tool.Destructive && !tool.ReadOnly);
    }

    [Fact]
    public void NothingRunsCommandsOrSql()
    {
        McpCatalog.Tools.Select(tool => tool.Name).Should().NotContain(name =>
            name.Contains("run", StringComparison.Ordinal)
            || name.Contains("exec", StringComparison.Ordinal)
            || name.Contains("sql", StringComparison.Ordinal)
            || name.Contains("copy", StringComparison.Ordinal)
            || name.Contains("restore_database", StringComparison.Ordinal)
            || name.Contains("purge", StringComparison.Ordinal));
    }

    private static IEnumerable<MethodInfo> ToolMethods() =>
        McpCatalog.ToolTypes.SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null);

    private static void Walk(Type type, string path, HashSet<Type> seen, List<string> offending)
    {
        foreach (var inner in Unwrap(type))
        {
            if (!seen.Add(inner) || inner.IsPrimitive || inner.IsEnum || inner == typeof(string) || inner.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
            {
                continue;
            }

            foreach (var property in inner.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (Secret().IsMatch(property.Name) && property.Name is not "HasPassword")
                {
                    offending.Add($"{path} {inner.Name}.{property.Name}");
                }

                Walk(property.PropertyType, $"{path} {inner.Name}.{property.Name} →", seen, offending);
            }
        }
    }

    /// <summary>Task&lt;T&gt;, listas, dicionários e anuláveis viram os tipos de dentro.</summary>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Unwrap(underlying);
        }

        if (type.IsArray)
        {
            return Unwrap(type.GetElementType()!);
        }

        if (type.IsGenericType && type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
        {
            return type.GetGenericArguments().SelectMany(Unwrap);
        }

        return [type];
    }

    [GeneratedRegex("password|secret|token|senha", RegexOptions.IgnoreCase)]
    private static partial Regex Secret();

    [GeneratedRegex("^[a-z]+(_[a-z]+)*$")]
    private static partial Regex NameRule();
}
