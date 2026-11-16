param([string]$Out)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class GP {
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(P f, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static IntPtr Find(string needle) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); if (sb.ToString().Contains(needle)) { found = h; return false; } return true; }, IntPtr.Zero);
    return found;
  }
}
"@
$h = [GP]::Find('Discord Share')
if ($h -eq [IntPtr]::Zero) { 'no projector'; exit 1 }
$r = New-Object GP+RECT; [void][GP]::GetClientRect($h, [ref]$r)
$bmp = New-Object System.Drawing.Bitmap ($r.R), ($r.B)
$g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
$ok = [GP]::PrintWindow($h, $dc, 2)
$g.ReleaseHdc($dc); $g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
# average brightness of the whole frame and of the bottom-left corner, where the overlay sits
function Mean($x0, $y0, $w, $h0) { $sum = 0; $n = 0; for ($y = $y0; $y -lt $y0 + $h0; $y += 8) { for ($x = $x0; $x -lt $x0 + $w; $x += 8) { $c = $bmp.GetPixel($x, $y); $sum += ($c.R + $c.G + $c.B) / 3; $n++ } }; [math]::Round($sum / [math]::Max(1,$n), 1) }
"printwindow ok=$ok client=$($r.R)x$($r.B) mean all=$(Mean 0 0 $r.R $r.B) bottom-left=$(Mean 0 ([int]($r.B*0.75)) ([int]($r.R*0.3)) ([int]($r.B*0.25)))"
$bmp.Dispose()
