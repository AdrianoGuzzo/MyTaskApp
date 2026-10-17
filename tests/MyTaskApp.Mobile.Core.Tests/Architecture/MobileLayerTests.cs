using System.Reflection;
using MyTaskApp.Mobile.Core.Composition;

namespace MyTaskApp.Mobile.Core.Tests.Architecture;

/// <summary>
/// O núcleo do celular (ADR-067) fica sem MAUI, para rodar e ser testado no
/// CI do desktop, e as telas não falam com banco, rede nem contêiner — só com
/// os casos de uso, pelo <c>IUseCaseRunner</c>.
/// </summary>
public sealed class MobileLayerTests
{
    private static readonly Assembly Core = typeof(MobileServices).Assembly;

    [Theory]
    [InlineData("Microsoft.Maui")]
    [InlineData("Avalonia")]
    [InlineData("MyTaskApp.Mcp")]
    [InlineData("Npgsql")]
    public void TheCore_DoesNotReference(string forbidden)
    {
        Core.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Should().NotContain(name => name.StartsWith(forbidden, StringComparison.Ordinal));
    }

    [Fact]
    public void TheCore_DoesNotReferenceTheDesktopApp()
    {
        // O executável do desktop se chama "MyTaskApp".
        Core.GetReferencedAssemblies().Select(reference => reference.Name).Should().NotContain("MyTaskApp");
    }

    [Fact]
    public void TheGuard_SeesTheRealReferences()
    {
        Core.GetReferencedAssemblies().Select(reference => reference.Name)
            .Should().Contain(["MyTaskApp.Application", "CommunityToolkit.Mvvm"]);
    }

    [Theory]
    [InlineData("MyTaskApp.Mobile.Core.ViewModels")]
    [InlineData("MyTaskApp.Mobile.Core.Presentation")]
    public void TheScreens_HoldNoDatabaseNetworkOrContainer(string ns)
    {
        var forbidden = new[] { "IServiceProvider", "IServiceScopeFactory", "DbContext", "HttpClient", "Process" };

        var offenders = Core.GetTypes()
            .Where(type => type.Namespace == ns)
            .SelectMany(type => type
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Select(field => (type.Name, Field: field.FieldType)))
            .Where(pair => forbidden.Any(name => pair.Field.Name.Contains(name, StringComparison.Ordinal))
                || pair.Field.Assembly.GetName().Name == "MyTaskApp.Infrastructure")
            .Select(pair => $"{pair.Name}: {pair.Field.Name}")
            .ToList();

        offenders.Should().BeEmpty();
    }
}
