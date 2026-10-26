# ADR-075 — Celular: os temas do desktop, concluir sem acidente e o teclado que fecha

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | mobile |
| **Registrado** | 2026-10-18, commit `97f96dc` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | MobileThemeCatalog, MauiThemeService, ThemeParityTests, SwipeView Reveal, Desfazer, teclado iOS, ThemedShellRenderer, pílula de sincronização |
| **Relações** | revisa: [ADR-067](067-celular-dispositivo-da-sincronizacao.md); relacionado: [ADR-041](041-temas-sete-paletas-e-automatico.md), [ADR-063](063-indicador-sincronizacao-no-cabecalho.md), [ADR-076](076-celular-pendentes-historico-dispositivo-lembrado.md) |
| **Código** | `src/MyTaskApp.Mobile.Core/Presentation/MobileThemeCatalog.cs`, `src/MyTaskApp.Mobile/Adapters/MauiThemeService.cs`, `src/MyTaskApp.Mobile/Platforms/iOS/KeyboardMappings.cs`, `tests/MyTaskApp.Desktop.Tests/Theming/ThemeParityTests.cs` |

**Contexto:** usando o app no iPhone, quatro queixas:

1. **O teclado não fechava.** Nada no app o tirava da frente:
   - tocar fora do campo e rolar a tela não faziam nada;
   - o teclado do iOS não tinha botão "OK";
   - o Return do título não fazia nada, e o da descrição só pulava linha.

   No iOS não existe o "voltar" do Android, então quem abria a descrição
   ficava com metade da tela coberta.
2. **O estado da sincronização não aparecia.** Ele era só o texto de um
   `ToolbarItem` ligado por binding, sem cor, e no iOS esse texto não
   aparece de forma confiável.
3. **Feio, e fora dos temas do app.** O celular tinha uma cópia à mão de
   Carvão e Papel (`Colors.xaml`), em pares `AppThemeBinding`, e seguia o
   sistema. As abas não tinham ícone, e cada cartão levava um botão de texto
   "Concluir".
4. **Arrastar concluía sem querer.** O `SwipeView` estava em
   `Mode="Execute"`: o arrasto até o fim concluía, sem confirmação nem
   desfazer. No iOS nem o aviso aparecia, porque o toast só existia no
   Android.

**Decisão:**

1. **Os sete temas do desktop, mais "Automático"** (ADR-041).
   - **Catálogo.** `MobileThemeCatalog` (Mobile.Core) tem os mesmos ids,
     nomes, descrições e cores do `ThemeCatalog`, em texto: o núcleo do
     celular não pode depender do Avalonia.
   - **Cópia vigiada.** `ThemeParityTests`, no projeto de testes do desktop,
     compara cor a cor. Com as paletas iguais, o `ThemeContrastTests` vale
     para o celular. O projeto do desktop continua sem conhecer o celular;
     só o de testes referencia os dois.
   - **Aplicação.** `MauiThemeService` grava cada papel da paleta como um
     recurso de mesmo nome (`Canvas`, `Accent`…) e as telas o pedem por
     `DynamicResource`. Trocar o tema troca os valores, e a tela acompanha
     na hora.
   - **Variante do sistema.** Um tema escolhido fixa o `UserAppTheme`
     (claro/escuro), para teclado, barra de status e seletores nativos
     ficarem na mesma variante da paleta. "Automático" deixa o sistema
     decidir: Papel no claro, Carvão no escuro, e acompanha a troca ao vivo.
     Não há alto contraste automático, como o desktop faz com o Windows.
   - **Escolha.** Fica na aba Conta, seção "Aparência": uma linha por tema,
     com a amostra (fundo e destaque) e o visto no ativo. Vale ao tocar e
     fica nas `Preferences` do aparelho, chave `theme`, como o `widget.json`
     do desktop. Não sincroniza.
2. **Arrastar só revela o botão.** O `SwipeView` passou a `Mode="Reveal"`:
   o arrasto mostra "Concluir" (ou "Reabrir"), e nada muda até tocar nele.
   - **No cartão.** O botão de texto deu lugar a um círculo de marcar à
     esquerda, como nos Lembretes do iOS: 24 pontos de desenho e 48 de toque.
     Ele fica cheio, com o visto, na concluída.
   - **"Desfazer" em vez de pergunta.** Depois de concluir ou reabrir pela
     lista, a barra "Tarefa concluída · Desfazer" fica 8 s na tela (com 5 s,
     no teste no emulador, ela sumia antes do toque).
     "Desfazer" roda o caso de uso oposto na mesma ocorrência. Uma barra nova
     substitui a anterior e recomeça o prazo.
   - **Igual nas duas plataformas.** A barra é da própria tela, não um aviso
     do sistema. Sair da lista a fecha.
