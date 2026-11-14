using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyTaskApp.Domain;

namespace MyTaskApp.Application.Terminals;

/// <summary>Os tipos de quadro do canal local do terminal (ADR-098).</summary>
public enum TerminalFrameType : byte
{
    /// <summary>Agente → cliente: saída (<c>offset</c> de 8 bytes, 1 byte de marcas, bytes).</summary>
    Output = 1,

    /// <summary>Agente → cliente: o <see cref="TerminalSessionStatus"/> em JSON.</summary>
    State = 2,

    /// <summary>Agente → cliente: um aviso para quem olha, em texto.</summary>
    Notice = 3,

    /// <summary>Agente → cliente: a ação foi recusada, com o motivo que a pessoa lê.</summary>
    Error = 4,

    /// <summary>Agente → cliente: a resposta de um pedido (abrir a sessão).</summary>
    Result = 5,

    /// <summary>Cliente → agente: o primeiro quadro da conexão, em JSON.</summary>
    Hello = 16,

    /// <summary>Cliente → agente: teclas cruas.</summary>
    Keys = 17,

    /// <summary>Cliente → agente: uma ação estruturada, em JSON.</summary>
    Action = 18,
}

/// <summary>As marcas de um quadro de saída.</summary>
[Flags]
public enum TerminalOutputFlags : byte
{
    None = 0,

    /// <summary>Começa o retrato: o cliente limpa o terminal antes.</summary>
    Reset = 1,

    /// <summary>Continua o retrato do quadro anterior.</summary>
    Continuation = 2,

    /// <summary>O último pedaço do retrato: o cliente já pode desenhar.</summary>
    Final = 4,
}

public sealed record TerminalFrame(TerminalFrameType Type, byte[] Payload);

/// <summary>
/// Quadros sobre um fluxo (o named pipe, o Unix socket): 1 byte de tipo, 4 de
/// tamanho (little-endian) e o conteúdo. Nada passa de
/// <see cref="MaxPayload"/>: quadro maior fecha a conexão, em vez de alocar o
/// que o outro lado pedir.
/// </summary>
public sealed class TerminalFrameStream(Stream stream) : IAsyncDisposable
{
    public const int MaxPayload = 256 * 1024;

    /// <summary>O retrato vai em pedaços deste tamanho.</summary>
    public const int SnapshotChunk = 64 * 1024;

    private readonly SemaphoreSlim _writing = new(1, 1);

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>O próximo quadro; <c>null</c> quando o outro lado fechou.</summary>
    public async Task<TerminalFrame?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var header = new byte[5];

        if (!await FillAsync(header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));

        if (length is < 0 or > MaxPayload)
        {
            throw new InvalidDataException("Quadro de terminal grande demais.");
        }

        var payload = new byte[length];

        if (!await FillAsync(payload, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("O quadro de terminal veio pela metade.");
        }

        return new TerminalFrame((TerminalFrameType)header[0], payload);
    }

    public async Task WriteAsync(TerminalFrameType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length > MaxPayload)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Quadro de terminal grande demais.");
        }

        var frame = new byte[5 + payload.Length];
        frame[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), payload.Length);
        payload.Span.CopyTo(frame.AsSpan(5));

        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
    }

    public Task WriteJsonAsync<T>(TerminalFrameType type, T value, CancellationToken cancellationToken = default) =>
        WriteAsync(type, JsonSerializer.SerializeToUtf8Bytes(value, Json), cancellationToken);

    public Task WriteTextAsync(TerminalFrameType type, string text, CancellationToken cancellationToken = default) =>
        WriteAsync(type, Encoding.UTF8.GetBytes(text), cancellationToken);

    /// <summary>Uma saída; o retrato vai em pedaços, o primeiro com <see cref="TerminalOutputFlags.Reset"/>.</summary>
    public async Task WriteOutputAsync(TerminalOutput output, CancellationToken cancellationToken = default)
    {
        var chunk = output.Reset ? SnapshotChunk : MaxPayload - 9;
        var first = true;

        for (var start = 0; first || start < output.Data.Length; start += chunk)
        {
            var length = Math.Min(chunk, output.Data.Length - start);
            var flags = !output.Reset ? TerminalOutputFlags.None
                : first ? TerminalOutputFlags.Reset
                : TerminalOutputFlags.Continuation;

            if (output.Reset && start + length >= output.Data.Length)
            {
                flags |= TerminalOutputFlags.Final;
            }
            var payload = new byte[9 + length];
            BinaryPrimitives.WriteInt64LittleEndian(payload, output.Offset + (output.Reset ? 0 : start));
            payload[8] = (byte)flags;
            output.Data.AsSpan(start, length).CopyTo(payload.AsSpan(9));
            await WriteAsync(TerminalFrameType.Output, payload, cancellationToken).ConfigureAwait(false);
            first = false;
        }
    }

    public static (long Offset, TerminalOutputFlags Flags, ReadOnlyMemory<byte> Data) ReadOutput(byte[] payload)
    {
        if (payload.Length < 9)
        {
            throw new InvalidDataException("Quadro de saída sem cabeçalho.");
        }

        return (BinaryPrimitives.ReadInt64LittleEndian(payload), (TerminalOutputFlags)payload[8], payload.AsMemory(9));
    }

    public static T ReadJson<T>(byte[] payload) =>
        JsonSerializer.Deserialize<T>(payload, Json) ?? throw new InvalidDataException("Quadro de terminal vazio.");

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync().ConfigureAwait(false);
        _writing.Dispose();
    }

    private async Task<bool> FillAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;

        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);

            if (count == 0)
            {
                return read == 0 ? false : throw new EndOfStreamException("O quadro de terminal veio pela metade.");
            }

            read += count;
        }

        return true;
    }
}

