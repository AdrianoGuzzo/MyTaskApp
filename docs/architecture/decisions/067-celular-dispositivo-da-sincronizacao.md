# ADR-067 — O celular como mais um dispositivo da sincronização (.NET MAUI)

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | mobile |
| **Registrado** | 2026-10-17, commit `ed0bfba` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | .NET MAUI, MyTaskApp.Mobile.Core, AddLocalData, AppLinkAuthorizationRedirect, ISyncAuthorizationRedirect, PlacementMatch, mytaskapp-mobile, Android |
| **Relações** | revisto por: [ADR-075](075-celular-temas-concluir-e-teclado.md), [ADR-076](076-celular-pendentes-historico-dispositivo-lembrado.md); relacionado: [ADR-059](059-servidor-mcp-local-loopback.md), [ADR-061](061-sincronizacao-outbox-mescla-por-campo.md), [ADR-062](062-lista-por-computador-onde-aparece.md), [ADR-063](063-indicador-sincronizacao-no-cabecalho.md), [ADR-070](070-servidor-sync-production-tls.md), [ADR-074](074-consentimento-authorize-e-porta-efemera.md), [ADR-078](078-servidor-de-sync-do-usuario.md) |
| **Código** | `src/MyTaskApp.Mobile/MyTaskApp.Mobile.csproj`, `src/MyTaskApp.Mobile.Core/Composition/MobileServices.cs`, `src/MyTaskApp.Infrastructure/Sync/Http/SyncAuthorizationRedirect.cs`, `src/MyTaskApp.Infrastructure/DependencyInjection.cs` |

**Contexto:** o pedido era um app para Android e iOS que mostre todas as
tarefas numa lista, crie, edite, conclua e filtre, funcione sem rede por um
tempo e sincronize com o resto. O repositório já tinha quase tudo:

- Domain, Application e Sync.Contracts são `net10.0` puro, sem UI, com as
  regras nos handlers (os mesmos que o MCP chama, ADR-059);
- o servidor de sincronização (ADR-061) com OAuth, outbox, merge por campo,
  idempotência por `OpId`, conflitos visíveis e isolamento por workspace;
- as listas por dispositivo (ADR-062).

Faltavam um projeto mobile, um login que não dependesse do loopback, um
cofre de segredos para Android e iOS, e um jeito de usar os dados e a
sincronização sem levar junto Git, terminais, Jira e PostgreSQL.

**Decisão:** o celular é **mais um dispositivo da sincronização**. Ele roda a
mesma Application sobre o mesmo SQLite (mesmo `DbContext`, mesmas migrations,
mesmo outbox) e fala só com a API de sincronização que já existe. Não há
endpoint REST de "tarefas".

## Alternativas

- **Uma API REST de tarefas no servidor**, com o celular como cliente fino e
  cache local. Exigiria rodar as regras do domínio no servidor, sobre o jsonb
  do PostgreSQL, e conviver com o merge por campo da sincronização: duas
  maneiras de gravar a mesma tarefa, uma delas sem outbox. Recusada.
- **Celular lendo o PostgreSQL direto, ou um SQLite compartilhado.** Contra o
  ADR-061 e sem isolamento por usuário. Recusada.
- **Mais um dispositivo (escolhida).** Funciona sem rede desde o primeiro
  dia, conflitos e reenvios já estão resolvidos e testados, e nenhuma regra
  é escrita duas vezes.

## Projetos

| Projeto | Alvo | Papel |
|---|---|---|
| `MyTaskApp.Mobile.Core` | `net10.0` | ViewModels (CommunityToolkit.Mvvm), apresentação, composição e as portas da plataforma |
| `MyTaskApp.Mobile` | `net10.0-android` (+ `net10.0-ios` no macOS ou com `-p:IncludeIos=true`) | Telas XAML, Shell e adaptadores MAUI |
| `MyTaskApp.Mobile.Core.Tests` | `net10.0` | ViewModels sobre a pilha de verdade com SQLite temporário |

O núcleo fica sem MAUI para os testes rodarem no CI do desktop sem workload;
um teste de arquitetura garante isso. `Mobile.Core` e seus testes estão no
`MyTaskApp.slnx` (e no portão de cobertura). A casca MAUI tem a solução
própria, `MyTaskApp.Mobile.slnx`.

## O que mudou nos projetos existentes

Tudo aditivo; o desktop se comporta como antes.

- **`AddLocalData`** (Infrastructure): os dados e a sincronização (SQLite,
  repositórios, consultas, captura, aplicação, cliente HTTP) saíram de dentro
  do `AddInfrastructure`, que agora o chama e completa com o que só o
  computador tem.
- **Login sem loopback.** `ISyncAuthorizationRedirect` diz por onde o login
  volta e com qual cliente. Sem registro, vale o loopback do desktop
  (`mytaskapp-desktop`). O celular registra o `AppLinkAuthorizationRedirect`
  (`mytaskapp-mobile`, volta por `io.github.adrianoguzzo.mytaskapp://oauth/callback`),
  que recebe a volta entregue pelo `WebAuthenticator` (Custom Tabs no
  Android, `ASWebAuthenticationSession` no iOS). PKCE, troca do código, cofre
  do refresh e renovação continuam sendo do `SyncAuthenticationService`.
- **Servidor:** `PublicClientSeeder` semeia os dois clientes públicos, cada um
  só com os seus redirects (`Sync:MobileRedirectUris`). Um código emitido
  para um cliente não vira token no outro.
- **`ISecretStore` público**, para o app entregar o cofre da plataforma
  (`SecureStorage`). As implementações do desktop continuam internas, e não
  há `InternalsVisibleTo` para o mobile.