3. **O teclado sai da frente.**
   - **Tocar fora.** `HideSoftInputOnTapped` na lista e no formulário.
   - **"OK" em cima do teclado (iOS).** Em todo `Entry`, `Editor` e
     `SearchBar`, por `AppendToMapping` nos handlers, há um `UIToolbar` com o
     "OK" do sistema. O `Info.plist` declara `pt-BR`, e os botões do sistema
     ("OK", "Cancelar" da busca) saem em português.
   - **Arrastar para baixo (iOS).** Toda rolagem tem
     `KeyboardDismissMode = Interactive`, menos o `UITextView` da descrição,
     que rola o próprio texto.
   - **Return.** O título usa `ReturnType="Done"` e tira o foco; o "Buscar"
     da busca também.
4. **A sincronização à vista.** Uma pílula no topo da lista, tocável, que
   leva à aba Conta.
   - **Bolinha.** Tem as cores do ADR-063: sucesso (sincronizado), atenção
     (pendente), informação pulsando (sincronizando), perigo (atenção), anel
     de atenção (sem conexão) e anel apagado (pausada, só neste celular).
   - **Frase.** É o título do `SyncStatusText` e, quando está em dia, a idade
     ("Sincronizado · há 2 min"), na escala do desktop. Um relógio de 30 s
     só refaz esse texto.
   - **Aba Conta.** Passou a ouvir `CycleStarted` e `ChangeQueued`, e mostra
     "Sincronizando…" enquanto o ciclo corre.
5. **Polimento.**
   - Ícones nas abas (SVG pintado pela barra).
   - A barra do topo na cor do fundo.
   - Etiquetas com fundo suave na cor delas.
   - Sombra no botão +.
   - No iOS, o aviso curto ("Tarefa salva") passa a existir: uma etiqueta
     acima das abas, que o leitor de tela anuncia.

## Alternativas

- **Perguntar "Concluir esta tarefa?" a cada arrasto.** Protege, mas pede um
  toque a mais em todo uso certo. Revelar e desfazer protege o arrasto
  acidental e o toque errado, sem cobrar do uso normal.
- **Tirar o arrasto.** Perderia o gesto que quem usa iOS espera.
- **Paletas num projeto comum ao desktop e ao celular.** Exigiria tirar o
  `Avalonia.Media.Color` do `ThemePalette` e mexer no desktop inteiro por
  uma cópia que o teste já vigia.
- **Sincronizar o tema.** O desktop guarda o tema por computador, e não na
  conta. Um tema por aparelho continua coerente com isso.
- **CommunityToolkit.Maui** (Snackbar, `HideKeyboardAsync`). Seria mais uma
  dependência para três coisas pequenas que o MAUI e o UIKit já fazem.

## Consequências

- O cronômetro parado ao concluir **não volta** com "Desfazer": a tarefa
  reabre, o tempo não. É o mesmo de "Reabrir".
- As cores do celular vêm do código. O XAML usa só `DynamicResource` para
  cor, e um `StaticResource` de cor não acharia nada.
- **Nenhuma cor do tema passa por gatilho**, e foi o emulador que mostrou
  por quê.
  - **O defeito.** No Android, numa linha reciclada da lista (e num
    `Button`), o `DataTrigger` que sai devolvia a cor do tema de **antes** de
    uma troca. Títulos ficavam escuros sobre fundo escuro.
  - **Estados com cor.** O círculo, os atalhos e as etiquetas marcadas viram
    duas views alternadas por `IsVisible`, cada uma com o seu
    `DynamicResource`.
  - **Cor que depende de um valor.** Prioridade, prazo e a bolinha usam
    conversores que leem a cor do tema na hora. Na troca de tema, a lista é
    relida e a bolinha repintada.
  - **Gatilho que fica.** Só o que não é cor do tema, como a opacidade do
    título concluído.
- **Android, barra do topo.** A `AppBarLayout` do Shell vai por baixo da
  barra de status e era pintada com o `colorPrimary` do pacote do MAUI
  (#2C3E50). Um `ShellRenderer` próprio (`ThemedShellRenderer`) a pinta com a
  cor da barra e tira a sombra. Os ícones da barra de status seguem o claro ou
  escuro do tema.
- O teclado do iOS, o "OK" e o aviso curto foram escritos sem Mac local: o
  CI do iOS (`mobile-ios.yml`) compila, e o teste no aparelho está no
  checklist do docs/mobile.md.

**Testes:**

- **Mobile.Core:**
  - concluir pela lista abre o "Desfazer" e não um toast;
  - "Desfazer" reabre, e reconclui o que foi reaberto;
  - a barra some depois de 8 s, e uma nova recomeça o prazo;
  - a pílula diz "há N min" e o relógio a refaz;
  - os temas da aba Conta, o marcado e a troca;
  - catálogo (ordem, `Normalize`, `Resolve`) e paletas completas em hex;
  - `LastSuccessAt` no `SyncBadge`;
  - `Labels.Ago`.
- **Desktop:** `ThemeParityTests`, com os mesmos temas, na mesma ordem e com
  as mesmas cores.
