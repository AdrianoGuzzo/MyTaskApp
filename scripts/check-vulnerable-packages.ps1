<#
.SYNOPSIS
    Reprova se algum pacote NuGet, direto ou transitivo, tiver vulnerabilidade conhecida.

.DESCRIPTION
    E o mesmo passo do CI (.github/workflows/ci.yml chama este script): se
    passa aqui, passa la (SEC-016).

    O `dotnet list package --vulnerable` so lista: sai com 0 mesmo achando
    pacote vulneravel. Por isso a saida vai em JSON e quem decide e este
    script. Um problema de nivel "error" (a base de vulnerabilidades nao
    respondeu, o projeto nao foi restaurado) tambem reprova: sem a consulta,
    "nenhum pacote vulneravel" nao quer dizer nada.

    Le o project.assets.json: rode depois do `dotnet restore`. O --no-restore
    e de proposito. Sem ele, o SDK 10 restaura de novo antes de listar, e com
    TreatWarningsAsErrors o NuGetAudit transforma o pacote vulneravel em erro
    NU190x: a restauracao falha e o relatorio nao sai. O CI restaura com
    -p:TreatWarningsAsErrors=false so neste passo, para o relatorio sair
    inteiro; o restore do build continua reprovando por conta propria.

.PARAMETER Targets
    Solutions ou projetos a conferir. Padrao: as duas do CI, a do desktop
    (MyTaskApp.slnx) e a do servidor (MyTaskApp.Server.slnx).

.EXAMPLE
    dotnet restore MyTaskApp.slnx -p:TreatWarningsAsErrors=false
    dotnet restore MyTaskApp.Server.slnx -p:TreatWarningsAsErrors=false
    .\scripts\check-vulnerable-packages.ps1
#>
[CmdletBinding()]
param(
    [string[]]$Targets = @('MyTaskApp.slnx', 'MyTaskApp.Server.slnx')
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$failures = New-Object System.Collections.Generic.List[string]

foreach ($target in $Targets) {
    $path = Join-Path $root $target
    Write-Host "==> $target"

    $output = & dotnet list $path package --vulnerable --include-transitive --no-restore --format json
    if ($LASTEXITCODE -ne 0) {
        $output | Write-Host
        $failures.Add("${target}: dotnet list package saiu com $LASTEXITCODE")
        continue
    }

    $report = ($output -join "`n") | ConvertFrom-Json

    foreach ($problem in @($report.problems)) {
        if ($null -eq $problem) { continue }
        Write-Host "  [$($problem.level)] $($problem.project) $($problem.text)"
        if ($problem.level -eq 'error') {
            $failures.Add("${target}: $($problem.text)")
        }
    }

    $found = 0
    foreach ($project in @($report.projects)) {
        $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project.path)

        foreach ($framework in @($project.frameworks)) {
            if ($null -eq $framework) { continue }
            $packages = @($framework.topLevelPackages) + @($framework.transitivePackages)

            foreach ($package in $packages) {
                if ($null -eq $package) { continue }

                foreach ($vulnerability in @($package.vulnerabilities)) {
                    if ($null -eq $vulnerability) { continue }
                    $found++
                    $finding = "$($package.id) $($package.resolvedVersion) - $($vulnerability.severity)" +
                        " - $($vulnerability.advisoryurl)"
                    Write-Host "  $projectName ($($framework.framework)): $finding"
                    # O mesmo pacote aparece em cada projeto que o herda: um achado so.
                    if (-not $failures.Contains($finding)) {
                        $failures.Add($finding)
                    }
                }
            }
        }
    }

    if ($found -eq 0) {
        Write-Host '  nenhum pacote vulneravel'
    }
}

if ($failures.Count -gt 0) {
    # Uma anotacao por pacote e advisory: no GitHub Actions cada uma aparece no
    # resumo da execucao. Os projetos afetados ficam no log, acima.
    foreach ($failure in $failures) {
        Write-Host "::error::$failure"
    }
    Write-Host ''
    Write-Host 'Atualize o pacote em Directory.Packages.props. Se o vulneravel for transitivo,'
    Write-Host 'fixe nele uma versao corrigida (CentralPackageTransitivePinningEnabled).'
    exit 1
}
