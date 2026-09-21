using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>
/// Um nome conhecido, para a auditoria poder ser conferida. <c>null</c> também é
/// um caso de teste legítimo: o §5 diz "quando houver identificação".
/// </summary>
internal sealed class FakeCurrentUser(string? name = "adriano") : ICurrentUser
{
    public string? Name { get; } = name;
}
