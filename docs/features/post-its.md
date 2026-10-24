# Post-its

| | |
|---|---|
| **Status** | Implementado. O atalho global `Ctrl+Alt+N` só existe no Windows. |
| **Decisões** | [ADR-054](../architecture/decisions/054-post-it-entidade-propria.md) |
| **Código** | `src/MyTaskApp.Domain/StickyNotes/`, `src/MyTaskApp.Application/StickyNotes/`, `src/MyTaskApp.Desktop/StickyNotes/StickyNoteWindowManager.cs`, `src/MyTaskApp.Desktop/ViewModels/StickyNoteViewModel.cs`, `src/MyTaskApp.Desktop/ViewModels/StickyNotesViewModel.cs`, `src/MyTaskApp.Desktop/Widget/HotkeyGesture.cs` |
| **Testes** | `tests/MyTaskApp.Desktop.Tests/StickyNotes/`, `tests/MyTaskApp.Application.Tests/StickyNotes/`, `tests/MyTaskApp.Infrastructure.Tests/StickyNotesEndToEndTests.cs` |

O que ainda não merece virar tarefa: "falar com o João sobre a API", uma ideia,
uma pergunta para depois. Post-its não aparecem nas seções do checklist.

- **Ctrl+Alt+N**, de qualquer programa e mesmo com o app na bandeja, abre um
  post-it novo com o cursor no texto (só no Windows). Dentro do app, o atalho é
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
  anotação. A etiqueta vem junto, a tarefa nasce para hoje, sem horário (seção
  `SEM HORÁRIO`), e o post-it vai para os arquivados. **Criar tarefa com a
  seleção** transforma só o trecho selecionado.
- O X (ou Esc) fecha sem excluir. *☰ → Post-its → Meus post-its…* reabre
  qualquer um, com tamanho, posição e pino. Na mesma lista ficam
  **Arquivados** e **Lixeira**, com o mesmo prazo de retenção dos checklists.
  Um post-it fechado em branco é descartado.
- Ao reabrir o app, só os post-its **fixados** voltam para a tela. Os outros
  continuam na lista.
- Com a sincronização ligada, os post-its aparecem em todos os computadores.

## Divergências (conferido em 2026-10-24)

- O README dizia que a tarefa criada a partir do post-it "entra em HOJE". O caso
  de uso agenda para hoje sem horário (`ConvertStickyNoteToTask.cs`), e o
  `TodayClassifier` põe tarefa sem horário em `SEM HORÁRIO`.
