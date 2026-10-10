using Avalonia.Controls;
using Avalonia.Data.Converters;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Desktop.Views;

/// <summary>A janela "Bancos de Dados…" (ADR-056), aberta pelo menu ☰.</summary>
public sealed partial class DatabaseOperationsWindow : Window
{
    /// <summary>Conexão desativada fica esmaecida na lista.</summary>
    public static readonly IValueConverter DisabledOpacity =
        new FuncValueConverter<bool, double>(disabled => disabled ? 0.55 : 1.0);

    /// <summary>"Anonimiza · recria o destino · verifica" — o resumo de um perfil de cópia.</summary>
    public static readonly IValueConverter OptionsSummary =
        new FuncValueConverter<DatabaseCopyOptions?, string>(options => options is null
            ? string.Empty
            : string.Join(" · ", new[]
            {
                options.RequireAnonymization ? "anonimiza" : "sem anonimização",
                options.IncludeSchema && options.IncludeData ? "estrutura e dados" : options.IncludeSchema ? "só estrutura" : "só dados",
                options.RecreateDestination ? "recria o destino" : null,
                options.VerifyAfterRestore ? "verifica" : null,
                options.KeepAnonymizedArtifact ? "mantém o dump da estrutura" : null,
            }.OfType<string>()));

    public DatabaseOperationsWindow() => InitializeComponent();

    public DatabaseOperationsWindow(DatabaseOperationsViewModel viewModel)
        : this() => DataContext = viewModel;

    /// <summary>
    /// O "X" esconde, não fecha — singleton, como as outras janelas do menu. A
    /// cópia em andamento continua; quem a para é o Cancelar, ou sair do app.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    /// <summary>Recarrega a cada abertura: conexões, perfis e histórico podem ter mudado.</summary>
    public void Reveal()
    {
        if (DataContext is DatabaseOperationsViewModel viewModel)
        {
            _ = viewModel.LoadAsync(CancellationToken.None);
        }
    }
}
