<#
.SYNOPSIS
    Liga o emulador Android (se preciso), compila o app do celular e abre nele.

.DESCRIPTION
    Junta num comando so os passos de docs/mobile.md ("Rodar no Android"):

      1. usa o SDK do .NET instalado por usuario (%LOCALAPPDATA%\Microsoft\dotnet),
         quando existir;
      2. reaproveita um emulador ou celular ja conectado, ou liga o AVD e espera
         o Android terminar de iniciar;
      3. tira a primeira execucao do Chrome do caminho do login;
      4. sem -SyncApiBaseUrl, faz o adb reverse da 5280 para o servidor local;
      5. compila em Debug com -t:Run, que instala e abre o app.

    Debug usa Fast Deployment: o APK sozinho nao roda. Por isso a instalacao e
    sempre pelo -t:Run (ou -t:Install), nunca por adb install.

.PARAMETER Avd
    Emulador a ligar quando nao ha aparelho conectado. Lista: emulator -list-avds.

.PARAMETER SyncApiBaseUrl
    Servidor de sincronizacao gravado no app. Vazio usa o padrao do Debug,
    http://localhost:5280/, com adb reverse para o computador. Tambem pode vir
    da variavel de ambiente MYTASKAPP_MOBILE_SYNC_URL.

.PARAMETER Serial
    Aparelho alvo (adb devices), quando ha mais de um conectado.

.PARAMETER AndroidSdk
    Pasta do Android SDK, com a plataforma 36.

.PARAMETER JavaSdk
    Pasta do JDK 21. Vazio procura o que vem com o Visual Studio.

.PARAMETER InstallOnly
    Instala sem abrir o app (-t:Install).

.EXAMPLE
    .\scripts\run-android.ps1

.EXAMPLE
    .\scripts\run-android.ps1 -SyncApiBaseUrl https://sync.seudominio.com/
#>
[CmdletBinding()]
param(
    [string]$Avd = 'Curitiba_API36',
    [string]$SyncApiBaseUrl = $env:MYTASKAPP_MOBILE_SYNC_URL,
    [string]$Serial,
    [string]$AndroidSdk = (Join-Path $env:LOCALAPPDATA 'Android\Sdk'),
    [string]$JavaSdk,
    [switch]$InstallOnly
)

# Sem $ErrorActionPreference = 'Stop': no PowerShell 5.1, qualquer linha de
# stderr de um executavel (adb, dotnet) viraria excecao. Confere-se o
# $LASTEXITCODE de cada passo.

function Fail([string]$message) {
    Write-Host "ERRO: $message" -ForegroundColor Red
    exit 1
}

function Step([string]$message) {
    Write-Host "==> $message" -ForegroundColor Cyan
}

$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'src\MyTaskApp.Mobile\MyTaskApp.Mobile.csproj'

# 1. SDK do .NET. O global.json pede 10.0.4xx; se ele estiver so na instalacao
# por usuario, o dotnet do sistema nao compila.
$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (Test-Path (Join-Path $userDotnet 'dotnet.exe')) {
    $env:DOTNET_ROOT = $userDotnet
    $env:PATH = "$userDotnet;$env:PATH"
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
}
Push-Location $repo
$sdkVersion = & dotnet --version 2>$null
Pop-Location
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notlike '10.*') {
    Fail "SDK do .NET 10 nao encontrado (dotnet --version: '$sdkVersion'). Veja docs/mobile.md."
}
Step "SDK do .NET $sdkVersion"

# 2. Android SDK e JDK.
$adb = Join-Path $AndroidSdk 'platform-tools\adb.exe'
$emulator = Join-Path $AndroidSdk 'emulator\emulator.exe'
if (-not (Test-Path $adb)) {
    Fail "adb nao encontrado em $adb. Ajuste -AndroidSdk."
}
if (-not $JavaSdk) {
    $jdk = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Android\openjdk') -Directory -Filter 'jdk-21*' -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
    if ($jdk) { $JavaSdk = $jdk.FullName }
}

function Get-ConnectedDevices {
    & $adb devices 2>$null |
        Where-Object { $_ -match '^(\S+)\s+device$' } |
        ForEach-Object { ($_ -split '\s+')[0] }
}

