# Generuje ikonę aplikacji ZetPDF (Assets\ZetPDF.ico + Assets\ZetPDF-256.png) – własny projekt, bez zasobów osób trzecich.
# Motyw: kartka z zagiętym rogiem, pasek akcentu u dołu i duża litera "Z".
# Użycie (PowerShell 7 lub Windows PowerShell 5.1):  pwsh -File src\ZetPDF.App\Assets\generate_icon.ps1
# Kolory są tu zapisane wprost, bo dotyczą wyłącznie pliku graficznego ikony (nie interfejsu).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$outDir = $PSScriptRoot
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

$paper = [System.Drawing.Color]::FromArgb(255, 250, 250, 248)
$paperEdge = [System.Drawing.Color]::FromArgb(255, 70, 84, 96)
$fold = [System.Drawing.Color]::FromArgb(255, 205, 214, 220)
$accent = [System.Drawing.Color]::FromArgb(255, 13, 110, 106)     # morski (teal) – akcent ZetPDF
$accentDark = [System.Drawing.Color]::FromArgb(255, 8, 74, 71)
$letter = [System.Drawing.Color]::FromArgb(255, 22, 32, 40)

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $m = [Math]::Max(1.0, $s * 0.09)            # margines poziomy
    $top = [Math]::Max(0.5, $s * 0.04)
    $w = $s - 2 * $m
    $h = $s - 2 * $top
    $f = $w * 0.30                              # zagięty róg
    $x0 = $m; $y0 = $top; $x1 = $m + $w; $y1 = $top + $h

    # kartka
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddLine([single]$x0, [single]$y0, [single]($x1 - $f), [single]$y0)
    $path.AddLine([single]($x1 - $f), [single]$y0, [single]$x1, [single]($y0 + $f))
    $path.AddLine([single]$x1, [single]($y0 + $f), [single]$x1, [single]$y1)
    $path.AddLine([single]$x1, [single]$y1, [single]$x0, [single]$y1)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush($paper)), $path)

    # pasek akcentu u dołu (przycięty do kartki)
    $bandH = $h * 0.22
    $g.SetClip($path)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush($accent)), [single]$x0, [single]($y1 - $bandH), [single]$w, [single]$bandH)
    $g.ResetClip()

    # obrys kartki
    $penW = [Math]::Max(1.0, $s / 32.0)
    $pen = New-Object System.Drawing.Pen($paperEdge, [single]$penW)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($pen, $path)

    # zagięty róg
    $foldPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $foldPath.AddLine([single]($x1 - $f), [single]$y0, [single]($x1 - $f), [single]($y0 + $f))
    $foldPath.AddLine([single]($x1 - $f), [single]($y0 + $f), [single]$x1, [single]($y0 + $f))
    $foldPath.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush($fold)), $foldPath)
    $g.DrawPath($pen, $foldPath)

    # litera Z – rysowana jako wielokąt (niezależnie od czcionek systemu)
    $zl = $x0 + $w * 0.20; $zr = $x0 + $w * 0.80
    $zt = $y0 + $h * 0.24; $zb = $y1 - $bandH - $h * 0.08
    $t = [Math]::Max(1.5, ($zb - $zt) * 0.22)   # grubość kresek
    $z = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pts = @(
        (New-Object System.Drawing.PointF([single]$zl, [single]$zt)),
        (New-Object System.Drawing.PointF([single]$zr, [single]$zt)),
        (New-Object System.Drawing.PointF([single]$zr, [single]($zt + $t))),
        (New-Object System.Drawing.PointF([single]($zl + $t * 1.25), [single]($zb - $t))),
        (New-Object System.Drawing.PointF([single]$zr, [single]($zb - $t))),
        (New-Object System.Drawing.PointF([single]$zr, [single]$zb)),
        (New-Object System.Drawing.PointF([single]$zl, [single]$zb)),
        (New-Object System.Drawing.PointF([single]$zl, [single]($zb - $t))),
        (New-Object System.Drawing.PointF([single]($zr - $t * 1.25), [single]($zt + $t))),
        (New-Object System.Drawing.PointF([single]$zl, [single]($zt + $t)))
    )
    $z.AddPolygon([System.Drawing.PointF[]]$pts)
    $g.FillPath((New-Object System.Drawing.SolidBrush($letter)), $z)

    # kreska akcentu pod literą (tylko większe rozmiary)
    if ($s -ge 48) {
        $g.FillRectangle((New-Object System.Drawing.SolidBrush($accentDark)), [single]($x0 + $w * 0.20), [single]($y1 - $bandH * 0.62), [single]($w * 0.32), [single]([Math]::Max(1.0, $bandH * 0.22)))
    }
    $g.Dispose()
    return $bmp
}

$frames = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $bmp.Save((Join-Path $outDir 'ZetPDF-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    $frames += , @($s, $ms.ToArray())
}

# Plik ICO: nagłówek + katalog + obrazy PNG (format obsługiwany przez Windows Vista+ i WIC/WPF).
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($fr in $frames) {
    $s = $fr[0]; $data = $fr[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($fr in $frames) { $bw.Write([byte[]]$fr[1]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $outDir 'ZetPDF.ico'), $out.ToArray())
Write-Host "Zapisano $(Join-Path $outDir 'ZetPDF.ico') ($($frames.Count) rozmiarów)"
