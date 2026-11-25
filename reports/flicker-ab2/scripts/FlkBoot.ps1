# FlkBoot.ps1 -Shot name: PLAY in the launcher, title -> dismiss notice -> Continue, then a world screenshot.
# Prints the SHA256 of the gparam and GraphicsConfig right after the game starts (to prove what it read).
param([string]$Shot = "flk2-world")
$S = "D:\The Dark sign\TheDarkSign\Tools\Scripts"
& "$S\Launcher-UI.ps1" -Click PlayButton
$sw = [Diagnostics.Stopwatch]::StartNew()
while (-not (Get-Process eldenring -EA SilentlyContinue | Where-Object MainWindowHandle -ne 0) -and $sw.Elapsed.TotalSeconds -lt 90) { Start-Sleep 1 }
Get-FileHash "D:\The Dark sign\TheDarkSign\Mods\ER\param\drawparam\m45_00_0000.gparam.dcx", "D:\The Dark sign\TheDarkSign\Saves\Default\ER\GraphicsConfig.xml" | ForEach-Object { "{0} {1}" -f $_.Hash.Substring(0, 16), (Split-Path -Leaf $_.Path) }
$sw.Restart(); while ($sw.Elapsed.TotalSeconds -lt 25) { Start-Sleep -Milliseconds 500 }
& "$S\ER-Input.ps1" -Steal -Keys "Enter,Wait3000,Enter,Wait2500,Enter,Wait28000" -Shot $Shot
