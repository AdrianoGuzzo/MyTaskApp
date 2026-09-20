# macOS — roteiro, não implementado

Esta pasta existe para registrar **como** empacotar para macOS quando for a
hora, e para deixar claro que a omissão é decisão, não esquecimento.

Não foi implementado porque nada disso é verificável sem um Mac: `codesign`,
`hdiutil` e a notarização são ferramentas da Apple, e um script entregue sem
nunca ter rodado seria pior do que a sua ausência — daria a impressão de existir.

## O que já está pronto

A parte difícil, que é a de dentro do app, **já funciona**:

- `UserDataLocation` resolve `~/Library/Application Support/MyTaskApp` sozinho,
  porque `Environment.SpecialFolder.ApplicationData` já aponta para lá no
  macOS. Banco, logs e `widget.json` caem no lugar certo sem uma linha de
  código específica de sistema operacional.
- `SingleInstance` usa `Mutex` nomeado, que o .NET suporta no macOS. O segundo
  processo encerra em vez de duplicar o agendador. O sinal que faz a janela
  existente reaparecer é só do Windows (`EventWaitHandle` nomeado) — no macOS
  o equivalente seria `NSDistributedNotificationCenter` ou um socket de domínio
  Unix em `~/Library/Application Support/MyTaskApp`.
- `installer/assets/generate-brand-assets.ps1` já produz o PNG 256 que vira o
  `.icns`.

## O que falta

1. **Publicar** para `osx-arm64` e `osx-x64` (ou um binário universal):

   ```bash
   dotnet publish src/MyTaskApp.Desktop -c Release -r osx-arm64 \
     --self-contained true -p:PublishTrimmed=false
   ```

2. **Montar o bundle** `MyTaskApp.app`:

   ```
   MyTaskApp.app/Contents/
   ├── Info.plist          CFBundleIdentifier, CFBundleShortVersionString (vem do MSBuild)
   ├── MacOS/MyTaskApp     o binário publicado
   └── Resources/MyTaskApp.icns
   ```

   `LSUIElement` merece atenção: o MyTaskApp vive na bandeja, e no macOS isso é
   um item na barra de menus. `LSUIElement=1` tira o ícone do Dock — é
   provavelmente o que se quer, mas muda o comportamento de ativação e precisa
   ser testado junto com o `SingleInstance`.

3. **`.icns`** a partir do PNG 256 (`iconutil -c icns`).

4. **`.dmg`** com `hdiutil` e o atalho para `/Applications`.

5. **Assinar e notarizar** — sem isso o Gatekeeper bloqueia o app em qualquer
   máquina que não seja a de quem compilou. Exige conta paga no Apple Developer
   Program, `codesign --deep --options runtime` e `notarytool submit --wait`.

## Onde os dados ficariam

| | caminho |
|---|---|
| Aplicação | `/Applications/MyTaskApp.app` |
| Dados do usuário | `~/Library/Application Support/MyTaskApp` |
| Logs da aplicação | `~/Library/Application Support/MyTaskApp/logs` |

Mesma regra das outras plataformas: arrastar o `.app` para o Lixo remove o
aplicativo e **não** toca nos dados — que é exatamente o comportamento que o
usuário de macOS espera.
