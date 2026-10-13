# Sincronização entre computadores

A sincronização é **opcional**. Sem conta, o MyTaskApp continua inteiro, só
neste computador, e não grava uma linha de sincronização sequer. Com a conta
conectada, as mesmas tarefas, etiquetas, horas e post-its aparecem em todos os
computadores da pessoa.

A decisão e as alternativas estão no [ADR-061](ARCHITECTURE.md). O servidor,
com o cliente do Google, as variáveis e a implantação, está em
[`sync-server.md`](sync-server.md).

## Na tela

*☰ → Sincronização…*:

- **Conta.** "Conectar com o Google" abre o navegador. O MyTaskApp nunca vê a
  senha: a volta é por `http://127.0.0.1:47833/callback`, com PKCE.
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
que passaram pela sincronização.

Sem servidor configurado na build, a janela só explica que a versão não tem
sincronização.

## O que sincroniza

| Dado | O que vai | O que fica em cada computador |
|---|---|---|
| Tarefa | título, anotação, prioridade, próxima ação, estimativa, lembrete, prazo e alertas, issue do Jira (o retrato), arquivamento, lixeira | auditoria; a conclusão é recalculada |
| Agendamento | data e hora, situação, prazo | posição manual, estado dos lembretes e dos alertas |
| Etiqueta e vínculo com a tarefa | nome, cor | — |
| Diretório da etiqueta | alias, nome, descrição, branch padrão | **o caminho da pasta** |
| Comandos globais e do diretório | texto, nome, modo, parâmetros, pasta relativa, ordem, ligado | a revisão local |
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
de outro computador nunca sobrescreve a daqui.

O ambiente de desenvolvimento chega como "Não criado neste computador", com a
branch. "Iniciar implementação" cria o worktree aqui, a partir do repositório
daqui.

### Comandos de outro computador

O texto de um comando viaja, então um comando mudado em outro computador pode
rodar algo que você não escreveu aqui. Por isso, até alguém conferir o texto na
janela de Sincronização, ele não roda, nem pelo botão, nem pelo ambiente, nem
pelo comando rápido.

Não escreva senha dentro de um comando: ela iria junto.

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
3. **Pull:** as mudanças desde o cursor, em páginas de 200. Cada página é
   aplicada numa transação SQLite que também avança o cursor: ou entra a
   página inteira, ou nada.

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
  etiqueta) podem voltar.

A sincronização nunca para por causa de um conflito.

### Primeira conexão

1. Traz tudo o que a conta já tem (cursor zero).
2. Junta pelo nome o que já existia aqui e lá: etiquetas, alias de comando
   global e alias de diretório. Os vínculos da tarefa local passam para a
   etiqueta da conta.
3. Manda o que só existe aqui, com a origem "este computador" e a data de
   criação original.

O que veio do servidor já tem estado, então não sobe de novo.

### Versões diferentes do app

O contrato tem versão (`SyncSchema.Current`, hoje 1). Um campo que o app não
conhece é preservado no servidor, e o app mais novo o enxerga. Abaixo da
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
- Os registros de exclusão (tombstones) não são podados nesta versão.
- O texto dos comandos viaja. Segredo digitado num comando vai junto. A
  revisão local é a barreira, e não a solução.
- A porta de volta do login (47833) é fixa: o servidor só aceita o endereço
  registrado.
- Um espaço pessoal por conta, sem convites. O esquema do servidor já tem
  membros e papéis.
- Uma janela de edição aberta pode sobrescrever, ao salvar, uma mudança que
  chegou enquanto ela estava aberta. A mudança vai para o servidor como uma
  edição normal.
- Trocar de máquina exige conectar de novo, porque o cofre DPAPI é deste
  usuário nesta máquina.
- Sem anexos nem arquivos: a sincronização leva dados, nunca conteúdo de
  pastas.
