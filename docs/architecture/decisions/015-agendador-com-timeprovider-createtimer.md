# ADR-015 — Agendador com `TimeProvider.CreateTimer`, não `IHostedService`

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | aplicacao |
| **Registrado** | 2026-09-20, commit `396e55f` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | ReminderScheduler, TimeProvider.CreateTimer, IHostedService, FakeTimeProvider, SemaphoreSlim, ReminderTickSkew, Generic Host |
| **Relações** | relacionado: [ADR-002](002-hora-de-parede-no-dominio.md), [ADR-004](004-lembretes-sobrevivem-ao-processo.md), [ADR-012](012-iusecaserunner-um-escopo-por-operacao.md), [ADR-021](021-agendador-de-manutencao-do-ciclo-de-vida.md), [ADR-030](030-sessoes-claude-code-por-tarefa.md), [ADR-059](059-servidor-mcp-local-loopback.md) |
| **Código** | `src/MyTaskApp.Application/Reminders/ReminderScheduler.cs`, `src/MyTaskApp.Desktop/App.axaml.cs` |

**Decisão:** `ReminderScheduler` é uma classe comum que cria um `ITimer` pelo
`TimeProvider`, iniciada em `App.OnFrameworkInitializationCompleted`. **Não** foi
adicionado o Generic Host.

**Por quê — testabilidade pesa mais que tudo aqui:** o `FakeTimeProvider` dirige
`CreateTimer` deterministicamente; um `Advance(30s)` produz exatamente um tique,
em processo, sem dormir. Um `BackgroundService` espera internamente em
`Task.Delay`, e torná-lo testável significaria reimplementar o laço sobre
`TimeProvider` de qualquer forma. Como o ADR-002 já obriga `TimeProvider` e o
`Microsoft.Extensions.TimeProvider.Testing` já está referenciado, a decisão se
resolve sozinha.

**Alternativa rejeitada — introduzir o Generic Host:** obrigaria a reestruturar o
`Program.Main` em torno de `IHost` e a conciliar dois ciclos de vida concorrentes
(`IHost.StopAsync` × `StartWithClassicDesktopLifetime`), para um laço de fundo. E
não ajudaria na injeção: `IHostedService` é singleton e precisaria do
`IUseCaseRunner` do mesmo jeito.

**Mora na Application, não no Desktop:** não toca em nenhum tipo de UI, então é
testável sem levantar o host headless da Avalonia.

**Detalhes que são decisão, não acaso:**
- primeiro tique **imediato** (`dueTime: Zero`) — é ele que recupera o que venceu
  com o app fechado, sem caminho de código separado para "catch-up";
- `SemaphoreSlim(1,1)` com espera zero: tique sobreposto é **pulado, não
  enfileirado**;
- o corpo do tique é `try/catch` de ponta a ponta — um tique que lança nunca pode
  matar o timer, que seria o app parar de lembrar em silêncio;
- salto de relógio e volta da suspensão não exigem nada: o disparo é instante
  absoluto e o tique é um poll de "o que venceu". O buraco vira `ReminderTickSkew`
  no log, que é onde se procura depois;
- é `IDisposable` **e** `IAsyncDisposable`: um singleton que só fosse
  `IAsyncDisposable` faria o `Dispose()` do contêiner **lançar**, e o composition
  root descarta o contêiner com um `using` comum no fim do `Main`.
