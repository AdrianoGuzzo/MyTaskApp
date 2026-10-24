# Changelog

## [1.26.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.25.0...v1.26.0) (2026-10-24)


### Added

* **remoto:** fase 2 da orquestração remota, com o plano, o pareamento entre computadores e a janela Máquinas ([#157](https://github.com/AdrianoGuzzo/MyTaskApp/issues/157)) ([36eb99c](https://github.com/AdrianoGuzzo/MyTaskApp/commit/36eb99cc9a336424bdbb80db6c6a17d15ac7d724))

## [1.25.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.24.0...v1.25.0) (2026-10-23)


### Added

* **database:** diagnóstico ensina a instalar tudo e cria banco local no Docker (ADR-083) ([#155](https://github.com/AdrianoGuzzo/MyTaskApp/issues/155)) ([f283eb0](https://github.com/AdrianoGuzzo/MyTaskApp/commit/f283eb0e5e0abcfdd3b66898c207fc06ef10803b))

## [1.24.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.23.0...v1.24.0) (2026-10-22)


### Added

* **consulta:** aba Consulta Inteligente na tarefa, com o Claude Code só lendo (ADR-081) ([#153](https://github.com/AdrianoGuzzo/MyTaskApp/issues/153)) ([7f01515](https://github.com/AdrianoGuzzo/MyTaskApp/commit/7f01515b0502cb9bfa434b93410dc878d212fafa))

## [1.23.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.22.0...v1.23.0) (2026-10-22)


### Added

* **desktop:** ensina a instalar o Git no diretório da etiqueta (ADR-064) ([#151](https://github.com/AdrianoGuzzo/MyTaskApp/issues/151)) ([0465d32](https://github.com/AdrianoGuzzo/MyTaskApp/commit/0465d328d2ac45ab20bcb65f177e5e254cdc8d97))

## [1.22.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.21.0...v1.22.0) (2026-10-22)


### Added

* **consulta:** base da Consulta Inteligente e leitura segura do banco (ADR-081, parcial) ([#148](https://github.com/AdrianoGuzzo/MyTaskApp/issues/148)) ([dd289bc](https://github.com/AdrianoGuzzo/MyTaskApp/commit/dd289bc0e4856a0d98f3b0c07b3ae5f23ce5aa51))
* **desktop:** linhas sem máscara na anonimização, por condição SQL (ADR-080) ([#145](https://github.com/AdrianoGuzzo/MyTaskApp/issues/145)) ([35acfcd](https://github.com/AdrianoGuzzo/MyTaskApp/commit/35acfcd4d0f12c95d095b0c472e7524921d70676))
* **desktop:** script SQL depois da cópia no perfil de anonimização (ADR-082) ([#150](https://github.com/AdrianoGuzzo/MyTaskApp/issues/150)) ([92160df](https://github.com/AdrianoGuzzo/MyTaskApp/commit/92160df09df5d6c68499d9366ff8e3598ad6aade))
* **desktop:** título inteiro no balão do histórico de 7 dias ([#149](https://github.com/AdrianoGuzzo/MyTaskApp/issues/149)) ([0631112](https://github.com/AdrianoGuzzo/MyTaskApp/commit/0631112725ea083d5d85295e45759a0046da0c85))
* **mobile:** aba Tarefas enxuta, dispositivo no título e Nova tarefa discreta (ADR-079) ([#146](https://github.com/AdrianoGuzzo/MyTaskApp/issues/146)) ([1db0168](https://github.com/AdrianoGuzzo/MyTaskApp/commit/1db0168d3bdbf607d49221998bd05e3b95b87cba))

## [1.21.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.20.0...v1.21.0) (2026-10-20)


### Added

* **desktop:** importar etiqueta de outro computador (ADR-077) ([#141](https://github.com/AdrianoGuzzo/MyTaskApp/issues/141)) ([cdeed70](https://github.com/AdrianoGuzzo/MyTaskApp/commit/cdeed70f9628db56db735ae3157263bb9cb380bc))
* **sync:** servidor de sincronização configurado pelo usuário, sem servidor embutido (ADR-078) ([#144](https://github.com/AdrianoGuzzo/MyTaskApp/issues/144)) ([7453854](https://github.com/AdrianoGuzzo/MyTaskApp/commit/7453854dfefde82f973c084e78a0eadde053d552))

## [1.20.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.19.0...v1.20.0) (2026-10-19)


### Added

* **mobile:** pendentes na frente, Histórico numa aba e o dispositivo lembrado (ADR-076) ([#136](https://github.com/AdrianoGuzzo/MyTaskApp/issues/136)) ([9a08abb](https://github.com/AdrianoGuzzo/MyTaskApp/commit/9a08abb906dd82884bcef221c05ec2a5537b50f2))
* **mobile:** temas do desktop, concluir sem acidente e teclado que fecha (ADR-075) ([#132](https://github.com/AdrianoGuzzo/MyTaskApp/issues/132)) ([97f96dc](https://github.com/AdrianoGuzzo/MyTaskApp/commit/97f96dc95c7c1b060163efccef0164d74b017447))


### Fixed

* **mobile:** build do iOS sem workload do Android e com Xcode fixo ([#127](https://github.com/AdrianoGuzzo/MyTaskApp/issues/127)) ([dd33903](https://github.com/AdrianoGuzzo/MyTaskApp/commit/dd339031a4c370c2c49bcd628cafa4fa4fd50688))
* **mobile:** simulador do iOS sem certificado e aviso para tag antiga ([#129](https://github.com/AdrianoGuzzo/MyTaskApp/issues/129)) ([4cc86b3](https://github.com/AdrianoGuzzo/MyTaskApp/commit/4cc86b336c1c3a184ab87dfebf798f455f2a0ed3))
* **server:** API do compose de produção sem superusuário do banco (SEC-020) ([#133](https://github.com/AdrianoGuzzo/MyTaskApp/issues/133)) ([927b7c4](https://github.com/AdrianoGuzzo/MyTaskApp/commit/927b7c457896fb4086efa8eadecc81de8a1e3eb5))
* **server:** caminho reescapado no log (SEC-019) ([#130](https://github.com/AdrianoGuzzo/MyTaskApp/issues/130)) ([d76d88f](https://github.com/AdrianoGuzzo/MyTaskApp/commit/d76d88fcb798b3f1d4a218bc209a022da3af964c))

## [1.19.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.18.0...v1.19.0) (2026-10-18)


### Added

* APK do Android na release e .ipa de teste do iOS ([#124](https://github.com/AdrianoGuzzo/MyTaskApp/issues/124)) ([bd48336](https://github.com/AdrianoGuzzo/MyTaskApp/commit/bd48336640feeabbb7fdc751b3ee64c44cbb447d))


### Fixed

* **desktop:** "Abrir sincronização…" no painel da bolinha volta a abrir a janela ([#123](https://github.com/AdrianoGuzzo/MyTaskApp/issues/123)) ([f0630d9](https://github.com/AdrianoGuzzo/MyTaskApp/commit/f0630d958b12c457693e01da53cd1db63e56d98b))
* **reminders:** aviso não fica mais preso com "Ocorrência não encontrada." ([#126](https://github.com/AdrianoGuzzo/MyTaskApp/issues/126)) ([42d58cc](https://github.com/AdrianoGuzzo/MyTaskApp/commit/42d58ccd349f29ee09b33c7c711e359a0ef1eee7))
* **server:** consentimento no authorize fora do login recém-feito e loopback com porta efêmera (SEC-018, ADR-074) ([#121](https://github.com/AdrianoGuzzo/MyTaskApp/issues/121)) ([7c80bbe](https://github.com/AdrianoGuzzo/MyTaskApp/commit/7c80bbeb99b0816189d12a25c8f2175130752d72))

## [1.18.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.17.0...v1.18.0) (2026-10-18)


### Added

* servidor de sincronização fechado, com lista de quem pode entrar (ADR-068) ([#96](https://github.com/AdrianoGuzzo/MyTaskApp/issues/96)) ([590059f](https://github.com/AdrianoGuzzo/MyTaskApp/commit/590059f12b368a2dfc6592a0e19e83fcd7373e2e))
* servidor de sync em Production atrás de TLS, e Development só no loopback (ADR-070) ([#94](https://github.com/AdrianoGuzzo/MyTaskApp/issues/94)) ([ee766d3](https://github.com/AdrianoGuzzo/MyTaskApp/commit/ee766d3f043377b2f7b110dd2790863e3b27d686))


### Fixed

* **server:** access token revogado leva 401 na hora, e o sair revoga access e refresh (SEC-010) ([#104](https://github.com/AdrianoGuzzo/MyTaskApp/issues/104)) ([96635d1](https://github.com/AdrianoGuzzo/MyTaskApp/commit/96635d1fcc84d7c4fa85e4066525ed5db5986535))
* **server:** chaves do Data Protection cifradas com certificado e as em claro revogadas (SEC-011, ADR-073) ([#103](https://github.com/AdrianoGuzzo/MyTaskApp/issues/103)) ([bdb4ca9](https://github.com/AdrianoGuzzo/MyTaskApp/commit/bdb4ca9407355a69ca5223eda08bc84447a634c1))
* **server:** corpos com nulos e NUL viram 400 ou recusa da operação, não 500 (SEC-008) ([#99](https://github.com/AdrianoGuzzo/MyTaskApp/issues/99)) ([6ad1a6a](https://github.com/AdrianoGuzzo/MyTaskApp/commit/6ad1a6aa6f75478993927e202785c75da0ac706f))
* **server:** GUID em qualquer formato vira D e instante exige fuso no push (SEC-014) ([#107](https://github.com/AdrianoGuzzo/MyTaskApp/issues/107)) ([2ffd779](https://github.com/AdrianoGuzzo/MyTaskApp/commit/2ffd77977f79831bcb76fe1bfd33083bc7fd934d))
* **server:** issuer fixo do OpenIddict e AllowedHosts com o nome público (SEC-007) ([#100](https://github.com/AdrianoGuzzo/MyTaskApp/issues/100)) ([e62e704](https://github.com/AdrianoGuzzo/MyTaskApp/commit/e62e704762af1a2187efa51e2c461305a5edb87b))
* **server:** no-store e nosniff nas respostas, sem header Server e CSP no logout (SEC-009) ([#102](https://github.com/AdrianoGuzzo/MyTaskApp/issues/102)) ([b8a6cb8](https://github.com/AdrianoGuzzo/MyTaskApp/commit/b8a6cb8678e651613caead19fd48b901d947bc50))
* **server:** pull com teto por página e sem reenviar a família da tarefa (SEC-005, ADR-071) ([#98](https://github.com/AdrianoGuzzo/MyTaskApp/issues/98)) ([2df9d60](https://github.com/AdrianoGuzzo/MyTaskApp/commit/2df9d60355e6d2af99ca07c7805b11cc7e5a4ed5))
* **server:** push recusa baseVersion negativa ou à frente do servidor (SEC-017) ([#120](https://github.com/AdrianoGuzzo/MyTaskApp/issues/120)) ([01e21a2](https://github.com/AdrianoGuzzo/MyTaskApp/commit/01e21a255eb589c5d0eb54a095f1f3a411ad91e6))
* **server:** push recusa campo fora do contrato e limita entidade e workspace (SEC-006, ADR-072) ([#101](https://github.com/AdrianoGuzzo/MyTaskApp/issues/101)) ([5f67c4c](https://github.com/AdrianoGuzzo/MyTaskApp/commit/5f67c4c6ecc50ed6ebaf4f55ccd7cd15fb8ff823))
* **server:** rate limit da API por conta, e não por IP (SEC-003, ADR-069) ([#95](https://github.com/AdrianoGuzzo/MyTaskApp/issues/95)) ([66eb4f7](https://github.com/AdrianoGuzzo/MyTaskApp/commit/66eb4f738c63b725be4369817bdee404b9d7b06d))
* **server:** returnUrl com caractere de controle vira "/" e logout em HTTP dá 400 (SEC-013) ([#105](https://github.com/AdrianoGuzzo/MyTaskApp/issues/105)) ([01fbfd5](https://github.com/AdrianoGuzzo/MyTaskApp/commit/01fbfd52963554f11b63a00e6eabdf311d7c1412))
* **server:** senha do banco de desenvolvimento por variável e user-secrets (SEC-012) ([#106](https://github.com/AdrianoGuzzo/MyTaskApp/issues/106)) ([f4fd593](https://github.com/AdrianoGuzzo/MyTaskApp/commit/f4fd5932f0850027c95427b8326044bc88e1e9c7))
* **server:** teto de dispositivos ativos por conta e textos do dispositivo sem Cc/Cf (SEC-015) ([#119](https://github.com/AdrianoGuzzo/MyTaskApp/issues/119)) ([82a118d](https://github.com/AdrianoGuzzo/MyTaskApp/commit/82a118d71c32ae738b531a2a6fddaf80abb857db))

## [1.17.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.16.0...v1.17.0) (2026-10-17)


### Added

* app do celular em .NET MAUI como mais um dispositivo da sincronização (ADR-067) ([#92](https://github.com/AdrianoGuzzo/MyTaskApp/issues/92)) ([8a8d7fc](https://github.com/AdrianoGuzzo/MyTaskApp/commit/8a8d7fc0f03bccd1eb2470a0e43a2b6f5a9347ee))

## [1.16.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.15.0...v1.16.0) (2026-10-16)


### Added

* comandos globais só sincronizam quando marcados para compartilhar (ADR-066) ([#91](https://github.com/AdrianoGuzzo/MyTaskApp/issues/91)) ([f3dc243](https://github.com/AdrianoGuzzo/MyTaskApp/commit/f3dc2436b4d69ae4cc18dfb1eb40a109367696d7))
* limpeza de worktrees sem tarefa ou de tarefa encerrada (ADR-065) ([#89](https://github.com/AdrianoGuzzo/MyTaskApp/issues/89)) ([a21e4aa](https://github.com/AdrianoGuzzo/MyTaskApp/commit/a21e4aa47426648a203a13d04a86c5fddabb7949))

## [1.15.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.14.1...v1.15.0) (2026-10-15)


### Added

* repositório Git no diretório da etiqueta, com clone e URL que acompanha a pasta (ADR-064) ([#88](https://github.com/AdrianoGuzzo/MyTaskApp/issues/88)) ([3deaf3b](https://github.com/AdrianoGuzzo/MyTaskApp/commit/3deaf3b2cb6f641c01a5652bd4a808683528905a))
* tema Ponta passa a se chamar Verde Pecuária ([#85](https://github.com/AdrianoGuzzo/MyTaskApp/issues/85)) ([9315d1d](https://github.com/AdrianoGuzzo/MyTaskApp/commit/9315d1df433aa42c1d2946e7afa6748903b1454b))


### Fixed

* botões do cabeçalho saíam da pílula no modo recolhido ([#86](https://github.com/AdrianoGuzzo/MyTaskApp/issues/86)) ([07aa53c](https://github.com/AdrianoGuzzo/MyTaskApp/commit/07aa53c526c97cc50e359c2d4d77e50bd7b1f6f9))
* o @ da anotação acompanha a etiqueta marcada na janela da tarefa ([#84](https://github.com/AdrianoGuzzo/MyTaskApp/issues/84)) ([ac47154](https://github.com/AdrianoGuzzo/MyTaskApp/commit/ac471544a5d8765c05fe092aca2bffb43c730f51))

## [1.14.1](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.14.0...v1.14.1) (2026-10-14)


### Changed

* organização interna sem mudar o comportamento, e seis defeitos corrigidos ([#82](https://github.com/AdrianoGuzzo/MyTaskApp/issues/82)) ([9803845](https://github.com/AdrianoGuzzo/MyTaskApp/commit/9803845e9da973ba69576e7675e596686a8df4a1))

## [1.14.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.13.0...v1.14.0) (2026-10-14)


### Added

* bolinha de status da sincronização no cabeçalho (ADR-063) ([#80](https://github.com/AdrianoGuzzo/MyTaskApp/issues/80)) ([7f48341](https://github.com/AdrianoGuzzo/MyTaskApp/commit/7f483419127a571f9ead77c0ca333c353b7af3a4))

## [1.13.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.12.0...v1.13.0) (2026-10-13)


### Added

* cada computador com a sua lista, e enviar ou compartilhar a tarefa entre eles (ADR-062) ([#78](https://github.com/AdrianoGuzzo/MyTaskApp/issues/78)) ([7455470](https://github.com/AdrianoGuzzo/MyTaskApp/commit/74554707950f0ce7fe43b72235e65064d8393850))

## [1.12.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.11.0...v1.12.0) (2026-10-13)


### Added

* sincronização opcional entre computadores (ADR-061) ([#76](https://github.com/AdrianoGuzzo/MyTaskApp/issues/76)) ([77a46ae](https://github.com/AdrianoGuzzo/MyTaskApp/commit/77a46aed9b9e74196cbcc8f735ef845bd6e4dda6))

## [1.11.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.10.0...v1.11.0) (2026-10-13)


### Added

* cópia do banco na aba Desenvolvimento e {database} nos comandos ([#75](https://github.com/AdrianoGuzzo/MyTaskApp/issues/75)) ([39adee0](https://github.com/AdrianoGuzzo/MyTaskApp/commit/39adee03b5abbbcb454d0a25d55a670affd9ade9))
* escolher as etiquetas na janela da tarefa, abaixo do título ([#73](https://github.com/AdrianoGuzzo/MyTaskApp/issues/73)) ([e3668ad](https://github.com/AdrianoGuzzo/MyTaskApp/commit/e3668ad17d8d14a38f80d76a587d164ced896ab0))

## [1.10.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.9.0...v1.10.0) (2026-10-10)


### Added

* banco escolhido na cópia e anonimização na própria consulta ([#69](https://github.com/AdrianoGuzzo/MyTaskApp/issues/69)) ([d277ae6](https://github.com/AdrianoGuzzo/MyTaskApp/commit/d277ae697b761248ba7bfd7cc9683970514cc309))
* servidor MCP local para operar tarefas, horas e perfis de banco pelo Claude Code ([#71](https://github.com/AdrianoGuzzo/MyTaskApp/issues/71)) ([2ef6cf6](https://github.com/AdrianoGuzzo/MyTaskApp/commit/2ef6cf6b47d95a448f8f750902783f602c538a0c))

## [1.9.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.8.0...v1.9.0) (2026-10-09)


### Added

* bancos de dados, cópia de produção anonimizada com política de segurança ([#68](https://github.com/AdrianoGuzzo/MyTaskApp/issues/68)) ([dc8ad18](https://github.com/AdrianoGuzzo/MyTaskApp/commit/dc8ad18ebd9dd4f5ba830db5b372fa01006bd120))
* horário e data digitáveis no lançamento de tempo, 0831 é 08:31 ([#66](https://github.com/AdrianoGuzzo/MyTaskApp/issues/66)) ([7694d3e](https://github.com/AdrianoGuzzo/MyTaskApp/commit/7694d3eee044295e0b1749ff03701d35c8889322))

## [1.8.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.7.0...v1.8.0) (2026-10-08)


### Added

* comando só do diretório da etiqueta, sem precisar ser global ([#63](https://github.com/AdrianoGuzzo/MyTaskApp/issues/63)) ([22ebe7f](https://github.com/AdrianoGuzzo/MyTaskApp/commit/22ebe7fe88b1c2e77699367ef1371b3dbc82b4cf))
* post-its, anotações flutuantes que viram tarefa com um clique ([#64](https://github.com/AdrianoGuzzo/MyTaskApp/issues/64)) ([6af2262](https://github.com/AdrianoGuzzo/MyTaskApp/commit/6af226293f74c26ea0d6ea6a2393fb90fe8ac6e9))

## [1.7.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.6.0...v1.7.0) (2026-10-07)


### Added

* histórico dos últimos 7 dias, com o concluído e o trabalhado em cada dia ([#62](https://github.com/AdrianoGuzzo/MyTaskApp/issues/62)) ([ab49824](https://github.com/AdrianoGuzzo/MyTaskApp/commit/ab498247364ba4c6efd924cfe35afed6377ec850))
* tempo trabalhado por tarefa, com cronômetro e lançamento manual ([#60](https://github.com/AdrianoGuzzo/MyTaskApp/issues/60)) ([a057465](https://github.com/AdrianoGuzzo/MyTaskApp/commit/a0574657012f227bd441733601083a2c9e8dbfc6))

## [1.6.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.5.0...v1.6.0) (2026-10-06)


### Added

* comandos rápidos, o comando global vira botão no worktree da tarefa ([#58](https://github.com/AdrianoGuzzo/MyTaskApp/issues/58)) ([01bd23b](https://github.com/AdrianoGuzzo/MyTaskApp/commit/01bd23b46174c4a12c2df33922f3da3f2fdb4d43))

## [1.5.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.4.0...v1.5.0) (2026-10-05)


### Added

* prazos para tarefas longas, com seção PRAZOS e avisos por degrau ([#56](https://github.com/AdrianoGuzzo/MyTaskApp/issues/56)) ([93c0a22](https://github.com/AdrianoGuzzo/MyTaskApp/commit/93c0a22fbad51934f002d636b92540953e2e9ef9))

## [1.4.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.3.0...v1.4.0) (2026-10-04)


### Added

* aba Desenvolvimento já vem com o repositório quando a etiqueta tem um só ([#52](https://github.com/AdrianoGuzzo/MyTaskApp/issues/52)) ([29f010d](https://github.com/AdrianoGuzzo/MyTaskApp/commit/29f010d09c76e97140711daaef0af5395ee5a0db))
* instalador avisa e fecha o MyTaskApp aberto antes de instalar ([#54](https://github.com/AdrianoGuzzo/MyTaskApp/issues/54)) ([f6c6983](https://github.com/AdrianoGuzzo/MyTaskApp/commit/f6c6983394882d65ad9b044036c66af58ec49b65))
* tema Ponta, escuro na identidade verde da Ponta ([#55](https://github.com/AdrianoGuzzo/MyTaskApp/issues/55)) ([a587b25](https://github.com/AdrianoGuzzo/MyTaskApp/commit/a587b255f020e161e256c7a2d35895680b01afb6))


### Fixed

* abrir a tarefa já aberta traz a janela de volta, mesmo minimizada ([#51](https://github.com/AdrianoGuzzo/MyTaskApp/issues/51)) ([bc73ef9](https://github.com/AdrianoGuzzo/MyTaskApp/commit/bc73ef9548a7ad8dc2be8153f30fb0adc5a3b73e))

## [1.3.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.2.1...v1.3.0) (2026-10-04)


### Added

* modo HUD substitui o pino transparente, com comportamento da janela configurável ([#49](https://github.com/AdrianoGuzzo/MyTaskApp/issues/49)) ([9a1be6a](https://github.com/AdrianoGuzzo/MyTaskApp/commit/9a1be6a0a14063868d2ed9a5d6f872ccc5e45cdf))
* mostra a PR aberta da branch na aba Desenvolvimento ([#48](https://github.com/AdrianoGuzzo/MyTaskApp/issues/48)) ([6198054](https://github.com/AdrianoGuzzo/MyTaskApp/commit/6198054e1643589124a8a77126efb2c878a5585a))

## [1.2.1](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.2.0...v1.2.1) (2026-10-03)


### Fixed

* ícones do cabeçalho e da lista aparecem no Linux ([#46](https://github.com/AdrianoGuzzo/MyTaskApp/issues/46)) ([0c0269e](https://github.com/AdrianoGuzzo/MyTaskApp/commit/0c0269e3e2371670374014b9d3b61fc8f90b770d))

## [1.2.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.1.0...v1.2.0) (2026-10-03)


### Added

* integração com Jira Cloud — busca ao digitar, vínculo, branch e contexto da tarefa ([#44](https://github.com/AdrianoGuzzo/MyTaskApp/issues/44)) ([4aba37f](https://github.com/AdrianoGuzzo/MyTaskApp/commit/4aba37f1ed2fd9befa21f6e96bf73c073df73640))

## [1.1.0](https://github.com/AdrianoGuzzo/MyTaskApp/compare/v1.0.0...v1.1.0) (2026-10-03)


### Added

* versionar releases com Release Please, changelog e checksums ([#40](https://github.com/AdrianoGuzzo/MyTaskApp/issues/40)) ([9f5bda6](https://github.com/AdrianoGuzzo/MyTaskApp/commit/9f5bda63adbd9917d371f841fb34872162c73045))
