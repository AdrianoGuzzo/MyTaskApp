namespace MyTaskApp.Domain.Planning;

/// <summary>
/// Onde a ocorrência aparece. <paramref name="IsLate"/> só qualifica itens da
/// seção <see cref="TodaySection.Today"/> — nas outras a própria seção já diz
/// o que precisa ser dito.
/// </summary>
public sealed record TodayPlacement(TodaySection Section, bool IsLate);
