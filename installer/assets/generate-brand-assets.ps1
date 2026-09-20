<#
.SYNOPSIS
    Gera os icones e as artes do instalador a partir da paleta real do app.

.DESCRIPTION
    A identidade visual mora em src/MyTaskApp.Desktop/Styles/Tokens.axaml
    (ADR-017). Este script a reproduz em bitmap para os lugares que nao
    conseguem ler XAML: o Win32 resource do .exe, o assistente do Inno Setup
    e o icone .desktop do Linux.

    Roda so no Windows (System.Drawing) e so quando a arte precisa mudar -- os
    arquivos gerados sao versionados, porque <ApplicationIcon> exige o .ico em
    disco em qualquer plataforma de build.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer/assets/generate-brand-assets.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

# Tokens.axaml -- os mesmos valores, nao uma aproximacao.
$Canvas  = [System.Drawing.ColorTranslator]::FromHtml('#17181C')  # WidgetCanvasColor
$Surface = [System.Drawing.ColorTranslator]::FromHtml('#1E2025')  # WidgetSurfaceColor
$Accent  = [System.Drawing.ColorTranslator]::FromHtml('#6366F1')  # WidgetAccentColor
$Stroke  = [System.Drawing.ColorTranslator]::FromHtml('#2E313A')  # WidgetStrokeColor
$TextHi  = [System.Drawing.ColorTranslator]::FromHtml('#E9EAEE')  # WidgetTextHighColor
$TextMid = [System.Drawing.ColorTranslator]::FromHtml('#9AA0AC')  # WidgetTextMidColor

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function New-RoundedPath {
    param([single]$X, [single]$Y, [single]$W, [single]$H, [single]$Radius)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $Radius * 2
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Set-Quality {
    param($Graphics)

    $Graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $Graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $Graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $Graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
}

# A marca: quadrado arredondado no acento, visto branco por cima. Um simbolo so,
# legivel a 16px, que e onde a maioria dos icones morre.
function Write-Mark {
    param($Graphics, [single]$X, [single]$Y, [single]$Size)

    $path = New-RoundedPath -X $X -Y $Y -W $Size -H $Size -Radius ($Size * 0.24)
    $brush = New-Object System.Drawing.SolidBrush $Accent
    $Graphics.FillPath($brush, $path)
    $brush.Dispose()
    $path.Dispose()

    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ($Size * 0.115)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $Graphics.DrawLines($pen, @(
        (New-Object System.Drawing.PointF (($X + $Size * 0.28), ($Y + $Size * 0.53))),
        (New-Object System.Drawing.PointF (($X + $Size * 0.44), ($Y + $Size * 0.69))),
        (New-Object System.Drawing.PointF (($X + $Size * 0.73), ($Y + $Size * 0.33)))
    ))
    $pen.Dispose()
}

function New-MarkBitmap {
    param([int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    Set-Quality $graphics
    $graphics.Clear([System.Drawing.Color]::Transparent)

    # Margem de 6%: sem ela o canto arredondado encosta na borda e some nos
    # cantos das barras de tarefa que recortam o icone.
    $inset = [single]($Size * 0.06)
    Write-Mark -Graphics $graphics -X $inset -Y $inset -Size ([single]($Size - $inset * 2))

    $graphics.Dispose()
    return $bitmap
}

<#
    Escreve um .ico com quadros PNG (o mesmo formato do tray.ico atual).
    System.Drawing nao sabe montar icone multi-resolucao, e o container e
    simples o bastante para escrever a mao: cabecalho de 6 bytes, uma entrada
    de 16 bytes por quadro, depois os PNGs.
#>
function Write-Icon {
    param([int[]]$Sizes, [string]$Path)

    $frames = @()

    foreach ($size in $Sizes) {
        $bitmap = New-MarkBitmap -Size $size
        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        $frames += [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
        $stream.Dispose()
    }

    $output = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $output

    $writer.Write([uint16]0)                 # reservado
    $writer.Write([uint16]1)                 # 1 = icone
    $writer.Write([uint16]$frames.Count)

    $offset = 6 + (16 * $frames.Count)

    foreach ($frame in $frames) {
        # 256 e gravado como 0: o campo tem um byte so.
        if ($frame.Size -ge 256) { $dimension = 0 } else { $dimension = $frame.Size }

        $writer.Write([byte]$dimension)      # largura
        $writer.Write([byte]$dimension)      # altura
        $writer.Write([byte]0)               # paleta
        $writer.Write([byte]0)               # reservado
        $writer.Write([uint16]1)             # planos
        $writer.Write([uint16]32)            # bits por pixel
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }

    foreach ($frame in $frames) {
        $writer.Write($frame.Bytes)
    }

    $writer.Flush()
    [System.IO.File]::WriteAllBytes($Path, $output.ToArray())
    $writer.Dispose()
    $output.Dispose()

    Write-Host "  $Path  ($($Sizes -join '/') px)"
}

function Write-Png {
    param([int]$Size, [string]$Path)

    $bitmap = New-MarkBitmap -Size $Size
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()

    Write-Host "  $Path  (${Size}px)"
}

# Arte grande do assistente (164x314). Aparece na pagina final do Inno, entao e
# a ultima coisa que o usuario ve: fundo carvao do app, marca e assinatura.
function Write-WizardLarge {
    param([string]$Path)

    $width = 164
    $height = 314
    $bitmap = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    Set-Quality $graphics

    $background = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point 0, 0),
        (New-Object System.Drawing.Point $width, $height),
        $Canvas, $Surface)
    $graphics.FillRectangle($background, 0, 0, $width, $height)
    $background.Dispose()

    # Cartoes fantasma: a tela "Hoje" sugerida, nao desenhada.
    $ghost = New-Object System.Drawing.SolidBrush $Stroke
    $accentBrush = New-Object System.Drawing.SolidBrush $Accent

    foreach ($row in 0..2) {
        $y = 198 + ($row * 26)

        $card = New-RoundedPath -X 34 -Y $y -W 96 -H 14 -Radius 4
        $graphics.FillPath($ghost, $card)
        $card.Dispose()

        $tick = New-RoundedPath -X 34 -Y $y -W 14 -H 14 -Radius 4
        $graphics.FillPath($accentBrush, $tick)
        $tick.Dispose()
    }

    $ghost.Dispose()
    $accentBrush.Dispose()

    Write-Mark -Graphics $graphics -X 46 -Y 74 -Size 72

    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center

    $font = New-Object System.Drawing.Font 'Segoe UI', 13, ([System.Drawing.FontStyle]::Regular)
    $brush = New-Object System.Drawing.SolidBrush $TextHi
    $graphics.DrawString('MyTaskApp', $font, $brush, ([single]($width / 2)), 158, $format)
    $font.Dispose()
    $brush.Dispose()

    $small = New-Object System.Drawing.Font 'Segoe UI', 7.5, ([System.Drawing.FontStyle]::Regular)
    $smallBrush = New-Object System.Drawing.SolidBrush $TextMid
    $graphics.DrawString('sempre a vista', $small, $smallBrush, ([single]($width / 2)), 288, $format)
    $small.Dispose()
    $smallBrush.Dispose()
    $format.Dispose()

    $graphics.Dispose()
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bitmap.Dispose()

    Write-Host "  $Path  (${width}x${height})"
}

# Icone do cabecalho (55x55). O cabecalho do Inno moderno e claro, entao aqui o
# fundo e branco -- um quadrado carvao viraria um borrao preto no topo.
function Write-WizardSmall {
    param([string]$Path)

    $size = 55
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    Set-Quality $graphics
    $graphics.Clear([System.Drawing.Color]::White)

    Write-Mark -Graphics $graphics -X 7 -Y 7 -Size 41

    $graphics.Dispose()
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bitmap.Dispose()

    Write-Host "  $Path  (${size}x${size})"
}

Write-Host 'Gerando a identidade visual a partir de Tokens.axaml:'

$iconSizes = @(16, 24, 32, 48, 64, 128, 256)

Write-Icon -Sizes $iconSizes -Path (Join-Path $repoRoot 'src\MyTaskApp.Desktop\Assets\app.ico')
Write-Icon -Sizes $iconSizes -Path (Join-Path $repoRoot 'installer\windows\assets\MyTaskApp.ico')
Write-WizardLarge -Path (Join-Path $repoRoot 'installer\windows\assets\wizard-large.bmp')
Write-WizardSmall -Path (Join-Path $repoRoot 'installer\windows\assets\wizard-small.bmp')
Write-Png -Size 256 -Path (Join-Path $repoRoot 'installer\linux\assets\mytaskapp.png')

Write-Host 'Pronto.'
