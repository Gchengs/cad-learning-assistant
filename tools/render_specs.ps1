# 把 specs.json 里的矢量基本图画成 PNG（GDI+，不依赖 AutoCAD）。
# 用法： render_specs.ps1 -SpecPath tools\image_specs.json -OutDir <输出目录>
#
# 画布 1600×1120；字号/线宽按 SCALE 统一放大，同一份 specs 在面板里缩小后仍清楚，
# 点开放大窗口也不糊。尺寸线带实心箭头，剖面线用裁剪区裁在多边形内。
param(
    [Parameter(Mandatory = $true)][string]$SpecPath,
    [Parameter(Mandatory = $true)][string]$OutDir
)

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$W = 1600
$H = 1120
$margin = 70
$titleBand = 92
$SCALE = 1.6

function New-Pen([string]$color, [single]$width) {
    $c = [System.Drawing.ColorTranslator]::FromHtml($color)
    return New-Object System.Drawing.Pen($c, [single][Math]::Max(1.0, $width * 1.2))
}

function Touch([double]$x, [double]$y) {
    if ($x -lt $script:gminx) { $script:gminx = $x }
    if ($y -lt $script:gminy) { $script:gminy = $y }
    if ($x -gt $script:gmaxx) { $script:gmaxx = $x }
    if ($y -gt $script:gmaxy) { $script:gmaxy = $y }
}

function Get-Bounds($img) {
    $script:gminx = [double]::MaxValue
    $script:gminy = [double]::MaxValue
    $script:gmaxx = [double]::MinValue
    $script:gmaxy = [double]::MinValue
    foreach ($p in $img.prims) {
        switch ($p.t) {
            'line' { Touch $p.p[0] $p.p[1]; Touch $p.p[2] $p.p[3] }
            'rect' { Touch $p.x $p.y; Touch ($p.x + $p.w) ($p.y + $p.h) }
            'circ' { Touch ($p.x - $p.r) ($p.y - $p.r); Touch ($p.x + $p.r) ($p.y + $p.r) }
            'poly' { for ($i = 0; $i -lt $p.p.Count; $i++) { Touch $p.p[$i][0] $p.p[$i][1] } }
            'hatch' { for ($i = 0; $i -lt $p.p.Count; $i++) { Touch $p.p[$i][0] $p.p[$i][1] } }
            'dim' {
                Touch $p.x1 $p.y1
                Touch $p.x2 $p.y2
                Touch ($p.x1 + $p.off) ($p.y1 + $p.off)
                Touch ($p.x2 + $p.off) ($p.y2 + $p.off)
            }
            'text' { Touch $p.x $p.y }
        }
    }
    return @{ minx = $script:gminx; miny = $script:gminy; maxx = $script:gmaxx; maxy = $script:gmaxy }
}

