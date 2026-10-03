using MyTaskApp.Infrastructure.Jira;

namespace MyTaskApp.Infrastructure.Tests.Jira;

public class JiraQueryTests
{
    [Fact]
    public void ATitleFragment_SearchesTheSummary_NewestFirst()
    {
        JiraQuery.ForText("corrigir erro", null)
            .Should().Be("summary ~ \"corrigir erro*\" ORDER BY updated DESC");
    }

    [Fact]
    public void TheDefaultProject_NarrowsTheSearch()
    {
        JiraQuery.ForText("corrigir erro", "gaeco")
            .Should().Be("project = \"GAECO\" AND summary ~ \"corrigir erro*\" ORDER BY updated DESC");
    }

    [Theory]
    [InlineData("erro\" OR project = SECRET OR summary ~ \"x")]
    [InlineData("erro\\\" ORDER BY created")]
    [InlineData("erro) OR (assignee = currentUser()")]
    public void WhatTheUserTyped_NeverBecomesJql(string text)
    {
        var jql = JiraQuery.ForText(text, null)!;

        // Uma string só, de ponta a ponta: nada fecha a aspa e emenda outra cláusula.
        jql.Count(c => c == '"').Should().Be(2);
        jql.Should().StartWith("summary ~ \"").And.EndWith("*\" ORDER BY updated DESC");
    }

    [Fact]
    public void Accents_AreKept()
    {
        JiraQuery.ForText("sincronização", null).Should().Contain("sincronização*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("!!! ---")]
    public void NothingToSearch_IsNull(string text)
    {
        JiraQuery.ForText(text, null).Should().BeNull();
    }

    [Fact]
    public void AnInvalidDefaultProject_IsIgnored()
    {
        JiraQuery.ForText("erro", "GAECO\" OR x").Should().Be("summary ~ \"erro*\" ORDER BY updated DESC");
    }

    [Fact]
    public void ANumber_WithADefaultProject_IsAKey()
    {
        JiraQuery.KeyFromNumber(" 1234 ", "GAECO").Should().Be("GAECO-1234");
        JiraQuery.KeyFromNumber("1234", null).Should().BeNull();
        JiraQuery.KeyFromNumber("corrigir", "GAECO").Should().BeNull();
    }
}
