# ADR-011 — `DateTimeOffset` vira ticks UTC no SQLite

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | persistencia |
| **Registrado** | 2026-09-20, commit `396e55f` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | DateTimeOffset, ticks UTC, value converter, UtcInstantConverter, SQLite, could not be translated |
| **Relações** | — |
| **Código** | `src/MyTaskApp.Infrastructure/Persistence/Configurations/UtcInstantConverter.cs`, `src/MyTaskApp.Infrastructure/Persistence/Configurations/TimeSpanTicksConverter.cs` |

**Decisão:** value converter de `DateTimeOffset` para `long` (ticks em UTC) nas
colunas de instante.

**Por quê:** o SQLite não tem tipo de data nativo. Como TEXT com offset embutido,
o EF Core **se recusa a traduzir comparações** — a consulta da tela "Hoje" falhava
com "could not be translated". Ticks em UTC são ordenáveis, indexáveis e sem
ambiguidade de fuso.

**Preço aceito:** abrir o banco à mão mostra número em vez de data legível.
