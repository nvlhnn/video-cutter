# Draws the app icon (play triangle + cut marks) and saves src\app.ico. Only needed if you want to change the icon.
Add-Type -AssemblyName System.Drawing
$sizes = 256, 48, 32, 16
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = [single]($s * 0.2)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = [single]($s - 1)
    $path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc($w - $r, 0, $r, $r, 270, 90)
    $path.AddArc($w - $r, $w - $r, $r, $r, 0, 90); $path.AddArc(0, $w - $r, $r, $r, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 33, 40))), $path)
    # play triangle
    $tri = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ([single]($s*0.36)), ([single]($s*0.24))),
        (New-Object System.Drawing.PointF ([single]($s*0.36)), ([single]($s*0.62))),
        (New-Object System.Drawing.PointF ([single]($s*0.68)), ([single]($s*0.43))))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 79, 140, 255))), $tri)
    # timeline bar with green/red cut marks
    $bh = [single][Math]::Max(2, $s * 0.07)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 70, 76, 90))), [single]($s*0.14), [single]($s*0.76), [single]($s*0.72), $bh)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 79, 140, 255))), [single]($s*0.30), [single]($s*0.76), [single]($s*0.40), $bh)
    $mw = [single][Math]::Max(1.5, $s * 0.05)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 79, 209, 139))), [single]($s*0.30 - $mw/2), [single]($s*0.70), $mw, [single]($s*0.19))
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 107, 107))), [single]($s*0.70 - $mw/2), [single]($s*0.70), $mw, [single]($s*0.19))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
}
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$d.Length); $bw.Write([UInt32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'app.ico'), $out.ToArray())
Write-Host "Saved app.ico"
