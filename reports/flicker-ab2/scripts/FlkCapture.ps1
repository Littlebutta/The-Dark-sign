# FlkCapture.ps1 -Name X -Seconds 15 -Out dir
# Grabs Elden Ring's client area as fast as possible for N seconds. For each frame writes "<ms>\t<mean R+G+B>" to
# <Out>\<Name>.tsv (sparse 48x27 grid, 765 = white; same measure as reports/flicker-ab). Saves frame 0 as
# <Name>-0.png and one JPEG every 500 ms (<Name>-s<ms>.jpg) for the contact sheet.
param([Parameter(Mandatory)][string]$Name, [double]$Seconds = 15, [Parameter(Mandatory)][string]$Out)
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class Flk {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int l, t, r, b; }
  [StructLayout(LayoutKind.Sequential)] public struct PT { public int x, y; }
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref PT p);
  public static double Mean(Bitmap b) {
    var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    long sum = 0; int n = 0;
    for (int gy = 0; gy < 27; gy++) for (int gx = 0; gx < 48; gx++) {
      int x = (int)((gx + 0.5) * b.Width / 48), y = (int)((gy + 0.5) * b.Height / 27);
      int px = Marshal.ReadInt32(d.Scan0, y * d.Stride + x * 4);
      sum += (px & 255) + ((px >> 8) & 255) + ((px >> 16) & 255); n++;
    }
    b.UnlockBits(d); return (double)sum / n;
  }
}
'@
$game = Get-Process eldenring -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $game) { "eldenring not running"; exit 2 }
$cr = New-Object Flk+RECT; $pt = New-Object Flk+PT
[void][Flk]::GetClientRect($game.MainWindowHandle, [ref]$cr); [void][Flk]::ClientToScreen($game.MainWindowHandle, [ref]$pt)
$w = $cr.r; $h = $cr.b
New-Item -ItemType Directory -Force $Out | Out-Null
$full = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($full)
$jpg = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$ep = New-Object System.Drawing.Imaging.EncoderParameters 1
$ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 80L
$sb = New-Object System.Text.StringBuilder
$clock = [System.Diagnostics.Stopwatch]::StartNew(); $n = 0; $nextSheet = 0
while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
    $t = $clock.ElapsedMilliseconds
    $g.CopyFromScreen($pt.x, $pt.y, 0, 0, $full.Size)
    [void]$sb.AppendFormat("{0:D5}`t{1:F1}`n", $t, [Flk]::Mean($full))
    if ($n -eq 0) { $s = New-Object System.Drawing.Bitmap $full, 960, 540; $s.Save("$Out\$Name-0.png", [System.Drawing.Imaging.ImageFormat]::Png); $s.Dispose() }
    if ($t -ge $nextSheet) { $s = New-Object System.Drawing.Bitmap $full, 640, 360; $s.Save(("{0}\{1}-s{2:D5}.png" -f $Out, $Name, $t), [System.Drawing.Imaging.ImageFormat]::Png); $s.Dispose(); $nextSheet += 500 }
    $n++
}
[IO.File]::WriteAllText("$Out\$Name.tsv", $sb.ToString())
"{0} frames in {1:N1} s, {2:N1} ms apart" -f $n, $clock.Elapsed.TotalSeconds, ($clock.ElapsedMilliseconds / [math]::Max(1, $n))
