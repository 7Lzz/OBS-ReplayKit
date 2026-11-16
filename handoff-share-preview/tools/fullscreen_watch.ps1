param([string]$Dir, [int]$Seconds = 180)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Text; using System.Diagnostics; using System.Runtime.InteropServices;
public static class FW {
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(P f, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetLayeredWindowAttributes(IntPtr h, out uint k, out byte a, out uint f);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr m, ref MI mi);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int size);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct MI { public int cb; public RECT mon; public RECT work; public uint flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dev; }
  public static IntPtr Find(string needle) { IntPtr found = IntPtr.Zero; EnumWindows((h, l) => { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); if (sb.ToString().Contains(needle)) { found = h; return false; } return true; }, IntPtr.Zero); return found; }
  public static string Title(IntPtr h) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
  public static string Exe(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); try { return Process.GetProcessById((int)pid).ProcessName; } catch { return "?"; } }
  public static string Mon(IntPtr h) { var mi = new MI(); mi.cb = Marshal.SizeOf(typeof(MI)); return GetMonitorInfoW(MonitorFromWindow(h, 2), ref mi) ? mi.dev + " " + mi.mon.L + "," + mi.mon.T + "," + mi.mon.R + "," + mi.mon.B : "?"; }
  public static int Cloaked(IntPtr h) { int v; return DwmGetWindowAttribute(h, 14, out v, 4) == 0 ? v : -1; }
}
"@
$log = Join-Path $Dir "fullscreen_watch.log"
"started $(Get-Date -Format o)" | Set-Content $log
$saved = 0; $savedBefore = $false
$end = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $end) {
  $p = [FW]::Find('Discord Share')
  $fg = [FW]::GetForegroundWindow()
  $fr = New-Object FW+RECT; [void][FW]::GetWindowRect($fg, [ref]$fr)
  $line = "{0} fg={1} '{2}' rect={3},{4},{5},{6} mon={7}" -f (Get-Date -Format HH:mm:ss), [FW]::Exe($fg), ([FW]::Title($fg) -replace '\s+',' ').Substring(0, [math]::Min(40, [FW]::Title($fg).Length)), $fr.L, $fr.T, $fr.R, $fr.B, [FW]::Mon($fg)
  if ($p -ne [IntPtr]::Zero) {
    $pr = New-Object FW+RECT; [void][FW]::GetWindowRect($p, [ref]$pr)
    $cr = New-Object FW+RECT; [void][FW]::GetClientRect($p, [ref]$cr)
    $k=0;$a=0;$f=0; [void][FW]::GetLayeredWindowAttributes($p,[ref]$k,[ref]$a,[ref]$f)
    $ex = [FW]::GetWindowLongPtr($p, -20).ToInt64()
    $line += " | proj rect={0},{1},{2},{3} client={4}x{5} topmost={6} alpha={7} vis={8} iconic={9} cloaked={10}" -f $pr.L,$pr.T,$pr.R,$pr.B,$cr.R,$cr.B,(($ex -band 8) -ne 0),$a,[FW]::IsWindowVisible($p),[FW]::IsIconic($p),[FW]::Cloaked($p)
    if ($cr.R -gt 0 -and $cr.B -gt 0) {
      $bmp = New-Object System.Drawing.Bitmap ($cr.R), ($cr.B)
      $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc(); $ok = [FW]::PrintWindow($p, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
      $s=0;$n=0; for($y=[int]($cr.B*0.8);$y -lt $cr.B;$y+=6){ for($x=0;$x -lt [int]($cr.R*0.2);$x+=6){ $c=$bmp.GetPixel($x,$y); $s+=($c.R+$c.G+$c.B)/3; $n++ } }
      $line += " | grab ok=$ok overlay-corner mean=$([math]::Round($s/[math]::Max(1,$n),1))"
      $isGame = [FW]::Exe($fg) -match 'Roblox'
      if (($isGame -and $saved -lt 4) -or (-not $isGame -and -not $savedBefore)) {
        $name = if ($isGame) { "grab_roblox_$saved.png" } else { "grab_before.png" }
        $bmp.Save((Join-Path $Dir $name), [System.Drawing.Imaging.ImageFormat]::Png)
        if ($isGame) { $saved++ } else { $savedBefore = $true }
        $line += " saved=$name"
      }
      $bmp.Dispose()
    }
  } else { $line += " | no projector" }
  Add-Content $log $line
  Start-Sleep -Seconds 2
}
"done $(Get-Date -Format o)" | Add-Content $log
