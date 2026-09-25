using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Domain.Tests.Commands;

/// <summary>Os <c>{nome}</c> de um comando global (ADR-028).</summary>
public class CommandParametersTests
{
    [Fact]
    public void Names_AreInOrder_WithoutRepeating()
    {
        CommandParameters.Names("eco-sync {nomebanco} -Dev --to {destino} --log {NomeBanco}.log")
            .Should().Equal("nomebanco", "destino");
    }

    [Theory]
    [InlineData("dotnet build")]
    [InlineData("Get-ChildItem | % { $_.Name }")]
    [InlineData("echo ${env:PATH}")]
    [InlineData("git reset HEAD@{1}")]
    [InlineData("echo {}")]
    [InlineData("echo {nome banco}")]
    public void WhatBelongsToTheShell_IsNotAParameter(string command)
    {
        CommandParameters.Names(command).Should().BeEmpty();
    }

    [Fact]
    public void Fill_ReplacesEveryOccurrence_AndLeavesTheUnknownOnes()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["nomebanco"] = "MeuBanco" };

        CommandParameters.Fill("eco-sync {nomebanco} -Dev {NOMEBANCO} {outro}", values)
            .Should().Be("eco-sync MeuBanco -Dev MeuBanco {outro}");
    }
}
