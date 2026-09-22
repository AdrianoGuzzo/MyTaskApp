; MyTaskApp -- instalador Windows (ADR-018)
;
; Compilado por installer/windows/build.ps1, que passa a versao e o diretorio
; publicado. Nao compile a mao sem definir os dois:
;
;   ISCC.exe MyTaskApp.iss /DAppVersion=1.0.0 /DAppVersionFull=1.0.0.0 ^
;            /DPublishDir=..\..\artifacts\publish\win-x64
;
; Regra que nao se negocia: este arquivo NAO conhece o banco de dados.
; O instalador copia binarios; quem cria e evolui o schema e a aplicacao, via
; migrations do EF Core, no primeiro start (ADR-005).

#ifndef AppVersion
  #error AppVersion nao foi definido. Use installer/windows/build.ps1.
#endif

#ifndef AppVersionFull
  #define AppVersionFull AppVersion + ".0"
#endif

#ifndef PublishDir
  #define PublishDir "..\..\artifacts\publish\win-x64"
#endif

#define AppName        "MyTaskApp"
#define AppPublisher   "MyTaskApp"
#define AppExeName     "MyTaskApp.exe"
#define AppUrl         "https://github.com/mytaskapp/mytaskapp"

; O mesmo nome que src/MyTaskApp.Desktop/Composition/SingleInstance.cs cria.
; Ha teste de packaging cobrando os dois lados: se um mudar sem o outro, a
; build quebra antes de alguem descobrir em producao.
#define AppMutexName   "MyTaskApp.SingleInstance"

; Onde o Windows procura o que abrir no login. Relativo a HKCU -- ver [Registry].
#define AppRunKey      "Software\Microsoft\Windows\CurrentVersion\Run"

; O mesmo argumento que src/MyTaskApp.Desktop/Composition/LaunchOptions.cs
; conhece, e pelo mesmo motivo do AppMutexName: ha teste de packaging cobrando
; os dois lados. Sem ele o app subiria no login com a janela na cara do usuario.
#define StartupFlag    "--startup"

