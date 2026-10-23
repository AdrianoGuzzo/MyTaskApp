using System.Reflection;
using System.Text.RegularExpressions;

namespace MyTaskApp.Remote.Contracts.Tests;

/// <summary>
/// O que viaja na orquestração nunca leva segredo, caminho, host ou texto de
/// comando (RS-28 do modelo de ameaças). Como o <c>ContractSafetyTests</c> do
/// MCP: um campo novo com um desses nomes quebra o teste antes de ir para a rede.
/// </summary>
public sealed class ContractSafetyTests
{
    /// <summary>Palavras inteiras do PascalCase: "Description" não é "Script".</summary>
    private static readonly string[] Forbidden =
    [
        "Password", "Secret", "Token", "ConnectionString", "Credential", "Host", "User", "Username",
        "Path", "Directory", "Folder", "CommandLine", "CommandText", "Arguments", "Script",
    ];

    /// <summary>Ids de coisa que mora na máquina executora; o nome é do id, não do valor.</summary>
    private static readonly HashSet<string> Allowed = ["TagDirectoryId"];

    private static bool IsForbidden(string name) =>
        Forbidden.Any(word => Regex.IsMatch(name, $"(^|(?<=[a-z0-9])){word}(?=[A-Z]|$)"));

    [Fact]
    public void NoContract_CarriesASecret_APath_OrCommandText()
    {
        var offenders = typeof(RemoteJson).Assembly.GetExportedTypes()
            .Where(type => type.IsClass && type.Namespace == typeof(RemoteJson).Namespace)
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => !Allowed.Contains(property.Name) && IsForbidden(property.Name))
                .Select(property => $"{type.Name}.{property.Name}"));

        offenders.Should().BeEmpty();
    }

    /// <summary>Um comando chega por id, e nunca como texto para um shell (ADR-088).</summary>
    [Fact]
    public void TheCommandRequest_HasNoFreeText_BesidesItsParameterValues()
    {
        typeof(WorktreeExecutionRequest).GetProperties().Where(property => property.PropertyType == typeof(string))
            .Should().BeEmpty();
    }

    /// <summary>A regra realmente pega os nomes de sempre, e não passa por acaso.</summary>
    [Theory]
    [InlineData("WorkingDirectory")]
    [InlineData("ConnectionString")]
    [InlineData("AccessToken")]
    [InlineData("CommandLine")]
    [InlineData("Password")]
    [InlineData("PostCopyScript")]
    [InlineData("HostName")]
    public void TheRule_CatchesTheUsualNames(string name)
    {
        IsForbidden(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("Description")]
    [InlineData("Hosting")]
    [InlineData("TokenizerKind")]
    [InlineData("Paths2")]
    public void TheRule_LeavesWordsThatOnlyContainThem(string name)
    {
        IsForbidden(name).Should().BeFalse();
    }
}
