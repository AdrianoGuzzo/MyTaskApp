using System.Buffers.Binary;
using MyTaskApp.Application.Sounds;

namespace MyTaskApp.Infrastructure.Sounds;

/// <summary>
/// Os sons que vêm com o app, feitos de notas (ADR-042): cada um é uma receita
/// curta de parciais com ataque rápido e decaimento exponencial, renderizada
/// num WAV PCM 16 bits mono. Sem arquivo na instalação e sem áudio de
/// terceiros — e o mesmo id sempre gera os mesmos bytes.
/// </summary>
internal static class SoundSynthesizer
{
    public const int SampleRate = 44_100;

    /// <summary>O pico depois de normalizar: alto o bastante para ouvir, longe de estourar.</summary>
    private const double Peak = 0.7;

    private const double Attack = 0.005;

    /// <summary>Parciais harmônicos que somem depressa: marimba, vibrafone.</summary>
    private static readonly Partial[] Mallet = [new(1, 1, 1), new(2, 0.35, 0.5), new(3, 0.12, 0.3)];

    /// <summary>Os parciais inarmônicos de um sino de verdade.</summary>
    private static readonly Partial[] Bell =
        [new(1, 1, 1), new(2, 0.5, 0.7), new(2.76, 0.35, 0.5), new(5.4, 0.2, 0.25), new(8.93, 0.08, 0.15)];

    /// <summary>Só os ímpares, como uma onda quadrada: áspero de propósito, é o som do erro.</summary>
    private static readonly Partial[] Buzzy = [new(1, 1, 1), new(3, 0.33, 0.8), new(5, 0.2, 0.6), new(7, 0.14, 0.5)];

    private static readonly Partial[] Pure = [new(1, 1, 1)];

    private static readonly Dictionary<string, Tone[]> Recipes = new(StringComparer.Ordinal)
    {
        // Três notas subindo: soa como pergunta.
        [BuiltInSounds.Call] =
        [
            new(0.00, 1046.50, 0.45, 0.16, Mallet),
            new(0.11, 1318.51, 0.45, 0.16, Mallet),
            new(0.22, 1567.98, 0.70, 0.25, Mallet),
        ],

        // O acorde maior em arpejo: resolvido, "pronto".
        [BuiltInSounds.Done] =
        [
            new(0.00, 523.25, 1.10, 0.35, Mallet, 0.8),
            new(0.08, 659.25, 1.00, 0.35, Mallet, 0.8),
            new(0.16, 783.99, 0.95, 0.35, Mallet, 0.8),
            new(0.24, 1046.50, 1.00, 0.40, Mallet),
        ],

        // Duas notas descendo, duas vezes, com timbre áspero.
        [BuiltInSounds.Warning] =
        [
            new(0.00, 440.00, 0.22, 0.10, Buzzy),
            new(0.20, 349.23, 0.30, 0.12, Buzzy),
            new(0.55, 440.00, 0.22, 0.10, Buzzy),
            new(0.75, 349.23, 0.45, 0.16, Buzzy),
        ],

        [BuiltInSounds.Bell] = [new(0.00, 880.00, 1.80, 0.60, Bell)],

        // Campainha: "dim-dom".
        [BuiltInSounds.TwoTones] =
        [
            new(0.00, 659.25, 0.60, 0.30, Mallet),
            new(0.30, 523.25, 1.00, 0.45, Mallet),
        ],

        [BuiltInSounds.Soft] = [new(0.00, 659.25, 1.40, 0.45, Pure, 0.6)],

        // Duas bolhas: a nota sobe rápido, como gota.
        [BuiltInSounds.Pop] =
        [
            new(0.00, 420.00, 0.12, 0.035, Pure, SweepTo: 1300),
            new(0.14, 520.00, 0.14, 0.040, Pure, SweepTo: 1600),
        ],
    };

    public static bool Knows(string soundId) => Recipes.ContainsKey(soundId);

    /// <summary>O WAV inteiro, cabeçalho incluído.</summary>
    public static byte[] Render(string soundId)
    {
        var tones = Recipes[soundId];
        var length = tones.Max(tone => tone.Start + tone.Length) + 0.02;
        var samples = new double[(int)Math.Ceiling(length * SampleRate)];

        foreach (var tone in tones)
        {
            Mix(tone, samples);
        }

        Normalize(samples);

        return Wav(samples);
    }

    private static void Mix(Tone tone, double[] samples)
    {
        var first = (int)(tone.Start * SampleRate);
        var count = (int)(tone.Length * SampleRate);
        var phase = new double[tone.Partials.Length];

        for (var i = 0; i < count && first + i < samples.Length; i++)
        {
            var t = (double)i / SampleRate;

            // O glissando da bolha: sobe nos primeiros 40% e fica.
            var frequency = tone.SweepTo is { } target
                ? tone.Frequency + ((target - tone.Frequency) * Math.Min(1, t / (tone.Length * 0.4)))
                : tone.Frequency;

            var attack = Math.Min(1, t / Attack);

            // Os últimos 10 ms vão a zero: sem estalo no corte.
            var release = Math.Min(1, (tone.Length - t) / 0.01);
            var value = 0.0;

            for (var p = 0; p < tone.Partials.Length; p++)
            {
                var partial = tone.Partials[p];
                phase[p] += 2 * Math.PI * frequency * partial.Ratio / SampleRate;
                value += partial.Gain * Math.Sin(phase[p]) * Math.Exp(-t / (tone.Decay * partial.DecayScale));
            }

            samples[first + i] += tone.Gain * attack * release * value;
        }
    }

    private static void Normalize(double[] samples)
    {
        var peak = samples.Max(Math.Abs);

        if (peak <= 0)
        {
            return;
        }

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = samples[i] / peak * Peak;
        }
    }

    private static byte[] Wav(double[] samples)
    {
        const int headerLength = 44;
        const short channels = 1;
        const short bitsPerSample = 16;
        const short blockAlign = channels * bitsPerSample / 8;

        var dataLength = samples.Length * blockAlign;
        var wav = new byte[headerLength + dataLength];
        var span = wav.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], headerLength - 8 + dataLength);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], bitsPerSample);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataLength);

        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)Math.Round(Math.Clamp(samples[i], -1, 1) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(span[(headerLength + (i * blockAlign))..], value);
        }

        return wav;
    }

    /// <param name="Ratio">Múltiplo da frequência da nota.</param>
    /// <param name="DecayScale">Multiplica o decaimento da nota: agudos somem antes.</param>
    private sealed record Partial(double Ratio, double Gain, double DecayScale);

    /// <param name="Start">Segundos desde o começo do som.</param>
    /// <param name="Decay">A constante de tempo do decaimento, em segundos.</param>
    private sealed record Tone(
        double Start,
        double Frequency,
        double Length,
        double Decay,
        Partial[] Partials,
        double Gain = 1,
        double? SweepTo = null);
}
