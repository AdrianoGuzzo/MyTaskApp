using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.Tests;

/// <summary>
/// O executor de todo host (ADR-012): um escopo por operação, descartado no fim,
/// e registrado pelo <c>AddApplication</c> sem tomar o lugar de quem chegou antes.
/// </summary>
public class ScopedUseCaseRunnerTests
{
    [Fact]
    public async Task EachOperation_GetsItsOwnScope_DisposedAtTheEnd()
    {
        await using var provider = new ServiceCollection()
            .AddScoped<ScopedProbe>()
            .AddSingleton<IUseCaseRunner, ScopedUseCaseRunner>()
            .BuildServiceProvider(validateScopes: true);
        var runner = provider.GetRequiredService<IUseCaseRunner>();

        var first = await runner.RunAsync<ScopedProbe, ScopedProbe>((probe, _) => Task.FromResult(probe), TestContext.Current.CancellationToken);
        ScopedProbe? second = null;
        await runner.RunAsync<ScopedProbe>((probe, _) =>
        {
            second = probe;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        second.Should().NotBeNull().And.NotBeSameAs(first);
        first.IsDisposed.Should().BeTrue();
        second!.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task TheOperation_ReceivesTheCallersToken()
    {
        await using var provider = new ServiceCollection()
            .AddScoped<ScopedProbe>()
            .AddSingleton<IUseCaseRunner, ScopedUseCaseRunner>()
            .BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();

        var received = await provider.GetRequiredService<IUseCaseRunner>()
            .RunAsync<ScopedProbe, CancellationToken>((_, token) => Task.FromResult(token), cancellation.Token);

        received.Should().Be(cancellation.Token);
    }

    [Fact]
    public void AddApplication_RegistersTheScopedRunner()
    {
        using var provider = new ServiceCollection().AddApplication().BuildServiceProvider();

        provider.GetRequiredService<IUseCaseRunner>().Should().BeOfType<ScopedUseCaseRunner>();
    }

    /// <summary>Os testes de ViewModel registram um executor falso antes, e ele ganha.</summary>
    [Fact]
    public void AddApplication_KeepsARunnerRegisteredBefore()
    {
        var fake = new FakeRunner();
        using var provider = new ServiceCollection()
            .AddSingleton<IUseCaseRunner>(fake)
            .AddApplication()
            .BuildServiceProvider();

        provider.GetRequiredService<IUseCaseRunner>().Should().BeSameAs(fake);
    }

    private sealed class ScopedProbe : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeRunner : IUseCaseRunner
    {
        public Task<TResult> RunAsync<THandler, TResult>(Func<THandler, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
            where THandler : notnull => throw new NotSupportedException();

        public Task RunAsync<THandler>(Func<THandler, CancellationToken, Task> operation, CancellationToken cancellationToken = default)
            where THandler : notnull => throw new NotSupportedException();
    }
}
