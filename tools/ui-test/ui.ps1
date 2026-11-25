# Helpers for checking the Wipriss UI by hand or from an agent. Dot-source it:  . tools\ui-test\ui.ps1
# Coordinates are in DIP (like WPF); the screen scale is read from the system.
# Screenshots go to %TEMP%\wipriss-ui. Nothing here reads or sends personal data.
# Note: the island, the screenshot toast and the scroll status window are excluded from screen captures,
# so they never show on these screenshots; use WiprissWindows to find them.

Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class UI {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
  public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; }
  [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
  static void Mouse(uint flags) { var a = new INPUT[1]; a[0].type = 0; a[0].u.mi.dwFlags = flags; SendInput(1, a, Marshal.SizeOf(typeof(INPUT))); }
  public static void Click() { Mouse(0x2); Mouse(0x4); }
  public static void Down() { Mouse(0x2); }
  public static void Up() { Mouse(0x4); }
  public static void Key(ushort vk) {
    var a = new INPUT[2]; a[0].type = 1; a[0].u.ki.wVk = vk; a[1].type = 1; a[1].u.ki.wVk = vk; a[1].u.ki.dwFlags = 0x2;
    SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));
  }
  public static void Type(string s) {
    foreach (char c in s) {
      var a = new INPUT[2]; a[0].type = 1; a[0].u.ki.wScan = c; a[0].u.ki.dwFlags = 0x4; a[1].type = 1; a[1].u.ki.wScan = c; a[1].u.ki.dwFlags = 0x4 | 0x2;
      SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));
    }
  }
}
public static class Wins {
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(P p, IntPtr l);
  [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out R r);
  public struct R { public int L, T, Rt, B; }
  // "Title|left|top|right|bottom" in physical pixels, for visible windows of one process.
  public static List<string> Of(int pid) {
    var list = new List<string>();
    EnumWindows((h, l) => { int p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) { var s = new StringBuilder(256); GetWindowText(h, s, 256); R r; GetWindowRect(h, out r);
        list.Add(s + "|" + r.L + "|" + r.T + "|" + r.Rt + "|" + r.B); }
      return true; }, IntPtr.Zero);
    return list;
  }
}
"@

[UI]::SetProcessDPIAware() | Out-Null
$script:S = [UI]::GetDpiForSystem() / 96.0
$script:ScreenDip = [UI]::GetSystemMetrics(0) / $script:S
$script:Shots = Join-Path $env:TEMP 'wipriss-ui'
New-Item -ItemType Directory -Force $script:Shots | Out-Null
$script:Exe = Join-Path (Split-Path (Split-Path $PSScriptRoot)) 'artifacts\publish\Wipriss.exe'

# Screenshot of a DIP rectangle into %TEMP%\wipriss-ui\<name>.png
function Shot([string]$name, [double]$x, [double]$y, [double]$w, [double]$h) {
  $b = New-Object System.Drawing.Bitmap ([int]($w * $S)), ([int]($h * $S))
  $g = [System.Drawing.Graphics]::FromImage($b); $g.CopyFromScreen([int]($x * $S), [int]($y * $S), 0, 0, $b.Size)
  $b.Save("$Shots\$name.png"); $g.Dispose(); $b.Dispose()
}

# A module panel opens centred under the island, 48 DIP from the top.
function ShotFlyout([string]$name, [double]$w, [double]$h) { Shot $name (($ScreenDip - $w) / 2) 48 $w $h }

function ClickDip([double]$x, [double]$y) { [UI]::SetCursorPos([int]($x * $S), [int]($y * $S)) | Out-Null; Start-Sleep -Milliseconds 80; [UI]::Click() }

function DragDip([double]$x1, [double]$y1, [double]$x2, [double]$y2) {
  [UI]::SetCursorPos([int]($x1 * $S), [int]($y1 * $S)) | Out-Null; Start-Sleep -Milliseconds 120
  [UI]::Down(); Start-Sleep -Milliseconds 80
  for ($i = 1; $i -le 10; $i++) {
    [UI]::SetCursorPos([int](($x1 + ($x2 - $x1) * $i / 10) * $S), [int](($y1 + ($y2 - $y1) * $i / 10) * $S)) | Out-Null
    Start-Sleep -Milliseconds 25
  }
  Start-Sleep -Milliseconds 80; [UI]::Up()
}

# Only type or press keys when Wipriss has focus, so nothing lands in another program.
function FgIsWipriss {
  $fpid = 0; [UI]::GetWindowThreadProcessId([UI]::GetForegroundWindow(), [ref]$fpid) | Out-Null
  $p = Get-Process -Id $fpid -ErrorAction SilentlyContinue
  return ($p -and $p.ProcessName -eq 'Wipriss')
}

function SaveCursor { $p = New-Object UI+POINT; [UI]::GetCursorPos([ref]$p) | Out-Null; return $p }
function RestoreCursor($p) { [UI]::SetCursorPos($p.X, $p.Y) | Out-Null }

function WiprissWindows { $p = Get-Process Wipriss -ErrorAction SilentlyContinue | Select-Object -First 1; if ($p) { [Wins]::Of($p.Id) } }

# "WIDTHxHEIGHT" of the image on the clipboard, or "no image".
function ClipImageSize { $img = [System.Windows.Forms.Clipboard]::GetImage(); if ($img) { "$($img.Width)x$($img.Height)"; $img.Dispose() } else { "no image" } }

# Opens the island by hovering its top centre, then moves the pointer away again.
function HoverIsland([int]$ms = 900) {
  [UI]::SetCursorPos([int](($ScreenDip / 2) * $S), [int](20 * $S)) | Out-Null; Start-Sleep -Milliseconds $ms
}

function PressPrintScreen { [UI]::Key(0x2C) }
