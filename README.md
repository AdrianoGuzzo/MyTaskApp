# MyTaskApp

[![CI](https://github.com/AdrianoGuzzo/MyTaskApp/actions/workflows/ci.yml/badge.svg)](https://github.com/AdrianoGuzzo/MyTaskApp/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/AdrianoGuzzo/MyTaskApp)](https://github.com/AdrianoGuzzo/MyTaskApp/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**Widget de tarefas que não deixa você esquecer.**

O MyTaskApp é um painel pequeno de checklist que fica na área de trabalho, mora
na bandeja do sistema e insiste até você dar atenção. Registrar uma tarefa custa
uma linha de texto. A partir daí, o app se encarrega de lembrar. Para quem
desenvolve, a tarefa também pode virar um **worktree Git** com um **Claude Code**
trabalhando nele, e o painel avisa quando o agente precisa de você.

Aplicativo desktop em **.NET 10** + **Avalonia 12**, com SQLite local. Feito
primeiro para Windows. Também roda no Linux, com algumas funções a menos.

---

## Sumário

- [Funcionalidades](#funcionalidades)
- [Instalação](#instalação)
- [Onde ficam os dados](#onde-ficam-os-dados)
- [Configuração](#configuração)
- [Desenvolvimento](#desenvolvimento)
- [Arquitetura](#arquitetura)
- [Estrutura do repositório](#estrutura-do-repositório)
- [Contribuindo](#contribuindo)
- [Releases e versionamento](#releases-e-versionamento)
- [Documentação](#documentação)
- [Licença](#licença)

---

## Funcionalidades

### Checklist do dia

- **Captura rápida.** A tela abre com uma caixa já focada. Cada linha vira uma
  tarefa para hoje. `Enter` registra a lista inteira, `Shift+Enter` quebra a
  linha. A captura é atômica: ou entra tudo, ou nada (até 100 linhas).
- **Seções do dia.** `ATRASADAS`, `AGORA`, `HOJE`, `PRAZOS`, `SEM HORÁRIO` e `CONCLUÍDAS`.
  A janela "agora" vai de 15 min antes a 60 min depois do horário, e é
  configurável. O quadro se atualiza sozinho a cada minuto.
- **Ordem manual.** Arraste as linhas para reordenar dentro da seção.
- **Concluir e reabrir** pelo checkbox. Título e prioridade são editados na
  janela da tarefa.

### Lembretes que insistem

- Lembrete por tarefa, contado **a partir da criação** ou **antes do horário**,
  com repetição até alguém responder ("a cada 15 min até eu dar atenção").
- Escalonamento por canais: notificação, som e trazer a janela para a frente.
- No aviso: **adiar** ou **marcar como visto**. Na bandeja: **pausar os
  lembretes** por uma hora.
- Os lembretes sobrevivem ao fechamento do app: o próximo disparo fica gravado
  no banco. Ao abrir, o app avisa o que deveria ter avisado enquanto estava
  fechado.
- O padrão de lembrete é ajustado em *☰ → Configuração de lembretes…*.

### Prazos para tarefas longas

- Uma tarefa pode ter um **prazo** ("sexta às 18:00"), independente do horário
  e do lembrete. Pelo menu `⋯` da linha: Hoje, Amanhã, Final da semana, Próxima
  semana, Em 3 dias, Em 1 semana ou Personalizado; e Remover prazo.
- A linha mostra quanto falta, com a severidade escrita: "5 dias restantes",
  "ATENÇÃO · vence amanhã às 18:00", "URGENTE · vence em 1h 42min",
  "ATRASADA · há 3 horas".
- A seção `PRAZOS` junta o que tem prazo e não está marcado para uma hora de
  hoje, ordenado pelo prazo, com o resumo no cabeçalho. A tarefa só vai para
  `ATRASADAS` quando o **prazo** passa. No HUD, só o que vence em até 48 horas.
- Avisos por degrau, um de cada: por padrão na véspera, faltando 8 h e 2 h, e
  ao atrasar (repetindo uma vez por dia). Configuráveis em *☰ → Configuração de
  lembretes…*, e por tarefa (Padrão, Silencioso, Só na véspera, Personalizado).
  "Adiar 1 h" adia o aviso, não o prazo.
- Na janela da tarefa: prazo, avisos, **próxima ação** e **estimativa**.

### Tempo trabalhado

- **Cronômetro por tarefa.** O botão ▶ da linha inicia e o ⏹ para. Enquanto
  corre, a linha mostra o relógio (`00:37:42`) e fica levemente destacada.
  Há **um cronômetro por vez**: iniciar outra tarefa pergunta se é para parar a
  atual ("Parar e iniciar").
- O início fica gravado no banco desde o clique. Fechar o app, reiniciar o
  Windows ou suspender o computador não perde nada: ao voltar, o relógio
  continua contando do início.
- Concluir, cancelar, arquivar ou mandar a tarefa para a lixeira para o
  cronômetro dela.
- A aba **Tempo** da janela da tarefa mostra o registrado, o que está em
  andamento, o gasto contra a estimativa ("4h 30min / 6h · 75%") e o histórico
  por dia, com início, fim, duração, origem (Timer ou Manual) e observação.
  **+ Adicionar tempo** lança um período esquecido. Cada período pode ser
  editado ou excluído, e períodos da mesma tarefa não se sobrepõem.
- No HUD, uma faixa com a tarefa e o relógio fica visível enquanto o
  cronômetro corre.

### Histórico dos últimos 7 dias

- O botão ↺ do cabeçalho abre **Histórico · 7 dias**: hoje e os seis dias
  anteriores, cada um com o que foi **concluído** (✓, com a hora e o tempo do
  dia) e o que foi **trabalhado** sem concluir (○, com o tempo do dia).
- Uma tarefa longa aparece em cada dia em que foi trabalhada, com o tempo
  daquele dia. Um período que atravessa a meia-noite conta em cada lado dela.
- No rodapé fica o resumo da semana ("18 concluídas · 24h 35min"). O dia sem
  nada aparece como "Nenhuma atividade".
- Clicar numa linha abre a mesma janela da tarefa, já na aba **Tempo**.
- O painel abre por cima do checklist e fecha com Esc, no × ou com um clique
  fora. No HUD compacto o botão fica de fora, por falta de espaço.

### Post-its

O que ainda não merece virar tarefa: "falar com o João sobre a API", uma ideia,
uma pergunta para depois. Post-its não aparecem nas seções do checklist.

- **Ctrl+Alt+N**, de qualquer programa e mesmo com o app na bandeja, abre um
  post-it novo com o cursor no texto. Dentro do app, o atalho é
  **Ctrl+Shift+N**. Também dá para criar pelo menu *☰ → Post-its* e pela
  bandeja. O atalho global pode ser trocado ou desligado em
  *☰ → Janela e comportamento…*.
- Cada post-it é uma janela pequena e independente, que arrasta pelo
  cabeçalho e redimensiona pelas bordas. Posição, tamanho e texto são salvos
  sozinhos, sem botão Salvar.
- **Fixar na tela** (o alfinete) deixa aquele post-it sobre as outras
  janelas, sem mexer no widget nem nos outros post-its.
- No menu ⋯ ficam **Cor** (automática, sete cores ou a cor da etiqueta),
  **Etiqueta**, **Atenção** (mais cor e uma faixa lateral) e **Opacidade**. A
  cor é uma tinta sobre o cartão do tema, então o texto continua legível em
  qualquer tema, inclusive no Alto contraste.
- **Transformar em tarefa:** a primeira linha vira o título e o texto inteiro a
  anotação. A etiqueta vem junto, a tarefa entra em HOJE e o post-it vai para
  os arquivados. **Criar tarefa com a seleção** transforma só o trecho
  selecionado.
- O X (ou Esc) fecha sem excluir. *☰ → Post-its → Meus post-its…* reabre
  qualquer um, com tamanho, posição e pino. Na mesma lista ficam
  **Arquivados** e **Lixeira**, com o mesmo prazo de retenção dos checklists.
  Um post-it fechado em branco é descartado.
- Ao reabrir o app, só os post-its **fixados** voltam para a tela. Os outros
  continuam na lista.

### Um widget, não uma janela

- Painel de 360×560 sem moldura, arrastável e redimensionável, com três modos:
  **Painel completo**, **Modo compacto** e **Recolher**. A posição e o tamanho
  ficam salvos.
- **Modo HUD** (o alfinete do cabeçalho). O painel vira um cartão compacto,
  sempre visível sobre as outras janelas num canto da tela, só com o que falta
  fazer. Fora do cartão, o clique vai para o app de trás. Posição (seis cantos ou
  onde você arrastar), tamanho (Compacto, Normal, Expandido), opacidade do fundo
  e "HUD recolhido" (uma pílula que abre ao passar o mouse) ficam em
  *☰ → Janela e comportamento…*. Para voltar, clique no alfinete aceso.
- **Sempre no topo** (no menu) vale para a janela normal e não muda posição nem
  tamanho.
- **Ao fechar a janela:** fechar o aplicativo, minimizar para a bandeja (o
  padrão; os lembretes continuam rodando) ou entrar no modo HUD. Dá para
  **iniciar no modo HUD** e, no Windows, ligar **Ctrl+Shift+Espaço** para
  alternar entre janela e HUD. O atalho vem desligado porque o Visual Studio usa
  a mesma combinação.
- **Instância única.** Abrir o atalho de novo traz a janela que já existe e não
  cria um segundo processo.
- **Iniciar com o Windows** (ligado por padrão no instalador). O app sobe
  direto na bandeja. Dá para mudar em *☰ → Iniciar com o Windows*.
- **Temas:** Papel, Sépia, Carvão, Nórdico, Ameixa, Verde Pecuária e Alto
  contraste, além de **Automático**, que segue o tema claro, escuro ou de alto contraste
  do Windows.

### Anotações, etiquetas e ciclo de vida

- **Anotação em Markdown** por tarefa, com barra de formatação e os modos
  **Escrever**, **Visualizar** e **Lado a lado** (`Ctrl+Shift+V`, como no VS
  Code). A renderização segue o preview do VS Code: títulos, listas aninhadas,
  `- [ ]`, código, citação, tabela e links.
- **Correção ortográfica** com o corretor do Windows: sublinhado vermelho e
  sugestões no clique direito.
- **Etiquetas** com nome e cor, quantas quiser por tarefa. Na linha elas
  aparecem como bolinhas coloridas.
- **Diretórios da etiqueta:** um `@alias` aponta para uma pasta, que pode ter
  uma branch de origem padrão. Digitar `@` na anotação troca o alias pelo
  caminho real.
- **Repositório Git do diretório:** com o Git instalado, cole a URL do
  repositório e clique em **Clonar**. A pasta já vem sugerida, ao lado dos
  outros projetos, e o diretório é salvo junto. Numa pasta que já é
  repositório, a URL acompanha o `origin` dela
  ([ADR-064](docs/ARCHITECTURE.md)).
- **Arquivar e mover para a lixeira** pelo menu `⋯` da linha. Em
  *☰ → Gerenciamento de dados…* ficam as abas Concluídos, Arquivados e Lixeira,
  com restauração, exclusão definitiva, histórico de auditoria e a política de
  arquivamento automático. A aba Worktrees limpa os worktrees que sobraram.

### Desenvolvimento: tarefa → worktree → agente

A janela da tarefa tem a aba **Desenvolvimento**:

1. **Iniciar implementação.** Escolha o repositório (por `@alias` ou caminho),
   a branch de origem e, se quiser, uma **tag** para partir de uma versão
   antiga. O app faz o `fetch`, cria a branch (sugestão:
   `feature/{slug-do-título}`) e um **worktree** numa pasta irmã do
   repositório.
2. **Comandos pós-worktree.** Uma lista ordenada, como `dotnet restore` e
   `npm ci`, roda em sequência com o output ao vivo e para no primeiro erro.
   Os **comandos globais** (`@restore` → `dotnet restore`) ficam em
   *☰ → Comandos globais…*. Com a sincronização ligada, cada um fica só no
   computador onde foi criado, a menos que você marque **Compartilhar entre
   computadores** (ADR-066).
3. **Comandos rápidos.** Ligue um comando global a um diretório da etiqueta
   (*☰ → Etiquetas… → Comandos*) e ele vira um botão **▶** no worktree pronto
   de toda tarefa daquele repositório: "▶ Executar aplicação" abre um terminal
   no worktree rodando `dotnet run`; "▶ Testes" roda escondido e mostra o
   resultado. Variáveis como `{worktree}` e `{branch}` são preenchidas
   sozinhas, e parâmetros e confirmação são perguntados antes (ADR-051).
   Comando que só faz sentido num repositório não precisa ser global:
   **+ Novo comando** cria um comando só daquele diretório, que não aparece em
   Comandos globais nem em outros diretórios (ADR-055).
4. **Vários ambientes por tarefa**, um worktree por repositório (o app e a
   API, o front e o back).
5. **Bolinha de estado Git na linha:** âmbar quando há alteração não
   commitada, azul quando há commit sem push e verde quando tudo foi enviado.
   Ao concluir a tarefa, o app pergunta o que fazer com o worktree.
6. **PR aberta da branch.** Se a branch já existe e tem PR aberta no GitHub, o
   formulário mostra **"PR #123 aberta ↗"**, um link que abre a PR. Os balões
   da aba do repositório e da lista Hoje também mostram a PR. Usa o
   [GitHub CLI](https://cli.github.com/) (`gh`) com o seu login. Se ele faltar,
   o app mostra como instalar (ADR-047).
7. **Remover worktree** apaga a pasta. Se algum processo a estiver segurando,
   o app mostra qual.
8. **Limpeza de worktrees.** Em *☰ → Gerenciamento de dados… → Worktrees*, o
   app lista os worktrees que não pertencem a nenhuma tarefa em andamento:
   - os **sem tarefa**: a tarefa foi apagada de vez, ou o worktree foi criado
     à mão ou por outra ferramenta;
   - os de tarefa **concluída, cancelada, arquivada ou na lixeira**.

   A busca cobre os repositórios dos diretórios das etiquetas e os das
   tarefas. Cada linha mostra as alterações não commitadas e os commits sem
   push, e vem marcada. Desmarque o que quiser manter e clique em
   **Limpar N worktrees**. A limpeza apaga **inclusive as alterações não
   commitadas**, e as branches continuam no repositório.

   Nunca entram na limpeza:
   - o worktree principal;
   - um worktree com um agente de IA aberto;
   - uma pasta aberta num terminal ou editor. Ela fica, com o nome de quem a
     segura.

   Worktree travado com `git worktree lock` aparece desmarcado (ADR-065).

### Claude Code por tarefa

- **Iniciar Claude Code** abre o `claude` num terminal real, dentro do
  worktree. O card mostra PID, início e pasta, e **Abrir terminal do agente**
  traz a janela certa para a frente.
- **Parâmetros, modelo e esforço** escolhidos no card. O padrão é
  `--dangerously-skip-permissions`, já que o worktree é isolado e descartável.
- **Texto para o agente com `@`:** completa os caminhos dos ambientes da
  tarefa e dos arquivos dentro deles.
- **Acompanhamento pelos hooks oficiais** do Claude Code. O selo da linha e um
  aviso no canto da tela mostram **Trabalhando**, **Aguardando você**,
  **Aguardando revisão** ou **Erro na resposta**. Os hooks são passados com
  `claude --settings <arquivo do app>`, e o `~/.claude/settings.json` do
  usuário não é alterado.
- **Um som por estado**, com sons do app ou arquivos seus, em
  *☰ → Sons do Claude Code…*.
- **Abrir Claude Code** também fica no menu `⋯` da linha, sem precisar abrir a
  tarefa.

### Jira: a issue vira o contexto da tarefa

O Jira continua sendo onde a issue mora. O MyTaskApp busca a issue enquanto
você escreve, guarda a chave, o título e o link na tarefa e dá o nome da
branch. Detalhes em [ADR-045](docs/ARCHITECTURE.md).

- **Conectar com um clique** em *☰ → Integrações…*: o navegador abre na
  Atlassian, você autoriza e volta conectado. E-mail + API token fica como
  caminho avançado.
- **Buscar ao digitar o título.** Escreva parte do título na caixa de captura,
  e as issues parecidas aparecem embaixo: tipo, chave, título e status. ↓/↑ e
  Enter escolhem, Tab pega a primeira, Esc fecha e Ctrl+Espaço busca na hora.
  Digitar a chave (`GAECO-1234`) também funciona, e com projeto padrão basta o
  número. A lista nunca vem escolhida: Enter sem escolha continua criando a
  tarefa sem vínculo.
- **A linha vira `GAECO-1234 título`** e a tarefa nasce vinculada. Apagar a
  chave desfaz o vínculo.
- **Na lista**, o tipo e a chave aparecem em destaque acima do título. Um
  clique abre a issue. O menu `⋯` copia a chave, o link e o nome da branch, e
  abre o terminal e a pasta do worktree.
- **Na tarefa**, um cartão com tipo, chave, status, título, branch e "lido do
  Jira há…", mais **Atualizar do Jira**, **Copiar** e **Desvincular**. Uma
  tarefa sem issue ganha **Vincular ao Jira…**.
- **Branch pela convenção do tipo:** `Bug → bug/GAECO-1234`,
  `Story → feature/…`, `Task → task/…`, `Improvement → improvement/…`,
  `Hotfix → hotfix/…`. É editável em Integrações, e "História", "Tarefa" e
  "Melhoria" seguem as mesmas linhas. A aba Desenvolvimento avisa quando a
  branch já existe, local ou no remoto, e a usa em vez de criar outra.
- **Funciona sem rede.** A chave, o título e o link ficam no banco. Sem Jira, só
  a busca e o "Atualizar" ficam indisponíveis.

### Bancos de Dados: cópia anonimizada de produção

Em *☰ → Bancos de Dados…*, o MyTaskApp cadastra conexões PostgreSQL por
ambiente e copia Produção para Desenvolvimento, Teste ou Homologação. Os dados
chegam anonimizados, mascarados na própria consulta à origem, sem nada
instalado nela. Produção nunca é alterada a partir do app. O guia completo
está em [`docs/database-operations.md`](docs/database-operations.md), e as
decisões em [ADR-056, ADR-057 e ADR-058](docs/ARCHITECTURE.md).

- **Conexões por ambiente**, com o badge PRODUCTION / STAGING / TEST /
  DEVELOPMENT. A senha vai para o cofre do sistema (DPAPI no Windows, chaveiro
  pelo `secret-tool` no Linux), nunca para o banco do app. O banco é opcional:
  sem ele, a conexão é o servidor, e o banco é escolhido na hora de copiar.
- **Apelidos:** a origem, o banco e a anonimização dele ficam salvos com um
  nome (`lock_eco_core_1010`). A cópia para um servidor sem banco cria um banco
  novo, `lock_eco_core_1010_20261009_143000`, com a data e a hora da cópia.
- **Produção é só leitura:** nunca é destino, não recebe restore, `createdb`,
  `dropdb`, SQL livre nem mascaramento estático. As permissões são travadas
  pelo ambiente, e a política vale nos casos de uso e de novo antes de cada
  processo, não só na tela.
- **Mascarado na consulta:** cada tabela sai da origem por um `COPY (SELECT …)`
  que já troca as colunas sensíveis, em sessão somente leitura, direto para o
  destino. O dado real não sai do servidor de produção, e nada é instalado lá.
  Só a estrutura (`pg_dump --schema-only`) vai para o disco.
- **Diagnóstico:** `psql`, `pg_dump`, `pg_restore`, `pg_isready`, `createdb` e
  `dropdb` com versão e compatibilidade, mais o servidor, com instruções quando
  falta algo.
- **Perfis:** a cópia "ECO Production → ECO Development" é cadastrada uma vez.
  O perfil de anonimização sugere colunas sensíveis (alta, média ou baixa
  probabilidade), com uma máscara de um catálogo fixo (hash, e-mail falso,
  parcial, nome falso, texto/número fixo, vazio, data deslocada, ruído), só
  grava o que você confirma e pré-visualiza os valores já mascarados. Tabelas
  marcadas como **sem dados** (logs, auditoria) vão vazias: a estrutura sim,
  nenhuma linha.
- **Copiar Banco:** [Validar] antes de [Executar], confirmação explícita de
  produção, barra, etapas e logs ao vivo. No fim, o relatório de verificação
  (estrutura, linhas e dados sensíveis diferentes da origem). Os temporários
  são apagados.
- **Histórico** de todas as operações, inclusive as bloqueadas, sem senha nem
  dado de linha.

### Servidor MCP: o app pelo Claude Code

Em *☰ → Servidor MCP…*, o MyTaskApp liga um servidor
[MCP](https://modelcontextprotocol.io) local. Um cliente de IA, como o Claude
Code, passa a consultar e alterar suas tarefas, o cronômetro e os lançamentos
de horas, as conexões de banco e os perfis de anonimização, pelas mesmas regras
da tela. O guia completo, com as 81 ferramentas, está em
[`docs/mcp-server.md`](docs/mcp-server.md), e a decisão no
[ADR-059](docs/ARCHITECTURE.md).

- **Só neste computador:** `http://127.0.0.1:5180/mcp`, porta configurável,
  sempre com token (`Authorization: Bearer`) guardado no cofre do sistema.
  Pedidos de navegador e `Host` estranho são recusados.
- **Desligado por padrão.** Habilitar, iniciar e parar, iniciar com o app e
  **somente leitura** ficam na tela, junto com o estado real, o endereço, o
  token e as instruções do cliente prontas para copiar.
- **As mesmas regras:** as ferramentas chamam os casos de uso da tela. Um
  cronômetro só, sem sobreposição, prazo no futuro, a política de bancos.
  Excluir manda para a lixeira, e a auditoria grava "(MCP)".
- **Horas que batem:** relatórios por dia, tarefa, etiqueta, issue do Jira e
  origem, a partir dos períodos gravados, com os ids de cada total.
- **Anonimização por partes:** incluir, trocar ou remover uma regra com
  pré-visualização e diff antes de gravar, validação contra o banco real (só
  leitura) e uma análise que separa o comprovado do risco.
- **Sem segredos e sem execução:** senha nunca entra nem sai, e não há SQL
  livre, cópia nem comando do sistema pelo MCP.

```bash
claude mcp add --transport http mytaskapp http://127.0.0.1:5180/mcp   --header "Authorization: Bearer <token copiado da tela>"
```

### Sincronização entre computadores (opcional)

Em *☰ → Sincronização…*, você conecta a conta Google, e tarefas, etiquetas,
horas e post-its vão para todos os seus computadores. Sem conta, nada muda: o
app continua inteiro, só local. O guia está em [`docs/sync.md`](docs/sync.md),
o servidor em [`docs/sync-server.md`](docs/sync-server.md), e as decisões no
[ADR-061 e no ADR-062](docs/ARCHITECTURE.md).

- **Cada computador com a sua lista.** A tarefa aparece só onde foi criada.
  "Onde aparece…" compartilha com todos ou manda para outro computador, e a
  caixa de captura escolhe o destino na hora ("estou no trabalho e anoto para
  casa"). O que chega de outro computador aparece no topo, em CHEGARAM.
  "Mostrando" e o histórico da semana mostram este computador, outro, ou
  todos. Escondida não é privada: tudo continua sincronizado.

- **O estado de relance.** Uma bolinha no cabeçalho mostra se está tudo
  sincronizado (verde), se há alterações esperando (âmbar), se está
  sincronizando (azul pulsando), se está offline (anel âmbar) ou se precisa de
  você (vermelha). O tooltip diz o estado por extenso e o clique traz
  "Sincronizar agora" ([ADR-063](docs/ARCHITECTURE.md)).
- **Funciona offline.** As mudanças esperam num outbox, gravado junto com a
  própria mudança, e sobem quando a rede volta, sem duplicar.
- **Conflito visível.** Campos diferentes editados em dois computadores ficam
  os dois. No mesmo campo, vence o que chegou primeiro ao servidor, e o outro
  valor aparece na janela com "Reaplicar o meu" ou "Descartar".
- **O que é da máquina fica na máquina:** pastas dos diretórios e dos
  worktrees, posição das janelas, conexões de banco, configurações e qualquer
  senha ou token. O diretório de uma etiqueta chega "não mapeado neste
  computador", com a URL do repositório para clonar aqui.
- **Comandos de outro computador** só rodam depois de você conferir o texto.
- **Dispositivos** com apelido e revogação. Desconectar não apaga nada.
- Na janela da tarefa: "Criada em Notebook · … · alterada por último neste
  computador".

### No celular (Android; iOS configurado)

O app do celular é mais um dispositivo da mesma sincronização
([`docs/mobile.md`](docs/mobile.md), [ADR-067](docs/ARCHITECTURE.md)):

- todas as tarefas de todos os dispositivos numa lista, com busca, atalhos
  (pendentes, vencidas, concluídas) e filtros por prioridade, etiqueta e
  origem (onde a tarefa aparece);
- criar, editar, concluir, reabrir, arquivar e mandar para a lixeira, pelas
  mesmas regras do desktop;
- funciona sem rede, mostra o que está pendente e sobe quando a conexão volta;
- entra com a mesma conta Google; o refresh token fica no Keystore/Keychain.

---

## Instalação

Os instaladores ficam em
[**Releases**](https://github.com/AdrianoGuzzo/MyTaskApp/releases). Eles são
*self-contained* e já levam o runtime do .NET, então não é preciso instalar
nada antes.

| Plataforma | Arquivo |
|---|---|
| Windows x64 | `MyTaskApp-<versão>-win-x64-setup.exe` |
| Linux x64 | `MyTaskApp-<versão>-linux-x64.tar.gz` |
| macOS | ainda não disponível ([roteiro](installer/macos/README.md)) |

Cada release publica também um `SHA256SUMS`, para conferir o download:

```bash
sha256sum -c SHA256SUMS --ignore-missing
```

### Windows

Rode o instalador. A instalação é **por usuário**, sem UAC, em
`%LOCALAPPDATA%\Programs\MyTaskApp`. Para atualizar, basta rodar o instalador
da versão nova por cima: os dados e os atalhos são mantidos.

> O instalador ainda não é assinado digitalmente, então o SmartScreen avisa na
> primeira execução.

Instalação silenciosa:

```bat
MyTaskApp-1.1.0-win-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

> No modo silencioso, "Iniciar com o Windows" também vem ligado. Para
> desligar, acrescente `/MERGETASKS="!startupicon"`.

Desinstalação (os dados ficam, a não ser que você peça para apagar):

```bat
"%LOCALAPPDATA%\Programs\MyTaskApp\unins000.exe" /VERYSILENT
"%LOCALAPPDATA%\Programs\MyTaskApp\unins000.exe" /VERYSILENT /DELETEDATA=1
```

### Linux

```bash
tar -xzf MyTaskApp-1.1.0-linux-x64.tar.gz
cd MyTaskApp-1.1.0
./install.sh                                      # sem sudo, em ~/.local (respeita PREFIX=)

~/.local/share/MyTaskApp/uninstall.sh             # remove o app e mantém os dados
~/.local/share/MyTaskApp/uninstall.sh --purge     # pergunta antes de apagar os dados
```

No Linux não há corretor ortográfico, sons, terminal do agente, indicação de
quem segura a pasta do worktree nem início automático com o sistema, porque
essas funções dependem de APIs do Windows.

Todos os parâmetros, o comportamento de atualização e o teste de fumaça estão
em [`installer/README.md`](installer/README.md).

---

## Onde ficam os dados

A aplicação e os dados ficam em lugares separados. Atualizar ou desinstalar o
app **nunca** apaga suas tarefas.

| | Windows | Linux |
|---|---|---|
| Aplicação | `%LOCALAPPDATA%\Programs\MyTaskApp` | `~/.local/share/MyTaskApp` |
| Banco SQLite | `%APPDATA%\MyTaskApp\mytaskapp.db` | `~/.config/MyTaskApp/mytaskapp.db` |
| Estado da janela | `%APPDATA%\MyTaskApp\widget.json` | `~/.config/MyTaskApp/widget.json` |
| Config do usuário | `%APPDATA%\MyTaskApp\appsettings.user.json` | `~/.config/MyTaskApp/appsettings.user.json` |
| Logs | `%APPDATA%\MyTaskApp\logs` | `~/.config/MyTaskApp/logs` |
| Conexão com o Jira | `%APPDATA%\MyTaskApp\jira.json` (sem segredo) | — |
| Token do Jira | `%APPDATA%\MyTaskApp\secrets\jira.bin` (DPAPI) | chaveiro do sistema (`secret-tool`) |
| Senhas das conexões de banco | `%APPDATA%\MyTaskApp\secrets\postgres-<id>.bin` (DPAPI) | chaveiro do sistema (`secret-tool`) |
| Temporários das cópias de banco | `%LOCALAPPDATA%\MyTaskApp\database-operations` | `~/.local/share/MyTaskApp/database-operations` |

O banco é criado e atualizado pela própria aplicação na inicialização (migrations
do EF Core). O instalador não mexe nele. Para fazer backup, copie o
`mytaskapp.db` com o app fechado.

A variável de ambiente **`MYTASKAPP_DATA_DIR`** move banco, estado e logs para
outra pasta, para uma instalação portátil ou para testes.

---

## Configuração

Quase tudo é configurado na própria interface, e as escolhas ficam salvas no
banco. O `appsettings.json` guarda só os parâmetros de funcionamento do app. Para
mudar algum deles sem editar o diretório de instalação, crie um
`appsettings.user.json` na pasta de dados com as chaves que quer sobrescrever:

```json
{
  "Application": {
    "TimeZoneId": "America/Sao_Paulo",
    "NowWindowBeforeMinutes": 15,
    "NowWindowAfterMinutes": 60,
    "ReminderTickSeconds": 30,
    "LifecycleSweepMinutes": 360,
    "AgentSessionReconcileSeconds": 60,
    "AgentEventsPort": 47831
  },
  "Jira": {
    "CallbackPort": 47832,
    "RequestTimeoutSeconds": 8
  }
}
```

| Chave | Padrão | O que faz |
|---|---|---|
| `TimeZoneId` | `America/Sao_Paulo` | fuso IANA usado para "hoje" e para os horários |
| `NowWindowBeforeMinutes` / `AfterMinutes` | 15 / 60 | janela da seção `AGORA` |
| `ReminderTickSeconds` | 30 | intervalo do agendador de lembretes |
| `LifecycleSweepMinutes` | 360 | intervalo da varredura de arquivamento e lixeira |
| `AgentSessionReconcileSeconds` | 60 | intervalo da conferência de processos do agente |
| `AgentEventsPort` | 47831 | porta local (`127.0.0.1`) que recebe os hooks do Claude Code |
| `Jira:CallbackPort` | 47832 | porta da volta do login do Jira; precisa ser a registrada no app OAuth |
| `Jira:RequestTimeoutSeconds` | 8 | quanto uma chamada ao Jira espera antes de desistir |
| `Jira:ClientId` / `ClientSecret` | do build | o app OAuth da Atlassian ([como registrar](docs/jira-oauth-app.md)) |
| `Sync:ApiBaseUrl` | do build | o servidor de sincronização; HTTPS, ou HTTP só no loopback. Vazio = sem sincronização ([servidor](docs/sync-server.md)) |
| `Sync:CallbackPort` | 47833 | porta da volta do login da sincronização; precisa ser uma das registradas no servidor |
| `DatabaseOperations:*` | ver o guia | tempos limite e pasta das cópias de banco ([detalhes](docs/database-operations.md#configuração)) |

Linha de comando: `MyTaskApp.exe --startup` abre direto na bandeja. É o
argumento que o instalador grava na chave `Run` do Windows.

---

## Desenvolvimento

### Pré-requisitos

| | Versão | Para quê |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | **10.0.401** (fixado em `global.json`) | tudo |
| Git | qualquer recente | aba Desenvolvimento e testes de Git |
| Ferramentas cliente do PostgreSQL | a versão do servidor de origem, ou mais nova | só para usar *Bancos de Dados…*; os testes não precisam ([instalação](docs/database-operations.md#instalação)) |
| Inno Setup 6 | `winget install -e --id JRSoftware.InnoSetup` | só para gerar o instalador Windows |

As ferramentas locais (`dotnet-ef` e `reportgenerator`) ficam em
`.config/dotnet-tools.json`:

```bash
dotnet tool restore
```

### Compilar e rodar

O repositório tem duas solutions:

| Solution | O que tem |
|---|---|
| `MyTaskApp.slnx` | o app desktop: Domain, Application, Infrastructure, Mcp, Desktop e os testes deles |
| `MyTaskApp.Server.slnx` | o servidor de sincronização opcional ([guia](docs/sync-server.md)): Server.Api, Server.Application, Server.Infrastructure e os testes deles |
| `MyTaskApp.Mobile.slnx` | o app do celular ([guia](docs/mobile.md)): a casca .NET MAUI (precisa do workload `maui-android`) e o Mobile.Core |

`MyTaskApp.Sync.Contracts` está nas duas, porque é o contrato entre elas. A do
servidor também lista Domain, Application e Infrastructure, numa pasta
`desktop`: os testes ponta a ponta sobem dois desktops inteiros contra a API
(e um celular, pelo Mobile.Core). O `MyTaskApp.Mobile.Core` e os testes dele
também estão no `MyTaskApp.slnx`: sem MAUI, compilam sem workload.

```bash
dotnet build MyTaskApp.slnx
dotnet run --project src/MyTaskApp.Desktop
```

> **Atenção:** a build de desenvolvimento usa **o mesmo banco do app
> instalado** (`%APPDATA%\MyTaskApp`). Para trabalhar sem tocar nos seus dados
> reais, aponte para uma pasta descartável:
>
> ```powershell
> $env:MYTASKAPP_DATA_DIR = "$env:TEMP\mytaskapp-dev"
> dotnet run --project src/MyTaskApp.Desktop
> ```
>
> Não deixe essa variável definida ao rodar `dotnet test`: há testes que
> verificam justamente o caminho padrão, e a suíte já se isola sozinha.

> **Instância única:** se um MyTaskApp estiver aberto na mesma sessão do
> Windows, seja o instalado ou o de outro checkout, a build de dev só sinaliza
> esse processo e encerra. Os 4 testes de `SingleInstanceTests` também falham.
> Feche o app pela bandeja antes de rodar.

A build trata avisos como erros (`TreatWarningsAsErrors` +
`EnforceCodeStyleInBuild`). Esse é o lint do projeto: um aviso novo reprova no
CI.

### Testes

```bash
dotnet test MyTaskApp.slnx          # desktop
dotnet test MyTaskApp.Server.slnx   # servidor de sincronização
```

As suítes usam xUnit v3, AwesomeAssertions e NSubstitute:

| Projeto | Cobre |
|---|---|
| `MyTaskApp.Domain.Tests` | regras puras: recorrência, classificação de "Hoje", lembretes, ciclo de vida |
| `MyTaskApp.Application.Tests` | casos de uso e agendadores, com `FakeTimeProvider` |
| `MyTaskApp.Infrastructure.Tests` | EF Core contra **SQLite real**, Git real, processos, migrations e upgrade |
| `MyTaskApp.Desktop.Tests` | ViewModels e testes de UI **headless** (Avalonia.Headless), onde um binding quebrado passaria despercebido |
| `MyTaskApp.Packaging.Tests` | contratos entre código e instalador (mutex, `--startup`, versão), lidos como texto e sem precisar do Inno Setup |
| `MyTaskApp.Sync.Contracts.Tests` | o contrato da sincronização: JSON, ids determinísticos, registro de campos e validação |
| `MyTaskApp.Server.Application.Tests` | a mescla por campo, o push e o pull do servidor, em memória |
| `MyTaskApp.Server.IntegrationTests` | o servidor contra **PostgreSQL real** (Testcontainers) e o ponta a ponta com dois desktops; pulam sem Docker |

### Cobertura

O mesmo portão do CI, com mínimo de **80% de linhas** e **70% de branches**:

```powershell
.\scripts\coverage.ps1 -Open
```

O relatório HTML vai para `artifacts/coverage/report/index.html`, e o script
falha se algum teste falhar ou se a cobertura ficar abaixo do mínimo.

### Migrations

O banco evolui só por migrations do EF Core. `EnsureCreated` é proibido.

```bash
dotnet tool restore
dotnet ef migrations add <Nome> \
  --project src/MyTaskApp.Infrastructure \
  --startup-project src/MyTaskApp.Infrastructure
```

As migrations pendentes são aplicadas no próximo start, antes da primeira tela.

### Gerar o instalador localmente

```powershell
.\installer\windows\build.ps1    # → artifacts/installer/MyTaskApp-<versão>-win-x64-setup.exe
```

```bash
installer/linux/build.sh         # → artifacts/installer/MyTaskApp-<versão>-linux-x64.tar.gz
```

São os mesmos scripts que a pipeline roda: versão, testes, `publish`
self-contained e empacotamento.

---

## Arquitetura

Clean Architecture em cinco projetos, com dependências apontando para dentro:

```
Desktop ─────────────► Application ──► Domain
   │                       ▲
   ├──► Infrastructure ────┤
   └──► Mcp ───────────────┘
```

| Projeto | Responsabilidade |
|---|---|
| **Domain** | Entidades e regras puras: `TaskItem` (a série) e `TaskOccurrence` (cada dia), lembretes, ciclo de vida, etiquetas, ambientes de desenvolvimento e sessões de agente. Sem dependências externas. |
| **Application** | Casos de uso (sem mediador), agendadores sobre `TimeProvider` e portas (`ITaskItemRepository`, `IGitClient`, `IAgentCliProvider`, …). |
| **Infrastructure** | EF Core + SQLite, Git, processos, terminal do Windows, listener dos hooks do Claude Code e sons. |
| **Mcp** | O servidor MCP local (ADR-059): Kestrel no loopback, as ferramentas, resources e prompts, sobre os casos de uso da Application. |
| **Desktop** | Avalonia + MVVM (CommunityToolkit.Mvvm), bandeja, temas, editor Markdown, corretor ortográfico e composição (DI + Serilog). |

Algumas decisões que moldam o código:

- **Série e ocorrência:** toda tarefa tem pelo menos uma ocorrência, inclusive
  as que não se repetem. O histórico fica preservado (cada dia é um registro), e
  a recorrência não se espalha em `if`s pelo código.
- **Tempo:** o domínio usa hora local (`DateOnly` + `TimeOnly` + fuso), e o
  instante UTC só é calculado na borda. "Agora" sempre vem de `TimeProvider`,
  nunca de `DateTime.Now`.
- **Lembretes persistidos:** o próximo disparo é uma coluna no banco, não um
  timer em memória.
- **Um escopo de DI por operação** (`IUseCaseRunner`), para o widget de longa
  duração não segurar um `DbContext` aberto.
- **`DomainException` é a mensagem que o usuário lê.** Stack trace vai só para
  o log.

Cada decisão, com o motivo e as alternativas descartadas, está registrada como
ADR em [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

### Stack

| | Versão |
|---|---|
| .NET | 10.0.401 |
| Avalonia | 12.1.2 |
| CommunityToolkit.Mvvm | 8.4.2 |
| EF Core + SQLite | 10.0.12 |
| Serilog | 4.4.0 |
| xUnit | v3 (3.2.2) |
| AwesomeAssertions | 9.6.0 |
| NSubstitute | 6.2.0 |

As versões ficam centralizadas em `Directory.Packages.props` (Central Package
Management).

---

## Estrutura do repositório

```
.
├── src/
│   ├── MyTaskApp.Domain/            entidades e regras
│   ├── MyTaskApp.Application/       casos de uso, agendadores, portas
│   ├── MyTaskApp.Infrastructure/    EF Core/SQLite, Git, processos, Claude Code
│   ├── MyTaskApp.Mcp/               servidor MCP local (Kestrel no loopback)
│   ├── MyTaskApp.Sync.Contracts/    contrato da sincronização (desktop e servidor)
│   ├── MyTaskApp.Server.*/          servidor de sincronização (ASP.NET Core + PostgreSQL)
│   ├── MyTaskApp.Mobile.Core/       o app do celular sem MAUI: ViewModels, composição
│   ├── MyTaskApp.Mobile/            o app do celular: .NET MAUI, Android e iOS
│   └── MyTaskApp.Desktop/           app Avalonia (views, view models, temas)
├── tests/                           uma suíte por projeto + Packaging.Tests
├── deploy/sync-server/              docker compose do servidor de sincronização
├── installer/
│   ├── windows/                     Inno Setup 6 (MyTaskApp.iss, build.ps1)
│   ├── linux/                       tarball + install.sh/uninstall.sh
│   └── macos/                       roteiro (não implementado)
├── scripts/
│   ├── coverage.ps1                 testes + cobertura + portão mínimo
│   └── generate-icon-font.py        fonte de ícones embutida (ADR-046)
├── docs/
│   ├── ARCHITECTURE.md              ADRs
│   ├── mcp-server.md                servidor MCP: configuração e ferramentas
│   ├── sync.md                      sincronização: o que vai, conflitos, limitações
│   ├── sync-server.md               servidor de sincronização: Google, variáveis, implantação
│   ├── mobile.md                    o app do celular: ambiente, Android, iOS, limitações
│   └── release-process.md           versionamento e release
├── .github/workflows/               CI, CodeQL, título de PR, Release Please, release
├── .github/dependabot.yml           atualizações de NuGet e de actions
├── Directory.Build.props            versão do produto e regras de build
├── Directory.Packages.props         versões dos pacotes
└── global.json                      SDK fixado
```

---

## Contribuindo

1. Crie uma branch a partir do `master`: `feature/…` ou `fix/…`.
2. Escreva os commits como preferir. O merge é **squash**, e o **título do PR**
   vira o único commit no `master`.
3. Use no título do PR o formato
   [Conventional Commits](https://www.conventionalcommits.org/), com a
   descrição em português. Ela vai para o changelog, então escreva pensando em
   quem usa o app:

   | Tipo | Quando | Versão |
   |---|---|---|
   | `feat:` | funcionalidade nova | MINOR |
   | `fix:` | correção de bug | PATCH |
   | `perf:` / `refactor:` / `revert:` | sem mudar o comportamento | PATCH |
   | `docs:` `test:` `build:` `ci:` `chore:` `style:` | o resto | nenhuma |
   | `feat!:` ou rodapé `BREAKING CHANGE:` | quebra de compatibilidade | MAJOR |

   O workflow `PR title` reprova títulos fora do padrão.
4. Antes de abrir o PR:

   ```powershell
   dotnet build MyTaskApp.slnx -c Release
   .\scripts\coverage.ps1
   ```

O CI roda em todo PR: build Release, as suítes (o servidor de sincronização num job ubuntu, com PostgreSQL), o portão de cobertura
(com comentário no PR) e um `publish` self-contained para `win-x64` e
`linux-x64`. Também reprova um pacote NuGet com vulnerabilidade conhecida, e o
CodeQL analisa o C# e os workflows; o Dependabot abre os PRs de atualização.
Onde fica cada check e como rodar o de pacotes local:
[docs/release-process.md §4](docs/release-process.md#segurança-sec-016).

---

## Releases e versionamento

Ninguém edita número de versão à mão. O fluxo é este:

```
PR com título convencional → squash no master
  → Release Please mantém o PR "chore(master): release X.Y.Z"
  → merge desse PR → tag vX.Y.Z + release em rascunho
  → release.yml: confere tag × código → testes → instaladores → SHA256SUMS
  → teste de fumaça manual → publicar o rascunho
```

- Versão em [SemVer](https://semver.org/lang/pt-BR/), com tag `vX.Y.Z`. A
  versão fica em `Directory.Build.props` e é mantida pelo Release Please.
- Tags nunca são movidas e releases publicadas não são refeitas. Uma correção
  sai como versão nova.
- A versão instalada aparece na última linha do menu ☰ (`MyTaskApp 1.1.0 ·
  <commit> · <data>`). Clicar nela copia esses dados para colar num relato de
  problema.

O processo completo, com hotfix em branch de manutenção, configuração do
repositório e assinatura digital, está em
[`docs/release-process.md`](docs/release-process.md). O histórico de versões
fica em [`CHANGELOG.md`](CHANGELOG.md).

---

## Documentação

| Documento | Conteúdo |
|---|---|
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | decisões de arquitetura (ADR-001 a ADR-072), com o motivo de cada uma |
| [`docs/sync.md`](docs/sync.md) | sincronização entre computadores: a tela, o que sincroniza, onde cada tarefa aparece, conflitos e limitações |
| [`docs/sync-server.md`](docs/sync-server.md) | o servidor de sincronização: cliente OAuth do Google, configuração, migrations, implantação e testes |
| [`docs/mobile.md`](docs/mobile.md) | o app do celular: arquitetura, login, sincronização, ambiente, rodar no Android, o que falta no iOS, testes e limitações |
| [`docs/mcp-server.md`](docs/mcp-server.md) | servidor MCP local: ativar, porta, token, Claude Code, as ferramentas, segurança e limitações |
| [`docs/database-operations.md`](docs/database-operations.md) | conexões PostgreSQL, ambientes, ferramentas, máscaras, cópia anonimizada de produção e solução de problemas |
| [`docs/jira-oauth-app.md`](docs/jira-oauth-app.md) | registrar o app OAuth do Jira e pôr as credenciais no build |
| [`docs/release-process.md`](docs/release-process.md) | Conventional Commits, SemVer, pipeline de release, hotfix, verificação de versão |
| [`installer/README.md`](installer/README.md) | instaladores Windows e Linux, parâmetros, atualização, teste de fumaça |
| [`installer/macos/README.md`](installer/macos/README.md) | roteiro para o empacotamento macOS |
| [`CHANGELOG.md`](CHANGELOG.md) | o que mudou em cada versão |

---

## Licença

Distribuído sob a licença MIT. O texto completo está em [`LICENSE`](LICENSE).

Os ícones da fonte embutida (`src/MyTaskApp.Desktop/Assets/Fonts/MyTaskAppIcons.ttf`)
são do [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons),
© Microsoft Corporation, também sob licença MIT.
