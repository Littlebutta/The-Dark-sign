# FastBurst.ps1 -Name x -Seconds 7 [-Scale 0.5]: grabs Elden Ring's client area as fast as possible for N seconds,
# saving JPEGs named <Name>-<ms since start>.jpg in Work\reports\boot\fast. Prints the frame count and mean interval.
param([Parameter(Mandatory)][string]$Name, [double]$Seconds = 7, [double]$Scale = 0.5)
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace FB -Name R -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int l, t, r, b; }
[StructLayout(LayoutKind.Sequential)] public struct PT { public int x, y; }
[DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref PT p);
'@ -ErrorAction SilentlyContinue
$game = Get-Process eldenring -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $game) { "eldenring not running"; exit 2 }
$cr = New-Object FB.R+RECT; $pt = New-Object FB.R+PT
[void][FB.R]::GetClientRect($game.MainWindowHandle, [ref]$cr); [void][FB.R]::ClientToScreen($game.MainWindowHandle, [ref]$pt)
$w = $cr.r; $h = $cr.b
$out = "D:\The Dark sign\TheDarkSign\Work\reports\boot\fast"
New-Item -ItemType Directory -Force $out | Out-Null
$full = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($full)
$sw = [int]($w * $Scale); $sh = [int]($h * $Scale)
$jpg = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$ep = New-Object System.Drawing.Imaging.EncoderParameters 1
$ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 80L
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$n = 0
while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
    $t = $clock.ElapsedMilliseconds
    $g.CopyFromScreen($pt.x, $pt.y, 0, 0, $full.Size)
    $small = New-Object System.Drawing.Bitmap $full, $sw, $sh
    $small.Save(("{0}\{1}-{2:D5}.jpg" -f $out, $Name, $t), $jpg, $ep)
    $small.Dispose()
    $n++
}
"{0} frames in {1:N1} s, {2:N0} ms apart; started {3:HH:mm:ss.fff}" -f $n, $clock.Elapsed.TotalSeconds, ($clock.ElapsedMilliseconds / [math]::Max(1, $n)), (Get-Date).AddMilliseconds(-$clock.ElapsedMilliseconds)
