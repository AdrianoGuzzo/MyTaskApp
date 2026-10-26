# ADR-005 — Persistência isolada por interfaces estreitas

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | persistencia |
| **Registrado** | 2026-09-20, commit `396e55f` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | ITaskItemRepository, ITodayQuery, IUnitOfWork, repositório genérico, migrations, EnsureCreated, TempSqliteDatabase |
| **Relações** | relacionado: [ADR-018](018-instalador-inno-setup-e-dados-do-usuario.md) |
| **Código** | `src/MyTaskApp.Application/Abstractions/ITaskItemRepository.cs`, `src/MyTaskApp.Application/Abstractions/IUnitOfWork.cs`, `src/MyTaskApp.Application/Planning/ITodayQuery.cs`, `src/MyTaskApp.Infrastructure/Persistence/DatabaseInitializer.cs` |

**Decisão:** `Application` define interfaces por necessidade
(`ITaskItemRepository`, `ITodayQuery`, `IUnitOfWork`); `Infrastructure`
implementa com EF Core. **Sem** `IRepository<T>` genérico.

**Por quê:** o repositório genérico só reembala o `DbSet` e obriga a vazar
`IQueryable` — acoplamento sem benefício. Consultas de leitura ("Hoje",
histórico, busca) vão por interfaces de *query* que devolvem DTOs, sem passar
por agregados.

**Migrations:** obrigatórias desde a primeira versão. `EnsureCreated()` não é
usado em lugar nenhum (§19). Testes de integração rodam **as migrations** contra
SQLite em arquivo temporário — assim a migration também é testada.
