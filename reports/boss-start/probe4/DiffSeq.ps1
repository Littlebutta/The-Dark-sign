# DiffSeq.ps1 -Name p5a [-X 105 -Y 90 -W 130 -H 150]: for each fast-burst frame, the mean absolute difference of a grid
# of pixels in the region from the first frame, to find when the character's pose changes.
param([Parameter(Mandatory)][string]$Name, [int]$X = 105, [int]$Y = 90, [int]$W = 130, [int]$H = 150, [int]$Step = 3)
Add-Type -AssemblyName System.Drawing
$dir = "D:\The Dark sign\TheDarkSign\Work\reports\boot\fast"
$files = Get-ChildItem $dir -Filter "$Name-*.jpg" | Sort-Object Name
function Read-Grid($path) {
    $b = [System.Drawing.Bitmap]::FromFile($path)
    $v = New-Object System.Collections.Generic.List[int]
    for ($yy = $Y; $yy -lt $Y + $H; $yy += $Step) { for ($xx = $X; $xx -lt $X + $W; $xx += $Step) { $c = $b.GetPixel($xx, $yy); $v.Add($c.R + $c.G + $c.B) } }
    $b.Dispose(); return ,$v.ToArray()
}
$ref = Read-Grid $files[0].FullName
foreach ($f in $files) {
    $g = Read-Grid $f.FullName
    $sum = 0; for ($i = 0; $i -lt $g.Length; $i++) { $sum += [math]::Abs($g[$i] - $ref[$i]) }
    "{0}`t{1:N1}" -f ($f.BaseName -replace "^$Name-", ''), ($sum / $g.Length)
}
