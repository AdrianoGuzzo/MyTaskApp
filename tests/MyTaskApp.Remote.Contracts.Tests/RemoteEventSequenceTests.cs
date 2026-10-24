namespace MyTaskApp.Remote.Contracts.Tests;

/// <summary>
/// O ack dos eventos: o agente só apaga do diário até onde a sequência anda
/// sem buraco, e duplicados ou atrasados não mudam a conta.
/// </summary>
public sealed class RemoteEventSequenceTests
{
    [Theory]
    [InlineData(0, new long[] { 1, 2, 3 }, 3)]
    [InlineData(0, new long[] { 2, 3 }, 0)]
    [InlineData(0, new long[] { 1, 3, 4 }, 1)]
    [InlineData(3, new long[] { 5, 4 }, 5)]
    [InlineData(3, new long[] { 1, 2, 3 }, 3)]
    [InlineData(2, new long[] { 3, 3, 4, 4 }, 4)]
    [InlineData(7, new long[0], 7)]
    public void TheAck_StopsAtTheFirstGap(long confirmed, long[] received, long expected)
    {
        RemoteEventSequence.LastContiguous(confirmed, received).Should().Be(expected);
    }

    [Fact]
    public void AGapFilledLater_MovesTheAckForward()
    {
        var received = new List<long> { 1, 2, 4, 5 };
        var ack = RemoteEventSequence.LastContiguous(0, received);
        ack.Should().Be(2);

        received.Add(3);

        RemoteEventSequence.LastContiguous(ack, received).Should().Be(5);
    }
}
