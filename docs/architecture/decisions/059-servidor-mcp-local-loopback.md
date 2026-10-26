# ADR-059 — Servidor MCP local: Kestrel só no loopback, e os casos de uso da tela

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | integracoes |
| **Registrado** | 2026-10-10, commit `90da189` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | MCP, McpGateway, McpSecurityMiddleware, McpServerManager, Kestrel, token Bearer, somente leitura, OperationOrigin |
| **Relações** | exceção aberta por: [ADR-081](081-consulta-inteligente-somente-leitura.md); relacionado: [ADR-012](012-iusecaserunner-um-escopo-por-operacao.md), [ADR-015](015-agendador-com-timeprovider-createtimer.md), [ADR-037](037-acompanhar-claude-code-pelos-hooks.md), [ADR-052](052-tempo-trabalhado-por-periodo.md), [ADR-060](060-banco-do-ambiente-variavel-database.md), [ADR-067](067-celular-dispositivo-da-sincronizacao.md), [ADR-080](080-linhas-sem-mascara-condicao-sql.md), [ADR-085](085-agente-sem-tela-sobre-o-mesmo-sqlite.md) |
| **Código** | `src/MyTaskApp.Mcp/Hosting/McpServerManager.cs`, `src/MyTaskApp.Mcp/Hosting/McpGateway.cs`, `src/MyTaskApp.Mcp/Hosting/McpSecurityMiddleware.cs`, `src/MyTaskApp.Application/Mcp/McpServerSettings.cs` |

