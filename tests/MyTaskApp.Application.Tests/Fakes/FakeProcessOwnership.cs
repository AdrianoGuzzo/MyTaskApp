using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>Este processo é o 100; os outros vivem, menos os que o teste matou.</summary>
internal sealed class FakeProcessOwnership : IProcessOwnership
{
    public static readonly DateTimeOffset Started = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);

    public ProcessStamp Current { get; } = new(100, Started);

    public HashSet<int> Dead { get; } = [];

    public bool IsAlive(ProcessStamp process) => !Dead.Contains(process.ProcessId);
}
