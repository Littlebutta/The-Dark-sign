# FlkRun.ps1 -Name X [-NoWarp]: one timed flicker capture on DS3 map 1 (Firelink), game already in the world.
# Warps with Ctrl+Shift+N1, waits for the load, then starts FlkCapture (16 s) and, in parallel:
#   Ctrl+Shift+F8 (perf start) -> stand still until 4.0 s -> W 2.5 s -> turn -> W 2.5 s -> turn back -> W 2 s -> Ctrl+Shift+F8.
# Writes <Out>\<Name>.tsv, <Name>-0.png, <Name>-sheet.png, <Name>-times.txt (input times in capture ms) and
# prints/appends a summary line to <Out>\runs.txt (still vs moving, perf lines from dksign_testmenu.log).
param([Parameter(Mandatory)][string]$Name, [switch]$NoWarp, [string]$Out = "D:\The Dark sign\TheDarkSign\Work\reports\flicker-ab4")
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$log = "D:\The Dark sign\TheDarkSign\Saves\Default\ER\dksign_testmenu.log"
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class FkIn {
  [StructLayout(LayoutKind.Sequential)] public struct KI { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public KI ki; public long pad; }
  [StructLayout(LayoutKind.Sequential)] public struct MI { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct MINPUT { public uint type; public MI mi; }
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] i, int size);
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, MINPUT[] i, int size);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  public static void Key(ushort scan, bool up) {
    var i = new INPUT[1]; i[0].type = 1; i[0].ki.scan = scan; i[0].ki.flags = 0x0008u | (up ? 0x0002u : 0);
    SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
  }
  public static void Move(int dx) {
    var i = new MINPUT[1]; i[0].type = 0; i[0].mi.dx = dx; i[0].mi.flags = 1; SendInput(1, i, Marshal.SizeOf(typeof(MINPUT)));
  }
}
"@
$game = Get-Process eldenring -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $game) { "eldenring not running"; exit 2 }
[void][FkIn]::ShowWindow($game.MainWindowHandle, 9); [void][FkIn]::SetForegroundWindow($game.MainWindowHandle); Start-Sleep -Milliseconds 500
function CS([uint16]$c) { [FkIn]::Key(0x1D, $false); [FkIn]::Key(0x2A, $false); Start-Sleep -Milliseconds 60; [FkIn]::Key($c, $false); Start-Sleep -Milliseconds 150; [FkIn]::Key($c, $true); Start-Sleep -Milliseconds 60; [FkIn]::Key(0x2A, $true); [FkIn]::Key(0x1D, $true) }
function HoldW([int]$ms) { [FkIn]::Key(0x11, $false); Start-Sleep -Milliseconds $ms; [FkIn]::Key(0x11, $true) }
function Turn([int]$dx) { for ($j = 0; $j -lt 15; $j++) { [FkIn]::Move($dx); Start-Sleep -Milliseconds 30 } }

if (-not $NoWarp) { CS 0x02; Start-Sleep -Seconds 60 }   # round 4: the save sits in the Cemetery, so the warp load is long   # Ctrl+Shift+1 = warp to map 1
$l0 = if (Test-Path $log) { (Get-Content $log).Count } else { 0 }
CS 0x43; Start-Sleep -Seconds 2   # Ctrl+Shift+F9 = enemies ignore player (round 4)
CS 0x3E; Start-Sleep -Seconds 3   # Ctrl+Shift+F4 = clear spawned enemies (F9 alone did not stop the attack)
"F9 log: " + ((Get-Content $log | Select-Object -Skip $l0) -join " | ")
New-Item -ItemType Directory -Force $Out | Out-Null
Remove-Item "$Out\$Name-s[0-9]*.png", "$Out\$Name-0.png" -ErrorAction SilentlyContinue
$logLines = if (Test-Path $log) { (Get-Content $log).Count } else { 0 }
$cap = Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$here\FlkCapture.ps1`" -Name $Name -Seconds 16 -Out `"$Out`"" -PassThru -WindowStyle Hidden -RedirectStandardOutput "$Out\$Name-cap.txt"
# Wait for the capture's first frame, so input times line up with capture ms.
$sw = [Diagnostics.Stopwatch]::StartNew()
while (-not (Test-Path "$Out\$Name-0.png") -and $sw.ElapsedMilliseconds -lt 15000) { Start-Sleep -Milliseconds 10 }
$t0 = [Diagnostics.Stopwatch]::StartNew(); $times = @()
[void][FkIn]::SetForegroundWindow($game.MainWindowHandle)
CS 0x42; $times += "perf-start`t$($t0.ElapsedMilliseconds)"      # Ctrl+Shift+F8
while ($t0.ElapsedMilliseconds -lt 4000) { Start-Sleep -Milliseconds 5 }
$times += "move-start`t$($t0.ElapsedMilliseconds)"
HoldW 2500; Turn 60; HoldW 2500; Turn -60; HoldW 2000
$times += "move-end`t$($t0.ElapsedMilliseconds)"
CS 0x42; $times += "perf-end`t$($t0.ElapsedMilliseconds)"
$times | Set-Content "$Out\$Name-times.txt"
$cap.WaitForExit()
Start-Sleep -Milliseconds 800

# Analysis: still = frames before move-start, moving = frames from move-start to move-end.
$moveStart = 4000; $moveEnd = [int](($times | Where-Object { $_ -like 'move-end*' }) -split "`t")[1]
$rows = Get-Content "$Out\$Name.tsv" | Where-Object { $_ } | ForEach-Object { $p = $_ -split "`t"; [pscustomobject]@{ t = [int]$p[0]; b = [double]$p[1] } }
function Stats($set) {
    $d = @(); for ($i = 1; $i -lt $set.Count; $i++) { $d += [math]::Abs($set[$i].b - $set[$i - 1].b) }
    $mb = ($set | Measure-Object b -Average).Average; $md = if ($d.Count) { ($d | Measure-Object -Average).Average } else { 0 }
    "{0,6:N1} {1,5:N2} {2,3}" -f $mb, $md, @($d | Where-Object { $_ -gt 8 }).Count
}
$still = @($rows | Where-Object { $_.t -lt $moveStart }); $mov = @($rows | Where-Object { $_.t -ge $moveStart -and $_.t -le $moveEnd })
$perf = if (Test-Path $log) { Get-Content $log | Select-Object -Skip $logLines | Where-Object { $_ -match 'perf' } } else { @() }
$line = "{0}`tframes {1}`tall {2}`tstill(mean chg jumps) {3}`tmoving {4}`t{5}" -f $Name, $rows.Count, (Stats $rows), (Stats $still), (Stats $mov), (($perf | Where-Object { $_ -match 'perf summary: \d+ s,' }) -join ' | ')
$line | Add-Content "$Out\runs.txt"; $line
Get-Content "$Out\$Name-cap.txt"
& "D:\The Dark sign\TheDarkSign\Tools\Scripts\Montage.ps1" -Out "$Out\$Name-sheet.png" -Width 427 -Glob "$Out\$Name-s*.png" | Out-Null
Remove-Item "$Out\$Name-s[0-9]*.png" -ErrorAction SilentlyContinue





