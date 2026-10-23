using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Mobile.Core.Composition;

/// <summary>
/// Quem mexeu, na auditoria local: o nome do celular. Sem conta no aparelho,
/// é o que identifica de onde veio a mudança.
/// </summary>
internal sealed class DeviceUser(string? deviceName) : ICurrentUser
{
    public string? Name => OperationOrigin.Describe(string.IsNullOrWhiteSpace(deviceName) ? null : deviceName.Trim());
}