- **`SyncScheduler.StopAsync`:** o laço para em segundo plano e volta com
  `Start`, sem nunca rodar dois ciclos juntos.
- **`PlacementMatch`:** o filtro de origem exata na busca (abaixo).
- **`SyncIndicatorClassifier`:** a precedência do indicador (ADR-063) saiu do
  ViewModel do desktop para a Application. O celular decide do mesmo jeito.
- `SyncEngine.Hello` identifica Android e iOS; `DeviceIdentityFile` aceita o
  nome do aparelho como apelido.

## Origem, projeto e etiquetas

- **Origem = o "onde aparece" do ADR-062:** compartilhada, só neste celular,
  ou só num computador da conta. Não há entidade de origem, e não foi criada
  uma: contextos como Trabalho e Estudos continuam sendo etiquetas.
- **A lista do celular é de todos os dispositivos** (`DeviceView.All`). O
  filtro de origem é **exato**: "Notebook" é só o que está no notebook, sem
  as compartilhadas. Isso é o `PlacementMatch`, diferente do `PlacementFilter`,
  que responde "o que aparece num dispositivo". O legado sem lugar conta como
  compartilhada. (O ADR-076 levou o filtro para chips no topo, lembrados entre
  aberturas, e separou pendentes e Histórico.)
- **Tarefa nova no celular nasce compartilhada.** No desktop o padrão é "este
  computador"; uma tarefa só no celular sumiria da visão padrão do desktop.
- **Projeto não existe** no domínio. Não foi inventado: o detalhe mostra o
  desenvolvimento (repositórios e worktrees) em modo somente leitura, e
  "projeto" fica como decisão em aberto.
- As etiquetas são as de sempre; o celular escolhe entre as existentes e não
  tem cadastro próprio.

## Telas

- **Tarefas:** busca com espera de 300 ms; atalhos Todas, Pendentes,
  Vencidas, Concluídas e Canceladas; folha de filtros (prioridade, etiquetas,
  origem, ordem); páginas de 50; puxar para atualizar pede um ciclo; concluir
  e reabrir pela linha; estados de carregando, vazio, sem resultados e erro.
- **Detalhe:** tudo o que a busca sabe, a procedência (criada e alterada em
  qual dispositivo), o histórico local e o desenvolvimento. As ações aparecem
  conforme a regra permite; quem decide é o caso de uso.
- **Formulário:** criar e editar. Na edição só vai o que mudou. O status
  passa pelas transições do domínio: reabrir vem antes de editar, porque o
  prazo só muda em pendente, e concluir ou cancelar vem depois. Sair com
  alterações pergunta antes.
- **Conta:** estado da sincronização, conflitos ("Reaplicar o meu" ou
  "Manter o do servidor"), recusadas (tentar de novo ou descartar),
  dispositivos, pausar e sair.
- Tema claro e escuro pelo sistema, com as cores do desktop (Carvão e o claro
  índigo). O ADR-075 trouxe os sete temas do desktop, com escolha na aba Conta.

## Consequências

- O celular recebe **todos** os dados da conta, como os computadores:
  escondido não é privado (ADR-062).
- A auditoria é local e não sincroniza (ADR-061): o histórico do detalhe é o
  que aconteceu neste celular.
- A sincronização roda **só em primeiro plano**: o laço para em
  `Window.Stopped` e volta em `Resumed`, e também quando a rede volta. Sem
  sincronização em segundo plano.
- **Sem lembretes nem notificações** no celular. Quem avisa são os
  computadores que mostram a tarefa.
- O servidor valida campo, tamanho, referência e posse, mas não roda as
  regras de estado (ADR-061). Um cliente adulterado poderia gravar uma
  transição que a Application recusaria. Num workspace pessoal, o risco é da
  própria pessoa.
- Desktop chama o celular de "computador" em "Enviar para…" e na
  procedência. Ficou assim.
- **Backup do sistema desligado** para os dados do app (`allowBackup=false`
  no Android, `NSUrlIsExcludedFromBackupKey` no iOS). Um backup restaurado
  noutro aparelho levaria o id deste dispositivo, e os dois falariam em nome
  do mesmo.
- O MAUI faz trimming parcial; o JSON por reflexão (identidade do dispositivo,
  captura) precisa de `JsonSerializerIsReflectionEnabledByDefault=true`. No
  iOS, o EF Core exige o interpretador (`UseInterpreter`).
- **iOS configurado, não validado:** não houve Mac neste ambiente
  (docs/mobile.md).

**Testes:**

- **Mobile.Core:** composição real (cada caso de uso que as telas pedem
  monta); lista, filtros, paginação, busca com espera, estados de tela,
  concluir e reabrir; formulário (validação, edição mínima, as transições de
  status, origem, prazo recusado pelo domínio, perguntar antes de sair);
  detalhe por estado; entrar e a aba Conta; apresentação; camadas.
- **Infrastructure:** o login do celular inteiro com o cliente e o redirect
  do app, `device_revoked`, recusa, `state` errado, abandono, troca de login;
  `AddLocalData` sem nada do computador; origem exata no SQLite.
- **Application:** parar e voltar o agendador (inclusive com um ciclo preso
  na rede); a origem chegando à busca.
- **Servidor:** o cliente do celular e os redirects de cada cliente; e, de
  ponta a ponta com PostgreSQL, o celular entrando pela tela Entrar e
  sincronizando com um desktop nos dois sentidos, sem rede e de volta, e o
  filtro de origem.

## Divergências com o código (conferido em 2026-10-24)

- O ADR diz 'iOS configurado, não validado: não houve Mac neste ambiente', mas hoje o iOS compila no CI em macOS (jobs ios-simulator e ios-device em .github/workflows/mobile-ios.yml).
