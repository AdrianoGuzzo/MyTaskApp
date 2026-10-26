# ADR-012 — `IUseCaseRunner`: um escopo por operação

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | aplicacao |
| **Registrado** | 2026-09-20, commit `396e55f` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | IUseCaseRunner, ScopedUseCaseRunner, escopo, DbContext, service locator, FakeUseCaseRunner |
| **Relações** | relacionado: [ADR-015](015-agendador-com-timeprovider-createtimer.md), [ADR-021](021-agendador-de-manutencao-do-ciclo-de-vida.md), [ADR-059](059-servidor-mcp-local-loopback.md) |
| **Código** | `src/MyTaskApp.Application/Abstractions/IUseCaseRunner.cs`, `src/MyTaskApp.Application/Abstractions/ScopedUseCaseRunner.cs`, `tests/MyTaskApp.Desktop.Tests/ViewModels/FakeUseCaseRunner.cs` |

**Decisão:** ViewModels não recebem contêiner nem `IServiceScopeFactory`. Recebem
`IUseCaseRunner`, que abre um escopo, resolve o handler e o executa.

**Por quê:** os handlers dependem de `DbContext`, que é *scoped*, enquanto um
ViewModel vive enquanto a janela existir. Um `DbContext` longevo acumula
entidades rastreadas e serve dado velho. A alternativa — injetar
`IServiceProvider` no ViewModel — é service locator disfarçado e impede testar
sem levantar DI.

**Consequência:** os testes de ViewModel usam um executor falso que só registra
*qual* caso de uso foi pedido. O que cada handler faz já está coberto em
Application e Infrastructure; repetir ali seria teste frágil e duplicado (§6).