$spec = [System.IO.File]::ReadAllText($SpecPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

foreach ($img in $spec.images) {
    # 允许 spec 用 bounds 指定取景框（小图形不再被放大到失真）
    $b = Get-Bounds $img
    if ($img.bounds) {
        $b = @{ minx = [double]$img.bounds[0]; miny = [double]$img.bounds[1]; maxx = [double]$img.bounds[2]; maxy = [double]$img.bounds[3] }
    }
    $spanx = [Math]::Max(1e-6, $b.maxx - $b.minx)
    $spany = [Math]::Max(1e-6, $b.maxy - $b.miny)
    $usableW = $W - 2 * $margin
    $usableH = $H - 2 * $margin - $titleBand
    $scale = [Math]::Min($usableW / $spanx, $usableH / $spany)
    $ox = $margin + ($usableW - $spanx * $scale) / 2 - $b.minx * $scale
    $oy = $H - $margin - ($usableH - $spany * $scale) / 2 + $b.miny * $scale

    function X([double]$v) { return $ox + $v * $scale }
    function Y([double]$v) { return $oy - $v * $scale }

    $bmp = New-Object System.Drawing.Bitmap($W, $H)
    $bmp.SetResolution(150, 150)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $g.Clear([System.Drawing.Color]::White)

    # 字号统一用像素（GraphicsUnit.Pixel）：图纸单位只有几百，用磅值会随比例乱跳。
    $fontLabel = New-Object System.Drawing.Font('Microsoft YaHei', [single]20, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $fontDim = New-Object System.Drawing.Font('Microsoft YaHei', [single]18, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $dimColor = [System.Drawing.ColorTranslator]::FromHtml('#0f7a3d')
    $brushDim = New-Object System.Drawing.SolidBrush($dimColor)
    $arrowBrush = New-Object System.Drawing.SolidBrush($dimColor)
    $arrowSize = 10

    foreach ($p in $img.prims) {
        $col = if ($p.c) { $p.c } else { '#1a1a1a' }
        # rect 的 w 是几何宽度，线宽单独用 lw，两都不能共用一个键
        $wdt = if ($p.t -eq 'rect') { if ($p.lw) { $p.lw } else { 2 } } elseif ($p.w) { $p.w } else { 2 }
        $pen = New-Pen $col $wdt
        if ($p.dash) { $pen.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash }
        if ($p.dashdot) { $pen.DashStyle = [System.Drawing.Drawing2D.DashStyle]::DashDot }
        switch ($p.t) {
            'line' {
                $g.DrawLine($pen, [single](X $p.p[0]), [single](Y $p.p[1]), [single](X $p.p[2]), [single](Y $p.p[3]))
            }
            'rect' {
                $g.DrawRectangle($pen, [single](X $p.x), [single](Y ($p.y + $p.h)), [single]($p.w * $scale), [single]($p.h * $scale))
            }
            'circ' {
                $r = [single]($p.r * $scale)
                $g.DrawEllipse($pen, [single]((X $p.x) - $r), [single]((Y $p.y) - $r), $r * 2, $r * 2)
            }
            'poly' {
                $pts = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
                foreach ($pt in $p.p) {
                    $pts.Add((New-Object System.Drawing.PointF([single](X $pt[0]), [single](Y $pt[1]))))
                }
                if ($p.close) { $g.DrawPolygon($pen, $pts.ToArray()) } else { $g.DrawLines($pen, $pts.ToArray()) }
            }
            'hatch' {
                # 45° 剖面线：先在像素坐标里算好多边形的包围盒，再逐条画线，出界由裁剪区裁掉
                $pts = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
                $minpx = [double]::MaxValue; $minpy = [double]::MaxValue
                $maxpx = [double]::MinValue; $maxpy = [double]::MinValue
                foreach ($pt in $p.p) {
                    $fx = [double](X $pt[0]); $fy = [double](Y $pt[1])
                    $pts.Add((New-Object System.Drawing.PointF([single]$fx, [single]$fy)))
                    if ($fx -lt $minpx) { $minpx = $fx }
                    if ($fy -lt $minpy) { $minpy = $fy }
                    if ($fx -gt $maxpx) { $maxpx = $fx }
                    if ($fy -gt $maxpy) { $maxpy = $fy }
                }
                $path = New-Object System.Drawing.Drawing2D.GraphicsPath
                $path.AddPolygon($pts.ToArray())
                $state = $g.Save()
                $g.SetClip($path, [System.Drawing.Drawing2D.CombineMode]::Replace)
                $gap = [Math]::Max(5.0, [double]($p.gap) * $scale)
                $hpen = New-Pen $col 1
                $height = $maxpy - $minpy + 4
                $x = $minpx - $height
                while ($x -lt $maxpx + 4) {
                    $g.DrawLine($hpen, [single]$x, [single]($minpy - 2), [single]($x + $height), [single]($maxpy + 2))
                    $x += $gap
                }
                $hpen.Dispose()
                $g.Restore($state)
                $path.Dispose()
            }
            'text' {
                $f = if ($p.size) { New-Object System.Drawing.Font('Microsoft YaHei', [single]$p.size, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel) } else { $fontLabel }
                $br = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($col))
                $sz = $g.MeasureString([string]$p.s, $f)
                $ty = [single]((Y $p.y) - $sz.Height / 2)
                if ($p.align -eq 'c') {
                    $g.DrawString([string]$p.s, $f, $br, [single]((X $p.x) - $sz.Width / 2), $ty)
                }
                else {
                    $g.DrawString([string]$p.s, $f, $br, [single](X $p.x), $ty)
                }
                $br.Dispose()
                if ($p.size) { $f.Dispose() }
            }
            'dim' {
                $vert = [Math]::Abs($p.x2 - $p.x1) -lt 1e-9
                if ($vert) {
                    $xs = X ($p.x1 + $p.off)
                    $g.DrawLine($pen, [single](X $p.x1), [single](Y $p.y1), [single]$xs, [single](Y $p.y1))
                    $g.DrawLine($pen, [single](X $p.x2), [single](Y $p.y2), [single]$xs, [single](Y $p.y2))
                    $g.DrawLine($pen, [single]$xs, [single](Y $p.y1), [single]$xs, [single](Y $p.y2))
                    $y1p = Y $p.y1
                    $y2p = Y $p.y2
                    $d = [Math]::Sign($y2p - $y1p); if ($d -eq 0) { $d = 1 }
                    $g.FillPolygon($arrowBrush, @(
                        (New-Object System.Drawing.PointF([single]$xs, [single]$y1p)),
                        (New-Object System.Drawing.PointF([single]($xs - 4), [single]($y1p + $d * $arrowSize))),
                        (New-Object System.Drawing.PointF([single]($xs + 4), [single]($y1p + $d * $arrowSize)))
                    ))
                    $g.FillPolygon($arrowBrush, @(
                        (New-Object System.Drawing.PointF([single]$xs, [single]$y2p)),
                        (New-Object System.Drawing.PointF([single]($xs - 4), [single]($y2p - $d * $arrowSize))),
                        (New-Object System.Drawing.PointF([single]($xs + 4), [single]($y2p - $d * $arrowSize)))
                    ))
                    $sz = $g.MeasureString([string]$p.txt, $fontDim)
                    $g.DrawString([string]$p.txt, $fontDim, $brushDim,
                        [single]($xs + 8), [single]((($y1p + $y2p) / 2) - $sz.Height / 2))
                }
                else {
                    $ys = Y ($p.y1 + $p.off)
                    $g.DrawLine($pen, [single](X $p.x1), [single](Y $p.y1), [single](X $p.x1), [single]$ys)
                    $g.DrawLine($pen, [single](X $p.x2), [single](Y $p.y2), [single](X $p.x2), [single]$ys)
                    $g.DrawLine($pen, [single](X $p.x1), [single]$ys, [single](X $p.x2), [single]$ys)
                    $x1p = X $p.x1
                    $x2p = X $p.x2
                    $d = [Math]::Sign($x2p - $x1p); if ($d -eq 0) { $d = 1 }
                    $g.FillPolygon($arrowBrush, @(
                        (New-Object System.Drawing.PointF([single]$x1p, [single]$ys)),
                        (New-Object System.Drawing.PointF([single]($x1p + $d * $arrowSize), [single]($ys - 4))),
                        (New-Object System.Drawing.PointF([single]($x1p + $d * $arrowSize), [single]($ys + 4)))
                    ))
                    $g.FillPolygon($arrowBrush, @(
                        (New-Object System.Drawing.PointF([single]$x2p, [single]$ys)),
                        (New-Object System.Drawing.PointF([single]($x2p - $d * $arrowSize), [single]($ys - 4))),
                        (New-Object System.Drawing.PointF([single]($x2p - $d * $arrowSize), [single]($ys + 4)))
                    ))
                    $sz = $g.MeasureString([string]$p.txt, $fontDim)
                    $g.DrawString([string]$p.txt, $fontDim, $brushDim,
                        [single]((($x1p + $x2p) / 2) - $sz.Width / 2), [single]($ys - $sz.Height - 5))
                }
            }
        }
        $pen.Dispose()
    }

    # 左上角标题，底部一行来源说明（原来那行长句在面板宽度下读不清）
    $title = if ($img.title) { $img.title } else { $img.id }
    $ft = New-Object System.Drawing.Font('Microsoft YaHei', [single]30, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $bt = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#1f3b57'))
    $g.DrawString([string]$title, $ft, $bt, [single]$margin, [single]22)
    if ($img.caption) {
        $fc = New-Object System.Drawing.Font('Microsoft YaHei', [single]17, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        $bc = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#666666'))
        $g.DrawString([string]$img.caption, $fc, $bc, [single]$margin, [single]($H - 44))
        $fc.Dispose()
        $bc.Dispose()
    }

    $outPath = Join-Path $OutDir ($img.id + '.png')
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
    Write-Output ("wrote " + $outPath)
}
