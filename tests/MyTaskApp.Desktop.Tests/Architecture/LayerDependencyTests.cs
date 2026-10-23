using System.Diagnostics;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure;
using MyTaskApp.Mcp.Hosting;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Desktop.Tests.Architecture;

/// <summary>
/// As camadas valiam só por disciplina (ADR-005, ADR-009, ADR-012). Aqui elas
/// viram build quebrada: um atalho de uma camada para outra aparece no PR, e
/// não meses depois numa tela que não dá para testar.
/// </summary>
public class LayerDependencyTests
{
    private static readonly string[] Persistence = ["Microsoft.EntityFrameworkCore", "Npgsql"];
    private static readonly string[] Ui = ["Avalonia"];

    /// <summary>O celular (ADR-067) usa a Application; o contrário nunca.</summary>
    private const string Mobile = "MyTaskApp.Mobile";

    [Fact]
    public void TheDomain_DependsOnNothingFromTheApp_NorOnPersistenceOrUi()
    {
        References(typeof(TaskItem).Assembly).Should().NotContain(name =>
            name.StartsWith("MyTaskApp", StringComparison.Ordinal) || IsAnyOf(name, Persistence) || IsAnyOf(name, Ui));
    }

    /// <summary>O desktop e o celular são duas cascas sobre o mesmo núcleo: nenhum conhece o outro.</summary>
    [Fact]
    public void TheDesktop_DoesNotKnowTheMobileApp()
    {
        References(typeof(TodayViewModel).Assembly).Should().NotContain(name =>
            name.StartsWith(Mobile, StringComparison.Ordinal));
    }

    [Fact]
    public void TheSyncContracts_DependOnNothingFromTheApp()
    {
        References(typeof(SyncJson).Assembly).Should().NotContain(name =>
            name.StartsWith("MyTaskApp", StringComparison.Ordinal));
    }

    /// <summary>
    /// O contrato da orquestração remota (ADR-084) é lido pelo servidor, pelo
    /// agente, pelo desktop e pelo celular: como o do sync, não conhece nenhum.
    /// </summary>
    [Fact]
    public void TheRemoteContracts_DependOnNothingFromTheApp()
    {
        References(typeof(RemoteJson).Assembly).Should().NotContain(name =>
            name.StartsWith("MyTaskApp", StringComparison.Ordinal));
    }

    /// <summary>A Application define as portas; quem implementa é a Infrastructure (ADR-005).</summary>
    [Fact]
    public void TheApplication_DoesNotKnowTheInfrastructure_NorPersistenceOrUi()
    {
        References(typeof(IUseCaseRunner).Assembly).Should().NotContain(name =>
            name == typeof(DependencyInjection).Assembly.GetName().Name
            || name == typeof(McpServerManager).Assembly.GetName().Name
            || name == typeof(TodayViewModel).Assembly.GetName().Name
            || name.StartsWith(Mobile, StringComparison.Ordinal)
            || IsAnyOf(name, Persistence)
            || IsAnyOf(name, Ui));
    }

    /// <summary>O MCP chama os mesmos casos de uso da tela (ADR-059), nunca o banco.</summary>
    [Fact]
    public void TheMcpServer_DoesNotKnowTheInfrastructure_NorPersistenceOrUi()
    {
        References(typeof(McpServerManager).Assembly).Should().NotContain(name =>
            name == typeof(DependencyInjection).Assembly.GetName().Name
            || name.StartsWith(Mobile, StringComparison.Ordinal)
            || IsAnyOf(name, Persistence)
            || IsAnyOf(name, Ui));
    }

    /// <summary>
    /// ViewModel recebe o executor de casos de uso, e não o contêiner, o banco,
    /// um processo ou a rede (ADR-012).
    /// </summary>
    [Fact]
    public void NoViewModel_HoldsTheContainer_TheDatabase_AProcess_OrTheNetwork()
    {
        Type[] forbidden = [typeof(IServiceProvider), typeof(Microsoft.Extensions.DependencyInjection.IServiceScopeFactory), typeof(DbContext), typeof(Process), typeof(HttpClient)];

        var offenders = ViewModelTypes()
            .SelectMany(type => Dependencies(type).Select(dependency => (type, dependency)))
            .Where(pair => forbidden.Any(bad => bad.IsAssignableFrom(pair.dependency)))
            .Select(pair => $"{pair.type.Name} → {pair.dependency.Name}");

        offenders.Should().BeEmpty();
    }

    /// <summary>As implementações da Infrastructure são internas (ADR-005); nenhum ViewModel chega a elas.</summary>
    [Fact]
    public void NoViewModel_DependsOnAnInfrastructureType()
    {
        var infrastructure = typeof(DependencyInjection).Assembly;

        var offenders = ViewModelTypes()
            .SelectMany(type => Dependencies(type).Select(dependency => (type, dependency)))
            .Where(pair => pair.dependency.Assembly == infrastructure)
            .Select(pair => $"{pair.type.Name} → {pair.dependency.Name}");

        offenders.Should().BeEmpty();
    }

    /// <summary>
    /// As portas da UI (confirmar, pedir parâmetros) são declaradas junto dos
    /// ViewModels; as janelas que as implementam ficam em <c>Views</c>, e o
    /// ViewModel nunca enxerga uma janela.
    /// </summary>
    [Fact]
    public void NoViewModel_DependsOnAView()
    {
        var views = typeof(TodayViewModel).Namespace!.Replace("ViewModels", "Views", StringComparison.Ordinal);

        var offenders = ViewModelTypes()
            .SelectMany(type => Dependencies(type).Select(dependency => (type, dependency)))
            .Where(pair => pair.dependency.Namespace?.StartsWith(views, StringComparison.Ordinal) == true)
            .Select(pair => $"{pair.type.Name} → {pair.dependency.Name}");

        offenders.Should().BeEmpty();
    }

    /// <summary>Sem isto, uma reflexão que não acha nada passaria todos os testes acima.</summary>
    [Fact]
    public void TheGuard_SeesTheDependenciesThatDoExist()
    {
        References(typeof(IUseCaseRunner).Assembly).Should().Contain(typeof(TaskItem).Assembly.GetName().Name);
        ViewModelTypes().Should().Contain(typeof(TodayViewModel));
        Dependencies(typeof(TodayViewModel)).Should().Contain(typeof(IUseCaseRunner));
    }

    private static IEnumerable<Type> ViewModelTypes() =>
        typeof(TodayViewModel).Assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith(typeof(TodayViewModel).Namespace!, StringComparison.Ordinal) == true);

    /// <summary>Campos e parâmetros de construtor: é por onde uma dependência entra e fica.</summary>
    private static IEnumerable<Type> Dependencies(Type type)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        return type.GetFields(all).Select(field => field.FieldType)
            .Concat(type.GetConstructors(all).SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType))
            .SelectMany(Unwrap)
            .Distinct();
    }

    /// <summary><c>Func&lt;HttpClient&gt;</c> ou <c>Lazy&lt;DbContext&gt;</c> continuam sendo a mesma dependência.</summary>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;

        var element = type.IsArray ? type.GetElementType() : null;

        foreach (var inner in (element is null ? type.GenericTypeArguments : [element]).SelectMany(Unwrap))
        {
            yield return inner;
        }
    }

    private static string[] References(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Select(reference => reference.Name!)];

    private static bool IsAnyOf(string name, string[] prefixes) =>
        prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
}
