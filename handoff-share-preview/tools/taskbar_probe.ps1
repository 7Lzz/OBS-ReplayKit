param([int]$ObsPid)
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class TB {
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(P f, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint c);
  [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  public static List<string> Visible(uint pid) {
    var rows = new List<string>();
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p != pid || !IsWindowVisible(h)) return true;
      long ex = GetWindowLongPtr(h, -20).ToInt64(); IntPtr own = GetWindow(h, 4);
      bool eligible = (own == IntPtr.Zero && (ex & 0x80) == 0) || (ex & 0x40000) != 0;
      var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
      rows.Add(string.Format("{0} | ex={1:X} owner={2} taskbar-eligible={3}", sb, ex, own, eligible));
      return true;
    }, IntPtr.Zero);
    return rows;
  }
}
"@
"visible windows of obs ${ObsPid}:"
[TB]::Visible([uint32]$ObsPid)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'Shell_TrayWnd')
$tray = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
"taskbar buttons:"
foreach ($e in $tray.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) { "  " + ($e.Current.Name -replace "`r?`n", ' / ') }
