# Flk5Run.ps1 -Name A1 -File m45_00_0000.A-asbuilt.gparam.dcx
# Flicker round 5: one run = close game, install gparam, put the test save back, PLAY, Continue, wait, capture.
# Capture timeline (ms from first captured frame): Ctrl+Shift+F8 perf start at 0; still 0-8000 (no input);
# camera-only pan 90 deg left 8000-11000 and back 11000-14000 (1800 mouse counts = 90 deg, calibrated on the compass);
# Ctrl+Shift+F8 at 14500; capture runs 15.5 s.
# Writes <Out>\<Name>.tsv, <Name>-0.png, <Name>-sheet.png, <Name>-times.txt, and a line in <Out>\runs.txt.
param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$File,
      [string]$Out = "D:\The Dark sign\TheDarkSign\Work\reports\flicker-ab5")
$ErrorActionPreference = 'Stop'
$d = "D:\The Dark sign\TheDarkSign\Work\scratch\flicker-ab"
$g = "D:\The Dark sign\TheDarkSign\Mods\ER\param\drawparam\m45_00_0000.gparam.dcx"
$sv = "D:\The Dark sign\TheDarkSign\Saves\Default\ER\76561198069423426"
$ts = "$d\testsave5"
$S = "D:\The Dark sign\TheDarkSign\Tools\Scripts"
$llog = "D:\The Dark sign\Launcher\TheDarkSignLauncher.log"
$log = "D:\The Dark sign\TheDarkSign\Saves\Default\ER\dksign_testmenu.log"
New-Item -ItemType Directory -Force $Out | Out-Null

# 1. Close the game, install variant + test save, prove hashes.
$p = Get-Process eldenring -EA SilentlyContinue; if ($p) { $p | Stop-Process -Force; $p | Wait-Process -Timeout 60 -EA SilentlyContinue; Start-Sleep 5 }
Copy-Item "$d\$File" $g -Force
Copy-Item "$ts\ER0000.dksign", "$ts\ER0000.dksign.bak" $sv -Force
$hg = (Get-FileHash $g).Hash; $hs = (Get-FileHash "$sv\ER0000.dksign").Hash; $hb = (Get-FileHash "$sv\ER0000.dksign.bak").Hash
$hc = (Get-FileHash "D:\The Dark sign\TheDarkSign\Saves\Default\ER\GraphicsConfig.xml").Hash
"$Name gparam $hg save $hs bak $hb gfx $hc"
if ($hs -ne 'F4FD0517204CFE578ECD3D8D0C897A1C176F486C8C3215EF1D791678AB737CB1' -or $hb -ne $hs) { "test save hash wrong"; exit 3 }

# 2. Guard: newest release must still be the installed 1.0.636, and PLAY must start the game (no rebuild).
$rel = (curl.exe -s "https://api.github.com/repos/Littlebutta/The-Dark-sign/releases?per_page=1" | ConvertFrom-Json)[0].tag_name
if ($rel -and $rel -ne 'launcher-v1.0.636') { "STOP: newer release $rel"; exit 4 }
$n = (Get-Content $llog).Count
& "$S\Launcher-UI.ps1" -Click PlayButton
$sw = [Diagnostics.Stopwatch]::StartNew()
while (-not (Get-Process eldenring -EA SilentlyContinue | Where-Object MainWindowHandle -ne 0) -and $sw.Elapsed.TotalSeconds -lt 90) { Start-Sleep 1 }
$new = Get-Content $llog | Select-Object -Skip $n
if (-not ($new -match 'Starting .*me3\.exe launch') -or ($new -match 'Rebuild|port-maps|update')) { "STOP: PLAY did not start the game cleanly:"; $new; exit 5 }
$gp = Get-Process eldenring -EA SilentlyContinue | Select-Object -First 1
if (-not $gp) { "STOP: game did not start"; exit 6 }
$par = (Get-CimInstance Win32_Process -Filter "ProcessId=$($gp.Id)").ParentProcessId
$me3 = Get-CimInstance Win32_Process -Filter "ProcessId=$par"; $me3p = Get-CimInstance Win32_Process -Filter "ProcessId=$((Get-CimInstance Win32_Process -Filter "ProcessId=$($me3.ParentProcessId)").ParentProcessId)" -EA SilentlyContinue
"game pid $($gp.Id) <- $($me3.Name) <- ... <- $($me3p.Name)"

# 3. Title -> Continue -> load (~6-10 s, camera settles) -> wait ~5 s more.
Start-Sleep 25
& "$S\ER-Input.ps1" -Steal -Keys "Enter,Wait3000,Enter,Wait2500,Enter,Wait10000" | Out-Null

