Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PP {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetLayeredWindowAttributes(IntPtr h, out uint k, out byte a, out uint f);
  [DllImport("user32.dll")] public static extern int GetWindowRgnBox(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
$out = @()
[PP]::EnumWindows({ param($h, $l)
  $sb = New-Object System.Text.StringBuilder 512
  [void][PP]::GetWindowText($h, $sb, 512)
  $t = $sb.ToString()
  if ($t -like '*Discord Share*' -or $t -like '*Projector*') {
    $cb = New-Object System.Text.StringBuilder 256; [void][PP]::GetClassName($h, $cb, 256)
    $r = New-Object PP+RECT; [void][PP]::GetWindowRect($h, [ref]$r)
    $c = New-Object PP+RECT; [void][PP]::GetClientRect($h, [ref]$c)
    $k = 0; $a = 0; $f = 0; $lay = [PP]::GetLayeredWindowAttributes($h, [ref]$k, [ref]$a, [ref]$f)
    $rg = New-Object PP+RECT; $rgType = [PP]::GetWindowRgnBox($h, [ref]$rg)
    $procId = 0; [void][PP]::GetWindowThreadProcessId($h, [ref]$procId)
    $script:out += [pscustomobject]@{
      hwnd = $h.ToInt64(); title = $t; cls = $cb.ToString(); pid = $procId
      style = '{0:X8}' -f $([PP]::GetWindowLongPtr($h, -16).ToInt64()); ex = '{0:X8}' -f $([PP]::GetWindowLongPtr($h, -20).ToInt64())
      rect = "$($r.L),$($r.T) $($r.R - $r.L)x$($r.B - $r.T)"; client = "$($c.R)x$($c.B)"
      visible = [PP]::IsWindowVisible($h); iconic = [PP]::IsIconic($h); owner = [PP]::GetWindow($h, 4).ToInt64()
      layered = "$lay a=$a f=$f"; rgn = "type=$rgType $($rg.L),$($rg.T),$($rg.R),$($rg.B)"; dpi = [PP]::GetDpiForWindow($h)
    }
  }
  return $true
}, [IntPtr]::Zero) | Out-Null
$out | Format-List
