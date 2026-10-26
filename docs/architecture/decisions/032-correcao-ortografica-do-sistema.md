# ADR-032 — Correção ortográfica: o corretor do sistema, desenhado por cima da caixa

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | ui-desktop |
| **Registrado** | 2026-09-24, commit `20b86a2` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | ISpellChecker, WindowsSpellChecker, SpellCheck.IsEnabled, SpellingTokenizer, SpellingSquiggles, DeveloperTerms, corretor ortográfico, sublinhado |
| **Relações** | relacionado: [ADR-026](026-diretorios-da-etiqueta-alias.md) |
| **Código** | `src/MyTaskApp.Desktop/SpellChecking/SpellCheckBinder.cs`, `src/MyTaskApp.Desktop/SpellChecking/WindowsSpellChecker.cs`, `src/MyTaskApp.Desktop/SpellChecking/SpellingTokenizer.cs`, `src/MyTaskApp.Desktop/SpellChecking/SpellingSquiggles.cs` |

**Contexto:** as anotações, os títulos e a captura rápida são texto livre em
português, e a `TextBox` da Avalonia não tem corretor ortográfico. O pedido era
o sublinhado vermelho e as sugestões no clique direito, como em qualquer editor.

**Decisão:** usar o **corretor do próprio sistema** atrás de uma porta
(`ISpellChecker`, em `Desktop/SpellChecking`) e ligá-lo por uma attached
property: `spell:SpellCheck.IsEnabled="True"`.

```
TextBox ─ SpellCheck.IsEnabled ─► SpellCheckBinder ─► SpellCheckSession ─► ISpellChecker
                                  (adorner, menu)     (tokens, cache)      ├─ WindowsSpellChecker (COM)
                                                                           └─ NoSpellChecker
```

**Por que o do sistema, e não um dicionário embarcado.** O Windows já traz o
dicionário pt-BR, as sugestões e o dicionário do usuário, que é compartilhado
com os outros apps. Um Hunspell embarcado traria vários megabytes de dicionário
para manter atualizados.

**A API.** Windows Spell Checking API (`spellcheck.h`, Windows 8+), com COM
gerado em compilação (`[GeneratedComInterface]`), o mesmo caminho do
`[LibraryImport]` do resto do app. A vtable é declarada à mão, e os métodos que
não usamos ficam como marcadores (`...Slot`) só para segurar a posição. Um
teste de integração com o COM real existe porque um método fora de ordem
compila e chama outro método.

**Português e inglês.** Uma palavra só está errada se estiver errada em todos
os idiomas carregados: pt-BR e, quando existir, en-US. O Windows só tem o
dicionário de um idioma instalado, e numa máquina só em português o inglês não
está. Por isso existe `DeveloperTerms`, uma lista curta do jargão que as
anotações usam como português (branch, merge, deploy, commit…). Ela vale em
qualquer máquina.

**O que não é prosa não é verificado** (`SpellingTokenizer`, função pura):

- código Markdown (entre crases e em bloco ```` ``` ````);
- URL, e-mail, `@alias`, `#tag` e path;
- palavra grudada em dígito ou em `_` (`v2`, `snake_case`); o `_` na ponta é
  itálico e não conta;
- sigla, camelCase e palavra de uma letra.

Sublinhado em cada path ensinaria o usuário a não olhar para o sublinhado.

**O desenho é um adorner.** A `TextBox` não tem decoração por trecho de texto,
e trocar o template das caixas para enfiar uma camada seria caro de manter. O
`SpellingSquiggles` fica na `AdornerLayer`, sobre o `TextPresenter`: acompanha
a rolagem, é recortado pela área visível e usa as coordenadas do `TextLayout`,
as mesmas do cursor (`AliasCompletionBinder`, ADR-026).

**Quando verifica.**

- A cada tecla roda só o cache, para os sublinhados andarem junto com o texto.
- Depois de 400 ms parado, pergunta ao sistema as palavras novas.
- A palavra sob o cursor não é marcada enquanto se digita. Ela é julgada no
  espaço, quando o cursor sai dela ou quando a caixa perde o foco.
- O COM só é chamado da thread de UI.

**Menu.** O clique direito numa palavra errada abre, em vez do flyout padrão,
as sugestões, "Adicionar ao dicionário" (o dicionário do usuário do Windows,
persiste), "Ignorar" (até fechar o app), Recortar, Copiar e Colar. A troca
passa pela seleção (`SelectedText`), então Ctrl+Z desfaz e o binding é avisado.
Adicionar ou ignorar avisa todas as caixas abertas (`DictionaryChanged`).

**Onde está ligado.** Anotação, título da tarefa, captura rápida e as
descrições de comando global e de diretório. Paths, aliases, comandos e buscas
ficam de fora.

**Limites aceitos:**

- Linux e macOS usam o `NoSpellChecker`. O próximo passo é o
  WeCantSpell.Hunspell com `/usr/share/hunspell/pt_BR.*`, sem mexer na UI;
- sem dicionário pt-BR nem en-US no Windows, o corretor fica desligado (log
  `SpellCheckerUnavailable`);
- "Ignorar" não sobrevive a reiniciar o app.