# 3. Aparelho: o que ja estiver conectado, ou o emulador.
$devices = @(Get-ConnectedDevices)
if ($Serial) {
    if ($devices -notcontains $Serial) { Fail "Aparelho $Serial nao esta conectado (adb devices)." }
} elseif ($devices.Count -gt 1) {
    Fail "Mais de um aparelho conectado ($($devices -join ', ')). Escolha com -Serial."
} elseif ($devices.Count -eq 1) {
    $Serial = $devices[0]
    Step "Usando o aparelho ja conectado: $Serial"
} else {
    if (-not (Test-Path $emulator)) { Fail "Emulador nao encontrado em $emulator." }
    $avds = @(& $emulator -list-avds 2>$null)
    if ($avds -notcontains $Avd) {
        Fail "AVD '$Avd' nao existe. Disponiveis: $($avds -join ', ')."
    }
    Step "Ligando o emulador $Avd"
    Start-Process -FilePath $emulator -ArgumentList '-avd', $Avd
    $deadline = (Get-Date).AddMinutes(3)
    while (-not $Serial) {
        if ((Get-Date) -gt $deadline) { Fail 'O emulador nao apareceu no adb em 3 minutos.' }
        Start-Sleep -Seconds 2
        $Serial = @(Get-ConnectedDevices) | Select-Object -First 1
    }
}

Step "Esperando o Android de $Serial terminar de iniciar"
$deadline = (Get-Date).AddMinutes(3)
while ((& $adb -s $Serial shell getprop sys.boot_completed 2>$null) -ne '1') {
    if ((Get-Date) -gt $deadline) { Fail 'O Android nao terminou de iniciar em 3 minutos.' }
    Start-Sleep -Seconds 2
}

# 4. A primeira execucao do Chrome joga no "adicionar conta" do sistema, que
# o voltar nao fecha, e o login do app fica preso la.
& $adb -s $Serial shell 'echo _ --disable-fre --no-first-run > /data/local/tmp/chrome-command-line' 2>$null | Out-Null
& $adb -s $Serial shell am set-debug-app --persistent com.android.chrome 2>$null | Out-Null

# 5. Servidor de sincronizacao.
if ($SyncApiBaseUrl) {
    Step "Servidor de sincronizacao: $SyncApiBaseUrl"
} else {
    & $adb -s $Serial reverse tcp:5280 tcp:5280 | Out-Null
    Step 'Servidor de sincronizacao: http://localhost:5280/ (adb reverse para este computador)'
    if (-not (Get-NetTCPConnection -LocalPort 5280 -State Listen -ErrorAction SilentlyContinue)) {
        Write-Host '    Nada escutando na 5280 deste computador: o app abre, mas o login e a sincronizacao falham.' -ForegroundColor Yellow
        Write-Host '    Suba o servidor (docs/sync-server.md) ou passe -SyncApiBaseUrl.' -ForegroundColor Yellow
    }
}

# Um limite de uso do Bem-estar digital pausa o app ate o fim do dia e
# sobrevive a reinstalacao: o -t:Run "abre" e so aparece o aviso. O shell nao
# tira essa pausa (pm unsuspend so desfaz a dele); so o botao do aviso.
$suspended = & $adb -s $Serial shell dumpsys package io.github.adrianoguzzo.mytaskapp 2>$null |
    Select-String 'suspended=true' -Quiet
if ($suspended) {
    Write-Host '    O app esta pausado pelo Bem-estar digital neste aparelho. Ao abrir,' -ForegroundColor Yellow
    Write-Host '    toque em "Unpause app" no aviso e remova o limite em Configuracoes > Bem-estar digital.' -ForegroundColor Yellow
}

# 6. Compila, instala e abre.
$target = if ($InstallOnly) { 'Install' } else { 'Run' }
$buildArgs = @(
    'build', $project, "-t:$target", '-f', 'net10.0-android',
    "-p:AndroidSdkDirectory=$AndroidSdk",
    "-p:AdbTarget=-s $Serial"
)
if ($JavaSdk) { $buildArgs += "-p:JavaSdkDirectory=$JavaSdk" }
if ($SyncApiBaseUrl) { $buildArgs += "-p:SyncApiBaseUrl=$SyncApiBaseUrl" }

Step "dotnet build -t:$target (a primeira vez demora alguns minutos)"
Push-Location $repo
& dotnet @buildArgs
$code = $LASTEXITCODE
Pop-Location
if ($code -ne 0) { Fail "dotnet build terminou com o codigo $code." }

Step 'Pronto.'
