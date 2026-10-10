using System.Net;
using System.Net.Sockets;

namespace MyTaskApp.Mcp.Hosting;

/// <summary>
/// "Dá para escutar nesta porta?" — perguntado do único jeito confiável:
/// tentando, com uso exclusivo, e soltando em seguida.
/// </summary>
internal static class PortProbe
{
    public static PortCheck Check(int port)
    {
        if (port is < 1 or > 65535)
        {
            return new PortCheck(port, false, "Porta inválida.");
        }

        var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };

        try
        {
            listener.Start();
            return new PortCheck(port, true, $"A porta {port} está livre.");
        }
        catch (SocketException exception)
        {
            return new PortCheck(port, false, Describe(port, exception));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Por que a porta não serve, em português, com o que fazer.</summary>
    public static string Describe(int port, SocketException exception) => exception.SocketErrorCode switch
    {
        SocketError.AddressAlreadyInUse =>
            $"A porta {port} já está em uso por outro programa — ou por outra cópia do MyTaskApp. " +
            "Escolha outra porta, ou feche quem a usa.",
        SocketError.AccessDenied =>
            $"O sistema não deixou usar a porta {port} (reservada ou bloqueada). Escolha outra porta.",
        _ => $"Não foi possível escutar na porta {port}: {exception.SocketErrorCode}.",
    };
}
