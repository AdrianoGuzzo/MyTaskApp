; MyTaskApp -- instalador Windows (ADR-018)
;
; Compilado por installer/windows/build.ps1, que passa a versao e o diretorio
; publicado. Nao compile a mao sem definir os dois:
;
;   ISCC.exe MyTaskApp.iss /DAppVersion=1.0.0 /DAppVersionFull=1.0.0.0 ^
;            /DAppRuntime=win-x64 /DPublishDir=..\..\artifacts\publish\win-x64
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

#ifndef AppRuntime
  #define AppRuntime "win-x64"
#endif

#ifndef PublishDir
  #define PublishDir "..\..\artifacts\publish\win-x64"
#endif

#define AppName        "MyTaskApp"
#define AppPublisher   "MyTaskApp"
#define AppExeName     "MyTaskApp.exe"
#define AppUrl         "https://github.com/AdrianoGuzzo/MyTaskApp"

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

; Um app rodando trava os proprios binarios. Sem AppMutex de proposito: ele so
; bloqueia e pede para o usuario fechar o app sozinho -- e o app vive na
; bandeja, onde ninguem acha. CloseRunningApp() (no [Code]) avisa e fecha tudo
; que roda de dentro de {app}; o Restart Manager, com force e *.*, cobre
; qualquer outro processo segurando um arquivo da pasta (ADR-049).
SetupMutex={#AppName}Setup
CloseApplications=force
CloseApplicationsFilter=*.*
RestartApplications=no

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

OutputDir=..\..\artifacts\installer
; Nome previsivel, igual nas duas plataformas: produto-versao-rid. O "-setup"
; diz que e instalador, nao binario solto (ADR-044).
OutputBaseFilename=MyTaskApp-{#AppVersion}-{#AppRuntime}-setup
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
brazilianportuguese.AppWillCloseSetup=O MyTaskApp está aberto e será fechado para continuar a instalação.%n%1%n%nSuas tarefas e lembretes já estão salvos. Clique em OK para fechar e continuar, ou em Cancelar para não instalar agora.
brazilianportuguese.AppWillCloseUninstall=O MyTaskApp está aberto e será fechado para continuar a desinstalação.%n%1%n%nClique em OK para fechar e continuar, ou em Cancelar para não desinstalar agora.
brazilianportuguese.AppCloseDeclined=O MyTaskApp continua aberto, então a instalação não pode continuar.%n%nRode o instalador de novo quando puder fechá-lo.
brazilianportuguese.AppCloseFailed=Não foi possível fechar o MyTaskApp:%n%1%n%nEncerre-o pelo Gerenciador de Tarefas e tente de novo.

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

; Atualizacao silenciosa que fechou o app: ele volta como estava, na bandeja.
; Um app de lembretes fechado por uma atualizacao automatica nao lembraria de
; mais nada ate alguem notar (ADR-016). Com assistente, quem decide e a caixa
; acima. runasoriginaluser: com /ALLUSERS o Setup esta elevado, e o app nao.
Filename: "{app}\{#AppExeName}"; Parameters: "{#StartupFlag}"; Flags: nowait runasoriginaluser; Check: ShouldReopenClosedApp

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

  { Quanto esperar o processo morrer depois do taskkill, e de quanto em quanto
    conferir. Morrer leva milissegundos; o teto existe para nao travar o
    assistente se algo estranho segurar o processo. }
  CloseTimeoutMs = 10000;
  ClosePollMs = 250;

var
  { A caixa "iniciar com o Windows" ja foi acertada pelo estado do registro? }
  StartupPreselected: Boolean;

  { O Setup fechou um MyTaskApp aberto? Decide se ele volta depois. }
  AppClosedBySetup: Boolean;

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
  Processos cujo executavel mora dentro de Dir: o MyTaskApp na bandeja e
  qualquer outro binario da pasta. Pelo caminho, e nao pelo nome, de proposito:
  um MyTaskApp rodando de outro lugar (uma build de desenvolvimento) nao segura
  nenhum arquivo daqui e nao tem por que morrer.

  O desinstalador fica de fora: o unins000.exe que o usuario abriu mora na
  pasta do app, e mata-lo seria interromper a propria desinstalacao.

  False = nao deu para perguntar ao Windows (WMI indisponivel).

  (Sem chaves no texto deste bloco e dos de baixo de proposito: comentario
  Pascal nao aninha, e o nome da constante da pasta entre chaves fecharia o
  comentario no meio.)
}
function FindProcessesIn(const Dir: String; var Pids: TArrayOfString;
  var Names: String): Boolean;
var
  Locator, Service, Processes, Process: Variant;
  Prefix, Path, Name: String;
  Index, Count, Pid: Integer;
begin
  Result := False;
  SetArrayLength(Pids, 0);
  Names := '';
  Prefix := AnsiLowercase(AddBackslash(Dir));

  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Service.ExecQuery(
      'SELECT ProcessId, Name, ExecutablePath FROM Win32_Process WHERE ExecutablePath IS NOT NULL');

    for Index := 0 to Processes.Count - 1 do
    begin
      Process := Processes.ItemIndex(Index);
      Path := Process.ExecutablePath;
      Name := Process.Name;

      if (Pos(Prefix, AnsiLowercase(Path)) = 1)
         and (Pos('unins', AnsiLowercase(Name)) <> 1) then
      begin
        Pid := Process.ProcessId;
        Count := GetArrayLength(Pids);
        SetArrayLength(Pids, Count + 1);
        Pids[Count] := IntToStr(Pid);
        Names := Names + #13#10 + '    ' + Name + ' (PID ' + Pids[Count] + ')';
      end;
    end;

    Result := True;
  except
    Log('Nao foi possivel listar os processos: ' + GetExceptionMessage);
  end;
end;

{ Ainda tem MyTaskApp de pe? Sem a lista de processos, o mutex responde. }
function AppStillRunning(const Dir: String; var Pids: TArrayOfString;
  var Names: String): Boolean;
begin
  if FindProcessesIn(Dir, Pids, Names) then
    Result := GetArrayLength(Pids) > 0
  else
    Result := CheckForMutexes('{#AppMutexName}');
end;

{
  Forcado, e sem /T. Forcado porque fechar a janela so a manda para a bandeja
  (ADR-016); e o app que cuida dos dados, e uma gravacao interrompida e
  desfeita por ele no proximo start (ADR-049). Sem /T porque os filhos do app -- agentes, terminais, gh -- sao
  trabalho do usuario e nao rodam de dentro da pasta do app.
}
procedure KillProcesses(const Pids: TArrayOfString; const Listed: Boolean);
var
  Index, ResultCode: Integer;
begin
  if not Listed then
  begin
    { Sem WMI, o que resta e o nome. So chega aqui com o mutex tomado. }
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExeName}', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Log(Format('taskkill /F /IM {#AppExeName}: codigo %d', [ResultCode]));
    Exit;
  end;

  for Index := 0 to GetArrayLength(Pids) - 1 do
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /PID ' + Pids[Index], '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Log(Format('taskkill /F /PID %s: codigo %d', [Pids[Index], ResultCode]));
  end;
end;

{
  Avisa e fecha o que estiver rodando de dentro de Dir, antes de trocar ou
  apagar binarios. O aviso e a escolha do usuario; em modo silencioso o padrao
  (OK) vale, porque uma atualizacao automatica que parasse num app aberto nunca
  terminaria.

  Retorna '' com a pasta livre; senao, o motivo para mostrar ao usuario.
}
function CloseRunningApp(const Dir, WarningMessage: String): String;
var
  Pids: TArrayOfString;
  Names: String;
  Listed: Boolean;
  Waited: Integer;
begin
  Result := '';

  { Instalacao nova: nada pode estar rodando de uma pasta que nao existe, e
    perguntar ao WMI custa uns dois segundos. }
  if not DirExists(Dir) then
    Exit;

  Listed := FindProcessesIn(Dir, Pids, Names);

  if Listed and (GetArrayLength(Pids) = 0) then
  begin
    if CheckForMutexes('{#AppMutexName}') then
      Log('Ha um MyTaskApp aberto fora de ' + Dir + '; ele nao segura arquivos daqui.');
    Exit;
  end;

  if (not Listed) and (not CheckForMutexes('{#AppMutexName}')) then
    Exit;

  Log('MyTaskApp aberto em ' + Dir + ':' + Names);

  if SuppressibleMsgBox(FmtMessage(CustomMessage(WarningMessage), [Names]),
       mbInformation, MB_OKCANCEL, IDOK) <> IDOK then
  begin
    Result := CustomMessage('AppCloseDeclined');
    Exit;
  end;

  KillProcesses(Pids, Listed);
  AppClosedBySetup := True;

  Waited := 0;

  while AppStillRunning(Dir, Pids, Names) do
  begin
    if Waited >= CloseTimeoutMs then
    begin
      Result := FmtMessage(CustomMessage('AppCloseFailed'), [Names]);
      Log('MyTaskApp continua aberto depois do taskkill:' + Names);
      Exit;
    end;

    Sleep(ClosePollMs);
    Waited := Waited + ClosePollMs;
  end;

  Log('MyTaskApp fechado para liberar ' + Dir + '.');
end;

{
  Antes de o Restart Manager olhar os arquivos em uso (CloseApplications): o
  que sobrar para ele ja nao e o MyTaskApp. Aqui, e nao em InitializeSetup, por
  dois motivos: a pasta so e conhecida depois da pagina de diretorio, e quem desiste
  no meio do assistente nao pode ter perdido o app aberto por nada.
}
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := CloseRunningApp(ExpandConstant('{app}'), 'AppWillCloseSetup');
end;

{ Com assistente, quem decide e a caixa "Iniciar o MyTaskApp". }
function ShouldReopenClosedApp(): Boolean;
begin
  Result := AppClosedBySetup and WizardSilent();
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

{
  usAppMutexCheck: depois do "tem certeza?" e antes de apagar qualquer arquivo.
  Abort aqui encerra o desinstalador sem tocar em nada.
}
procedure CloseRunningAppBeforeUninstall();
var
  Problem: String;
begin
  Problem := CloseRunningApp(ExpandConstant('{app}'), 'AppWillCloseUninstall');

  if Problem = '' then
    Exit;

  { Quem clicou em Cancelar ja sabe o que escolheu: nao ha o que avisar. }
  if Problem <> CustomMessage('AppCloseDeclined') then
    SuppressibleMsgBox(Problem, mbError, MB_OK, IDOK);

  Abort;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usAppMutexCheck then
  begin
    CloseRunningAppBeforeUninstall();
    Exit;
  end;

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
