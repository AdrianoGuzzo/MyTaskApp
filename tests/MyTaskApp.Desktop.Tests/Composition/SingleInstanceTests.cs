using MyTaskApp.Desktop.Composition;

namespace MyTaskApp.Desktop.Tests.Composition;

/// <summary>
/// ADR-019. O que importa aqui é o portão: dois processos no mesmo SQLite
/// significam aviso dobrado, <c>SQLITE_BUSY</c> e dois <c>widget.json</c>
/// disputando a mesma posição de janela.
/// </summary>
public class SingleInstanceTests
{
    [Fact]
    public void TheFirstLaunchOwnsTheApplication()
    {
        using var first = SingleInstance.Acquire();

        first.IsOwner.Should().BeTrue();
    }

    [Fact]
    public void ASecondLaunchDoesNotOwnIt()
    {
        using var first = SingleInstance.Acquire();
        using var second = SingleInstance.Acquire();

        first.IsOwner.Should().BeTrue();
        second.IsOwner.Should().BeFalse();
    }

    [Fact]
    public void ClosingTheAppLetsTheNextLaunchIn()
    {
        // Sem isto, sair e reabrir deixaria o app trancado para fora de si
        // mesmo até o usuário reiniciar a máquina.
        using (var first = SingleInstance.Acquire())
        {
            first.IsOwner.Should().BeTrue();
        }

        using var next = SingleInstance.Acquire();

        next.IsOwner.Should().BeTrue();
    }

    [Fact]
    public void SignallingFromASecondLaunchNeverThrows()
    {
        // O segundo processo vai encerrar de qualquer jeito: uma falha ao
        // avisar não pode virar diálogo de erro na cara do usuário.
        using var first = SingleInstance.Acquire();
        using var second = SingleInstance.Acquire();

        var signal = second.SignalOwner;

        signal.Should().NotThrow();
    }

    [Fact]
    public void TheOwnerIsToldWhenSomeoneClicksTheShortcutAgain()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Evento nomeado é API só do Windows; fora dele o segundo processo
            // apenas encerra, e não há nada a observar.
            return;
        }

        using var owner = SingleInstance.Acquire();
        using var revealed = new ManualResetEventSlim(false);

        owner.WhenActivated(revealed.Set);

        using (var second = SingleInstance.Acquire())
        {
            second.SignalOwner();
        }

        revealed.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
            .Should().BeTrue();
    }

    [Fact]
    public void TheMutexNameIsTheOneTheInstallerWatches()
    {
        // Literal de propósito: o teste de packaging compara o .iss com esta
        // mesma constante, então mudar o nome quebra os dois lados de uma vez.
        SingleInstance.MutexName.Should().Be("MyTaskApp.SingleInstance");
    }
}
