using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Os nomes da tela para os enums das operações de banco (ADR-056).</summary>
public static class DatabaseLabels
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("pt-BR");

    public static string Badge(DatabaseEnvironment environment) => environment switch
    {
        DatabaseEnvironment.Development => "DEVELOPMENT",
        DatabaseEnvironment.Test => "TEST",
        DatabaseEnvironment.Staging => "STAGING",
        DatabaseEnvironment.Production => "PRODUCTION",
        DatabaseEnvironment.CriticalProduction => "CRITICAL",
        _ => environment.ToString().ToUpperInvariant(),
    };

    public static string Environment(DatabaseEnvironment environment) => environment switch
    {
        DatabaseEnvironment.Development => "Desenvolvimento",
        DatabaseEnvironment.Test => "Teste",
        DatabaseEnvironment.Staging => "Homologação",
        DatabaseEnvironment.Production => "Produção",
        DatabaseEnvironment.CriticalProduction => "Produção crítica",
        _ => environment.ToString(),
    };

    public static string Ssl(DatabaseSslMode mode) => mode switch
    {
        DatabaseSslMode.Prefer => "Preferir SSL",
        DatabaseSslMode.Require => "Exigir SSL",
        DatabaseSslMode.VerifyFull => "Exigir SSL e conferir certificado",
        DatabaseSslMode.Disable => "Sem SSL",
        _ => mode.ToString(),
    };

    public static string Operation(DatabaseOperationType operation) => operation switch
    {
        DatabaseOperationType.Copy => "Copiar",
        DatabaseOperationType.CopyAndAnonymize => "Copiar + Anonimizar",
        DatabaseOperationType.Dump => "Dump",
        DatabaseOperationType.AnonymousDump => "Dump anônimo",
        DatabaseOperationType.Restore => "Restore",
        DatabaseOperationType.CreateDatabase => "Criar banco",
        DatabaseOperationType.DropDatabase => "Apagar banco",
        DatabaseOperationType.Verify => "Verificar",
        DatabaseOperationType.Diagnose => "Diagnóstico",
        DatabaseOperationType.TestConnection => "Testar conexão",
        DatabaseOperationType.InspectDatabase => "Inspecionar",
        _ => operation.ToString(),
    };

    public static string Status(DatabaseOperationStatus status) => status switch
    {
        DatabaseOperationStatus.Running => "● Em andamento",
        DatabaseOperationStatus.Succeeded => "✓ Concluída",
        DatabaseOperationStatus.Failed => "✗ Falhou",
        DatabaseOperationStatus.Canceled => "✗ Cancelada",
        DatabaseOperationStatus.Blocked => "⛔ Bloqueada",
        DatabaseOperationStatus.Interrupted => "⚠ Interrompida",
        _ => status.ToString(),
    };

    public static string Sensitivity(ColumnSensitivity sensitivity) => sensitivity switch
    {
        ColumnSensitivity.High => "Alta probabilidade",
        ColumnSensitivity.Medium => "Média probabilidade",
        _ => "Baixa probabilidade",
    };

    public static string Glyph(CheckOutcome outcome) => outcome switch
    {
        CheckOutcome.Pass => "✓",
        CheckOutcome.Warning => "⚠",
        _ => "✗",
    };
}

/// <summary>Um valor de enum com o nome que a tela mostra — para os ComboBox.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Uma conexão na lista e nos seletores.</summary>
public sealed class DatabaseConnectionItemViewModel(DatabaseConnectionRow row)
{
    public DatabaseConnectionRow Row { get; } = row;

    public Guid Id => Row.Id;

    public string Name => Row.Name;

    public string Badge => DatabaseLabels.Badge(Row.Environment);

    public bool IsProduction => Row.IsProtected;

    public bool IsStaging => Row.Environment == DatabaseEnvironment.Staging;

    public bool IsTest => Row.Environment == DatabaseEnvironment.Test;

    public bool IsDevelopment => Row.Environment == DatabaseEnvironment.Development;

    /// <summary>"backup_user@192.168.15.112:5432/eco_core".</summary>
    public string Endpoint => string.Create(CultureInfo.InvariantCulture, $"{Row.Username}@{Row.Host}:{Row.Port}/{Row.Database}");

    public string PasswordLabel => Row.HasPassword ? "Senha guardada ✓" : "Sem senha guardada";

    public bool IsDisabled => !Row.IsEnabled;

    public string? Description => Row.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Row.Description);

    public override string ToString() => $"{Name} ({Badge})";
}

/// <summary>Uma permissão no formulário da conexão: travada quando o ambiente decide por ela.</summary>
public sealed partial class PermissionOptionViewModel(ConnectionPermission permission, string label) : ObservableObject
{
    public ConnectionPermission Permission { get; } = permission;

    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockHint))]
    private bool _isEditable = true;

    public string? LockHint => IsEditable ? null : "Fixo pelo ambiente";
}

/// <summary>Um item de diagnóstico ou de verificação: ✓/⚠/✗, o nome e o detalhe.</summary>
public sealed class CheckItemViewModel(CheckResult check)
{
    public CheckResult Check { get; } = check;

