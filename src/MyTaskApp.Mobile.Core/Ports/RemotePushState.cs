namespace MyTaskApp.Mobile.Core.Ports;
public interface IRemotePushState { bool IsRegistered { get; } }
public sealed class NoRemotePushState : IRemotePushState { public bool IsRegistered => false; }
