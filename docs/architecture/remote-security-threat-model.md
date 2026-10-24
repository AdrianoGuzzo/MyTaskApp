# Orquestração remota — modelo de ameaças

> **Estado: proposta.** Acompanha [`remote-orchestration.md`](remote-orchestration.md)
> e os ADR-084 a ADR-090 de [índice de ADRs](README.md). Os
> requisitos (RS-xx) são critérios de aceitação das entregas em
> [`../roadmap/phase-2-remote-orchestration.md`](../roadmap/phase-2-remote-orchestration.md).

A fase 2 transforma o MyTaskApp num **executor remoto de código** na máquina do
usuário. Hoje, tudo o que roda foi pedido por alguém sentado na frente da
máquina. Este documento trata o novo caminho como o que ele é: um canal de
execução remota que precisa continuar seguro com o servidor comprometido, um
celular roubado ou um repositório malicioso.

## 1. Ativos

| Ativo | Onde | Por que importa |
|---|---|---|
| **Execução de código na máquina executora** | agente, como o usuário | quem executa, lê tudo o que o usuário lê: chaves SSH, cofres, cookies, credenciais de nuvem |
| Código-fonte e histórico Git | worktrees, repositórios | propriedade intelectual; um push indevido chega aos outros |
| Segredos do app | `ISecretStore` (DPAPI / libsecret): `sync`, `jira`, `mcp-access-token`, `postgres-*`, `device-key` | dão acesso à conta, ao Jira e aos bancos |
| Dados de produção | servidores PostgreSQL das conexões | vazamento de dado pessoal; a cópia só sai anonimizada (ADR-056/058) |
| Chaves dos dispositivos | `device-key` em cada máquina e no celular | quem assina, manda |
| Conta no servidor | OpenIddict, Google | controla a fila e a lista de dispositivos |
| Registro das operações | `RemoteOperations`, `RemoteOperationEvents` | a saída pode conter dados sensíveis |

## 2. Fronteiras de confiança

```mermaid
flowchart LR
    U(["Usuário"]) --> C["Controlador<br/>(celular / desktop)"]
    C -->|"F1: internet, TLS"| S["Servidor do usuário"]
    S -->|"F2: internet, TLS, conexão aberta pelo agente"| A["Agente"]
    A -->|"F3: processo filho"| P["git / shell / pg_dump"]
    A -->|"F4: processo filho com IA"| CL["claude -p"]
    CL -->|"F5: loopback"| M["MCP de aprovação"]
    A -->|"F6: rede"| DB[("PostgreSQL das conexões")]
    R[("Repositório<br/>conteúdo não confiável")] --> CL
    R --> P
```

- **F1/F2**: o servidor fica no meio, mas **não é confiável para autorizar
  execução** (ADR-087). Ele é confiável só para disponibilidade e entrega.
- **F3/F4**: o processo filho tem os poderes do usuário. O worktree **não é
  sandbox**.
- **Repositório**: o conteúdo de um repositório (arquivos, `CLAUDE.md`,
  `.claude/settings.json`, scripts de build, hooks do Git) é **dado não
  confiável**, inclusive para quem tem a conta.

## 3. Atacantes considerados

| # | Atacante | Capacidade |
|---|---|---|
| T1 | Alguém na rede | lê e altera tráfego sem TLS; repete pacotes |
| T2 | **Servidor comprometido** (máquina invadida, backup vazado, admin malicioso) | lê e escreve o PostgreSQL, emite tokens, manda mensagens pelo hub |
| T3 | **Conta Google comprometida** | faz login, cria um dispositivo novo |
| T4 | **Celular roubado ou desbloqueado** | usa o app já logado |
| T5 | **Repositório ou dado malicioso** (prompt injection, `settings.json` do projeto, script de build) | influencia o Claude e os comandos |
| T6 | Processo local de outro usuário | tenta falar com o pipe, o MCP loopback ou ler o diário |
| T7 | O próprio usuário, por engano | aprova sem ler, deixa a máquina exposta |

