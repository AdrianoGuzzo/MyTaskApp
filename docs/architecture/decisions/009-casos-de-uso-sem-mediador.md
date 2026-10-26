# ADR-009 — Casos de uso sem mediador

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | aplicacao |
| **Registrado** | 2026-09-20, commit `396e55f` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | MediatR, HandleAsync, AddApplication, handler, caso de uso, injeção de dependência |
| **Relações** | — |
| **Código** | `src/MyTaskApp.Application/DependencyInjection.cs`, `tests/MyTaskApp.Application.Tests/ApplicationRegistrationTests.cs` |

**Decisão:** uma classe por caso de uso, com `HandleAsync`. Sem MediatR.

**Por quê:** MediatR resolveria acoplamento entre módulos que este app não tem
— a UI chama o handler direto, por injeção. Acrescentaria indireção em runtime,
pipeline behaviors e uma dependência, sem eliminar nenhuma linha de código.
`AddApplication()` registra todos os handlers, e um teste resolve cada um do
contêiner para que esquecer um registro quebre a build, não a tela.
