# ADR-063 — A bolinha da sincronização no cabeçalho

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | sincronizacao |
| **Registrado** | 2026-10-13, commit `0ebeddd` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | SyncIndicatorViewModel, bolinha, SyncIndicatorClassifier, CycleStarted, ChangeQueued, Sincronizar agora, Offline |
| **Relações** | relacionado: [ADR-034](034-bolinha-worktree-e-pergunta-ao-concluir.md), [ADR-061](061-sincronizacao-outbox-mescla-por-campo.md), [ADR-067](067-celular-dispositivo-da-sincronizacao.md), [ADR-075](075-celular-temas-concluir-e-teclado.md), [ADR-085](085-agente-sem-tela-sobre-o-mesmo-sqlite.md) |
| **Código** | `src/MyTaskApp.Desktop/ViewModels/SyncIndicatorViewModel.cs`, `src/MyTaskApp.Application/Sync/SyncIndicatorClassifier.cs`, `src/MyTaskApp.Application/Sync/SyncScheduler.cs` |

**Contexto:** a sincronização (ADR-061) roda em segundo plano, e só a janela
*☰ → Sincronização…* mostrava o estado dela. Quem acabou de editar não sabia
se a mudança já tinha subido, se havia algo esperando ou se o app estava sem
conexão. O pedido: um indicador discreto, sempre à vista, que deixe isso claro.

**Decisão:** uma bolinha de 8px no cabeçalho, entre o título e o histórico,
sem texto e sem ocupar altura. Por isso ela aparece também no modo recolhido
e no HUD, menos no HUD compacto, que já tira o histórico pela largura. O
tooltip diz o estado por extenso. O clique abre um painel curto com
"Sincronizar agora" e "Abrir sincronização…" (ou "Conectar…", "Entrar de
novo…").

## Alternativas

- **Rodapé com bolinha e texto**, como a barra de status do VS Code. É o mais
  explícito, mas custa uma linha de altura num widget de 360×560, e o rodapé
  não existe nos modos recolhido e HUD.
- **Só na janela de sincronização** (o que havia). O estado fica a dois
  cliques, justo quando ele importa.
- **Bolinha no cabeçalho (escolhida).** Aparece em todos os modos e custa
  24px de largura.

## Estados

Em ordem de precedência: o primeiro que valer é o que aparece.

| Estado | Bolinha | Título |
|---|---|---|
| Sem servidor na build | oculta | — |
| Sem conta | anel cinza | "Só neste computador" |
| Precisa de você: login vencido, revogado, ciclo `Blocked` ou alteração recusada | vermelha cheia | "O login da sincronização expirou", "A sincronização parou", … |
| Pausada | anel cinza | "Sincronização pausada" |
| Sincronizando | azul, pulsando | "Sincronizando…" |
| Sem conexão: último ciclo `Transient`, ou erro gravado antes do primeiro ciclo | anel âmbar | "Offline" |
| Alterações esperando | âmbar cheia | "3 alterações esperando para subir" |
| Em dia, com o que revisar: conflito, comando, recebido com falha | verde com halo âmbar | "Sincronizado — 2 itens para revisar" |
| Em dia | verde cheia | "Sincronizado" |

- **A cor nunca vem sozinha**, como na bolinha do worktree (ADR-034). Cheia
  quer dizer que a sincronização anda; anel, que não anda; halo, que há algo a
  ver; o pulso, trabalho em andamento. O tooltip e o nome de acessibilidade
  repetem o estado em texto. As cores são os tokens de tema, que o teste de
  contraste já cobre.
- **Vermelho só quando precisa da pessoa.** Sem conexão não é erro: as
  alterações esperam no outbox, e o detalhe diz isso ("3 alterações guardadas
  aqui; sobem quando a conexão voltar. Tentando de novo sozinho").
- **Pausada vence "sem conexão"**, porque pausada não tenta. **Sincronizando
  vence "sem conexão"**, porque a tentativa em andamento é a notícia.
- **A hora é relativa** ("Última sincronização há 2 min"). Um relógio de 30 s
  só refaz o texto, sem ler o banco.

## Nada pisca à toa

O ciclo de rotina roda a cada minuto e costuma durar milissegundos. Pulsar a
cada minuto seria ruído. O "sincronizando" só aparece se o ciclo passar de
700 ms, ou de cara quando leva alterações daqui. O pulso sai junto com o
estado novo, sem um instante do estado velho no meio.

## De onde vem o estado

- **Reusa `GetSyncStatusHandler`**, o mesmo da janela. Não há consulta nova,
  porque a leitura só acontece em evento, nunca por polling.
- O `SyncScheduler` ganhou dois eventos:
  - `CycleStarted`, para o pulso;
  - `ChangeQueued`, quando uma gravação acorda o laço, antes da rajada. Sem
    rede, o próximo ciclo pode estar a 5 min de backoff, e a contagem de
    pendentes precisa subir na hora.
- **Desconectar e pausar também acordam o laço.** O ciclo é ocioso, sem rede,
  e o `CycleCompleted` faz a bolinha mudar na hora, e não no próximo minuto.
- O `SyncIndicatorViewModel` é singleton e entra no `TodayViewModel` por
  propriedade. Nula, a bolinha não aparece, e os testes que montam o painel
  não mudam.

## Consequências

- Um ciclo ocioso a mais ao desconectar e ao pausar. É barato: sem conta ou
  pausado, o motor volta `Idle` sem rede.
- Logo depois de abrir o app, um ciclo `Blocked` da sessão anterior aparece
  como "Offline" até o primeiro ciclo, que roda na abertura.

**Testes:**

- **Application:** o agendador avisa o começo do ciclo com `IsRunning`
  ligado, e avisa a gravação antes da rajada.
- **Desktop:**
  - a precedência, uma linha por estado;
  - os textos de cada estado;
  - o "há N min" segue o relógio sem reler o banco;
  - o ciclo de rotina não pisca, o longo pulsa depois da espera, e o que leva
    alterações pulsa de cara;
  - a bolinha e o painel desenhados no cabeçalho.