[Setup]
; AppId identifica o produto para o Windows. Fixo para sempre: e ele que faz
; 1.1 atualizar 1.0 em vez de virar uma segunda instalacao ao lado.
AppId={{8F3A6C24-51E7-4E8B-9A4D-2B7C1F0D9E63}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
VersionInfoVersion={#AppVersionFull}
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

; Sem UAC por padrao. O usuario pode pedir instalacao para todos pela linha de
; comando (/ALLUSERS) ou pelo dialogo, e so ai o Windows eleva.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline

; Um app rodando trava os proprios binarios. AppMutex e a deteccao confiavel
; (o processo pode estar ocioso na bandeja, com os arquivos abertos);
; CloseApplications e a rede de seguranca para o resto.
AppMutex={#AppMutexName}
SetupMutex={#AppName}Setup
CloseApplications=yes
RestartApplications=no

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

OutputDir=..\..\artifacts\installer
OutputBaseFilename=MyTaskAppSetup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
InternalCompressLevel=max

; Assistente curto: Diretorio -> Opcoes -> Instalando -> Concluido.
WizardStyle=modern
DisableWelcomePage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
ShowLanguageDialog=no
SetupIconFile=assets\MyTaskApp.ico
WizardImageFile=assets\wizard-large.bmp
WizardSmallImageFile=assets\wizard-small.bmp
WizardImageStretch=no

; Log sempre, nao so com /LOG. "Nao instalou e nao sei por que" e exatamente a
; situacao em que ninguem lembra de ligar o log antes.
SetupLogging=yes

; Assinatura digital: descomente quando houver certificado configurado no
; Inno (Tools > Configure Sign Tools). build.ps1 -Sign passa o mesmo nome.
; SignTool=mytaskapp
; SignedUninstaller=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[CustomMessages]
brazilianportuguese.DesktopIcon=Criar um atalho na área de trabalho
brazilianportuguese.LaunchApp=Iniciar o MyTaskApp
brazilianportuguese.StartupGroup=Ao iniciar o Windows:
brazilianportuguese.StartupTask=Iniciar o MyTaskApp com o Windows (recolhido na bandeja)
brazilianportuguese.OtherScopeInstalled=Já existe uma instalação do MyTaskApp %1 nesta máquina.%n%nDesinstale-a primeiro para evitar duas cópias ao mesmo tempo.
brazilianportuguese.DowngradeWarning=A versão instalada (%1) é mais recente que esta (%2).%n%nInstalar assim mesmo?
brazilianportuguese.RemoveDataPrompt=Remover também suas tarefas, lembretes e ajustes?%n%nEles estão em:%n%1%n%nEscolha Não para manter seus dados — você poderá reinstalar o MyTaskApp e continuar de onde parou.
brazilianportuguese.DataKept=Seus dados continuam em:%n%1

[Tasks]
; Desmarcado de proposito: o Menu Iniciar ja garante o acesso, e area de
; trabalho cheia de icone e ruido que o usuario nao pediu.
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

; Marcada de proposito -- o oposto do atalho acima, e a diferenca tem motivo:
; "o sistema fica responsavel por me lembrar" so e verdade se o processo estiver
; vivo as 15:00 (ADR-016). Quem nao quer desmarca aqui; quem mudar de ideia
; depois usa o menu do proprio app, sem reinstalar nada.
Name: "startupicon"; Description: "{cm:StartupTask}"; GroupDescription: "{cm:StartupGroup}"

[Files]
; ignoreversion: numa atualizacao todo binario e substituido pelo da versao
; nova, inclusive os que o .NET nao versiona.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Comment: "Suas tarefas, sempre à vista"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; O suficiente para saber o que esta instalado e onde -- e nada alem disso.
; uninsdeletekey: a chave morre junto com a desinstalacao.
Root: HKA; Subkey: "Software\{#AppName}"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\{#AppName}"; ValueType: string; ValueName: "Version"; ValueData: "{#AppVersion}"
Root: HKA; Subkey: "Software\{#AppName}"; ValueType: string; ValueName: "DataPath"; ValueData: "{userappdata}\{#AppName}"

; Iniciar com o Windows (ADR-023). HKCU sempre, e nunca HKA: com /ALLUSERS o
; HKA viraria HKLM, e o app -- que roda sem elevacao -- conseguiria ler a
; entrada mas nunca apagar. O menu viraria um interruptor que so liga.
; As aspas sao dobradas porque o caminho de instalacao tem espacos.
Root: HKCU; Subkey: "{#AppRunKey}"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExeName}"" {#StartupFlag}"; Flags: uninsdeletevalue; Tasks: startupicon

; Desmarcar numa atualizacao precisa APAGAR o valor. Sem esta linha a caixa
; desmarcada nao faria nada e o app continuaria subindo no login.
Root: HKCU; Subkey: "{#AppRunKey}"; ValueType: none; ValueName: "{#AppName}"; Flags: deletevalue; Tasks: not startupicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

; -----------------------------------------------------------------------------
; [UninstallDelete] esta VAZIO de proposito, e precisa continuar assim.
;
; O Inno remove o que instalou -- isto e, apenas {app}. Os dados do usuario
; ficam em {userappdata}\MyTaskApp e NAO sao tocados por uma desinstalacao
; normal. A unica remocao de dados que existe neste arquivo esta em
; RemoveUserData(), abaixo, e so roda apos confirmacao explicita.
; -----------------------------------------------------------------------------

[Code]

const
  InstallerLogFolder = '{localappdata}\MyTaskApp\installer\logs';

var
  { A caixa "iniciar com o Windows" ja foi acertada pelo estado do registro? }
  StartupPreselected: Boolean;

{ Onde o app guarda banco, widget.json, logs e appsettings.user.json. }
function UserDataDir(): String;
begin
  Result := ExpandConstant('{userappdata}\MyTaskApp');
end;

{ Le a versao registrada, procurando na raiz indicada. Vazio = nao instalado. }
function InstalledVersion(RootKey: Integer): String;
begin
  if not RegQueryStringValue(RootKey, 'Software\MyTaskApp', 'Version', Result) then
    Result := '';
end;

{
  Uma instalacao por maquina e uma por usuario coexistiriam: dois atalhos, duas
  entradas em "Aplicativos Instalados" e dois processos disputando o mesmo
  banco. Barrar na entrada e mais barato do que explicar depois.
}
function OtherScopeIsInstalled(var Version: String): Boolean;
begin
  if IsAdminInstallMode() then
    Version := InstalledVersion(HKEY_CURRENT_USER)
  else
    Version := InstalledVersion(HKEY_LOCAL_MACHINE);

  Result := Version <> '';
end;

function InitializeSetup(): Boolean;
var
  Installed, Other: String;
  Current, Previous: Int64;
begin
  Result := True;

  if OtherScopeIsInstalled(Other) then
  begin
    SuppressibleMsgBox(FmtMessage(CustomMessage('OtherScopeInstalled'), [Other]),
      mbError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;

  { Downgrade nao e proibido -- as vezes e justamente o que se quer depois de
    uma versao ruim. Mas nao pode acontecer por acidente. }
  Installed := InstalledVersion(HKEY_AUTO);

  if (Installed <> '') and StrToVersion(Installed, Previous)
     and StrToVersion('{#AppVersion}', Current) then
  begin
    if ComparePackedVersion(Previous, Current) > 0 then
    begin
      Result := SuppressibleMsgBox(
        FmtMessage(CustomMessage('DowngradeWarning'), [Installed, '{#AppVersion}']),
        mbConfirmation, MB_YESNO, IDNO) = IDYES;
    end;
  end;
end;

{
  A verdade sobre "inicia com o Windows" e o valor em Run -- nunca o que foi
  marcado na instalacao anterior. O app escreve na mesma chave, pelo menu.
}
function StartupIsEnabled(): Boolean;
var
  Command: String;
begin
  Result := RegQueryStringValue(HKEY_CURRENT_USER, '{#AppRunKey}', '{#AppName}', Command)
            and (Command <> '');
end;

{
  Instalacao nova: vale o padrao marcado do [Tasks]. Atualizacao: a caixa mostra
  o estado de HOJE.

  Sem isto o UsePreviousTasks (ligado por padrao) reimporia a escolha gravada
  pelo instalador anterior, desfazendo em silencio um "desliga isso" que o
  usuario tivesse feito pelo menu do app. O item e localizado pelo texto, mas os
  dois lados saem da mesma CustomMessage -- nao ha como divergirem.

  (Sem chaves no texto acima de proposito: comentario Pascal nao aninha, e um
  "cm:" entre chaves fecharia este comentario no meio.)
}
procedure PreselectStartup();
var
  Index: Integer;
  Caption: String;
begin
  { Uma vez so: depois da primeira exibicao a caixa e do usuario, e voltar a
    pagina nao pode desfazer o clique dele. }
  if StartupPreselected then
    Exit;

  StartupPreselected := True;

  if InstalledVersion(HKEY_AUTO) = '' then
    Exit;

  Caption := ExpandConstant('{cm:StartupTask}');

  for Index := 0 to WizardForm.TasksList.Items.Count - 1 do
  begin
    if WizardForm.TasksList.Items[Index] = Caption then
    begin
      WizardForm.TasksList.Checked[Index] := StartupIsEnabled();
      Exit;
    end;
  end;
end;

{
  Em CurPageChanged, e nao em InitializeWizard: e o UsePreviousTasks que restaura
  a selecao da instalacao anterior, e ele age ate a pagina aparecer. Escrever
  antes disso seria escrever para ser sobrescrito.
}
procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectTasks then
    PreselectStartup();
end;

{
  O log do Inno nasce em %TEMP% com nome aleatorio -- ninguem acha. Uma copia
  numa pasta previsivel e o que transforma "nao instalou" em um arquivo para
  anexar. Fica em LocalAppData, subarvore separada dos logs da aplicacao
  (que ficam em Roaming): quem procura um nunca esbarra no outro.
}
procedure CopySetupLog();
var
  Folder, Target: String;
begin
  if ExpandConstant('{log}') = '' then
    Exit;

  Folder := ExpandConstant(InstallerLogFolder);

  if not ForceDirectories(Folder) then
    Exit;

  Target := Folder + '\install-' + GetDateTimeString('yyyymmdd-hhnnss', '-', '-') + '.log';

  { Sem tratar falha: copiar log nao pode derrubar uma instalacao que deu certo. }
  FileCopy(ExpandConstant('{log}'), Target, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    CopySetupLog();
end;

{
  A UNICA remocao de dados do produto. Chamada em um lugar so, sempre depois de
  uma confirmacao explicita. Se alguem precisar mexer aqui, e para dificultar,
  nunca para facilitar.
}
procedure RemoveUserData();
begin
  DelTree(UserDataDir(), True, True, True);
end;

{
  Silencioso: preserva por padrao, apaga so com /DELETEDATA=1. Nao e a mesma
  pergunta feita de outro jeito -- e a recusa de adivinhar. Uma desinstalacao
  automatizada que apagasse dados por omissao seria irreversivel.
}
function ShouldRemoveUserData(): Boolean;
begin
  if not DirExists(UserDataDir()) then
  begin
    Result := False;
    Exit;
  end;

  if UninstallSilent() then
  begin
    Result := ExpandConstant('{param:DELETEDATA|0}') = '1';
    Exit;
  end;

  { Default IDNO: um Enter distraido mantem os dados. }
  Result := MsgBox(FmtMessage(CustomMessage('RemoveDataPrompt'), [UserDataDir()]),
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  { Sai sempre, sem perguntar: registro de aplicacao nao e dado do usuario. O
    valor pode ter sido criado pelo menu do app, e ai nao existe registro de
    desinstalacao para o uninsdeletevalue apagar -- sobraria no Run um caminho
    para um exe que nao existe mais, falhando calado a cada login. }
  RegDeleteValue(HKEY_CURRENT_USER, '{#AppRunKey}', '{#AppName}');

  if ShouldRemoveUserData() then
    RemoveUserData()
  else if (not UninstallSilent()) and DirExists(UserDataDir()) then
    MsgBox(FmtMessage(CustomMessage('DataKept'), [UserDataDir()]), mbInformation, MB_OK);
end;
