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

    /// <summary>"backup_user@192.168.15.112:5432/eco_core"; só o servidor, "…:5432 (banco na cópia)".</summary>
    public string Endpoint => Row.Database is { } database
        ? string.Create(CultureInfo.InvariantCulture, $"{Row.Username}@{Row.Host}:{Row.Port}/{database}")
        : string.Create(CultureInfo.InvariantCulture, $"{Row.Username}@{Row.Host}:{Row.Port} (banco na cópia)");

    /// <summary>Tem banco fixo; sem ele, a conexão é só o servidor e o banco é escolhido na cópia (ADR-057).</summary>
    public bool HasDatabase => Row.Database is not null;

    public string PasswordLabel => Row.HasPassword ? "Senha guardada ✓" : "Sem senha guardada";

    public bool IsDisabled => !Row.IsEnabled;

    public string? Description => Row.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Row.Description);

    public override string ToString() => $"{Name} ({Badge})";
}

/// <summary>Um apelido na lista: "lock_eco_core_1010 — ECO Produção / eco_core_1010 · Anon ECO".</summary>
public sealed class SavedDatabaseItemViewModel(SavedDatabaseRow row, string connectionName, string? anonymizationName)
{
    public SavedDatabaseRow Row { get; } = row;

    public Guid Id => Row.Id;

    public string Alias => Row.Alias;

    public string Detail => anonymizationName is null
        ? $"{connectionName} / {Row.DatabaseName} · sem anonimização"
        : $"{connectionName} / {Row.DatabaseName} · {anonymizationName}";

    public override string ToString() => Alias;
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

    /// <summary>O que a probabilidade quer dizer — a sugestão é só pelo nome e tipo da coluna, nunca pelos dados.</summary>
    public string SensitivityHint => Sensitivity switch
    {
        ColumnSensitivity.High => "Alta: o nome e o tipo indicam dado pessoal quase certo (CPF, e-mail, telefone…). Marque, salvo engano.",
        ColumnSensitivity.Medium => "Média: costuma ser dado pessoal (nome, endereço, data de nascimento…). Confira a tabela.",
        _ => "Baixa: pode ser dado pessoal, mas muitas vezes não é. Marque só se souber que é.",
    };

    /// <summary>As duas formas de mascarar, como o ComboBox de cada linha mostra.</summary>
    public static IReadOnlyList<Choice<MaskingKind>> Kinds { get; } =
    [
        new(MaskingKind.Function, "Função"),
        new(MaskingKind.Value, "Valor fixo"),
    ];

    public Choice<MaskingKind> SelectedKind
    {
        get => Kinds.First(choice => choice.Value == Kind);
        set
        {
            if (value is not null)
            {
                Kind = value.Value;
            }
        }
    }

    /// <summary>O exemplo da expressão muda com o tipo: função do anon, ou um valor que vai igual em toda linha.</summary>
    public string ExpressionPlaceholder => Kind == MaskingKind.Value ? "NULL, 0 ou 'CONFIDENCIAL'" : "anon.fake_email()";

    /// <summary>Some com o filtro; "Selecionar todas" só alcança as visíveis.</summary>
    [ObservableProperty]
    private bool _isShown = true;

    /// <summary>Filtro por schema, tabela, coluna ou motivo, sem diferenciar maiúsculas.</summary>
    public bool Matches(string filter) =>
        string.IsNullOrEmpty(filter)
        || ColumnKey.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Reason?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;

    public bool IsHigh => Sensitivity == ColumnSensitivity.High;

    public bool IsMedium => Sensitivity == ColumnSensitivity.Medium;

    public string? Reason { get; }

    public string ColumnKey => $"{Schema}.{Table}.{Column}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValue), nameof(SelectedKind), nameof(ExpressionPlaceholder))]
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

    /// <summary>"Copiar + Anonimizar: ECO Produção/eco_core_1010 → Local/lock_eco_core_1010_20261009_143000".</summary>
    public string Title =>
        $"{DatabaseLabels.Operation(Row.OperationType)}: {Side(Row.Source, Row.SourceDatabase)} → {Side(Row.Destination, Row.DestinationDatabase)}";

    private static string Side(string? connection, string? database) =>
        database is null ? connection ?? "—" : $"{connection ?? "—"}/{database}";

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
