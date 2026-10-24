# ADR-046 — Fonte de ícones embutida: o cabeçalho também tem ícones no Linux

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | ui-desktop |
| **Registrado** | 2026-10-03, commit `8d741b4` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | MyTaskAppIcons.ttf, WidgetIconFont, Segoe Fluent Icons, Fluent UI System Icons, Linux, IconFontTests, generate-icon-font.py |
| **Relações** | relacionado: [ADR-017](017-janela-principal-como-widget.md), [ADR-052](052-tempo-trabalhado-por-periodo.md), [ADR-053](053-historico-sete-dias-projecao.md) |
| **Código** | `src/MyTaskApp.Desktop/Assets/Fonts/MyTaskAppIcons.ttf`, `scripts/generate-icon-font.py`, `src/MyTaskApp.Desktop/Styles/Tokens.axaml`, `tests/MyTaskApp.Desktop.Tests/Theming/IconFontTests.cs` |

**Contexto:** instalado no Linux, o cabeçalho do painel aparecia sem o
alfinete, sem o "recolher" e sem o "⋯" do menu — e, na lista, sem os ícones de
etiqueta, anotação e lembrete. Os botões estavam lá e respondiam ao clique; o
que faltava era o desenho. Os glifos são caracteres da área de uso privado da
`Segoe Fluent Icons`/`Segoe MDL2 Assets` (`U+E712` é o "⋯", por exemplo), e
essas fontes só existem no Windows. Sem a fonte, o caractere não tem quem o
desenhe e o botão fica vazio. Os testes não pegavam porque rodam no Windows.

**Decisão:** o app embute `Assets/Fonts/MyTaskAppIcons.ttf`, uma fonte pequena
(10 glifos, menos de 3 KB) que responde pelos **mesmos códigos** da Segoe, com
os contornos do Fluent UI System Icons (MIT, Microsoft). Ela entra no fim do
`WidgetIconFont`:

```
Segoe Fluent Icons, Segoe MDL2 Assets, avares://MyTaskApp/Assets/Fonts#MyTaskApp Icons
```

No Avalonia 12 a lista vira uma família composta, e tanto a escolha da fonte
quanto o fallback por caractere percorrem as entradas na ordem. No Windows a
Segoe responde primeiro e nada muda na tela; no Linux (e no macOS) quem desenha
é a embutida. Nenhuma tela, view model ou teste de glifo precisou mudar.

**Por que não trocar tudo por `PathIcon`:** seria o caminho "sem fonte", mas
mexe em toda tela que usa ícone, troca as propriedades `…Glyph` de texto por
geometria e muda a aparência no Windows, que é onde o app vive. A fonte de
reserva resolve o Linux sem tocar no Windows. A Segoe não pode ir junto: a
licença dela não permite redistribuir.

**A fonte é gerada, não desenhada:** `scripts/generate-icon-font.py`
(`pip install fonttools`) baixa os SVGs de 20px num commit fixo do repositório
de origem e monta a TTF. As métricas copiam as da Segoe (em de 2048,
ascendente 2048, descendente 0, avanço de 1 em) para o glifo ocupar a mesma
caixa nos dois sistemas. Rodar duas vezes gera o mesmo binário.

**A guarda:** `IconFontTests` varre `src/MyTaskApp.Desktop` atrás de todo
código da área de uso privado — literal, `` ou `&#xE712;` — e exige um
glifo para cada um na fonte embutida, lida pelo `avares://` de verdade. Ícone
novo sem passar pelo script quebra o teste no Windows, em vez de sumir só no
Linux.

**Limites aceitos:**

- fora do Windows o traço é o do Fluent UI System Icons, parecido mas não
  idêntico ao da Segoe;
- o alfinete solto (`U+E718`) e o fixado (`U+E840`) usam o contorno vazado e o
  cheio; na Segoe Fluent os dois são o mesmo desenho e quem diferencia é a cor.

## Divergências com o código (conferido em 2026-10-24)

- O ADR fala em 10 glifos e menos de 3 KB; o script hoje gera 16 glifos (ADR-052 e ADR-053 somaram ícones) e a fonte tem 3844 bytes (scripts/generate-icon-font.py, src/MyTaskApp.Desktop/Assets/Fonts/MyTaskAppIcons.ttf).
