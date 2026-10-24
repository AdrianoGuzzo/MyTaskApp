# ADR-014 — Configuração de lembrete é dado do usuário, não do `appsettings`

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | lembretes-prazos |
| **Registrado** | 2026-09-20, commit `396e55f` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | ReminderSettings, IReminderSettingsStore, ReminderPolicy.Default, appsettings, ReminderTickSeconds, HasData, configuração padrão |
| **Relações** | relacionado: [ADR-020](020-ciclo-de-vida-estado-derivado.md), [ADR-022](022-ordem-manual-por-arrasto.md), [ADR-033](033-parametros-do-agente-lembrados.md), [ADR-042](042-som-por-estado-do-claude-code.md), [ADR-050](050-prazo-com-aviso-por-degrau.md) |
| **Código** | `src/MyTaskApp.Application/Reminders/IReminderSettingsStore.cs`, `src/MyTaskApp.Infrastructure/Persistence/Repositories/ReminderSettingsStore.cs`, `src/MyTaskApp.Infrastructure/Persistence/Configurations/ReminderSettingsConfiguration.cs` |

**Decisão:** o padrão de lembrete mora numa **linha única** da tabela
`ReminderSettings` no SQLite, atrás de `IReminderSettingsStore`. O
`appsettings.json` guarda apenas a cadência do agendador
(`ReminderTickSeconds`).

**Por quê:** o `appsettings` é lido uma vez, com `reloadOnChange: false`, e não é
gravável — o diretório de instalação pode ser somente-leitura. "Configuração
padrão" é algo que o usuário muda na tela, então é dado, e dado mora no banco:
um artefato só para backup, transacional com o resto e testável com o
`TempSqliteDatabase` que já existe. A cadência do tique é botão de implantação,
não preferência, e por isso ficou do outro lado da linha.

**Sem `HasData`.** Instalação nova não semeia linha nenhuma: `GetAsync` devolve
`ReminderPolicy.Default` quando a linha falta. Semear um singleton mutável por
migration vira armadilha no dia em que o padrão mudar — a linha semeada com o
valor velho continuaria lá. Consequência boa: "Restaurar padrão" é literalmente
gravar `ReminderPolicy.Default`, e há **uma** fonte da verdade.

**Linha corrompida degrada, não derruba.** Um `DomainException` ao reconstruir a
política vira log `InvalidReminderDefaultsStored` e o padrão de fábrica — mesma
degradação que o `UserClock` já aplica a um fuso inválido.

**O padrão não é reaplicado retroativamente.** Ele vale na criação. Rearmar todas
as tarefas do banco porque o usuário mexeu num combo seria uma surpresa
genuinamente alarmante; há teste fixando isso.