## 4. Ameaças (STRIDE por fronteira)

| Id | Fronteira | Categoria | Ameaça | Mitigação | Requisitos |
|---|---|---|---|---|---|
| A-01 | F1/F2 | Spoofing | Servidor falso ou TLS interceptado | HTTPS/WSS obrigatório; issuer https (ADR-070); sem "aceitar certificado inválido" | RS-01, RS-02 |
| A-02 | F1 | Spoofing | Conta comprometida (T3) cria um controlador e manda operações | o controlador novo **não é confiável** para nenhum agente até ser pareado com código confirmado na máquina executora | RS-07, RS-08 |
| A-03 | F2 | Spoofing / Elevation | Servidor comprometido (T2) fabrica uma operação | o agente só aceita JWS ES256 de chave em `TrustedControllers`, gravada localmente; o servidor não tem nenhuma chave privada | RS-09, RS-10 |
| A-04 | F2 | Spoofing | Servidor comprometido troca a chave no pareamento | código curto derivado das duas impressões digitais e de um nonce, lido no controlador e digitado na máquina executora | RS-08 |
| A-05 | F1/F2 | Tampering | Alterar o payload de uma operação assinada | a assinatura cobre os bytes; o agente lê o que foi assinado, não uma cópia do servidor | RS-09 |
| A-06 | F2 | Repudiation / Replay | Reenviar uma operação antiga (rodar o deploy de novo) | `OperationId` único no diário (30 dias), `NotAfter`, `TargetDeviceId` e `WorkspaceId` dentro da assinatura | RS-11, RS-12 |
| A-07 | F2 | Replay | Reaproveitar uma aprovação antiga em outra operação | `ApprovalResponse` assinada com `ApprovalId`, `OperationId` e `RequestHash`; uso único; validade de 10 min | RS-13 |
| A-08 | F3 | Elevation | Injetar shell por parâmetro (`; rm -rf ~`) | só comando cadastrado, por id; valores remotos passam por `UnsafeCharacter` (o mesmo filtro dos valores de contexto) e por tamanho | RS-14, RS-15, RS-16 |
| A-09 | F3 | Elevation | Mudar o texto de um comando por sync e rodá-lo de longe (T2/T3) | `ICommandReviewGate` continua valendo; `RemoteAllowed` é coluna só local e **cai** quando o texto muda | RS-14, RS-17 |
| A-10 | F3 | Tampering | Diretório fora do worktree (`..`, caminho absoluto, link simbólico) | só ids; o caminho é resolvido no agente; `CommandWorkingDirectory.Normalize` já recusa `..` e absolutos; conferir que o resolvido fica sob o worktree | RS-18 |
| A-11 | F4 | Elevation | Prompt injection faz o Claude rodar `curl … \| sh` ou ler `~/.ssh` | perfil local com allowlist; Bash fora da lista pede aprovação humana; `permissions.deny` para caminhos sensíveis; ambiente sem segredos | RS-19 a RS-23 |
| A-12 | F4 | Elevation | `.claude/settings.json` do repositório libera ferramentas ou registra **hooks** (execução sem passar por permissão) | `--restricted` ignora os arquivos de configuração do usuário, do projeto e locais (conferido no 2.1.309); as regras do agente vão por `--settings` | RS-21 |
| A-13 | F4 | Elevation | Sessão remota herda `--dangerously-skip-permissions` do card (ADR-033) | o modo headless não lê os `DefaultArguments` do card; o agente recusa `bypassPermissions` e o flag em qualquer origem; o `--restricted` os recusa de novo, no próprio Claude | RS-19 |
| A-14 | F3/F4 | Elevation | Hook do Git (`post-checkout`) ou script de build malicioso no repositório | **fora do alcance do app**: é o mesmo risco de rodar o build localmente. Mitigação: só repositórios dos diretórios da etiqueta (cadastrados aqui) | RS-18, risco residual R-02 |
| A-15 | F5/T6 | Spoofing | Outro processo local chama o MCP de aprovação e se autoaprova | o MCP de aprovação só **pergunta** ao humano, não decide; loopback + `Host` + sem `Origin` + segredo por sessão (o padrão do `McpSecurityMiddleware`) | RS-24 |
| A-16 | IPC/T6 | Elevation | Outro usuário fala com o pipe do agente | `PipeOptions.CurrentUserOnly`; o pipe não aceita operação nem aprovação de controlador, só "dados mudaram", "sincronizar agora" e o pareamento local | RS-25 |
| A-17 | F6 | Information disclosure | Cópia de produção sem anonimizar pedida de longe | a mesma `DatabaseSecurityPolicy`, `PostgresProcessGuard` e PostgreSQL só leitura; produção pede aprovação com nome digitado; `RemoteAllowed` por conexão | RS-26, RS-27 |
| A-18 | F1/F2 | Information disclosure | Senha, token ou caminho no catálogo, nos eventos ou no log | catálogo só com ids e nomes; saída por `SensitiveText.Mask`; teste de reflexão como o `ContractSafetyTests` do MCP | RS-28, RS-29 |
| A-19 | F1 | Information disclosure | Access token na query string do WebSocket aparece em log | o `RequestLog` não registra a query de `/hubs/*`; o proxy também não | RS-03 |
| A-20 | F1/F2 | Denial of service | Inundar a fila ou o hub | rate limit `remote` por conta e por dispositivo; teto de operações abertas por executor; teto de eventos e bytes por operação | RS-30, RS-31 |
| A-21 | F3 | Denial of service | Processo que não termina, ou fork bomb | timeout por operação; Job Object (Windows) / grupo de processos (Linux); matar a árvore | RS-32 |
| A-22 | T4 | Elevation | Celular roubado aprova e manda operações | revogar o dispositivo de qualquer outro remove a chave dele de todos os agentes; destravar o app (biometria/PIN do sistema) antes de assinar | RS-33, RS-34 |
| A-23 | T7 | — | Aprovação por cansaço (aprovar tudo) | resumo legível e completo; "lembrar nesta sessão" só para a mesma ferramenta e padrão; produção exige digitar; nenhuma aprovação de "tudo" | RS-13, RS-35 |
| A-24 | F2 | Repudiation | Não saber quem mandou rodar o quê | auditoria nos dois lados, com o dispositivo que assinou; a assinatura guardada prova a origem | RS-36 |
| A-25 | Diário/T6 | Information disclosure | Saída sensível guardada no SQLite do agente | diário com teto e retenção (7 dias); mascarado antes de gravar | RS-29 |