    public string Glyph => DatabaseLabels.Glyph(Check.Outcome);

    public string Name => Check.Name;

    public string Category => Check.Category;

    public string? Detail => Check.Detail;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Check.Detail);

    public bool IsPass => Check.Outcome == CheckOutcome.Pass;

    public bool IsWarning => Check.Outcome == CheckOutcome.Warning;

    public bool IsFail => Check.Outcome == CheckOutcome.Fail;

    public string Outcome => VerificationReport.Label(Check.Outcome);
}

/// <summary>Uma etapa da cópia na lista de progresso.</summary>
public sealed partial class DatabaseCopyStepViewModel(DatabaseCopyStep step, string label) : ObservableObject
{
    public DatabaseCopyStep Step { get; } = step;

    public string Label { get; } = label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph), nameof(IsDone), nameof(IsRunning), nameof(IsFailed), nameof(IsPending))]
    private CommandStepState _state = CommandStepState.Waiting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private string? _detail;

    /// <summary>✓ feita, ● em andamento, ○ esperando, ✗ falhou ou cancelada, – não se aplicou.</summary>
    public string Glyph => State switch
    {
        CommandStepState.Succeeded => "✓",
        CommandStepState.Running => "●",
        CommandStepState.Failed or CommandStepState.Canceled => "✗",
        CommandStepState.NotRun => "–",
        _ => "○",
    };

    public bool IsDone => State is CommandStepState.Succeeded;

    public bool IsRunning => State is CommandStepState.Running;

    public bool IsFailed => State is CommandStepState.Failed or CommandStepState.Canceled;

    public bool IsPending => State is CommandStepState.Waiting or CommandStepState.NotRun;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}

/// <summary>Uma regra de mascaramento no editor do perfil: sugerida, ou confirmada.</summary>
public sealed partial class AnonymizationRuleItemViewModel : ObservableObject
{
    public AnonymizationRuleItemViewModel(AnonymizationRuleRow rule, bool confirmed, string? reason = null)
    {
        Schema = rule.Schema;
        Table = rule.Table;
        Column = rule.Column;
        Sensitivity = rule.Sensitivity;
        _kind = rule.Kind;
        _expression = rule.Expression;
        _isConfirmed = confirmed;
        Reason = reason;
    }

    public string Schema { get; }

    public string Table { get; }

    public string Column { get; }

    public ColumnSensitivity Sensitivity { get; }

    public string SensitivityLabel => DatabaseLabels.Sensitivity(Sensitivity);

    public bool IsHigh => Sensitivity == ColumnSensitivity.High;

    public bool IsMedium => Sensitivity == ColumnSensitivity.Medium;

    public string? Reason { get; }

    public string ColumnKey => $"{Schema}.{Table}.{Column}";

    [ObservableProperty]
    private MaskingKind _kind;

    [ObservableProperty]
    private string _expression;

    /// <summary>Só o que o usuário marcou vira regra: a sugestão é palpite pelo nome da coluna.</summary>
    [ObservableProperty]
    private bool _isConfirmed;

    public bool IsValue
    {
        get => Kind == MaskingKind.Value;
        set => Kind = value ? MaskingKind.Value : MaskingKind.Function;
    }

    public AnonymizationRuleRow ToRow() => new(Schema, Table, Column, Kind, Expression, Sensitivity);
}

/// <summary>Uma operação no histórico.</summary>
public sealed class DatabaseOperationItemViewModel(DatabaseOperationRow row)
{
    public DatabaseOperationRow Row { get; } = row;

    public string Title => $"{DatabaseLabels.Operation(Row.OperationType)}: {Row.Source ?? "—"} → {Row.Destination ?? "—"}";

    public string Status => DatabaseLabels.Status(Row.Status);

    public bool IsSucceeded => Row.Status == DatabaseOperationStatus.Succeeded;

    public bool IsFailed => Row.Status is DatabaseOperationStatus.Failed or DatabaseOperationStatus.Blocked
        or DatabaseOperationStatus.Canceled or DatabaseOperationStatus.Interrupted;

    public string When => Row.StartedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", DatabaseLabels.Culture);

    public string Meta => string.Join(" · ", new[]
    {
        $"{Row.User}@{Row.Host}",
        Row.Duration is { } duration ? FormatDuration(duration) : null,
        Row.AnonymizationProfile is { } profile ? $"perfil {profile}" : null,
        Row.MaskedColumnsCount is { } masked ? $"{masked} coluna(s) mascarada(s)" : null,
        Row.AnonymousDumpSize is { } size ? $"dump anônimo {PostgresEnvironmentDiagnostics.FormatBytes(size)}" : null,
        Row.ToolVersions,
    }.OfType<string>());

    public string? Summary => Row.Summary;

    public bool HasSummary => !string.IsNullOrWhiteSpace(Row.Summary);

    public string? Error => Row.Error;

    public bool HasError => !string.IsNullOrWhiteSpace(Row.Error);

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalMinutes >= 1
            ? string.Create(DatabaseLabels.Culture, $"{(int)duration.TotalMinutes} min {duration.Seconds} s")
            : string.Create(DatabaseLabels.Culture, $"{duration.TotalSeconds:0.#} s");
}
