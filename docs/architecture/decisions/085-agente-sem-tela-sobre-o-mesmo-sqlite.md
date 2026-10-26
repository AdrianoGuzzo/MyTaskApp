# ADR-085 — O agente: um processo sem tela, do usuário, sobre o mesmo SQLite

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Parcial |
| **Categoria** | remoto |
| **Registrado** | 2026-10-24, commit `36eb99c` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | MyTaskApp.Agent, ScopedUseCaseRunner, WAL, ProcessLeases, named pipe, systemd --user, DatabaseInitializer |
| **Relações** | relacionado: [ADR-049](049-instalador-fecha-o-app-aberto.md), [ADR-059](059-servidor-mcp-local-loopback.md), [ADR-061](061-sincronizacao-outbox-mescla-por-campo.md), [ADR-063](063-indicador-sincronizacao-no-cabecalho.md), [ADR-066](066-comandos-globais-locais-por-padrao.md), [ADR-084](084-orquestracao-remota-coordenador-e-agente.md), [ADR-086](086-operacao-remota-registro-diario-e-signalr.md), [ADR-090](090-copia-de-banco-remota-com-aprovacao.md) |
| **Código** | `src/MyTaskApp.Infrastructure/Persistence/DatabaseInitializer.cs`, `src/MyTaskApp.Application/Abstractions/ScopedUseCaseRunner.cs`, `src/MyTaskApp.Agent/AgentHost.cs`, `src/MyTaskApp.Infrastructure/Processes/SqliteProcessLeases.cs`, `src/MyTaskApp.Infrastructure/Processes/PipeLocalPeerChannel.cs` |

**Estado:** aceito (fase 2), implementado quase todo: o SQLite garantido em WAL (T-07), os leases entre processos com o ciclo do sync e o refresh do token sob eles (T-08), o `ScopedUseCaseRunner` único (T-09), o agente `MyTaskApp.Agent` no logon, o canal local com o desktop e o gate de banco entre processos (T-31). Falta o item 3: um processo mais velho que o esquema recusar subir.

**Contexto:** a máquina executora precisa atender pedidos com o desktop
fechado. O que o agente usa só existe nela: o caminho do repositório
(`TagDirectory.Path`, só local desde o ADR-061), os comandos que não
sincronizam (ADR-066), as conexões de banco e as senhas no `ISecretStore`.
Hoje um único processo usa o banco: `SingleInstance` garante isso, e o
`DatabaseOperationGate` e o `McpGateway` serializam dentro do processo. O
arquivo já está em WAL, porque o EF Core o cria assim; o ADR-059 dizia o
contrário. Não existe host sem tela; o `McpTestHost` dos testes é o mais perto
disso.

**Decisão:**

1. **Outro executável, o mesmo código.** `MyTaskApp.Agent` compõe
   `AddApplication` + `AddInfrastructure` como o `McpTestHost`, com
   `ICurrentUser` e `IAlertPresenter` próprios, e nenhum ViewModel. O
   executor de casos de uso é o `ScopedUseCaseRunner` da Application, o mesmo
   do desktop e do celular.
2. **Como o usuário, no logon.** Tarefa agendada por usuário no Windows e
   `systemd --user` no Linux. DPAPI e libsecret continuam funcionando, porque
   são do usuário. Mutex próprio (`MyTaskApp.Agent`); o instalador fecha e
   reabre o agente como faz com o app (ADR-049).
3. **SQLite para dois processos.** O `DatabaseInitializer` garante o WAL a cada
   abertura. O escritor ocupado espera, porque o Microsoft.Data.Sqlite repete o
   comando até o timeout. A migration concorrente o EF já trava
   (`__EFMigrationsLock`). Feito na T-07. Falta: um processo mais velho que o
   esquema recusar subir.
4. **Um dono para cada laço**, por lease numa tabela (`ProcessLeases`: nome,
   PID, início, validade): o ciclo do sync roda em quem tem o lease `sync`, e
   o outro processo devolve `Elsewhere` até o lease vencer ou o dono morrer
   (PID + início que não existem mais passam o lease na hora). O gate fica no
   `SyncEngine`, depois de conferir a conta: sem sync ligado, nada é gravado.
   Lembretes, bandeja e post-its são só do desktop; a manutenção ainda roda nos
   dois, porque os lotes são idempotentes. A renovação do token do sync, o
   login e o "desconectar" seguram o lease `sync-token`, relendo o cofre lá
   dentro, para dois processos não girarem o mesmo refresh token. O celular é
   um processo só e usa `SingleProcessLeases` (o iOS nem deixa perguntar por
   outro PID).
5. **Gate de banco entre processos.** A `DatabaseOperationAudit` ganha o dono
   (PID + início); a recuperação só marca como interrompida a operação cujo dono
   não existe mais.
6. **IPC local.** Named pipe com `PipeOptions.CurrentUserOnly` (Unix socket no
   Linux), só para "dados mudaram", "sincronizar agora" e o pareamento local.
   Nada de execução por ali.

## Alternativas

- **Executor dentro do desktop.** É o mais simples, mas exige o desktop aberto:
  fere o requisito.
- **Agente como outro dispositivo de sync, com banco próprio.** Não teria os
  dados só locais (caminhos, conexões, senhas) que justamente não sincronizam.
- **Desktop como cliente fino do agente**, com todo o acesso a dados passando
  por ele. É o destino mais limpo, mas reescreve o acesso a dados de todas as
  telas. Fica como evolução possível.
- **Serviço de sistema** (Windows Service, systemd de sistema). Rodaria sem
  logon, mas DPAPI e libsecret são do usuário; pediria um cofre de máquina e
  daria ao agente mais poder do que o usuário tem.
- **Mutex e evento nomeados para coordenar.** O evento nomeado não existe no
  .NET em Unix; a tabela de lease funciona igual nos dois sistemas e dá para
  ver quem é o dono.

## Consequências

- Backup que copia só o `.db` perde o que ainda está no `-wal`. Não é novo, mas
  dois processos abertos o tornam mais provável (documentar em "Onde ficam os
  dados").
- A bolinha do sync (ADR-063) passa a ler o estado do banco quando o dono do
  lease é o agente.
- "Desktop fechado" quer dizer usuário logado, com a tela bloqueada ou não.

## Fora de escopo, de propósito

- Rodar sem a sessão do usuário (cofre de máquina).
- macOS (não há instalador hoje).
