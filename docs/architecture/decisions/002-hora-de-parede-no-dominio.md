# ADR-002 — Tempo: parede (wall-clock) no domínio, instante só na borda

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | dominio |
| **Registrado** | 2026-09-20, commit `396e55f` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | TimeProvider, IUserClock, ToInstant, DateOnly, TimeOnly, horário de verão, DST, InvariantGlobalization |
| **Relações** | relacionado: [ADR-015](015-agendador-com-timeprovider-createtimer.md), [ADR-018](018-instalador-inno-setup-e-dados-do-usuario.md) |
| **Código** | `src/MyTaskApp.Application/Planning/IUserClock.cs`, `src/MyTaskApp.Application/Planning/UserClock.cs`, `Directory.Build.props` |

**Decisão:**
- Agendamento no domínio usa `DateOnly` + `TimeOnly?` (hora de parede) + o
  fuso configurado pelo usuário.
- O instante absoluto (`DateTimeOffset` UTC) é **derivado** apenas quando se
  precisa disparar algo (lembretes).
- "Agora" vem sempre de `TimeProvider` (BCL) — nunca de `DateTime.Now`.

**Por quê:** "verificar e-mails todo dia às 08:30" significa 08:30 no relógio
do usuário, em qualquer dia do ano. Se as ocorrências fossem materializadas em
UTC, o horário de verão deslocaria a tarefa para 07:30 ou 09:30. Guardar a hora
de parede e converter na borda é a única forma correta.

**Por que `TimeProvider` e não um `IClock` próprio:** já existe no .NET 8+, tem
`FakeTimeProvider` oficial e evita mais uma interface caseira (§24: interfaces
só quando agregam valor).

**Horário inexistente/ambíguo no DST:** regra explícita e testada — hora que
não existe avança para o primeiro instante válido; hora ambígua usa a primeira
ocorrência (offset de verão).

**A conversão parede → instante mora em `IUserClock.ToInstant(date, time)`.** É a
única ponte entre o agendamento do domínio e o instante que dispara um lembrete,
e é onde as duas regras acima estão implementadas. Não há sobrecarga para hora
opcional: ocorrência sem horário não tem instante, e quem chama precisa decidir o
que fazer com isso em vez de receber um palpite.

**Os testes de DST usam `America/New_York`, não São Paulo.** O Brasil acabou com
o horário de verão em 2019, então São Paulo não tem transição futura para
exercitar. `InvariantGlobalization=false` no `Directory.Build.props` é o que faz
ids IANA resolverem no Windows — não pode ser "otimizado" para `true`.

**Limite aceito:** a hora ambígua escolhe o maior offset, que é a primeira
ocorrência em todo fuso de DST positivo. Em fusos de DST *negativo* (Irlanda,
Lord Howe) a escolha seria a segunda. Não é o caso de nenhum usuário previsto.

## Divergências com o código (conferido em 2026-10-24)

- "Agora" lido fora do TimeProvider em alguns pontos de borda: DateTimeOffset.UtcNow em src/MyTaskApp.Desktop/ViewModels/McpServerViewModel.cs (linha 95) e src/MyTaskApp.Infrastructure/Terminals/Windows/WindowsTerminalLauncher.cs (linha 150), e DateTime.UtcNow em src/MyTaskApp.Infrastructure/Agents/ClaudeCode/ClaudeCodeCliProvider.cs (linha 312). Nenhum é DateTime.Now nem afeta agendamento.