/// <summary>O primeiro quadro: o que a conexão quer.</summary>
/// <param name="Operation"><c>attach</c> (seguir uma sessão) ou <c>start</c> (abrir uma).</param>
public sealed record TerminalHello(
    int Version,
    string Operation,
    Guid? SessionId = null,
    TerminalClientKind Kind = TerminalClientKind.Embedded,
    string? Label = null,
    int Columns = 0,
    int Rows = 0,
    TerminalStartRequest? Start = null)
{
    public const int CurrentVersion = 1;

    /// <summary>A abertura com provedor explícito não pode ser ignorada por um agente antigo.</summary>
    public const int ProviderStartVersion = 2;

    public const string Attach = "attach";

    public const string StartOperation = "start";
}

/// <summary>Abrir o Claude no terminal do agente, pedido pelo desktop desta máquina.</summary>
public sealed record TerminalStartRequest(
    Guid TaskId,
    Guid DevelopmentId,
    AgentHosting Hosting,
    string? Arguments = null,
    string? Prompt = null,
    bool RunDirectly = false,
    bool? Monitor = null,
    string? Model = null,
    string? Effort = null,
    string? ResumeConversation = null,
    string? ProviderId = null);

/// <summary>A resposta do <c>start</c>: a sessão aberta, ou o motivo de não abrir.</summary>
public sealed record TerminalStartResult(bool Ok, Guid? SessionId, string? Error, string? MonitoringNote);

/// <summary>Uma ação estruturada no fio. O <see cref="Type"/> diz quais campos valem.</summary>
public sealed record TerminalWireAction(
    string Type,
    string? Text = null,
    Guid? DialogId = null,
    bool Allow = false,
    int Columns = 0,
    int Rows = 0,
    bool Force = false)
{
    public const string Prompt = "prompt";
    public const string Choice = "choice";
    public const string Interrupt = "interrupt";
    public const string Resize = "resize";
    public const string Take = "take";
    public const string Release = "release";
    public const string Resync = "resync";
    public const string Stop = "stop";

    public static TerminalWireAction From(TerminalInput input) => input switch
    {
        TerminalPrompt prompt => new(Prompt, Text: prompt.Text),
        TerminalChoice choice => new(Choice, DialogId: choice.DialogId, Allow: choice.Allow),
        TerminalInterrupt => new(Interrupt),
        TerminalResize resize => new(Resize, Columns: resize.Size.Columns, Rows: resize.Size.Rows),
        TerminalTakeControl => new(Take),
        TerminalReleaseControl => new(Release),
        TerminalResync => new(Resync),
        _ => throw new ArgumentException("Ação sem forma no fio.", nameof(input)),
    };

    /// <summary>A ação que o host entende; <c>null</c> para <see cref="Stop"/>, que não é do host.</summary>
    public TerminalInput? ToInput() => Type switch
    {
        Prompt => new TerminalPrompt(Text ?? string.Empty),
        Choice when DialogId is { } dialog => new TerminalChoice(dialog, Allow),
        Interrupt => new TerminalInterrupt(),
        Resize => new TerminalResize(new TerminalSize(Columns, Rows)),
        Take => new TerminalTakeControl(),
        Release => new TerminalReleaseControl(),
        Resync => new TerminalResync(),
        Stop => null,
        _ => throw new DomainException("Ação de terminal desconhecida."),
    };
}