## 5. Requisitos

### Transporte

- **RS-01** HTTPS e WSS obrigatórios fora de Development; o agente recusa `http://`
  e `ws://` com servidor que não seja loopback (o `DevelopmentLoopbackGuard`
  já faz isso no servidor).
- **RS-02** Validação de certificado padrão do sistema, sem opção de ignorar.
- **RS-03** O access token nunca aparece em log: query de `/hubs/*` fora do
  `RequestLog` e fora do log do proxy.

### Credenciais por dispositivo, rotação e revogação

- **RS-04** Cada instalação tem **uma** identidade (`device_id`) e **uma** chave
  ES256 gerada localmente; a privada fica no `ISecretStore` (`"device-key"`) e
  nunca sai da máquina.
- **RS-05** Access token de 15 min e refresh com rotação, como hoje; o refresh
  roda sob trava entre processos (desktop e agente), relendo o cofre.
- **RS-06** Revogar um dispositivo (ADR-061) também revoga a chave em
  `DeviceKeys`; o servidor avisa os agentes, que a removem de
  `TrustedControllers`; um agente que não recebe o aviso continua protegido,
  porque o servidor recusa o token do revogado (403 `device_revoked`).

### Autenticação, autorização e pareamento

- **RS-07** Escopo OAuth novo `remote`; endpoints remotos exigem `sync` + `remote`
  e dispositivo ativo (`DeviceFilter`).
