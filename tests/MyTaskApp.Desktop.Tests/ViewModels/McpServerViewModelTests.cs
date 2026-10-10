using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Mcp;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Mcp.Hosting;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>A janela "Servidor MCP…" (ADR-059): o estado mostrado é o do gerente, e não o da configuração.</summary>
public class McpServerViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();

    private readonly FakeMcpServerManager _manager = new();

    private readonly FakeClipboardWriter _clipboard = new();

    private readonly FakeConfirmationDialog _confirmation = new() { Answer = true };

    public McpServerViewModelTests()
    {
        _runner.ResultsByHandler[typeof(GetMcpServerSettingsHandler)] = new McpServerSettings(true, 5180, false, false);
        _runner.ResultsByHandler[typeof(UpdateMcpServerSettingsHandler)] = new McpServerSettings(true, 5180, false, false);
    }

    private McpServerViewModel ViewModel() =>
        new(_runner, _manager, _clipboard, _confirmation, NullLogger<McpServerViewModel>.Instance);

    [AvaloniaFact]
    public async Task Loading_ShowsTheSettings_AndTheRealState()
    {
        var viewModel = ViewModel();

        await viewModel.LoadAsync(Ct);

        viewModel.IsEnabled.Should().BeTrue();
        viewModel.PortText.Should().Be("5180");
        viewModel.StateLabel.Should().Be("Parado");
        viewModel.EndpointText.Should().Be("http://127.0.0.1:5180/mcp");
        viewModel.TokenLabel.Should().EndWith(_manager.Token[^4..]).And.NotContain(_manager.Token[..10]);
        viewModel.CanStart.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task Starting_FollowsTheManager_NotTheSettings()
    {
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        await viewModel.StartCommand.ExecuteAsync(null);

        viewModel.IsRunning.Should().BeTrue();
        viewModel.StateLabel.Should().Be("Ativo");

        await viewModel.StopCommand.ExecuteAsync(null);

        viewModel.IsStopped.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task AFailedStart_ShowsTheReason()
    {
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        _manager.NextStart = new McpServerStatus(McpServerState.Error, null, 5180, "A porta 5180 já está em uso.", DateTimeOffset.UtcNow);

        await viewModel.StartCommand.ExecuteAsync(null);

        viewModel.IsFailed.Should().BeTrue();
        viewModel.StateLabel.Should().Be("Erro");
        viewModel.Status.Error.Should().Contain("em uso");
    }

    [AvaloniaFact]
    public async Task DisablingWhileRunning_TellsTheManager()
    {
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        await viewModel.StartCommand.ExecuteAsync(null);
        _runner.ResultsByHandler[typeof(UpdateMcpServerSettingsHandler)] = new McpServerSettings(false, 5180, false, false);

        viewModel.IsEnabled = false;
        await Task.Delay(50, Ct);

        _runner.Invoked.Should().Contain(typeof(UpdateMcpServerSettingsHandler));
        _manager.Applied.Should().ContainSingle().Which.Enabled.Should().BeFalse();
        viewModel.IsRunning.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task AnInvalidPort_IsRefusedBeforeSaving()
    {
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        _runner.Invoked.Clear();

        viewModel.PortText = "porta";
        await viewModel.ApplyPortCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.Should().Contain("números");
        _runner.Invoked.Should().NotContain(typeof(UpdateMcpServerSettingsHandler));
    }

    [AvaloniaFact]
    public async Task TestingThePort_ShowsTheAnswer()
    {
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        _manager.PortIsFree = false;

        await viewModel.TestPortCommand.ExecuteAsync(null);

        viewModel.PortCheckFailed.Should().BeTrue();
        viewModel.PortCheckMessage.Should().Contain("em uso");
    }

    [AvaloniaFact]
    public async Task CopyingTheClientConfig_GivesTheClaudeCommand_AndTheJson()
    {
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        await viewModel.CopyClientConfigCommand.ExecuteAsync(null);

        _clipboard.LastWritten.Should()
            .Contain("claude mcp add --transport http mytaskapp http://127.0.0.1:5180/mcp")
            .And.Contain($"Authorization: Bearer {_manager.Token}")
            .And.Contain("\"type\": \"http\"");
    }

    [AvaloniaFact]
    public async Task RegeneratingTheToken_AsksFirst()
    {
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        var before = _manager.Token;
        _confirmation.Answer = false;

        await viewModel.RegenerateTokenCommand.ExecuteAsync(null);
        _manager.Token.Should().Be(before);

        _confirmation.Answer = true;
        await viewModel.RegenerateTokenCommand.ExecuteAsync(null);
        _manager.Token.Should().NotBe(before);
        viewModel.TokenLabel.Should().EndWith(_manager.Token[^4..]);
    }

    [AvaloniaFact]
    public async Task TheActivityLog_IsListed_WithProblemsMarked()
    {
        var viewModel = ViewModel();
        _manager.Activity.Add(new McpActivityEntry(DateTimeOffset.UtcNow, "task_create", McpActivityOutcome.Succeeded, TimeSpan.FromMilliseconds(12), null));
        _manager.Activity.Add(new McpActivityEntry(DateTimeOffset.UtcNow, "http", McpActivityOutcome.Denied, TimeSpan.Zero, "Sem token de acesso."));

        await viewModel.LoadAsync(Ct);

        viewModel.HasActivity.Should().BeTrue();
        viewModel.Activity.Should().HaveCount(2);
        viewModel.Activity[0].IsProblem.Should().BeTrue();
        viewModel.Activity[0].Summary.Should().Contain("Sem token");
    }

    [AvaloniaFact]
    public void TheToolList_ComesFromTheCatalog()
    {
        var viewModel = ViewModel();

        viewModel.ToolCount.Should().Be(McpCatalog.Tools.Count);
        viewModel.ToolGroups.Select(group => group.Area).Should().Contain(["Tarefas", "Horas", "Anonimização"]);
    }

    [AvaloniaFact]
    public async Task TheWindow_DrawsEveryState_WithoutBrokenBindings()
    {
        var viewModel = ViewModel();
        var window = new McpServerWindow(viewModel);
        window.Show();
        await viewModel.LoadAsync(Ct);

        window.FindControl<Button>("StartButton")!.IsEffectivelyVisible.Should().BeTrue();
        window.FindControl<Button>("StopButton")!.IsEffectivelyVisible.Should().BeFalse();

        await viewModel.StartCommand.ExecuteAsync(null);
        window.FindControl<Button>("StopButton")!.IsEffectivelyVisible.Should().BeTrue();
        window.FindControl<SelectableTextBlock>("EndpointText")!.Text.Should().Be("http://127.0.0.1:5180/mcp");

        window.Close();
        window.IsVisible.Should().BeFalse("o X esconde: a janela é singleton");
    }

    /// <summary>O gerente sem Kestrel: obedece e registra o que pediram.</summary>
    private sealed class FakeMcpServerManager : IMcpServerManager
    {
        private McpServerStatus _status = McpServerStatus.Stopped(DateTimeOffset.UtcNow);

        public string Token { get; private set; } = "tok-0123456789abcdefghij-XYZW";

        public McpServerStatus? NextStart { get; set; }

        public bool PortIsFree { get; set; } = true;

        public List<McpServerSettings> Applied { get; } = [];

        public McpServerStatus Status => _status;

        public event Action<McpServerStatus>? StatusChanged;

        public McpActivityLog Activity { get; } = new();

        public IReadOnlyList<McpToolDescriptor> Tools => McpCatalog.Tools;

        public Task<McpServerStatus> StartAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Publish(NextStart ?? new McpServerStatus(
                McpServerState.Running, McpServerSettings.EndpointFor(5180), 5180, null, DateTimeOffset.UtcNow)));

        public Task<McpServerStatus> StopAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Publish(McpServerStatus.Stopped(DateTimeOffset.UtcNow)));

        public Task<McpServerStatus> ApplySettingsAsync(McpServerSettings settings, CancellationToken cancellationToken = default)
        {
            Applied.Add(settings);
            return Task.FromResult(settings.Enabled ? _status : Publish(McpServerStatus.Stopped(DateTimeOffset.UtcNow)));
        }

        public Task<McpServerStatus> StartIfConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(_status);

        public Task<PortCheck> CheckPortAsync(int port, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PortCheck(port, PortIsFree, PortIsFree ? $"A porta {port} está livre." : $"A porta {port} já está em uso."));

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(Token);

        public Task<string> RegenerateAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            Token = "tok-" + Guid.NewGuid().ToString("N");
            return Task.FromResult(Token);
        }

        private McpServerStatus Publish(McpServerStatus status)
        {
            _status = status;
            StatusChanged?.Invoke(status);
            return status;
        }
    }
}
