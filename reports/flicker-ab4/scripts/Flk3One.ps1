param([Parameter(Mandatory)][string]$Name,[Parameter(Mandatory)][string]$File)
$d="D:\The Dark sign\TheDarkSign\Work\scratch\flicker-ab"; $g="D:\The Dark sign\TheDarkSign\Mods\ER\param\drawparam\m45_00_0000.gparam.dcx"
$p=Get-Process eldenring -EA SilentlyContinue; if($p){ $p | Stop-Process -Force; $p | Wait-Process -Timeout 60 -EA SilentlyContinue; Start-Sleep 5 }
Copy-Item "$d\$File" $g -Force
"installed: " + (Get-FileHash $g).Hash.Substring(0,16) + " <- $File"
& "$d\FlkBoot.ps1" -Shot "flk3-$Name"
& "$d\FlkRun.ps1" -Name $Name -Out "D:\The Dark sign\TheDarkSign\Work\reports\flicker-ab3"