# 4. Capture + scripted input.
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class F5In {
  [StructLayout(LayoutKind.Sequential)] public struct KI { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public KI ki; public long pad; }
  [StructLayout(LayoutKind.Sequential)] public struct MI { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct MINPUT { public uint type; public MI mi; }
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] i, int size);
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, MINPUT[] i, int size);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public static void Key(ushort scan, bool up) { var i = new INPUT[1]; i[0].type = 1; i[0].ki.scan = scan; i[0].ki.flags = 0x0008u | (up ? 0x0002u : 0); SendInput(1, i, Marshal.SizeOf(typeof(INPUT))); }
  public static void Move(int dx) { var i = new MINPUT[1]; i[0].type = 0; i[0].mi.dx = dx; i[0].mi.flags = 1; SendInput(1, i, Marshal.SizeOf(typeof(MINPUT))); }
}
"@
function CS([uint16]$c) { [F5In]::Key(0x1D, $false); [F5In]::Key(0x2A, $false); Start-Sleep -Milliseconds 60; [F5In]::Key($c, $false); Start-Sleep -Milliseconds 150; [F5In]::Key($c, $true); Start-Sleep -Milliseconds 60; [F5In]::Key(0x2A, $true); [F5In]::Key(0x1D, $true) }
Remove-Item "$Out\$Name-s[0-9]*.png", "$Out\$Name-0.png" -EA SilentlyContinue
$logLines = (Get-Content $log).Count
# Ctrl+Shift+F8 also taps Shift, which is backstep in Devin's binds, so start the perf record 5 s BEFORE the capture:
# the backstep and camera recentre finish before the still phase. (The closing F8 at 14.5 s comes after the pan.)
[void][F5In]::SetForegroundWindow($gp.MainWindowHandle); Start-Sleep -Milliseconds 300
$tp = [Diagnostics.Stopwatch]::StartNew(); CS 0x42
Start-Sleep 5
$cap = Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$d\FlkCapture.ps1`" -Name $Name -Seconds 15.5 -Out `"$Out`"" -PassThru -WindowStyle Hidden -RedirectStandardOutput "$Out\$Name-cap.txt"
$w = [Diagnostics.Stopwatch]::StartNew(); while (-not (Test-Path "$Out\$Name-0.png") -and $w.ElapsedMilliseconds -lt 15000) { Start-Sleep -Milliseconds 5 }
$t0 = [Diagnostics.Stopwatch]::StartNew(); $times = @()
[void][F5In]::SetForegroundWindow($gp.MainWindowHandle)
$times += "perf-start`t-$($tp.ElapsedMilliseconds - $t0.ElapsedMilliseconds)"
while ($t0.ElapsedMilliseconds -lt 8000) { Start-Sleep -Milliseconds 2 }
$times += "pan-start`t$($t0.ElapsedMilliseconds)"
# Pan: send mouse counts so the total follows a linear ramp, -1800 over 3 s then +1800 over 3 s.
$sent = 0
while ($true) {
    $ms = $t0.ElapsedMilliseconds - 8000; if ($ms -ge 6000) { break }
    $target = if ($ms -lt 3000) { [int](-1800 * $ms / 3000) } else { [int](-1800 + 1800 * ($ms - 3000) / 3000) }
    $dx = $target - $sent; if ($dx -ne 0) { [F5In]::Move($dx); $sent += $dx }
    Start-Sleep -Milliseconds 8
}
if ($sent -ne 0) { [F5In]::Move(-$sent); $sent = 0 }
$times += "pan-end`t$($t0.ElapsedMilliseconds)"
while ($t0.ElapsedMilliseconds -lt 14500) { Start-Sleep -Milliseconds 5 }
CS 0x42; $times += "perf-end`t$($t0.ElapsedMilliseconds)"
$times | Set-Content "$Out\$Name-times.txt"
$cap.WaitForExit(); Start-Sleep 1

# 5. Stats.
$panS = [int](($times | Where-Object { $_ -like 'pan-start*' }) -split "`t")[1]; $panE = [int](($times | Where-Object { $_ -like 'pan-end*' }) -split "`t")[1]
$rows = Get-Content "$Out\$Name.tsv" | Where-Object { $_ } | ForEach-Object { $q = $_ -split "`t"; [pscustomobject]@{ t = [int]$q[0]; b = [double]$q[1] } }
function St($set) {
    $dd = @(); for ($i = 1; $i -lt $set.Count; $i++) { $dd += [math]::Abs($set[$i].b - $set[$i - 1].b) }
    $mb = ($set | Measure-Object b -Average).Average; $md = ($dd | Measure-Object -Average).Average; $mx = ($dd | Measure-Object -Maximum).Maximum
    "{0:N1}`t{1:N3}`t{2}`t{3:N1}" -f $mb, $md, @($dd | Where-Object { $_ -gt 8 }).Count, $mx
}
$still = @($rows | Where-Object { $_.t -lt $panS }); $pan = @($rows | Where-Object { $_.t -ge $panS -and $_.t -le $panE })
$perf = Get-Content $log | Select-Object -Skip $logLines | Where-Object { $_ -match 'perf summary: \d+ s,' }
$line = "{0}`t{1}`t{2}`t{3}`t{4}`t{5}" -f $Name, $hg.Substring(0, 8), $rows.Count, (St $still), (St $pan), ($perf -join ' | ')
$line | Add-Content "$Out\runs.txt"; $line
& "$S\Montage.ps1" -Out "$Out\$Name-sheet.png" -Width 320 -Glob "$Out\$Name-s*.png" | Out-Null
Remove-Item "$Out\$Name-s[0-9]*.png" -EA SilentlyContinue
$p = Get-Process eldenring -EA SilentlyContinue; if ($p) { $p | Stop-Process -Force; $p | Wait-Process -Timeout 60 -EA SilentlyContinue }