**Contexto:** o usuário quer que um cliente de IA (o Claude Code) opere o app
pelo [MCP](https://modelcontextprotocol.io): tarefas, cronômetro e horas,
conexões de banco e, com prioridade, os perfis de anonimização. O guia de uso,
com todas as ferramentas, está em [`mcp-server.md`](../../mcp-server.md).

**Decisão:** um servidor MCP **dentro do processo do app**, com o SDK oficial
(`ModelContextProtocol.AspNetCore` 2.2, spec 2026-07-28), transporte
Streamable HTTP **sem sessão**, em `http://127.0.0.1:{porta}/mcp`, sempre com
token. As ferramentas chamam os mesmos casos de uso que a tela chama.

## Por que Kestrel agora, se o ADR-037 o recusou

O ADR-037 escreveu um HTTP mínimo sobre `TcpListener` porque o Kestrel
"traria o ASP.NET inteiro para um app de bandeja". Lá era um endpoint de uma
rota, que só recebe avisos. O MCP é um protocolo inteiro:

- JSON-RPC e negociação de versão;
- Streamable HTTP com SSE e cabeçalhos padronizados;
- uma spec que mudou três vezes em um ano.

Escrever isso à mão seria manter um segundo SDK. O oficial roda sobre ASP.NET
Core. O custo é o `FrameworkReference Microsoft.AspNetCore.App` no projeto
`MyTaskApp.Mcp` e um instalador maior, porque a publicação é self-contained e
sem trimming.

O ADR-037 **não muda**: a porta dos hooks continua no `TcpListener`.

## Projeto próprio, e o contêiner do app

- `src/MyTaskApp.Mcp` conhece a Application e o Domain, e nada do Desktop. O
  Desktop o referencia e só liga, desliga e mostra.
- O Kestrel tem o próprio contêiner, montado com
  `WebApplication.CreateEmptyBuilder`. Não lê `appsettings`, variáveis
  `ASPNETCORE_*` nem URLs de fora: o único endereço é o
  `Listen(IPAddress.Loopback, porta)` do código.
- Não existe configuração de "escutar na rede", e é de propósito: seria a
  configuração acidental que o pedido proíbe.
- Esse contêiner **não cria casos de uso**. Recebe do app, como instâncias, o
  `McpGateway` (que tem o `IUseCaseRunner`), o relógio e o log. Cada
  ferramenta roda no escopo do app, com o `DbContext` do app, como um clique
  na tela (ADR-012). Não há segundo `DbContext` concorrente.

## O gateway, e nenhuma regra no MCP

Toda ferramenta passa pelo `McpGateway`, que não tem regra de negócio. Ele:

- recusa escrita no modo **somente leitura**;
- serializa as chamadas MCP (`SemaphoreSlim(1)`), porque o SQLite do app não
  tem WAL e a tela também grava. Correção posterior: o banco sempre esteve em
  WAL, porque o EF Core cria o arquivo assim (o `-wal` existe ao lado do
  `mytaskapp.db` de quem usa o app). A abertura agora também o garante
  (`DatabaseInitializer`), para um arquivo que tenha chegado em outro modo. A
  serialização fica: o WAL deixa ler durante uma gravação, mas continua
  aceitando um escritor por vez;
- liga `OperationOrigin.Enter("MCP")`, um `AsyncLocal` que o `ICurrentUser`
  lê. Assim a auditoria grava "adria (MCP)" sem mudar a assinatura de nenhum
  caso de uso;
- traduz `DomainException` na frase da regra (erro da ferramenta), e qualquer
  outra exceção num código curto, com a pilha só no log;
- avisa o `IDataChangeNotifier` depois de gravar. O `App` recarrega o quadro, a
  aba "Tempo" das janelas abertas e a janela de Bancos, e o que a IA cria
  aparece na hora, sem esperar o tique de 60 s.

## O que faltava, e entrou na camada certa

O que não existia virou caso de uso na Application, sem lógica paralela:

- **Busca de tarefas** (`SearchTasksHandler` + `TaskSearchQuery`). O quadro
  Hoje não lista pendentes sem data nem futuras. A linha devolvida é a mesma
  do quadro, montada pelo `TodayQuery.DescribeAsync` e pelo
  `GetTodayBoardHandler.Describe`, e o atraso usa o mesmo `TodayClassifier`.
- **Edição em lote** (`EditTaskHandler`, `CreateDetailedTaskHandler`). A tela
  edita cada coisa no seu card; a IA manda tudo junto. O handler chama os
  casos de uso da tela (`UpdateTask`, `UpdateTaskPlan`, `SetDeadline`…)
  dentro de `IUnitOfWork.ExecuteInTransactionAsync`, então um prazo recusado
  desfaz o título já trocado. A transação tem uma implementação padrão que só
  executa, para os dublês de teste continuarem valendo.
- **Relatórios de horas** (`TimeReportHandlers` + `TimeEntryReportQuery`): por
  intervalo de dias locais, tarefa, etiqueta, issue e origem, com recorte na
  meia-noite do usuário e os ids de cada total.
- **Perfis de anonimização por partes** (`ChangeAnonymizationProfileHandler`):
  - incluir, trocar ou remover uma regra preserva tudo o que não foi citado;
  - a proposta passa pelas validações do domínio num rascunho, e o handler
    devolve o diff;
  - `Apply=false` é a pré-visualização. Para gravar, `ExpectedUpdatedAt` é
    obrigatório e recusa uma leitura velha;
  - a validação contra o banco é o mesmo `MaskingPlanner` da cópia, em sessão
    só leitura;
  - a análise separa o comprovado, o risco estático e o que falta saber, e
    nunca afirma conformidade com a LGPD.
- **Correção encontrada no caminho:** nome repetido de perfil de anonimização
  estourava como `DbUpdateException`, pelo índice NOCASE. Agora é uma
  `DomainException` com a frase, na tela e no MCP.

## Segurança

HTTP no loopback não é autenticação: qualquer processo e qualquer página do
navegador alcançam `127.0.0.1`. São quatro barreiras antes do SDK, na
`McpSecurityMiddleware`:

1. a conexão vem do loopback;
2. o `Host` é `127.0.0.1` ou `localhost` na porta do servidor, contra DNS
   rebinding;
3. qualquer `Origin` é recusado, e não há CORS;
4. o `Authorization: Bearer` traz o token. São 256 bits, guardados no cofre do
   sistema e comparados pelo hash em tempo constante, como no ADR-037.

O corpo da requisição vai até 1 MB. Senhas não entram nem saem: criar e editar
uma conexão pelo MCP não aceita senha, e a resposta diz só `hasPassword`. Um
teste varre por reflexão tudo o que as ferramentas devolvem, atrás de
propriedade com cara de segredo.

A revisão achou mais quatro pontos, e todos entraram:

- **A senha segue a conexão.** O `SaveDatabaseConnection` mantém a senha ao
  editar, e o teste de conexão a usa. Trocar o host pelo MCP e testar mandaria
  a senha para outro servidor. Por isso, numa conexão com senha guardada,
  servidor, porta, usuário e SSL só mudam pela tela, e o ambiente só fica igual
  ou mais restrito.
- **Porta exclusiva.** O socket do Kestrel é criado com `SO_EXCLUSIVEADDRUSE`
  no Windows, para nenhum processo escutar junto com `SO_REUSEADDR`.
- **Enxurrada.** São até 64 conexões, e as recusas entram no log e na tela no
  máximo uma vez por segundo.
- **Prazo e tela.** O `AlertPresenter.DismissAsync` não espera a thread da tela
  quando é chamado de fora dela. Antes, o MCP segurava a transação do SQLite
  esperando a tela, enquanto a tela esperava o banco.

## Ciclo de vida

- O `McpServerManager` é singleton do app, com a máquina de estados
  Parado → Iniciando → Ativo | Erro → Encerrando.
- Um `SemaphoreSlim` impede start e stop concorrentes. A tela mostra o estado
  real, e não a configuração.
- Sobe fora da thread da tela. Junto com o app, só se estiver habilitado e com
  "iniciar com o app".
- Parar é gracioso por até 3 s. O `Dispose` síncrono espera esse limite,
  porque o contêiner do app descarta de forma síncrona (o mesmo cuidado dos
  agendadores, ADR-015).
- A configuração (habilitado, porta 5180, iniciar com o app, somente leitura) é
  dado do usuário e mora numa linha única no banco (migration
  `McpServerSettings`, só aditiva). Instalação nova e atualização chegam
  **desligadas**.

## Fora de escopo, de propósito

- **Executar comandos, SQL, cópia ou restore pelo MCP.** Os comandos
  cadastrados só se listam. Executar processos reais pede uma política de
  confirmação própria, que fica para outra decisão. A única exceção é a
  leitura do banco numa sessão da Consulta Inteligente, com segredo próprio e
  um usuário que só lê (ADR-081).
- **Senha pela conversa.** Ela ficaria no histórico do cliente de IA.
- **Exclusão definitiva.** `task_delete` manda para a lixeira.
- **stdio, SSE legado, sessões e notificações do servidor.** O modo stateless
  basta para ferramentas, e parar não deixa nada pendurado.
- **Escrita em etiquetas, diretórios, comandos, post-its e lembretes
  globais.** Fica na tela até haver pedido.

**Testes:**

- **`MyTaskApp.Mcp.Tests`:** o Kestrel de verdade numa porta livre, o cliente
  oficial, e a Application e a Infrastructure reais sobre SQLite temporário.
  Cobre o ciclo de vida, a porta, as quatro barreiras, o protocolo e cada área
  de ferramentas, inclusive a ausência de segredo nas respostas e nos logs.
- **Application:** configuração, token, origem, relatórios e edição de perfis,
  com fakes.
- **Infrastructure:** store, upgrade a partir de `SkippedTables`, transação,
  busca e relatório.
- **Desktop:** a tela com gerente falso, a janela headless, a bandeja e o
  composition root real.
