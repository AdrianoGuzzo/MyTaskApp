# ADR-013 — Captura rápida: uma linha, uma tarefa

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | ui-desktop |
| **Registrado** | 2026-09-20, commit `396e55f` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | QuickCapture, captura rápida, Enter, Shift+Enter, MaxLines, atomicidade, túnel |
| **Relações** | revisto por: [ADR-022](022-ordem-manual-por-arrasto.md); relacionado: [ADR-007](007-inbox-e-estado-nao-entidade.md), [ADR-045](045-jira-issue-como-contexto-da-tarefa.md), [ADR-054](054-post-it-entidade-propria.md) |
| **Código** | `src/MyTaskApp.Application/Tasks/QuickCapture.cs`, `src/MyTaskApp.Desktop/Views/TodayView.axaml.cs` |

**Decisão:** a tela "Hoje" abre com uma caixa de texto já focada. Cada linha
escrita vira um `TaskItem` agendado para **hoje, sem horário**. Enter registra a
lista inteira; Shift+Enter quebra linha.

**Por quê:** registrar precisa custar menos esforço do que a própria tarefa, ou o
usuário não registra (§36). Um formulário com título, data, hora e prioridade
transforma "comprar pão" em seis interações — e o app perde justamente as coisas
pequenas, que são as que se esquece.

**Alternativa rejeitada — sintaxe na linha** (`ligar dentista 14:30`, `!alta`):
economiza cliques de quem decora a sintaxe e confunde todo o resto, que fica com
uma tarefa chamada "ligar dentista 14:30". A linha é um título e nada mais.

**Alternativa rejeitada — capturar sem data (Inbox puro, ADR-007):** seria mais
fiel ao fluxo "pensei → registrei → organizei depois", mas o `TodayQuery` só traz
ocorrências com data — o que fosse escrito sumiria da única tela que existe. Datar
em hoje mantém o que foi escrito à vista. Quando houver tela de Inbox, o padrão
muda em uma linha do handler.

**Atomicidade:** a lista inteira é construída — e validada — antes de qualquer
`AddAsync`, e um único `SaveChanges` fecha a captura. Uma linha longa demais no
meio não pode deixar metade do checklist gravada: o usuário não teria como saber
onde parou para reescrever o resto. Testado contra o SQLite de verdade.

**Teto de 100 linhas por captura:** um Ctrl+V no documento errado criaria
centenas de tarefas que ninguém desfaz uma a uma. Recusar é mais barato do que
limpar.

**Enter precisa ser tratado no túnel:** a caixa aceita quebra de linha
(`AcceptsReturn`), então o próprio `TextBox` marca o Enter como tratado antes de a
tecla subir — um `KeyBinding` nunca a veria. Há teste headless que digita e aperta
Enter de verdade, porque esse é o tipo de fio que se rompe sem quebrar a build.

**Limitação conhecida:** a seção SEM HORÁRIO ordena por prioridade e depois por
título, então um checklist recém-escrito aparece em ordem alfabética, não na ordem
em que foi digitado.