- **RS-08** Pareamento com aprovação explícita **na máquina executora**, com o
  código curto derivado das duas impressões digitais. Sem tela, pelo CLI do
  agente. O pareamento expira em 10 min.
- **RS-09** Toda operação, cancelamento e resposta de aprovação é um JWS ES256
  conferido pelo agente contra `TrustedControllers`. O servidor confere também,
  mas não decide sozinho.
- **RS-10** "Aceitar controle remoto" é ligado **só localmente**, vem
  desligado e não sincroniza.

### Replay e duplicação

- **RS-11** `OperationId` (UUIDv7) único no diário por 30 dias; o servidor
  recusa o mesmo id com outro conteúdo.
- **RS-12** `TargetDeviceId`, `WorkspaceId` e `NotAfter` (≤ 24 h) dentro da
  assinatura.
- **RS-13** Aprovação amarrada a `ApprovalId` + `OperationId` + `RequestHash`, de
  uso único e com validade; sem resposta, nega.

### Allowlist e validação

- **RS-14** Catálogo fechado de tipos de operação; nenhum campo do pedido é
  texto de shell, SQL ou argumento de linha de comando.
- **RS-15** `command.run` só por id de comando com `RemoteAllowed` nesta
  máquina; `RequiresConfirmation` vira aprovação.
- **RS-16** Valores de parâmetro vindos de fora passam por
  `CommandVariables.UnsafeCharacter` e por um teto de tamanho, como os valores
  de contexto.
- **RS-17** Alterar o texto de um comando (local ou por sync) desliga o
  `RemoteAllowed`; o `ICommandReviewGate` continua valendo.
- **RS-18** Diretório de trabalho só relativo ao worktree, resolvido no agente,
  sem `..`, sem absoluto e sem atravessar link simbólico para fora; repositório
  só de diretório de etiqueta mapeado aqui.

### Claude Code

- **RS-19** Nunca `--dangerously-skip-permissions` nem `--permission-mode
  bypassPermissions` numa sessão remota, venha de onde vier; os argumentos do
  card (ADR-033) não entram no modo headless. A sessão sempre roda com
  `--restricted`, que no Claude Code 2.1.309 recusa os dois já na leitura dos
  argumentos: é uma segunda trava, independente do código do agente.
- **RS-20** O perfil de permissões é escolhido e guardado **na máquina
  executora** (`--allowedTools`, `--disallowedTools`, `permissions.deny`); o
  controlador só escolhe entre os perfis do catálogo.
- **RS-21** Sem configurações nem hooks do projeto na sessão remota. O
  `--restricted` ignora os arquivos de configuração do usuário, do projeto e
  locais; as regras do agente entram só por `--settings`. O `--mcp-config` leva
  só o MCP de aprovação (`--strict-mcp-config`).
- **RS-22** Ferramenta fora do perfil vai para `--permission-prompt-tool`, que
  vira `ApprovalRequest`; sem resposta, nega. Um perfil sem canal de aprovação
  usa `--permission-prompts none`, que nega tudo o que perguntaria.
- **RS-22a** Cada prompt tem teto de turnos (`--max-turns`) e de custo
  (`--max-budget-usd`), definidos na máquina executora.
- **RS-23** Ambiente do processo sem segredos: fora `MYTASKAPP_*` (exceto o
  segredo da própria sessão), `PG*`, `GH_TOKEN`, `GITHUB_TOKEN` e afins; o
  prompt vai pelo stdin, e não pela linha de comando.

### Processos e IPC

- **RS-24** MCP de aprovação: loopback, `Host` conferido, `Origin` recusado,
  segredo por sessão (só o hash guardado), que morre com o processo.
- **RS-25** Pipe local só do usuário (`CurrentUserOnly`), sem nenhuma
  capacidade de execução.

### Banco de dados

