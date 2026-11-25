# Flk5Summary.ps1: reads flicker-ab5\runs.txt (A1-A3, I1-I3) and writes summary.tsv with per-run rows and per-variant mean / SD / range.
param([string]$Out = "D:\The Dark sign\TheDarkSign\Work\reports\flicker-ab5")
$runs = Get-Content "$Out\runs.txt" | Where-Object { $_ -match '^[AI]\d\t' } | ForEach-Object {
    $p = $_ -split "`t"
    $fps = if ($_ -match '([\d.]+) fps') { [double]$Matches[1] } else { [double]::NaN }
    $med = if ($_ -match 'median ([\d.]+) ms') { [double]$Matches[1] } else { [double]::NaN }
    [pscustomobject]@{ run = $p[0]; v = $p[0].Substring(0, 1); hash = $p[1]; frames = [int]$p[2]
        sB = [double]$p[3]; sChg = [double]$p[4]; sJ = [int]$p[5]; sMax = [double]$p[6]
        pB = [double]$p[7]; pChg = [double]$p[8]; pJ = [int]$p[9]; pMax = [double]$p[10]; fps = $fps; med = $med }
}
$cols = 'frames', 'sB', 'sChg', 'sJ', 'sMax', 'pB', 'pChg', 'pJ', 'pMax', 'fps'
$lines = @("run`thash`t" + ($cols -join "`t"))
foreach ($r in $runs) { $lines += "$($r.run)`t$($r.hash)`t" + (($cols | ForEach-Object { $r.$_ }) -join "`t") }
foreach ($v in 'A', 'I') {
    $set = @($runs | Where-Object v -eq $v)
    $m = @(); $s = @(); $rg = @()
    foreach ($c in $cols) {
        $x = @($set | ForEach-Object { [double]$_.$c }); $mu = ($x | Measure-Object -Average).Average
        $sd = if ($x.Count -gt 1) { [math]::Sqrt((($x | ForEach-Object { ($_ - $mu) * ($_ - $mu) }) | Measure-Object -Sum).Sum / ($x.Count - 1)) } else { 0 }
        $m += "{0:N3}" -f $mu; $s += "{0:N3}" -f $sd; $rg += "{0:N2}-{1:N2}" -f ($x | Measure-Object -Minimum).Minimum, ($x | Measure-Object -Maximum).Maximum
    }
    $lines += "$v mean ($($set.Count))`t`t" + ($m -join "`t"); $lines += "$v SD`t`t" + ($s -join "`t"); $lines += "$v range`t`t" + ($rg -join "`t")
}
$lines | Set-Content "$Out\summary.tsv"; $lines
