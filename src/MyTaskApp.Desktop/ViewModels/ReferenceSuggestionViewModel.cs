using CommunityToolkit.Mvvm.ComponentModel;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>O que um item da referência por <c>@</c> aponta (ADR-039).</summary>
public enum ReferenceKind
{
    Environment,
    Directory,
    File,
}

/// <summary>
/// Um item da lista do <c>@</c> no texto do agente: um ambiente da tarefa, ou
/// uma pasta ou arquivo dentro de um deles. Aceitar insere <see cref="FullPath"/>;
/// o Tab, numa pasta, insere <see cref="Continuation"/> e a busca continua.
/// </summary>
public sealed partial class ReferenceSuggestionViewModel : ObservableObject
{
    public ReferenceSuggestionViewModel(
        ReferenceKind kind,
        string identity,
        string title,
        string? detail,
        string? badge,
        string fullPath,
        string? continuation)
    {
        Kind = kind;
        Identity = identity;
        Title = title;
        Detail = detail;
        Badge = badge;
        FullPath = fullPath;
        Continuation = continuation;
    }

    public ReferenceKind Kind { get; }

    /// <summary>O mesmo item entre duas filtragens: é por ele que o destaque do teclado fica.</summary>
    public string Identity { get; }

    /// <summary>"@MyTaskApp", "Views/", "TodayView.axaml".</summary>
    public string Title { get; }

    /// <summary>A branch e a pasta do ambiente, ou a pasta de cima do arquivo.</summary>
    public string? Detail { get; }

    /// <summary>"este ambiente", ou de que ambiente é o arquivo na busca geral.</summary>
    public string? Badge { get; }

    public bool HasBadge => Badge is not null;

    /// <summary>O caminho absoluto que entra no texto.</summary>
    public string FullPath { get; }

    /// <summary>O <c>@ambiente/pasta/</c> do Tab; <c>null</c> para arquivo.</summary>
    public string? Continuation { get; }

    public bool IsEnvironment => Kind is ReferenceKind.Environment;

    [ObservableProperty]
    private bool _isSelected;
}
