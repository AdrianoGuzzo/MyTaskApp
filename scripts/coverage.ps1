<#
.SYNOPSIS
    Roda os testes com cobertura, gera o relatorio e reprova abaixo do minimo.

.DESCRIPTION
    E o mesmo portao do CI (.github/workflows/ci.yml chama este script): se
    passa aqui, passa la. O minimo mora so aqui, nos parametros abaixo.

    Os cinco projetos de teste geram um coverage.cobertura.xml cada um
    (coverage.runsettings); o ReportGenerator junta os cinco. Uma linha
    coberta por qualquer suite conta como coberta.

    Saidas, em artifacts/coverage:
      report/index.html  relatorio navegavel, linha a linha
      summary.md         alcancado x minimo, e por assembly
    No GitHub Actions o summary.md e o detalhe por classe vao tambem para o
    resumo da execucao ($env:GITHUB_STEP_SUMMARY).

.PARAMETER MinLine
    Cobertura de linhas minima do produto inteiro, em %.

.PARAMETER MinBranch
    Cobertura de branches minima do produto inteiro, em %.

.PARAMETER Configuration
    Configuracao de build usada pelo dotnet test. Padrao Release, como no CI.

.PARAMETER NoBuild
    Passa --no-build ao dotnet test. O CI compila num passo proprio.

.PARAMETER Open
    Abre o relatorio HTML no navegador ao terminar.

.EXAMPLE
    .\scripts\coverage.ps1 -Open
