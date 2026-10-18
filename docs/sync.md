# Sincronização entre computadores

A sincronização é **opcional**. Sem conta, o MyTaskApp continua inteiro, só
neste computador, e não grava uma linha de sincronização sequer. Com a conta
conectada, tarefas, etiquetas, horas e post-its vão para todos os
computadores da pessoa. **Cada computador tem a sua lista:** uma tarefa
aparece só onde foi criada, até alguém mandá-la para outro computador ou
compartilhá-la com todos (veja [Onde cada tarefa aparece](#onde-cada-tarefa-aparece)).

A decisão e as alternativas estão no [ADR-061](ARCHITECTURE.md). O servidor,
com o cliente do Google, as variáveis e a implantação, está em
[`sync-server.md`](sync-server.md).

## Na tela

**A bolinha no cabeçalho** ([ADR-063](ARCHITECTURE.md)), ao lado do
histórico, diz de relance como está a sincronização. Passe o mouse para ler o
estado por extenso, ou clique para ver o painel com "Sincronizar agora":

| Bolinha | Quer dizer |
|---|---|
| verde cheia | **Sincronizado**, com a hora da última vez ("há 2 min") |
| âmbar cheia | Alterações daqui **esperando para subir** (sobem em segundos) |
| azul pulsando | **Sincronizando** |
| anel âmbar | **Offline**: as alterações ficam guardadas aqui e sobem quando a conexão voltar; o app tenta de novo sozinho |
| vermelha | **Precisa de você**: login expirado, computador revogado, versão antiga ou alteração recusada |
| verde com halo âmbar | Em dia, com **algo para revisar** (conflito, comando recebido) |
| anel cinza | **Pausada**, ou **só neste computador** (sem conta: o clique leva a conectar) |

Numa build sem servidor de sincronização, a bolinha não aparece.

*☰ → Sincronização…*:

- **Conta.** "Conectar com o Google" abre o navegador. O MyTaskApp nunca vê a
  senha: a volta é por `http://127.0.0.1:<porta>/callback`, numa porta
  efêmera aberta para aquele login, com PKCE. Se o navegador já tinha uma
  sessão aberta por outro login, o servidor pergunta antes ("Conectar …?").
  "Desconectar" sai da conta neste computador e **não apaga nada**: tudo
  continua aqui, só para de sincronizar.
- **Sincronização.** Liga e pausa, mostra a última sincronização e o último
  erro, e tem "Sincronizar agora".
- **Conflitos.** O valor que perdeu, com "Reaplicar o meu" e "Descartar".
- **Falhas.** Alterações que o servidor recusou de vez ("Tentar de novo" e
  "Descartar a minha alteração"), e mudanças da conta que não entraram aqui
  ("Tentar de novo").
- **Comandos recebidos.** Comandos criados ou mudados em outro computador. Eles
  só rodam aqui depois de "Revisei e libero", com o texto inteiro à vista.
- **Dispositivos.** Apelido editável, "este computador" e "Revogar". O apelido
  começa como "computador (usuário do Windows)", como `DESKTOP-XYZ (ana)`: cada
  login do Windows tem banco, dispositivo e token próprios, então dois logins na
  mesma máquina são dois dispositivos da conta.

Na janela da tarefa, abaixo do título, aparece "Criada em Notebook · 12/10/2026
14:30 · alterada por último neste computador". A linha só existe para tarefas
que passaram pela sincronização. Embaixo dela vem onde a tarefa aparece, com
"Mudar…".

## Onde cada tarefa aparece

A decisão está no [ADR-062](ARCHITECTURE.md). Cada tarefa e cada etiqueta
aparece **só no computador onde foi criada** ou **em todos os computadores**.

- **Criar:** a tarefa nasce neste computador. Na caixa de captura, "Para"
  escolhe outro destino:
  - "Este computador", o padrão;
  - "Todos os computadores";
  - "Enviar para Notebook", para anotar no trabalho o que é para fazer em
    casa.

  Depois de cada captura, "Para" volta para este computador.
- **Mudar:** "Onde aparece…" no menu da linha (⋯ ou clique direito), na janela
  da tarefa e na janela Etiquetas. As opções são:
  - "Só neste computador";
  - "Em todos os computadores";
  - "Enviar para …" cada outro computador da conta.

  Mandar para outro computador tira a tarefa da lista daqui, e a mensagem
  oferece "Desfazer". Com o cronômetro correndo nela, o envio é recusado.
- **Chegou:** a tarefa que outro computador mandou para cá aparece no topo, em
  **CHEGARAM DE OUTRO COMPUTADOR**, com ou sem data. Ela mostra "Chegou de
  Notebook" e sai dali quando é aberta.
- **Ver o outro computador:** "Mostrando", abaixo da caixa de captura, mostra
  este computador, cada outro, ou todos. O histórico da semana (↺) usa o mesmo
  seletor: é lá que se vê o que foi feito em cada computador. O app sempre abre
  em "Este computador".
- **Selos na linha:**
  - "⇄ todos os computadores" na compartilhada, porque mexer nela muda nos
    dois;
  - "→ Notebook" na de outro computador, quando o seletor mostra mais do que
    este.
- **Lembretes e avisos de prazo** tocam só no computador onde a tarefa
  aparece, seja qual for o seletor.
- **Horas, histórico e estatísticas** seguem a tarefa e o seletor.
- **Etiquetas:** o seletor de etiquetas mostra as daqui e as compartilhadas,
  mais as que já marcam a tarefa aberta. Criar uma etiqueta com o nome de uma
  que está escondida aqui reaproveita aquela etiqueta e a compartilha.
- **Post-its** continuam aparecendo em todos. **Comandos globais** não têm
  seletor: os compartilhados estão em todos, e os outros só no computador onde
  foram criados (ver "Comandos só deste computador").

Sem outro computador na conta, nada disso aparece, e a lista é a de sempre.

**Escondida não é privada.** Tudo continua sincronizado: o servidor e todos os
computadores têm todas as tarefas, e "Mostrando: Todos" ou o MCP com
`device: all` mostram tudo. A separação é de lista, não de segredo.

### O que já existia

Ao atualizar, cada computador assume as tarefas que ele mesmo criou: elas
passam a ser só dele. Uma etiqueta usada nos dois computadores vira
compartilhada. Até o outro computador atualizar, as tarefas dele aparecem em
todos.

A etiqueta criada nos dois antes da primeira conexão, como a "Casa", é juntada
pelo nome e fica compartilhada.

### Computador revogado

Revogado e reconectado, o computador volta com outro id e continua vendo as
suas tarefas. A próxima sincronização as leva para o id novo. Tarefas que
ficaram num computador revogado e que não volta aparecem em "Mostrando:
Todos", e "Onde aparece…" as traz para cá.

Sem servidor configurado na build, a janela só explica que a versão não tem
sincronização.

## O que sincroniza

| Dado | O que vai | O que fica em cada computador |
|---|---|---|
| Tarefa | título, anotação, prioridade, próxima ação, estimativa, lembrete, prazo e alertas, issue do Jira (o retrato), arquivamento, lixeira | auditoria; a conclusão é recalculada |
| Agendamento | data e hora, situação, prazo | posição manual, estado dos lembretes e dos alertas |
| Etiqueta e vínculo com a tarefa | nome, cor | — |
| Diretório da etiqueta | alias, nome, descrição, branch padrão, URL do repositório | **o caminho da pasta** |
| Comandos globais **marcados como compartilhados** e os do diretório | texto, nome, modo, parâmetros, pasta relativa, ordem, ligado | a revisão local; **a escolha de compartilhar**; o comando global desmarcado inteiro, com os botões dele |
| Ambiente de desenvolvimento | branch, origem, tag, prompt, lista de comandos | **repositório, worktree, situação, banco copiado** |
| Horas | início, fim, origem, nota | o cronômetro **enquanto roda** |
| Post-it | texto, aparência, arquivamento, lixeira, tarefa gerada | aberto, fixado, posição, tamanho |

**Nunca sai do computador:** conexões e perfis de banco, perfis de
anonimização e de cópia, histórico de operações de banco, execuções de comando,
sessões do agente, auditoria, configurações, estado das janelas e qualquer
segredo (senhas, tokens do Jira e do MCP, connection strings).

Um teste percorre todas as colunas mapeadas no EF dos tipos sincronizados e
falha se alguma não estiver classificada como "vai" ou "fica". Coluna nova
entra na lista de propósito, e nunca por acaso.

### Caminhos por computador

O diretório de uma etiqueta chega sem pasta: "Não mapeado neste computador". Em
*Etiquetas…*, você escolhe a pasta daqui. Ela nunca sobe, e uma pasta que veio
de outro computador nunca sobrescreve a daqui. Com a URL do repositório, editar
o diretório já sugere onde clonar, e **Clonar** baixa e mapeia de uma vez
(ADR-064).

O ambiente de desenvolvimento chega como "Não criado neste computador", com a
branch. "Iniciar implementação" cria o worktree aqui, a partir do repositório
daqui.

### Comandos de outro computador

O texto de um comando viaja, então um comando mudado em outro computador pode
rodar algo que você não escreveu aqui. Por isso, até alguém conferir o texto na
janela de Sincronização, ele não roda, nem pelo botão, nem pelo ambiente, nem
pelo comando rápido.

Não escreva senha dentro de um comando compartilhado: ela iria junto.

### Comandos só deste computador

O comando global nasce **só deste computador**: o texto costuma ter caminho,
executável ou script de uma máquina só (ADR-066). Para mandar para os outros,
marque **Compartilhar entre computadores** no formulário de *Comandos
globais*. A lista mostra "⇄ todos os computadores" no compartilhado. Os
comandos que já existiam antes desta versão continuam compartilhados, e os do
diretório da etiqueta sempre acompanham o diretório.

- **Desmarcar** um compartilhado não apaga nada: os outros computadores ficam
  com a cópia que já têm. Daí em diante, as mudanças daqui não vão, e as de lá
  não chegam aqui. A tela pede confirmação antes.
- **Excluir** o compartilhado tira de todos. O desmarcado, só daqui.
- **Marcar de novo** faz valer a versão daqui nos outros computadores, mesmo
  que um deles o tenha excluído nesse meio-tempo.
- **O mesmo apelido nos dois lados:** se chega um `@build` compartilhado e aqui
  já existe um `@build` só deste computador, o daqui fica como está, e o de lá
  espera com um aviso na janela de Sincronização. Renomeie um dos dois (o de lá
  entra sozinho no ciclo seguinte), ou marque o daqui para compartilhar, e os
  dois viram um.
- Uma tarefa que chama `@build` num computador sem ele avisa que o comando não
  existe neste computador.

## Como funciona

### Gravar aqui

Toda gravação passa pelo `SaveChanges` do app. Com a conta conectada, um
interceptor compara o que mudou com o último retrato sincronizado e grava a
operação no **outbox**, na mesma transação da mudança. Não existe tarefa salva
sem a operação, nem operação de algo que não foi salvo.

Cada operação tem um id próprio (`OpId`), a versão do servidor em que se
baseou e só os campos alterados. Duas edições seguidas da mesma tarefa, antes de
subir, viram uma operação só.

### O ciclo

1. **Hello:** registra a versão do app e lê os apelidos dos dispositivos.
2. **Push:** até 100 operações por lote. A operação é marcada como tentada
   *antes* de sair e mantém o mesmo `OpId` em toda tentativa.
3. **Pull:** as mudanças desde o cursor, em páginas de até 200. O servidor
   corta a página antes, se ela passar do teto de entidades ou de bytes, e o
   app segue para a próxima (ADR-071). Cada página é aplicada numa transação
   SQLite que também avança o cursor: ou entra a página inteira, ou nada.

O agendador roda 3 s depois de uma mudança local, a cada 60 s, e na hora com
"Sincronizar agora". Uma falha passageira (rede, timeout, 429, 5xx) espera de
2 s a 5 min, com sorteio para os computadores não baterem juntos, e respeita o
`Retry-After` do servidor.

### Reenvio sem duplicar

O servidor guarda o resultado de cada `OpId`. Se a resposta do push se perdeu
depois que ele gravou, o reenvio recebe o mesmo resultado, marcado como
duplicado, e nada é aplicado duas vezes. O app pode fechar no meio: ao abrir, o
outbox reenvia as mesmas operações.

### Conflitos

A mescla é **por campo**, e quem decide é o servidor, pela versão dele, e não
pelo relógio dos computadores:

- campos diferentes da mesma tarefa, editados em dois computadores, ficam os
  dois;
- o mesmo campo editado nos dois: **vence o que chegou primeiro ao servidor**.
  O outro computador recebe o valor vencedor, e o dele vira um conflito
  visível, com "Reaplicar o meu" e "Descartar";
- valor igual nos dois lados não é conflito;
- nome de etiqueta repetido na conta vira conflito "o nome já existe";
- editar algo que foi excluído em outro computador vira conflito "excluído em
  outro computador". **Exclusão não ressuscita**: só vínculos (tarefa ↔
  etiqueta) podem voltar, e o comando que outro computador volta a
  compartilhar depois de alguém excluí-lo (ADR-066).

A sincronização nunca para por causa de um conflito.

### Primeira conexão

1. Traz tudo o que a conta já tem (cursor zero).
2. Junta pelo nome o que já existia aqui e lá: etiquetas, alias de comando
   global compartilhado e alias de diretório. Os vínculos da tarefa local
   passam para a etiqueta da conta. Um comando só deste computador não é
   juntado: o de lá espera, com aviso (ver "Comandos só deste computador").
3. Manda o que só existe aqui, com a origem "este computador" e a data de
   criação original.

O que veio do servidor já tem estado, então não sobe de novo.

### Versões diferentes do app

O contrato tem versão (`SyncSchema.Current`, hoje 1). O app ignora, no pull, o
campo que não conhece. O servidor só guarda o que está no contrato dele e
recusa o resto com `unknown_field` (ADR-072): campo novo pede o servidor
atualizado antes dos apps. Abaixo da
versão mínima, o servidor responde 426, e a janela pede para atualizar. Quando
um app atualizado sobe a versão do contrato, ele refaz a primeira conexão.

### Dispositivos e contas

- O DeviceId fica em `%LOCALAPPDATA%\MyTaskApp\device.json`, fora do banco e
  fora do backup. Um banco copiado para outra máquina pede para conectar de
  novo.
- Revogar um dispositivo derruba o acesso dele na hora: a autorização e os
  tokens são revogados, e cada chamada confere a autorização no banco. O
  revogado mantém os dados. Para voltar, ele entra como um dispositivo novo e
  manda o que mudou.
- Entrar com outra conta limpa o estado de sincronização antes, mas não os
  dados locais.
- O refresh token fica no cofre do sistema (DPAPI). O access token fica só na
  memória.

### O celular

O app do celular ([`mobile.md`](mobile.md), ADR-067) é mais um dispositivo:
entra com a mesma conta, aparece na lista de dispositivos e sincroniza pelo
mesmo ciclo — só em primeiro plano. Diferenças:

- a lista dele é de **todos** os dispositivos, com um filtro "Origem" exato
  (compartilhadas, este celular, ou um computador);
- tarefa criada no celular nasce **compartilhada**, e aparece na visão padrão
  de todo computador;
- o DeviceId fica na pasta de dados do app, fora do backup do sistema, e o
  refresh token no Keystore/Keychain;
- no desktop, o celular aparece em "Enviar para…" como mais um computador.

## Limitações conhecidas

- **A mesma tarefa criada nos dois computadores antes da primeira sincronização
  aparece duas vezes.** Só etiquetas e aliases são fundidos pelo nome.
- **No mesmo campo, vence quem chega primeiro ao servidor.** O valor perdido
  fica só na lista de conflitos do computador que perdeu.
- A mescla por campo pode montar uma combinação que nenhum dos dois lados
  escreveu, por exemplo o título de um e a anotação do outro.
- Uma edição offline de algo excluído em outro computador se perde. Ela vira
  conflito, sem "Reaplicar".
- Só polling: até 60 s para ver a mudança do outro computador, ou na hora com
  "Sincronizar agora".
- Ficam por computador: posição manual na lista, estado dos lembretes e
  alertas, janelas, auditoria e configurações.
- Horas sobrepostas vindas de dois computadores são aceitas. Quem corrige é
  você.
- O cronômetro só sincroniza quando para.
- Texto com o caractere nulo (U+0000), em geral vindo de um colar, não
  sincroniza: o servidor recusa a alteração, que fica como falha. O resto do
  envio segue.
- Os registros de exclusão (tombstones) não são podados nesta versão.
- O texto dos comandos compartilhados viaja. Segredo digitado num comando
  compartilhado vai junto. A revisão local é a barreira, e não a solução.
- Um botão de diretório criado em outro computador enquanto o comando estava
  desmarcado aqui não chega quando ele volta a ser compartilhado.
- A versão com porta efêmera no login (ADR-074) precisa do servidor
  atualizado antes: um servidor antigo só aceita a 47833 e recusa a volta no
  navegador. O desktop antigo continua entrando no servidor novo.
- Um espaço pessoal por conta, sem convites. O esquema do servidor já tem
  membros e papéis.
- Escondida não é privada: a tarefa só de um computador também está no
  servidor e nos outros computadores (ADR-062).
- Um computador ainda na versão anterior ao ADR-062 mostra todas as tarefas e
  não muda onde elas aparecem.
- Uma janela de edição aberta pode sobrescrever, ao salvar, uma mudança que
  chegou enquanto ela estava aberta. A mudança vai para o servidor como uma
  edição normal.
- Trocar de máquina exige conectar de novo, porque o cofre DPAPI é deste
  usuário nesta máquina.
- Sem anexos nem arquivos: a sincronização leva dados, nunca conteúdo de
  pastas.