- **RS-26** A cópia remota passa por `RunDatabaseCopyHandler` com a mesma
  política, as mesmas etapas e a mesma auditoria; nenhuma via paralela.
- **RS-27** Conexão só no catálogo com `RemoteAllowed` local; origem de produção
  pede `ApprovalKind.ProductionDatabase`, com o nome digitado quando
  `CriticalProduction`; nenhuma operação remota cria, edita ou lê conexão ou
  senha.

### Sigilo

- **RS-28** Catálogo, eventos e resultados sem caminho absoluto, host, usuário,
  senha, token ou texto de comando; um teste de reflexão sobre os contratos.
- **RS-29** Toda saída passa por `SensitiveText.Mask` antes do diário e do
  servidor; o diário tem teto e retenção de 7 dias; o servidor guarda no máximo
  1 MiB de eventos por operação e 30 dias.

### Limites

- **RS-30** Rate limit `remote` por conta (separado de `sync`, ADR-069) e por
  dispositivo controlador.
- **RS-31** Por executor: operações simultâneas (padrão 2, uma "pesada" — cópia
  de banco ou sessão do Claude), fila máxima de 20 abertas.
- **RS-32** Timeout por operação e morte da árvore de processos, inclusive
  quando o agente cai.

### Controlador

- **RS-33** Assinar exige destravar o dispositivo (biometria/PIN do sistema no
  celular; no desktop, a sessão do usuário).
- **RS-34** Revogar de qualquer dispositivo confiável funciona mesmo com o
  revogado desligado.
- **RS-35** A tela de aprovação mostra o pedido inteiro (ferramenta, comando
  completo, diretório), não só um resumo.

### Auditoria

- **RS-36** Toda operação deixa rastro: no servidor (pedido assinado, estados,
  quem), no agente (`ITaskAuditLog` com a origem `"Remoto: <dispositivo>"`) e,
  no banco, a `DatabaseOperationAudit` de sempre.

## 6. O que fica igual nas operações de banco

Nenhuma barreira do ADR-056 é afrouxada:

- o teto e o piso de cada ambiente (`EnvironmentPolicy`);
- `Evaluate` antes e `Demand` em cada etapa (`DatabaseCopyRun`);
- `PostgresProcessGuard` nos argumentos reais;
- a sessão só leitura no PostgreSQL de origem;
- a senha só por `PGPASSWORD`, lida no executor;
- a auditoria gravada antes do primeiro processo.

O que muda é **quem confirma**: em vez do diálogo da tela, uma
`ApprovalResponse` assinada, com o nome digitado quando a política exige.

## 7. Riscos residuais

| Id | Risco | Por que fica | Como reduzir depois |
|---|---|---|---|
| R-01 | O Claude com Bash aprovado roda **como o usuário** e alcança tudo | worktree não é sandbox; o SO não isola por pasta | sandbox do SO (contêiner, AppContainer, bubblewrap) como perfil opcional |
| R-02 | Build e hooks do Git do repositório rodam código | é o mesmo risco de rodar o build localmente | só repositórios cadastrados; documentar |
| R-03 | Prompt injection (inclusive pelo `CLAUDE.md` do repositório) convence o usuário a aprovar | o humano é a última barreira | resumo completo, nenhum "aprovar tudo"; um perfil "endurecido" com `--safe-mode` (sem `CLAUDE.md`, skills, plugins nem hooks) para repositórios de terceiros |
| R-04 | Servidor comprometido vê saídas e nomes | o servidor guarda os eventos para quem não estava conectado | cifrar ponta a ponta os eventos (chave do controlador) numa versão futura |
| R-05 | Celular desbloqueado nas mãos de outro | a chave está no aparelho | biometria antes de assinar; revogação rápida |
| R-06 | O agente roda com a sessão do usuário aberta | DPAPI e libsecret são do usuário | cofre de máquina, se um dia rodar sem logon |
| R-07 | Disponibilidade depende do servidor do usuário | é o desenho do ADR-078 | nada: sem servidor, nada remoto, e o local segue inteiro |
