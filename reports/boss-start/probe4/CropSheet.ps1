# CropSheet.ps1 -Name p5a -From 0 -To 16000 -Every 500 [-X -Y -W -H -Cols 8]: one sheet of crops of fast-burst frames,
# the frame nearest each time, labelled with its ms.
param([Parameter(Mandatory)][string]$Name, [int]$From = 0, [int]$To = 16000, [int]$Every = 500,
      [int]$X = 95, [int]$Y = 80, [int]$W = 150, [int]$H = 170, [int]$Cols = 8, [string]$Out = "")
Add-Type -AssemblyName System.Drawing
$dir = "D:\The Dark sign\TheDarkSign\Work\reports\boot\fast"
$files = Get-ChildItem $dir -Filter "$Name-*.jpg" | ForEach-Object { [pscustomobject]@{ T = [int]($_.BaseName -replace "^$Name-", ''); P = $_.FullName } }
$times = for ($t = $From; $t -le $To; $t += $Every) { $t }
$rows = [math]::Ceiling($times.Count / $Cols)
$sheet = New-Object System.Drawing.Bitmap ($W * 2 * $Cols), ($H * 2 * $rows)
$g = [System.Drawing.Graphics]::FromImage($sheet)
$font = New-Object System.Drawing.Font "Arial", 12
for ($i = 0; $i -lt $times.Count; $i++) {
    $f = $files | Sort-Object { [math]::Abs($_.T - $times[$i]) } | Select-Object -First 1
    $im = [System.Drawing.Image]::FromFile($f.P)
    $dst = New-Object System.Drawing.Rectangle (($i % $Cols) * $W * 2), ([math]::Floor($i / $Cols) * $H * 2), ($W * 2), ($H * 2)
    $g.DrawImage($im, $dst, (New-Object System.Drawing.Rectangle $X, $Y, $W, $H), [System.Drawing.GraphicsUnit]::Pixel)
    $g.DrawString("$($f.T)", $font, [System.Drawing.Brushes]::Yellow, $dst.X + 3, $dst.Y + 3)
    $im.Dispose()
}
if (-not $Out) { $Out = "$dir\$Name-sheet-$From-$To.png" }
$sheet.Save($Out); $Out
