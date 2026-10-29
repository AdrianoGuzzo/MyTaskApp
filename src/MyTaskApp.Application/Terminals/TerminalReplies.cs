namespace MyTaskApp.Application.Terminals;

/// <summary>
/// Separa, no que uma janela de terminal manda, o que é o terminal falando
/// sozinho (respostas a consultas, foco, mouse) do que é alguém digitando
/// (ADR-098). Só digitação tira a vez de quem controla de longe: passar o
/// mouse sobre a janela, ou ela responder "sou um xterm", não.
/// </summary>
public static class TerminalReplies
{
    /// <summary><c>true</c> quando não há uma tecla sequer em <paramref name="data"/>.</summary>
    public static bool IsPassive(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return true;
        }

        var index = 0;

        while (index < data.Length)
        {
            var length = PassiveLength(data[index..]);

            if (length == 0)
            {
                return false;
            }

            index += length;
        }

        return true;
    }

    /// <summary>O tamanho da sequência passiva que começa aqui; 0 = é teclado.</summary>
    private static int PassiveLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3 || data[0] != 0x1B)
        {
            return 0;
        }

        return data[1] switch
        {
            (byte)'[' => CsiLength(data),
            (byte)']' or (byte)'P' => StringLength(data),
            _ => 0,
        };
    }

    /// <summary>
    /// <c>CSI ? … c</c> e <c>CSI &gt; … c</c> (quem é o terminal),
    /// <c>CSI n ; m R</c> (posição do cursor), <c>CSI 0 n</c>, <c>CSI ? … u</c>
    /// (teclado), <c>CSI ? … $ y</c> (modos), <c>CSI I</c> e <c>CSI O</c> (foco)
    /// e <c>CSI &lt; … M</c>/<c>m</c> (mouse).
    /// </summary>
    private static int CsiLength(ReadOnlySpan<byte> data)
    {
        var index = 2;
        var marker = data[index] is (byte)'?' or (byte)'>' or (byte)'<' ? data[index++] : (byte)0;
        var start = index;

        while (index < data.Length && (char.IsAsciiDigit((char)data[index]) || data[index] is (byte)';' or (byte)':' or (byte)'$'))
        {
            index++;
        }

        if (index >= data.Length)
        {
            return 0;
        }

        var final = data[index];
        var hasParameters = index > start;
        var passive = (marker, final) switch
        {
            ((byte)'?' or (byte)'>', (byte)'c') => true,
            ((byte)'?', (byte)'u' or (byte)'y') => true,
            ((byte)'<', (byte)'M' or (byte)'m') => hasParameters,
            (0, (byte)'R') => hasParameters,
            (0, (byte)'n') => hasParameters,
            (0, (byte)'I' or (byte)'O') => !hasParameters,
            _ => false,
        };

        return passive ? index + 1 : 0;
    }

    /// <summary>OSC (cores) e DCS (versão do terminal), até o BEL ou o ST.</summary>
    private static int StringLength(ReadOnlySpan<byte> data)
    {
        for (var index = 2; index < data.Length; index++)
        {
            if (data[index] == 0x07)
            {
                return index + 1;
            }

            if (data[index] == 0x1B && index + 1 < data.Length && data[index + 1] == (byte)'\\')
            {
                return index + 2;
            }
        }

        return 0;
    }
}
