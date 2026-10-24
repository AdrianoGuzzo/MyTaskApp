# ADR-008 — `DomainException` é a falha que o usuário lê

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | aplicacao |
| **Registrado** | 2026-09-20, commit `396e55f` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | DomainException, ScreenErrorPath, tratamento de erro, Tarefa não encontrada, Result<T>, TryAsync |
| **Relações** | — |
| **Código** | `src/MyTaskApp.Domain/DomainException.cs`, `src/MyTaskApp.Desktop/ViewModels/ScreenErrorPath.cs`, `src/MyTaskApp.Application/Abstractions/TaskItemRepositoryExtensions.cs` |

**Decisão:** uma única exceção representa "falha esperada, com mensagem
apresentável": `DomainException`. A Application também a lança quando o
agregado pedido não existe ("Tarefa não encontrada.").

A UI então tem **um** caminho de tratamento:

```
DomainException  → mostra a mensagem como está
qualquer outra   → log completo + "Não foi possível salvar. [Tentar novamente]"
```

**Por quê:** a alternativa (`Result<T>` em todo caso de uso) obrigaria a
encanar resultado por handler, ViewModel e binding, para um app cujas regras
já param a operação no agregado. Um segundo tipo (`NotFoundException`) só
dobraria os `catch` sem ganho — "não encontrada" é falha de negócio tanto
quanto "já foi concluída".

**Consequência testada:** nenhum caso de uso deixa vazar
`NullReferenceException` para a borda, e nada é persistido quando a regra
recusa a operação.

**Nas telas:** o caminho acima mora em `ScreenErrorPath`
(`Desktop/ViewModels`). O ViewModel informa só o que é dele:

- o evento de log;
- se limpa a linha de status ao começar;
- se o cancelamento é silencioso.

`ScreenErrorPathTests` fixa esse caminho tela a tela. As telas com um passo a mais
mantêm o próprio `TryAsync`: Sincronização (`SyncApiException`), alertas (avisam
quem os abriu) e post-its (recarregam a lista). Elas estão listadas em
`docs/refactoring/PROGRESS.md`.