#>
[CmdletBinding()]
param(
    # Subir o minimo e editar aqui. Baixar exige um motivo no PR.
    [double]$MinLine = 80,
    [double]$MinBranch = 70,
    [string]$Configuration = 'Release',
    [switch]$NoBuild,
    [switch]$Open
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'MyTaskApp.slnx'
$settings = Join-Path $root 'coverage.runsettings'
$outDir = Join-Path $root 'artifacts/coverage'
$rawDir = Join-Path $outDir 'raw'
$reportDir = Join-Path $outDir 'report'
$summaryFile = Join-Path $outDir 'summary.md'
$inv = [System.Globalization.CultureInfo]::InvariantCulture

function Format-Percent([double]$value) {
    $value.ToString('0.0', $inv) + '%'
}

# Relatorio velho misturado com o novo daria um numero que ninguem mediu.
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

$testArgs = @(
    'test', $solution,
    '-c', $Configuration,
    '--nologo',
    '--settings', $settings,
    '--collect', 'XPlat Code Coverage',
    '--results-directory', $rawDir,
    '--logger', 'trx'
)
if ($NoBuild) { $testArgs += '--no-build' }

& dotnet @testArgs
$testsFailed = $LASTEXITCODE -ne 0

# Mesmo com teste vermelho o relatorio sai: ajuda a ver o que ficou de fora.
$reports = Get-ChildItem $rawDir -Recurse -Filter 'coverage.cobertura.xml' -ErrorAction SilentlyContinue
if (-not $reports) {
    throw 'Nenhum coverage.cobertura.xml gerado. O coverlet.collector esta referenciado nos testes?'
}

Push-Location $root
try {
    & dotnet tool restore | Out-Null
    & dotnet reportgenerator `
        "-reports:$rawDir/**/coverage.cobertura.xml" `
        "-targetdir:$reportDir" `
        '-reporttypes:Html;JsonSummary;MarkdownSummaryGithub' `
        '-title:MyTaskApp' `
        '-verbosity:Warning'
    if ($LASTEXITCODE -ne 0) { throw "reportgenerator falhou (exit $LASTEXITCODE)" }
}
finally {
    Pop-Location
}

$json = Get-Content (Join-Path $reportDir 'Summary.json') -Raw | ConvertFrom-Json
$line = [double]$json.summary.linecoverage
$branch = [double]$json.summary.branchcoverage

$checks = @(
    [pscustomobject]@{
        Metric = 'Linhas'; Actual = $line; Minimum = $MinLine
        Detail = "$($json.summary.coveredlines) de $($json.summary.coverablelines)"
    }
    [pscustomobject]@{
        Metric = 'Branches'; Actual = $branch; Minimum = $MinBranch
        Detail = "$($json.summary.coveredbranches) de $($json.summary.totalbranches)"
    }
)
$failed = @($checks | Where-Object { $_.Actual -lt $_.Minimum })
$passed = $failed.Count -eq 0

$md = New-Object System.Collections.Generic.List[string]
$md.Add('## Cobertura de testes')
$md.Add('')
if ($passed) {
    $md.Add(':white_check_mark: **Aprovado** -- a cobertura atinge o minimo exigido.')
}
else {
    $md.Add(':x: **Reprovado** -- a cobertura ficou abaixo do minimo exigido.')
}
$md.Add('')
$md.Add('| Metrica | Alcancado | Minimo | Cobertos | |')
$md.Add('|:---|---:|---:|---:|:---:|')
foreach ($c in $checks) {
    $icon = if ($c.Actual -ge $c.Minimum) { ':white_check_mark:' } else { ':x:' }
    $md.Add("| $($c.Metric) | **$(Format-Percent $c.Actual)** | $(Format-Percent $c.Minimum) | $($c.Detail) | $icon |")
}
$md.Add('')
$md.Add('<details><summary>Por assembly</summary>')
$md.Add('')
$md.Add('| Assembly | Linhas | Branches |')
$md.Add('|:---|---:|---:|')
foreach ($a in $json.coverage.assemblies | Sort-Object name) {
    $md.Add("| $($a.name) | $(Format-Percent $a.coverage) | $(Format-Percent $a.branchcoverage) |")
}
$md.Add('')
$md.Add('</details>')
$md.Add('')
$md.Add('_O minimo fica em `scripts/coverage.ps1`. O relatorio HTML linha a linha esta no artefato `coverage-report`._')

$markdown = $md -join "`n"
[System.IO.File]::WriteAllText($summaryFile, $markdown + "`n", (New-Object System.Text.UTF8Encoding $false))

if ($env:GITHUB_STEP_SUMMARY) {
    # O detalhe por classe e longo: vai so para o resumo da execucao, nao para o PR.
    $detail = Get-Content (Join-Path $reportDir 'SummaryGithub.md') -Raw
    $full = $markdown + "`n`n<details><summary>Detalhe por classe</summary>`n`n" + $detail + "`n</details>`n"
    [System.IO.File]::AppendAllText($env:GITHUB_STEP_SUMMARY, $full, (New-Object System.Text.UTF8Encoding $false))
}

Write-Host ''
Write-Host 'Cobertura de testes' -ForegroundColor Cyan
foreach ($c in $checks) {
    $ok = $c.Actual -ge $c.Minimum
    $color = if ($ok) { 'Green' } else { 'Red' }
    $mark = if ($ok) { 'OK ' } else { 'FALHOU' }
    Write-Host ('  {0,-9} {1,7}  (minimo {2,6})  {3}' -f $c.Metric, (Format-Percent $c.Actual), (Format-Percent $c.Minimum), $mark) -ForegroundColor $color
}
Write-Host "  Relatorio: $(Join-Path $reportDir 'index.html')"

if ($Open) { Start-Process (Join-Path $reportDir 'index.html') }

# Write-Error com ErrorActionPreference=Stop pararia na primeira; as duas
# causas precisam aparecer quando as duas acontecem.
$problems = @()
if ($testsFailed) { $problems += 'Ha testes falhando.' }
if (-not $passed) {
    $names = ($failed | ForEach-Object { $_.Metric.ToLowerInvariant() }) -join ' e '
    $problems += "Cobertura de $names abaixo do minimo."
}
if ($problems) {
    Write-Host ''
    $problems | ForEach-Object { Write-Host "ERRO: $_" -ForegroundColor Red }
    exit 1
}
