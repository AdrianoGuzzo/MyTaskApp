using System.Text.Json;
using MyTaskApp.Application.Remote;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>
/// O que "Permitir nesta conversa" cobre: no shell, o comando exato; o
/// composto nunca é lembrado; nas outras ferramentas, a ferramenta.
/// </summary>
public sealed class HeadlessToolScopeTests
{
    [Theory]
    [InlineData("Bash", "dotnet test", "Bash: dotnet test")]
    [InlineData("Bash", "  dotnet   test\t--no-build ", "Bash: dotnet test --no-build")]
    [InlineData("PowerShell", "Get-ChildItem src", "PowerShell: Get-ChildItem src")]
    [InlineData("Bash", "dotnet test && rm -rf .", null)]
    [InlineData("Bash", "ls; rm -rf .", null)]
    [InlineData("Bash", "cat a | sh", null)]
    [InlineData("Bash", "echo $(whoami)", null)]
    [InlineData("Bash", "echo `whoami`", null)]
    [InlineData("Bash", "echo oi > arquivo", null)]
    [InlineData("Bash", "sleep 9 &", null)]
    [InlineData("Bash", "git commit -m \"a\nb\"", null)]
    [InlineData("Bash", "   ", null)]
    public void AShellCommand_IsRememberedExactly_AndACompoundOneNever(string tool, string command, string? expected) =>
        HeadlessToolScope.RememberKey(tool, JsonSerializer.SerializeToElement(new { command })).Should().Be(expected);

    [Fact]
    public void OtherTools_AreRememberedByName_AndAShellWithoutCommandIsNot()
    {
        HeadlessToolScope.RememberKey("Edit", JsonSerializer.SerializeToElement(new { file_path = "src/a.cs" })).Should().Be("Edit");
        HeadlessToolScope.RememberKey("WebFetch", JsonSerializer.SerializeToElement(new { url = "https://example.com" })).Should().Be("WebFetch");
        HeadlessToolScope.RememberKey("Bash", JsonSerializer.SerializeToElement(new { })).Should().BeNull();
        HeadlessToolScope.RememberKey(string.Empty, JsonSerializer.SerializeToElement(new { })).Should().BeNull();
    }

    [Fact]
    public void TheButton_ShowsOnlyWhenTheAgentOffersIt_AndNeverForATypedConfirmation()
    {
        var offered = Request(ApprovalKind.ToolUse, new { canRemember = true, rememberAs = "Bash: dotnet test" });

        RemoteApprovalTexts.CanRemember(offered).Should().BeTrue();
        RemoteApprovalTexts.RememberLabel(offered).Should().Be("Até o fim da conversa, sem perguntar de novo: Bash: dotnet test");

        RemoteApprovalTexts.CanRemember(Request(ApprovalKind.ToolUse, new { tool = "Bash" })).Should().BeFalse("o agente antigo não oferece");
        RemoteApprovalTexts.CanRemember(Request(ApprovalKind.ToolUse, new { canRemember = false })).Should().BeFalse();
        RemoteApprovalTexts.CanRemember(Request(ApprovalKind.CommandConfirmation, new { canRemember = true })).Should().BeFalse();
        RemoteApprovalTexts.CanRemember(Request(ApprovalKind.ToolUse, new { canRemember = true }) with { TypedConfirmationHint = "prod" }).Should().BeFalse();
        RemoteApprovalTexts.RememberLabel(Request(ApprovalKind.ToolUse, new { canRemember = true })).Should().Be("Até o fim da conversa, sem perguntar de novo.");
        RemoteApprovalTexts.RememberLabel(Request(ApprovalKind.ToolUse, new { })).Should().BeNull();
    }

    private static ApprovalRequest Request(ApprovalKind kind, object details) => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        kind,
        "O Claude quer usar Bash",
        JsonSerializer.SerializeToElement(details),
        null,
        DateTimeOffset.UtcNow.AddMinutes(5),
        string.Empty);
}
