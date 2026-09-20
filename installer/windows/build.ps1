<#
.SYNOPSIS
    Gera o instalador do MyTaskApp para Windows.

.DESCRIPTION
    Version -> Build -> Publish -> Package -> Installer, sem repetir a versao
    em lugar nenhum: ela sai de Directory.Build.props via MSBuild e e injetada
    no Inno Setup por /DAppVersion.

    Publish e self-contained de proposito -- o usuario baixa e instala, sem
    pre-requisito de runtime. Trimming fica desligado: EF Core e Avalonia
    dependem de reflexao, e InvariantGlobalization precisa continuar false
    para os ids IANA de fuso resolverem (ADR-002).

.PARAMETER Runtime
    RID de destino. Padrao win-x64.

.PARAMETER InstallPrerequisites
    Instala o Inno Setup 6 via winget se ele nao for encontrado.

.PARAMETER IsccPath
    Caminho explicito do ISCC.exe, quando ele nao esta em um lugar padrao.

.PARAMETER Sign
    Usa o sign tool "mytaskapp" configurado no Inno Setup. Exige que a linha
    SignTool esteja descomentada em MyTaskApp.iss.

.PARAMETER SkipTests
    Pula a suite de testes. Use so para iterar no proprio instalador.

.EXAMPLE
    .\installer\windows\build.ps1

.EXAMPLE
    .\installer\windows\build.ps1 -InstallPrerequisites -SkipTests
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [switch]$InstallPrerequisites,
    [string]$IsccPath,
    [switch]$Sign,
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$desktopProject = Join-Path $repoRoot 'src\MyTaskApp.Desktop\MyTaskApp.Desktop.csproj'
$solution = Join-Path $repoRoot 'MyTaskApp.slnx'
$publishDir = Join-Path $repoRoot "artifacts\publish\$Runtime"
$installerDir = Join-Path $repoRoot 'artifacts\installer'
$issFile = Join-Path $PSScriptRoot 'MyTaskApp.iss'

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Invoke-Checked {
    param([string]$Label, [scriptblock]$Action)

    & $Action

    if ($LASTEXITCODE -ne 0) {
        throw "$Label falhou (exit $LASTEXITCODE)."
    }
}

function Resolve-Iscc {
    if ($IsccPath) {
        if (-not (Test-Path $IsccPath)) { throw "ISCC.exe nao encontrado em '$IsccPath'." }
        return $IsccPath
    }

    # A terceira entrada e o destino da instalacao por usuario do proprio Inno
    # (winget sem elevacao) -- a que passa despercebida com mais frequencia.
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }

    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    return $null
}

function Install-InnoSetup {
    Write-Step 'Inno Setup 6 ausente, instalando via winget'

    if (-not (Get-Command 'winget' -ErrorAction SilentlyContinue)) {
        throw 'winget nao esta disponivel. Instale o Inno Setup 6 manualmente: https://jrsoftware.org/isdl.php'
    }

    winget install -e --id JRSoftware.InnoSetup --accept-source-agreements --accept-package-agreements

    $resolved = Resolve-Iscc
    if (-not $resolved) { throw 'Inno Setup foi instalado, mas o ISCC.exe nao foi encontrado. Abra um novo terminal e tente de novo.' }

    return $resolved
}

# --- Versao: a unica fonte e Directory.Build.props -------------------------

Write-Step 'Lendo a versao do produto'

$version = (& dotnet msbuild $desktopProject -getProperty:Version -v:q --nologo).Trim()

if ($LASTEXITCODE -ne 0 -or -not $version) {
    throw 'Nao consegui ler a propriedade Version do projeto.'
}

# VersionInfoVersion do Windows exige quatro campos numericos.
$numeric = ($version -split '[-+]')[0]
$parts = @($numeric -split '\.')
while ($parts.Count -lt 4) { $parts += '0' }
$versionFull = ($parts[0..3] -join '.')

Write-Host "    versao do produto : $version"
Write-Host "    versao do arquivo : $versionFull"

# --- Ferramenta ------------------------------------------------------------

$iscc = Resolve-Iscc

if (-not $iscc) {
    if ($InstallPrerequisites) {
        $iscc = Install-InnoSetup
    }
    else {
        throw @'
Inno Setup 6 nao encontrado.

Instale com:

    winget install -e --id JRSoftware.InnoSetup

ou rode este script com -InstallPrerequisites, ou aponte o caminho com
-IsccPath "C:\caminho\ISCC.exe".
'@
    }
}

Write-Host "    ISCC              : $iscc"

# --- Testes ----------------------------------------------------------------

if (-not $SkipTests) {
    Write-Step 'Rodando os testes'
    Invoke-Checked 'dotnet test' { dotnet test $solution -c Release --nologo }
}

# --- Publish ---------------------------------------------------------------

Write-Step "Publicando $Runtime (self-contained)"

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

Invoke-Checked 'dotnet publish' {
    dotnet publish $desktopProject `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -p:PublishTrimmed=false `
        -p:PublishSingleFile=false `
        -p:DebugType=none `
        -o $publishDir `
        --nologo
}

$exe = Join-Path $publishDir 'MyTaskApp.exe'
if (-not (Test-Path $exe)) { throw "Publish nao produziu $exe." }

# appsettings.json e carregado com optional:false -- sem ele o app morre na
# primeira linha do Main. Melhor falhar aqui do que na maquina do usuario.
$settings = Join-Path $publishDir 'appsettings.json'
if (-not (Test-Path $settings)) { throw "Publish nao produziu $settings (o app nao inicia sem ele)." }

$publishedSize = [math]::Round((Get-ChildItem $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host "    publicado         : $publishedSize MB em $publishDir"

# --- Instalador ------------------------------------------------------------

Write-Step 'Compilando o instalador'

New-Item -ItemType Directory -Force -Path $installerDir | Out-Null

$isccArgs = @(
    "/DAppVersion=$version",
    "/DAppVersionFull=$versionFull",
    "/DPublishDir=$publishDir",
    '/Qp'
)

if ($Sign) { $isccArgs += '/Smytaskapp=$p' }

$isccArgs += $issFile

Invoke-Checked 'ISCC' { & $iscc @isccArgs }

$setup = Join-Path $installerDir "MyTaskAppSetup-$version.exe"
if (-not (Test-Path $setup)) { throw "ISCC terminou sem erro, mas $setup nao existe." }

$setupSize = [math]::Round((Get-Item $setup).Length / 1MB, 1)

Write-Host ''
Write-Host "Instalador pronto: $setup ($setupSize MB)" -ForegroundColor Green
Write-Host ''
Write-Host 'Instalacao silenciosa:'
Write-Host "    MyTaskAppSetup-$version.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART"
