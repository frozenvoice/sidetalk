[CmdletBinding()]
param(
    # Inno scales its wizard from the dialog font, so a larger DialogFontSize
    # reproduces the layout Windows gets at a higher DPI without changing any
    # system setting. The measured scale of each run is reported.
    [int[]]$DialogFontSizes = @(11, 14, 18)
)

# Drives the real themed SIDETALK-Setup.exe on the interactive desktop: the
# confirmation (with the desktop shortcut option), progress, completion and failure
# pages, update, keyboard operation, the /SILENT window, the unstyled (high contrast)
# fallback and the layout at several scales. Every installer is compiled from
# installer\SIDETALK.iss with its own SIDETALKInstallerTest-* AppId, setup mutex,
# install, Start Menu and desktop folders under %TEMP%, exactly like Program.ps1. The
# payload is placeholder bytes that are never executed: Run is always unchecked or
# /SIDETALKNOLAUNCH is passed, and the uninstall cleanup executable is absent.
#
# The real SIDETALK installation, shortcuts and %LOCALAPPDATA%\SIDETALK are only read,
# and KakaoTalk and Sandboxie processes are only listed, to prove nothing changed.
# Only Setup processes this script started are ever stopped, by process id, and only
# if a check fails while one is still open.
#
# This is a presentation check on synthetic payload bytes. The shipped payload and the
# full install/update/uninstall contract stay covered by Program.ps1.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$scriptPath = Join-Path $repo 'installer\SIDETALK.iss'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) 'sidetalk-setup-ui-checks'
$evidenceDir = Join-Path $repo 'artifacts\verification\setup-ui'
$reportPath = Join-Path $evidenceDir 'report.json'
$productionAppId = 'AF6EF9DE-E012-4945-BBD1-39ED6572282D'
$timer = [Diagnostics.Stopwatch]::StartNew()

# The wizard's own words. Kept as escapes so this script stays ASCII.
function K([string]$Escaped) { return [regex]::Unescape($Escaped) }
$T = @{
    Install       = K '\uC124\uCE58'
    Cancel        = K '\uCDE8\uC18C'
    Finish        = K '\uB9C8\uCE68'
    Close         = K '\uB2EB\uAE30'
    Minimize      = K '\uCD5C\uC18C\uD654'
    Update        = K '\uC5C5\uB370\uC774\uD2B8'
    Yes           = K '\uC608(Y)'
    Run           = K 'SIDETALK \uC2E4\uD589'
    Desktop       = K '\uBC14\uD0D5 \uD654\uBA74\uC5D0 SIDETALK \uBC14\uB85C\uAC00\uAE30 \uB9CC\uB4E4\uAE30'
    HeadInstall   = K 'SIDETALK \uC124\uCE58'
    HeadUpdate    = K 'SIDETALK \uC5C5\uB370\uC774\uD2B8'
    HeadReinstall = K 'SIDETALK \uB2E4\uC2DC \uC124\uCE58'
    Installing    = K 'SIDETALK \uC124\uCE58 \uC911...'
    Updating      = K 'SIDETALK \uC5C5\uB370\uC774\uD2B8 \uC911...'
    Done          = K '\uC124\uCE58\uAC00 \uC644\uB8CC\uB418\uC5C8\uC2B5\uB2C8\uB2E4'
    DoneUpdate    = K '\uC5C5\uB370\uC774\uD2B8\uAC00 \uC644\uB8CC\uB418\uC5C8\uC2B5\uB2C8\uB2E4'
    Failed        = K '\uC124\uCE58\uD558\uC9C0 \uBABB\uD588\uC2B5\uB2C8\uB2E4'
    FailedUpdate  = K '\uC5C5\uB370\uC774\uD2B8\uD558\uC9C0 \uBABB\uD588\uC2B5\uB2C8\uB2E4'
    Running       = K '\uC2E4\uD589 \uC911'
    Lower         = K '\uB354 \uB0AE\uC740'
    LogLabel      = K '\uB85C\uADF8: '
}

$report = [ordered]@{
    kind = 'themed installer UI on the interactive desktop (synthetic payload, isolated SIDETALKInstallerTest-* fixture)'
    startedAt = [DateTime]::UtcNow.ToString('o')
    windows = [Environment]::OSVersion.VersionString
    checks = @()
    scales = @()
    screenshots = @()
    passed = $false
    error = $null
    cleanup = $null
}
function Add-Check([string]$Name) { $script:report.checks += $Name; Write-Host "PASS $Name" }

if (-not ('SidetalkSetupUi' -as [type])) {
    Add-Type -AssemblyName System.Drawing
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class SidetalkSetupUi {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct LOGFONT {
        public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
        public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet, lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
    }

    public sealed class Control {
        public IntPtr Handle;
        public string Class;
        public string Text;
        public RECT Rect;
        public bool Enabled;
        public int Style;
    }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, StringBuilder l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern IntPtr ChildWindowFromPointEx(IntPtr h, POINT p, uint flags);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DrawText(IntPtr dc, string text, int count, ref RECT r, uint format);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern int GetObject(IntPtr h, int size, ref LOGFONT lf);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateFontIndirect(ref LOGFONT lf);

    const uint BM_CLICK = 0x00F5, BM_GETCHECK = 0x00F0, WM_GETFONT = 0x0031, WM_NCHITTEST = 0x0084;
    const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    const uint DT_CALCRECT = 0x0400, DT_NOPREFIX = 0x0800, DT_WORDBREAK = 0x0010, DT_SINGLELINE = 0x0020, DT_EDITCONTROL = 0x2000;

    // Coordinates must be physical pixels whatever the monitor scale is.
    static void Aware() { SetThreadDpiAwarenessContext(new IntPtr(-4)); }

    static string ClassOf(IntPtr h) { var b = new StringBuilder(256); GetClassName(h, b, b.Capacity); return b.ToString(); }

    // WM_GETTEXT, unlike GetWindowText, also reads an edit box in another process.
    static string TextOf(IntPtr h) {
        IntPtr length;
        if (SendMessageTimeout(h, 0x000E, IntPtr.Zero, IntPtr.Zero, 2, 5000, out length) == IntPtr.Zero) return "";
        var b = new StringBuilder(length.ToInt32() + 1);
        IntPtr copied;
        SendMessageTimeout(h, 0x000D, new IntPtr(b.Capacity), b, 2, 5000, out copied);
        return b.ToString();
    }

    public static IntPtr[] TopLevel(uint[] pids) {
        Aware();
        var owned = new HashSet<uint>(pids);
        var found = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (owned.Contains(pid) && IsWindowVisible(h) && ClassOf(h) != "TApplication") found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found.ToArray();
    }

    public static string ClassName(IntPtr h) { return ClassOf(h); }
    public static uint Dpi(IntPtr h) { return GetDpiForWindow(h); }

    public static Control[] Children(IntPtr top) { return Descendants(top, false); }

    // Hidden ones too: a page's text stays set after Inno hides the page.
    public static Control[] Descendants(IntPtr top, bool includeHidden) {
        Aware();
        var found = new List<Control>();
        if (top == IntPtr.Zero) return found.ToArray();
        EnumChildWindows(top, delegate(IntPtr h, IntPtr l) {
            if (!includeHidden && !IsWindowVisible(h)) return true;
            var c = new Control();
            c.Handle = h;
            c.Class = ClassOf(h);
            c.Text = TextOf(h);
            GetWindowRect(h, out c.Rect);
            c.Enabled = IsWindowEnabled(h);
            c.Style = GetWindowLong(h, -16);
            found.Add(c);
            return true;
        }, IntPtr.Zero);
        return found.ToArray();
    }

    public static RECT ClientScreenRect(IntPtr top) {
        Aware();
        RECT r;
        GetClientRect(top, out r);
        var p = new POINT();
        ClientToScreen(top, ref p);
        r.Left += p.X; r.Right += p.X; r.Top += p.Y; r.Bottom += p.Y;
        return r;
    }

    // Posted, not sent: a click that opens a modal confirmation only returns
    // once that confirmation is closed.
    public static bool Click(IntPtr h) { return PostMessage(h, BM_CLICK, IntPtr.Zero, IntPtr.Zero); }

    // Inno's TBitmapButton is a custom control, not a BUTTON, so it is pressed
    // the way a mouse presses it: down and up inside its client area.
    public static bool Press(IntPtr h) {
        Aware();
        RECT r;
        GetClientRect(h, out r);
        var at = new IntPtr(((r.Bottom / 2) << 16) | (r.Right / 2));
        return PostMessage(h, 0x0201, new IntPtr(1), at) && PostMessage(h, 0x0202, IntPtr.Zero, at);
    }

    public static int CheckState(IntPtr h) { return SendMessage(h, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt32(); }

    // The control holding keyboard focus in the window's own thread.
    public static IntPtr Focus(IntPtr top) {
        uint pid;
        uint thread = GetWindowThreadProcessId(top, out pid);
        var info = new GUITHREADINFO();
        info.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
        return GetGUIThreadInfo(thread, ref info) ? info.hwndFocus : IntPtr.Zero;
    }

    // A key as the message loop receives it, posted to the focused control, so
    // the wizard's own Tab, Space, Enter and Esc handling runs without taking
    // the foreground from the person at the desk.
    public static bool Key(IntPtr focus, int vk) {
        return PostMessage(focus, WM_KEYDOWN, new IntPtr(vk), new IntPtr(1)) &&
               PostMessage(focus, WM_KEYUP, new IntPtr(vk), new IntPtr(unchecked((int)0xC0000001)));
    }

    public static int HitTest(IntPtr h, int screenX, int screenY) {
        Aware();
        IntPtr result;
        var lParam = new IntPtr((screenY << 16) | (screenX & 0xFFFF));
        SendMessageTimeout(h, WM_NCHITTEST, IntPtr.Zero, lParam, 2, 5000, out result);
        return result.ToInt32();
    }

    public static IntPtr DeepestChildAt(IntPtr top, int screenX, int screenY) {
        Aware();
        IntPtr current = top;
        while (true) {
            var p = new POINT(); p.X = screenX; p.Y = screenY;
            ScreenToClient(current, ref p);
            IntPtr child = ChildWindowFromPointEx(current, p, 0x0001 | 0x0004);
            if (child == IntPtr.Zero || child == current) return current;
            current = child;
        }
    }

    // Size the control's own font needs for the text, measured with that font.
    public static int[] Measure(IntPtr control, string text, int width, bool wrap) {
        Aware();
        IntPtr font = SendMessage(control, WM_GETFONT, IntPtr.Zero, IntPtr.Zero);
        var lf = new LOGFONT();
        if (font == IntPtr.Zero || GetObject(font, Marshal.SizeOf(typeof(LOGFONT)), ref lf) == 0)
            throw new InvalidOperationException("control font is not readable");
        IntPtr ours = CreateFontIndirect(ref lf);
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr previous = SelectObject(dc, ours);
        try {
            var r = new RECT(); r.Right = wrap ? width : 100000;
            uint format = DT_CALCRECT | DT_NOPREFIX | (wrap ? (DT_WORDBREAK | DT_EDITCONTROL) : DT_SINGLELINE);
            DrawText(dc, text, -1, ref r, format);
            return new int[] { r.Right - r.Left, r.Bottom - r.Top };
        } finally {
            SelectObject(dc, previous);
            DeleteDC(dc);
            DeleteObject(ours);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, [Out] int[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);

    public sealed class Shot { public int Width, Height; public int[] Pixels; }

    // The window as it paints itself (PW_RENDERFULLCONTENT), top-down 32bpp
    // BGRA, so the picture is the same even when another window covers it.
    public static Shot Capture(IntPtr top) {
        Aware();
        RECT r;
        GetWindowRect(top, out r);
        int w = Math.Max(1, r.Right - r.Left), h = Math.Max(1, r.Bottom - r.Top);
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr dc = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr previous = SelectObject(dc, bmp);
        try {
            PrintWindow(top, dc, 2);
            SelectObject(dc, previous);
            var info = new BITMAPINFOHEADER();
            info.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            info.biWidth = w; info.biHeight = -h; info.biPlanes = 1; info.biBitCount = 32;
            var shot = new Shot(); shot.Width = w; shot.Height = h; shot.Pixels = new int[w * h];
            GetDIBits(dc, bmp, 0, (uint)h, shot.Pixels, ref info, 0);
            for (int i = 0; i < shot.Pixels.Length; i++) shot.Pixels[i] |= unchecked((int)0xFF000000);
            return shot;
        } finally {
            DeleteObject(bmp);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // Pixels of the light-blue accent (#8DBBFF) along one row of a capture,
    // between two x positions: the real progress fill on the track.
    public static int AccentRun(Shot shot, int row, int fromX, int toX) {
        if (row < 0 || row >= shot.Height) return -1;
        int count = 0;
        for (int x = Math.Max(0, fromX); x < Math.Min(shot.Width, toX); x++) {
            int c = shot.Pixels[row * shot.Width + x];
            int red = (c >> 16) & 0xFF, green = (c >> 8) & 0xFF, blue = c & 0xFF;
            if (Math.Abs(red - 0x8D) < 24 && Math.Abs(green - 0xBB) < 24 && Math.Abs(blue - 0xFF) < 24) count++;
        }
        return count;
    }
}
'@
}

function Get-Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

# Read-only snapshot of everything a fixture must never touch (as Program.ps1).
function Get-RealStateSnapshot {
    $local = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    $rows = [ordered]@{}
    foreach ($path in @(
            (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) 'SIDETALK.lnk'),
            (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) 'SIDETALK.lnk'),
            (Join-Path $local 'Programs\SIDETALK\Sidetalk.exe'),
            (Join-Path $local 'SIDETALK\profile.json'))) {
        $rows[$path] = if (Test-Path -LiteralPath $path -PathType Leaf) { Get-Hash $path } else { 'absent' }
    }
    $data = Join-Path $local 'SIDETALK'
    $rows['data-entries'] = if (Test-Path -LiteralPath $data) { (@(Get-ChildItem -LiteralPath $data -Force | Sort-Object Name | ForEach-Object Name) -join '|') } else { 'absent' }
    $key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{$productionAppId}_is1"
    $rows['registration'] = if (Test-Path -LiteralPath $key) { [string](Get-ItemProperty -LiteralPath $key).DisplayVersion } else { 'absent' }
    $rows | ConvertTo-Json -Compress
}

# KakaoTalk and Sandboxie are listed by id and start time only; never opened or stopped.
function Get-ForeignProcessSnapshot {
    $names = @('KakaoTalk', 'SandMan', 'SbieCtrl', 'SbieSvc', 'Start')
    $rows = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $names -contains $_.ProcessName } | Sort-Object Id | ForEach-Object {
            $started = try { $_.StartTime.ToUniversalTime().ToString('o') } catch { 'unreadable' }
            "$($_.ProcessName):$($_.Id):$started"
        })
    $service = Get-Service -Name 'SbieSvc' -ErrorAction SilentlyContinue
    $rows += 'SbieSvc-service:' + $(if ($service) { [string]$service.Status } else { 'absent' })
    $rows -join ';'
}

function Get-TreeIds([Diagnostics.Process]$Process) {
    $ids = [Collections.Generic.List[uint32]]::new()
    $ids.Add([uint32]$Process.Id)
    foreach ($child in @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($Process.Id)")) { $ids.Add([uint32]$child.ProcessId) }
    return $ids.ToArray()
}

# $null, not IntPtr.Zero, when absent: PowerShell treats IntPtr.Zero as true.
# Inno's styled dialogs are TTaskDialogForm, unstyled (high contrast) ones #32770.
function Get-Window([Diagnostics.Process]$Process, [string]$Class) {
    foreach ($top in @([SidetalkSetupUi]::TopLevel((Get-TreeIds $Process)))) {
        if ([SidetalkSetupUi]::ClassName($top) -in ($Class -split '\|')) { return $top }
    }
    return $null
}

function Find-Control([IntPtr]$Top, [string]$Text, [string]$Class = '') {
    foreach ($control in @([SidetalkSetupUi]::Children($Top))) {
        if ($control.Text.Replace('&', '') -ceq $Text -and (-not $Class -or $control.Class -eq $Class)) { return $control }
    }
    return $null
}

function Wait-For([string]$What, [scriptblock]$Probe, [Diagnostics.Process]$Process, [int]$Seconds = 60) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $value = & $Probe
        if ($value) { return $value }
        if ($Process -and $Process.HasExited) { throw "Setup exited $($Process.ExitCode) while waiting for $What" }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for $What"
}

function Wait-Page([Diagnostics.Process]$Process, [string]$Text, [int]$Seconds = 60) {
    return Wait-For "the page showing '$Text'" {
        $top = Get-Window $Process 'TWizardForm'
        if ($top -and (Find-Control $top $Text)) { $top }
    } $Process $Seconds
}

function Click-Control([IntPtr]$Top, [string]$Text, [string]$Class = '') {
    $control = Find-Control $Top $Text $Class
    if (-not $control) { throw "No visible control '$Text' to click" }
    $done = if ($control.Class -eq 'TBitmapButton') { [SidetalkSetupUi]::Press($control.Handle) } else { [SidetalkSetupUi]::Click($control.Handle) }
    if (-not $done) { throw "Clicking '$Text' did not complete" }
}

function Wait-Exit([Diagnostics.Process]$Process, [int]$Expected, [string]$What) {
    if (-not $Process.WaitForExit(90000)) { throw "$What did not exit" }
    $Process.WaitForExit()
    if ($Process.ExitCode -ne $Expected) { throw "$What exited $($Process.ExitCode), expected $Expected" }
}

function Save-Capture($Shot, [string]$Path) {
    $bitmap = [Drawing.Bitmap]::new($Shot.Width, $Shot.Height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $data = $bitmap.LockBits([Drawing.Rectangle]::new(0, 0, $Shot.Width, $Shot.Height), [Drawing.Imaging.ImageLockMode]::WriteOnly, $bitmap.PixelFormat)
        try { [Runtime.InteropServices.Marshal]::Copy($Shot.Pixels, 0, $data.Scan0, $Shot.Pixels.Length) } finally { $bitmap.UnlockBits($data) }
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
}

function Save-Shot([IntPtr]$Top, [string]$Name) {
    Save-Capture ([SidetalkSetupUi]::Capture($Top)) (Join-Path $evidenceDir "$Name.png")
    $script:report.screenshots += "$Name.png"
}

function Set-Check([IntPtr]$Top, [string]$Text, [int]$Want, [Diagnostics.Process]$Process) {
    $box = Find-Control $Top $Text 'TNewCheckBox'
    if (-not $box) { throw "No visible checkbox '$Text'" }
    if ([SidetalkSetupUi]::CheckState($box.Handle) -ne $Want) {
        [void][SidetalkSetupUi]::Click($box.Handle)
        Wait-For "checkbox '$Text' = $Want" { [SidetalkSetupUi]::CheckState($box.Handle) -eq $Want } $Process 10 | Out-Null
    }
}

# Layout invariants of the visible page, in the spirit of CycleArc's
# SetupLayout.Problems and text-fit self-test: every control inside the client
# area, no two controls overlapping, and no text larger than its box when
# measured with the control's own font.
function Assert-Layout([IntPtr]$Top, [string]$What) {
    $client = [SidetalkSetupUi]::ClientScreenRect($Top)
    $scale = ($client.Right - $client.Left) / 600.0
    $problems = [Collections.Generic.List[string]]::new()
    $leaves = @([SidetalkSetupUi]::Children($Top) | Where-Object { $_.Class -in @('TNewStaticText', 'TNewButton', 'TNewCheckBox', 'TBitmapButton', 'TNewMemo') })
    if ($leaves.Count -lt 4) { $problems.Add("only $($leaves.Count) controls are visible") }
    foreach ($c in $leaves) {
        $r = $c.Rect
        $label = "$($c.Class) '$($c.Text)'"
        if ($r.Left -lt $client.Left -or $r.Top -lt $client.Top -or $r.Right -gt $client.Right -or $r.Bottom -gt $client.Bottom) {
            $problems.Add("$label at ($($r.Left),$($r.Top))-($($r.Right),$($r.Bottom)) falls outside the client area")
        }
        $width = $r.Right - $r.Left
        $height = $r.Bottom - $r.Top
        if ([string]::IsNullOrEmpty($c.Text) -or $c.Class -eq 'TBitmapButton') { continue }
        switch ($c.Class) {
            'TNewStaticText' {
                $wraps = ($c.Style -band 0x1F) -ne 0x0C
                $line = [SidetalkSetupUi]::Measure($c.Handle, $c.Text, $width, $false)
                if ($line[0] -gt $width) {
                    if (-not $wraps) { $problems.Add("$label needs $($line[0])px but has $width") }
                    else {
                        $wrapped = [SidetalkSetupUi]::Measure($c.Handle, $c.Text, $width, $true)
                        if ($wrapped[1] -gt $height) { $problems.Add("$label needs $($wrapped[1])px of height but has $height") }
                    }
                } elseif ($line[1] -gt $height) { $problems.Add("$label needs $($line[1])px of height but has $height") }
            }
            'TNewButton' {
                $need = [SidetalkSetupUi]::Measure($c.Handle, $c.Text, $width, $false)
                if ($need[0] + [int](16 * $scale) -gt $width -or $need[1] -gt $height) { $problems.Add("$label needs $($need[0])x$($need[1]) inside $width x $height") }
            }
            'TNewCheckBox' {
                $need = [SidetalkSetupUi]::Measure($c.Handle, $c.Text, $width, $false)
                if ($need[0] + [int](24 * $scale) -gt $width -or $need[1] -gt $height) { $problems.Add("$label needs $($need[0])x$($need[1]) beside its box in $width x $height") }
            }
            'TNewMemo' {
                # WS_VSCROLL: the reason scrolls instead of being cut off.
                $scrolls = ($c.Style -band 0x00200000) -ne 0
                $need = [SidetalkSetupUi]::Measure($c.Handle, $c.Text, $width - [int](8 * $scale), $true)
                if ($need[1] -gt $height -and -not $scrolls) { $problems.Add("$label needs $($need[1])px of height but has $height and no scroll bar; its last lines would be hidden") }
            }
        }
    }
    for ($i = 0; $i -lt $leaves.Count; $i++) {
        for ($j = $i + 1; $j -lt $leaves.Count; $j++) {
            $a = $leaves[$i].Rect; $b = $leaves[$j].Rect
            if ($a.Left -lt $b.Right -and $b.Left -lt $a.Right -and $a.Top -lt $b.Bottom -and $b.Top -lt $a.Bottom) {
                $problems.Add("$($leaves[$i].Class) '$($leaves[$i].Text)' overlaps $($leaves[$j].Class) '$($leaves[$j].Text)'")
            }
        }
    }
    if ($problems.Count) { throw "$What layout problems:`n  " + ($problems -join "`n  ") }
    return @{ width = $client.Right - $client.Left; height = $client.Bottom - $client.Top; scale = [Math]::Round($scale, 3); controls = $leaves.Count }
}

function New-Payload([string]$Destination, [string]$Marker) {
    # Placeholders only: the installer copies them, nothing ever starts them.
    foreach ($relative in @('Sidetalk.exe', 'Sidetalk.dll', 'Sidetalk.Core.dll', 'README.md', 'tools\IsolationProbe\Sidetalk.IsolationProbe.exe', 'docs\fixture-build.txt')) {
        $path = Join-Path $Destination $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
        [IO.File]::WriteAllText($path, "SIDETALK setup UI fixture $Marker`: $relative`r`n", [Text.UTF8Encoding]::new($false))
    }
    # Enough small files that the copy lasts long enough to watch progress. {app}\docs
    # is replaced on every install, so nothing stale piles up.
    $bulk = Join-Path $Destination 'docs\bulk'
    New-Item -ItemType Directory -Force -Path $bulk | Out-Null
    $random = [Random]::new(20261025)
    $bytes = [byte[]]::new(2048)
    for ($i = 0; $i -lt 3000; $i++) {
        $random.NextBytes($bytes)
        [IO.File]::WriteAllBytes((Join-Path $bulk "f$i.bin"), $bytes)
    }
}

function Compile-Setup([string]$Version, [string]$Payload, [string]$Name, [int]$DialogFontSize = 0) {
    $output = Join-Path $fixtureRoot "out-$Name"
    $source = $scriptPath
    if ($DialogFontSize -gt 0) {
        # A wrapper that only adds the font size; SourceDir keeps every relative
        # path of the real script pointing into installer\.
        $source = Join-Path $fixtureRoot "scale-$DialogFontSize.iss"
        $wrapper = "#include `"$scriptPath`"`r`n[Setup]`r`nSourceDir=$(Split-Path -Parent $scriptPath)`r`n[LangOptions]`r`nDialogFontSize=$DialogFontSize`r`n"
        [IO.File]::WriteAllText($source, $wrapper, [Text.UTF8Encoding]::new($false))
    }
    $arguments = @('/Qp', "/O$output", '/FSIDETALK-Setup', "/DAppVersion=$Version", "/DVersionInfoVersion=$Version.0", "/DSourceDir=$Payload",
        "/DAppId=$appId", "/DFixtureNamespaceSHA256=$namespace", "/DDefaultInstallDir=$installDir", "/DStartMenuDir=$programs",
        "/DDesktopDir=$desktop", '/DIntegrationCleanupExe=fixture-cleanup-absent.exe', $source)
    $log = @(& $iscc @arguments 2>&1 | ForEach-Object { [string]$_ })
    if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed compiling $Name ($LASTEXITCODE): $($log -join "`n")" }
    return (Join-Path $output 'SIDETALK-Setup.exe')
}

function Start-Setup([string]$Setup, [string[]]$Arguments, [string]$LogName) {
    $log = Join-Path $logDir $LogName
    $process = Start-Process -FilePath $Setup -ArgumentList (@($Arguments) + @("/LOG=`"$log`"")) -PassThru
    $script:owned += $process
    $process | Add-Member -NotePropertyName LogPath -NotePropertyValue $log
    return $process
}

function Get-InstalledVersion {
    if (-not (Test-Path -LiteralPath $uninstallKey)) { return $null }
    return [string](Get-ItemProperty -LiteralPath $uninstallKey).DisplayVersion
}

function Assert-NotInstalled([string]$What) {
    if (Test-Path -LiteralPath (Join-Path $installDir 'Sidetalk.exe')) { throw "$What installed files" }
    if (Test-Path -LiteralPath $uninstallKey) { throw "$What registered an installation" }
}

function Assert-Payload([string]$Payload, [string]$What) {
    foreach ($relative in @('Sidetalk.exe', 'Sidetalk.dll', 'Sidetalk.Core.dll', 'tools\IsolationProbe\Sidetalk.IsolationProbe.exe', 'docs\fixture-build.txt')) {
        $installed = Join-Path $installDir $relative
        if (-not (Test-Path -LiteralPath $installed -PathType Leaf)) { throw "$What did not install $relative" }
        if ((Get-Hash $installed) -ne (Get-Hash (Join-Path $Payload $relative))) { throw "$What installed a different $relative" }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $programs 'SIDETALK.lnk') -PathType Leaf)) { throw "$What did not create the Start Menu shortcut" }
}

function Assert-NoRunEntry([string]$Log, [string]$What) {
    if ([IO.File]::ReadAllText($Log) -match '-- Run entry --') { throw "$What started the installed program" }
}

function Assert-DataSentinel([string]$What) {
    if ((Get-Hash $sentinel) -ne $sentinelHash) { throw "$What changed the user-data sentinel" }
}

function Invoke-Uninstall([string]$What) {
    $uninstaller = Join-Path $installDir 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw "$What has no uninstaller" }
    $process = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "$What uninstall exited $($process.ExitCode)" }
    Wait-For "$What uninstall to finish" { -not (Test-Path -LiteralPath $uninstaller) -and -not (Test-Path -LiteralPath $uninstallKey) } $null 60 | Out-Null
}

# Inno passes through its preparing page on every installation. A themed page that
# once called a successful installation a failure keeps that heading text, so it is
# checked after the fact rather than by sampling the screen.
function Assert-NeverFailed([IntPtr]$Top, [string]$Tag) {
    foreach ($c in @([SidetalkSetupUi]::Descendants($Top, $true))) {
        if ($c.Text -ceq $T.Failed -or $c.Text -ceq $T.FailedUpdate) { throw "$Tag succeeded but its window showed the failure heading '$($c.Text)'" }
    }
}

# Confirm -> progress -> done, through the real buttons. Returns the largest partial
# fill seen, so the caller can tell the bar moved before completion.
function Invoke-InteractiveInstall([string]$Setup, [string]$Tag, [string]$Heading, [string]$Button, [int]$Desktop, [int]$DesktopBefore, [bool]$WatchProgress) {
    $process = Start-Setup $Setup @() "$Tag.log"
    $top = Wait-Page $process $Heading
    Start-Sleep -Milliseconds 300
    $box = Find-Control $top $T.Desktop 'TNewCheckBox'
    if (-not $box) { throw "$Tag confirmation page has no desktop shortcut option" }
    if ($DesktopBefore -ge 0 -and [SidetalkSetupUi]::CheckState($box.Handle) -ne $DesktopBefore) { throw "$Tag desktop option did not start as $DesktopBefore" }
    Set-Check $top $T.Desktop $Desktop $process
    $confirm = Assert-Layout $top "$Tag confirmation page"
    Save-Shot $top "$Tag-1-confirm"
    Click-Control $top $Button 'TNewButton'
    $partial = 0
    if ($WatchProgress) {
        $progressHeading = if ($Button -eq $T.Update) { $T.Updating } else { $T.Installing }
        $top = Wait-Page $process $progressHeading 30
        $client = [SidetalkSetupUi]::ClientScreenRect($top)
        $scale = ($client.Right - $client.Left) / 600.0
        $row = [int][Math]::Floor(214 * $scale)
        $from = [int](80 * $scale); $to = [int](520 * $scale); $full = $to - $from
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        $shot = Join-Path $evidenceDir "$Tag-2-progress.png"
        while ([DateTime]::UtcNow -lt $deadline -and -not (Find-Control $top $T.Finish)) {
            if (Find-Control $top $T.Failed) { throw "$Tag showed the failure page while installing" }
            $capture = [SidetalkSetupUi]::Capture($top)
            $run = [SidetalkSetupUi]::AccentRun($capture, $row, $from, $to)
            if ($run -gt 0 -and $run -lt ($full - 4) -and $run -gt $partial) {
                if ($partial -eq 0) {
                    Save-Capture $capture $shot
                    $script:report.screenshots += "$Tag-2-progress.png"
                }
                $partial = $run
            }
            Start-Sleep -Milliseconds 40
        }
    }
    $top = Wait-Page $process $T.Finish 120
    Assert-NeverFailed $top $Tag
    Start-Sleep -Milliseconds 300
    $done = Assert-Layout $top "$Tag completion page"
    Save-Shot $top "$Tag-3-done"
    $run = Find-Control $top $T.Run 'TNewCheckBox'
    if (-not $run) { throw "$Tag completion page has no Run checkbox" }
    if ([SidetalkSetupUi]::CheckState($run.Handle) -ne 1) { throw "$Tag Run checkbox was not checked by default" }
    Set-Check $top $T.Run 0 $process
    return @{ Process = $process; Top = $top; Partial = $partial; Confirm = $confirm; Done = $done }
}

# A failure the installer's own gate produces. Holding Sidetalk.exe open the way a
# running image is held is what InstalledFileIsInUse detects.
function Invoke-InteractiveFailure([string]$Setup, [string]$Tag, [string]$Heading, [string]$FailedHeading, [string]$Reason, [bool]$HoldExe) {
    $exe = Join-Path $installDir 'Sidetalk.exe'
    $before = Get-Hash $exe
    $versionBefore = Get-InstalledVersion
    $held = if ($HoldExe) { [IO.File]::Open($exe, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read) } else { $null }
    try {
        $process = Start-Setup $Setup @() "$Tag.log"
        $top = Wait-Page $process $Heading
        Click-Control $top $(if ($Heading -eq $T.HeadUpdate) { $T.Update } else { $T.Install }) 'TNewButton'
        $top = Wait-Page $process $FailedHeading 30
        Start-Sleep -Milliseconds 300
        $layout = Assert-Layout $top "$Tag failure page"
        Save-Shot $top "$Tag-4-failed"
        $detail = @([SidetalkSetupUi]::Children($top) | Where-Object { $_.Class -eq 'TNewMemo' })
        if ($detail.Count -ne 1) { throw "$Tag failure page has no detail box" }
        if (-not $detail[0].Text.Contains($Reason)) { throw "$Tag failure detail does not carry the installer's own reason: $($detail[0].Text)" }
        $logLine = [regex]::Match($detail[0].Text, [regex]::Escape($T.LogLabel) + '(.+)$', 'Multiline')
        if (-not $logLine.Success -or [IO.Path]::GetFullPath($logLine.Groups[1].Value.Trim()) -ne [IO.Path]::GetFullPath($process.LogPath)) {
            throw "$Tag failure detail does not name the setup log"
        }
        if ((Find-Control $top $T.Install) -or (Find-Control $top $T.Update)) { throw "$Tag failure page still offers to install" }
        Click-Control $top $T.Close 'TNewButton'
        Wait-Exit $process 7 "$Tag failure"
        if ([IO.File]::ReadAllText($process.LogPath) -notmatch 'SIDETALK install blocked') { throw "$Tag failure reason was not logged" }
        Assert-NoRunEntry $process.LogPath "$Tag failure"
    } finally { if ($held) { $held.Dispose() } }
    if ((Get-Hash $exe) -ne $before) { throw "$Tag failure changed installed files" }
    if ((Get-InstalledVersion) -ne $versionBefore) { throw "$Tag failure changed the registration" }
    return $layout
}

$owned = @()
$savedTemp = @{ TEMP = $env:TEMP; TMP = $env:TMP }
$realBefore = $null
$foreignBefore = $null
$appId = $null
$uninstallKey = $null
try {
    $realBefore = Get-RealStateSnapshot
    $foreignBefore = Get-ForeignProcessSnapshot
    $prepared = @(& (Join-Path $repo 'scripts\prepare-inno.ps1'))
    if ($LASTEXITCODE -ne 0) { throw 'The pinned Inno Setup compiler could not be prepared.' }
    $iscc = [string]$prepared[-1]
    if (Test-Path -LiteralPath $fixtureRoot) {
        if (Get-ChildItem -LiteralPath $fixtureRoot -Recurse -Force -Attributes ReparsePoint) { throw "A previous fixture contains a link: $fixtureRoot" }
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $evidenceDir) { Remove-Item -LiteralPath $evidenceDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $fixtureRoot, $evidenceDir | Out-Null

    $appId = 'SIDETALKInstallerTest-' + [Guid]::NewGuid().ToString('N')
    $namespace = (Get-FileHash -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($appId))) -Algorithm SHA256).Hash.ToLowerInvariant()
    $installDir = Join-Path $fixtureRoot 'LocalAppData\Programs\SIDETALK'
    $programs = Join-Path $fixtureRoot 'StartMenu\Programs'
    $desktop = Join-Path $fixtureRoot 'Desktop'
    $logDir = Join-Path $fixtureRoot 'logs'
    $tempDir = Join-Path $fixtureRoot 'Temp'
    $uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{$appId}_is1"
    New-Item -ItemType Directory -Force -Path $programs, $desktop, $logDir, $tempDir | Out-Null
    $sentinel = Join-Path $fixtureRoot 'LocalAppData\SIDETALK\profile.json'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $sentinel) | Out-Null
    Set-Content -LiteralPath $sentinel -Value '{"synthetic":"profile"}'
    $sentinelHash = Get-Hash $sentinel
    # Setup's {tmp}, its default log and the art it extracts stay inside the fixture.
    $env:TEMP = $tempDir
    $env:TMP = $tempDir

    $payload1 = Join-Path $fixtureRoot 'payload-1'
    $payload2 = Join-Path $fixtureRoot 'payload-2'
    New-Payload $payload1 'build 1'
    New-Payload $payload2 'build 2'
    $setup1 = Compile-Setup '0.2.0' $payload1 'v1'
    $setup2 = Compile-Setup '0.2.1' $payload2 'v2'
    $setupDown = Compile-Setup '0.1.9' $payload1 'down'

    # 1. Opening the installer installs nothing; the close button asks, then exits 2.
    $process = Start-Setup $setup1 @() 'cancel.log'
    $top = Wait-Page $process $T.HeadInstall
    Start-Sleep -Seconds 3
    Assert-NotInstalled 'Opening the confirmation page'
    Add-Check 'opening the installer shows the confirmation page and installs nothing'
    $client = [SidetalkSetupUi]::ClientScreenRect($top)
    $scale = ($client.Right - $client.Left) / 600.0
    $report.monitorDpi = [int][SidetalkSetupUi]::Dpi($top)
    if ([Math]::Abs(($client.Bottom - $client.Top) / $scale - 340) -gt 2) { throw 'The window is not the 600x340 design at its own scale' }
    $emptyX = $client.Left + [int](300 * $scale); $emptyY = $client.Top + [int](262 * $scale)
    $under = [SidetalkSetupUi]::DeepestChildAt($top, $emptyX, $emptyY)
    if ([SidetalkSetupUi]::HitTest($under, $emptyX, $emptyY) -ne -1) { throw 'Empty page space does not pass hits through to the window' }
    if ([SidetalkSetupUi]::HitTest($top, $emptyX, $emptyY) -ne 2) { throw 'Empty window space is not a drag area' }
    $installButton = Find-Control $top $T.Install 'TNewButton'
    $buttonX = [int](($installButton.Rect.Left + $installButton.Rect.Right) / 2); $buttonY = [int](($installButton.Rect.Top + $installButton.Rect.Bottom) / 2)
    if ([SidetalkSetupUi]::HitTest($installButton.Handle, $buttonX, $buttonY) -ne 1) { throw 'The install button does not keep its own clicks' }
    Add-Check 'frameless 600x340 window: empty space drags the window while buttons keep their clicks'
    Click-Control $top $T.Minimize 'TBitmapButton'
    Wait-For 'the minimised window' { [SidetalkSetupUi]::IsIconic($top) } $process 10 | Out-Null
    [void][SidetalkSetupUi]::ShowWindow($top, 9)
    Wait-For 'the restored window' { -not [SidetalkSetupUi]::IsIconic($top) } $process 10 | Out-Null
    Add-Check 'minimise button minimises and the window restores'
    Click-Control $top $T.Close 'TBitmapButton'
    $dialog = Wait-For 'the cancel confirmation' { Get-Window $process 'TTaskDialogForm|#32770' } $process 15
    Save-Shot $dialog 'cancel-confirmation'
    Click-Control $dialog $T.Yes
    Wait-Exit $process 2 'Cancelled setup'
    Assert-NotInstalled 'Cancelling'
    Add-Check 'the close button asks for the cancel confirmation and exits 2 without installing'

    # 2. A new installation through the real buttons, desktop shortcut chosen.
    $install = Invoke-InteractiveInstall $setup1 'install' $T.HeadInstall $T.Install 1 0 $true
    if ($install.Partial -le 0) { throw 'The progress bar never showed a partial fill while files were copied' }
    Click-Control $install.Top $T.Finish 'TNewButton'
    Wait-Exit $install.Process 0 'Interactive install'
    Assert-Payload $payload1 'Interactive install'
    if ((Get-InstalledVersion) -ne '0.2.0') { throw 'Interactive install registered the wrong version' }
    if (-not (Test-Path -LiteralPath (Join-Path $desktop 'SIDETALK.lnk'))) { throw 'The checked desktop option did not create the desktop shortcut' }
    Assert-NoRunEntry $install.Process.LogPath 'Interactive install'
    Assert-DataSentinel 'Interactive install'
    $report.scales += @{ dialogFontSize = 'default'; confirm = $install.Confirm; done = $install.Done }
    Add-Check "install button installs; the desktop option (off by default) created the shortcut; the bar filled from the engine's progress ($($install.Partial)px partial) and Finish exits 0"
    Add-Check 'unchecked Run on the completion page starts nothing'

    # 3. An update keeps the previous desktop choice and its own wording.
    $update = Invoke-InteractiveInstall $setup2 'update' $T.HeadUpdate $T.Update 1 1 $false
    if (-not (Find-Control $update.Top $T.DoneUpdate)) { throw 'Update completion page does not say the update finished' }
    Click-Control $update.Top $T.Close 'TBitmapButton'
    Wait-Exit $update.Process 0 'Interactive update closed from the title row'
    if ((Get-InstalledVersion) -ne '0.2.1') { throw 'Interactive update did not register the new version' }
    Assert-Payload $payload2 'Interactive update'
    if (-not (Test-Path -LiteralPath (Join-Path $desktop 'SIDETALK.lnk'))) { throw 'Update lost the desktop shortcut' }
    Assert-NoRunEntry $update.Process.LogPath 'Interactive update'
    Assert-DataSentinel 'Interactive update'
    Add-Check 'existing installation is offered as an update with the previous desktop choice; the close button finishes without running the app'

    # 4. Gate failures stay inside the window with the real reason and log.
    $failure = Invoke-InteractiveFailure $setup2 'running' $T.HeadReinstall $T.Failed $T.Running $true
    $report.scales += @{ dialogFontSize = 'default'; failed = $failure }
    Add-Check 'a running SIDETALK (Sidetalk.exe held open) is refused: the window shows the installer reason and log path, Close exits 7 and files are unchanged'
    $null = Invoke-InteractiveFailure $setupDown 'downgrade' $T.HeadInstall $T.Failed $T.Lower $false
    Add-Check 'a downgrade is refused with its own reason, exits 7 and keeps the installed version'

    # 5. Keyboard only: Tab reaches the desktop option, Space toggles it, Enter
    #    installs through the default button. Esc is checked on the next run.
    $keys = Start-Setup $setup2 @() 'keyboard.log'
    $top = Wait-Page $keys $T.HeadReinstall
    Start-Sleep -Milliseconds 300
    $box = Find-Control $top $T.Desktop 'TNewCheckBox'
    $initial = [SidetalkSetupUi]::CheckState($box.Handle)
    $reached = $false
    for ($i = 0; $i -lt 6 -and -not $reached; $i++) {
        if ([SidetalkSetupUi]::Focus($top) -eq $box.Handle) { $reached = $true; break }
        $focus = [SidetalkSetupUi]::Focus($top)
        if ($focus -eq [IntPtr]::Zero) { $focus = $top }
        [void][SidetalkSetupUi]::Key($focus, 0x09)
        Start-Sleep -Milliseconds 200
    }
    if (-not $reached) { throw 'Tab never reached the desktop shortcut option' }
    [void][SidetalkSetupUi]::Key($box.Handle, 0x20)
    Wait-For 'Space to toggle the desktop option' { [SidetalkSetupUi]::CheckState($box.Handle) -ne $initial } $keys 10 | Out-Null
    [void][SidetalkSetupUi]::Key($box.Handle, 0x20)
    Wait-For 'Space to restore the desktop option' { [SidetalkSetupUi]::CheckState($box.Handle) -eq $initial } $keys 10 | Out-Null
    [void][SidetalkSetupUi]::Key($box.Handle, 0x0D)
    $top = Wait-Page $keys $T.Finish 120
    Assert-NeverFailed $top 'keyboard'
    $run = Find-Control $top $T.Run 'TNewCheckBox'
    [void][SidetalkSetupUi]::Key($run.Handle, 0x20)
    Wait-For 'Space to uncheck Run' { [SidetalkSetupUi]::CheckState($run.Handle) -eq 0 } $keys 10 | Out-Null
    [void][SidetalkSetupUi]::Key($run.Handle, 0x0D)
    Wait-Exit $keys 0 'Keyboard reinstall'
    Assert-NoRunEntry $keys.LogPath 'Keyboard reinstall'
    Add-Check 'keyboard only: Tab reaches the desktop option, Space toggles it, Enter installs and finishes, Space unchecks Run'

    # 6. The unattended paths: /SILENT shows only the themed progress window.
    $silent = Start-Setup $setup2 @('/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/SIDETALKNOLAUNCH') 'silent.log'
    $silentTop = Wait-For 'the silent progress window' { Get-Window $silent 'TWizardForm' } $silent 30
    Start-Sleep -Milliseconds 200
    if (-not $silent.HasExited) {
        Save-Shot $silentTop 'silent-progress'
        $question = Get-Window $silent 'TTaskDialogForm|#32770'
        if ($question) { throw 'Silent install showed a dialog' }
    }
    Wait-Exit $silent 0 'Silent reinstall'
    Assert-NoRunEntry $silent.LogPath 'Silent reinstall'
    Add-Check '/SILENT /SIDETALKNOLAUNCH shows the themed progress window only, asks nothing and exits 0'

    # 7. High contrast turns Inno's custom style off; /NOSTYLE does the same. Esc cancels.
    $plain = Start-Setup $setup2 @('/NOSTYLE') 'nostyle.log'
    $plainTop = Wait-Page $plain $T.HeadReinstall
    Start-Sleep -Milliseconds 300
    $null = Assert-Layout $plainTop 'Unstyled confirmation page'
    if (Find-Control $plainTop $T.Minimize) { throw 'Unstyled window kept the custom caption buttons' }
    if (-not (Find-Control $plainTop $T.Desktop 'TNewCheckBox')) { throw 'Unstyled window lost the desktop option' }
    Save-Shot $plainTop 'nostyle-confirm'
    $focus = [SidetalkSetupUi]::Focus($plainTop)
    if ($focus -eq [IntPtr]::Zero) { $focus = $plainTop }
    [void][SidetalkSetupUi]::Key($focus, 0x1B)
    $dialog = Wait-For 'the cancel confirmation after Esc' { Get-Window $plain 'TTaskDialogForm|#32770' } $plain 15
    Click-Control $dialog $T.Yes
    Wait-Exit $plain 2 'Unstyled cancel'
    Add-Check 'unstyled (high contrast) fallback keeps the layout and desktop option inside a system frame; Esc asks and exits 2'

    Invoke-Uninstall 'Fixture'
    if ((Test-Path -LiteralPath (Join-Path $desktop 'SIDETALK.lnk')) -or (Test-Path -LiteralPath (Join-Path $programs 'SIDETALK.lnk'))) { throw 'Uninstall left a shortcut' }
    Assert-DataSentinel 'Uninstall'
    Add-Check 'fixture uninstall removes files, shortcuts and registration and keeps the user-data sentinel'

    # 8. The same pages at larger scales, with the desktop option left off.
    foreach ($size in $DialogFontSizes) {
        $scaled = Compile-Setup '0.2.1' $payload2 "scale-$size" $size
        $run = Invoke-InteractiveInstall $scaled "scale-$size" $T.HeadInstall $T.Install 0 0 $false
        Click-Control $run.Top $T.Finish 'TNewButton'
        Wait-Exit $run.Process 0 "Install at DialogFontSize $size"
        if (Test-Path -LiteralPath (Join-Path $desktop 'SIDETALK.lnk')) { throw "Unchecked desktop option created a shortcut at DialogFontSize $size" }
        $failed = Invoke-InteractiveFailure $scaled "scale-$size-running" $T.HeadReinstall $T.Failed $T.Running $true
        Invoke-Uninstall "DialogFontSize $size"
        $report.scales += @{ dialogFontSize = $size; confirm = $run.Confirm; done = $run.Done; failed = $failed }
        Add-Check ("DialogFontSize $size (scale {0}, {1}x{2}): confirmation, completion and failure pages fit; unchecked desktop option creates no shortcut" -f $run.Confirm.scale, $run.Confirm.width, $run.Confirm.height)
    }
    Assert-DataSentinel 'All scales'

    $realAfter = Get-RealStateSnapshot
    if ($realAfter -ne $realBefore) { throw "The real SIDETALK installation, shortcuts or user data changed:`n$realBefore`n$realAfter" }
    $foreignAfter = Get-ForeignProcessSnapshot
    if ($foreignAfter -ne $foreignBefore) { throw "KakaoTalk or Sandboxie processes changed during the run:`n$foreignBefore`n$foreignAfter" }
    $report.foreignProcesses = $foreignBefore
    Add-Check 'the real installation, shortcuts and %LOCALAPPDATA%\SIDETALK, and every KakaoTalk/Sandboxie process, are unchanged'
    $report.passed = $true
} catch {
    $report.error = $_.Exception.Message
    throw
} finally {
    foreach ($process in $owned) {
        try {
            if (-not $process.HasExited) {
                foreach ($id in @(Get-TreeIds $process)) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
            }
        } catch { }
    }
    $env:TEMP = $savedTemp.TEMP
    $env:TMP = $savedTemp.TMP
    $cleanup = 'fixture removed'
    try {
        if ($uninstallKey -and (Test-Path -LiteralPath $uninstallKey)) {
            $leftover = Join-Path $installDir 'unins000.exe'
            if (Test-Path -LiteralPath $leftover -PathType Leaf) { Invoke-Uninstall 'Recovery' }
            if (Test-Path -LiteralPath $uninstallKey) { Remove-Item -LiteralPath $uninstallKey -Recurse -Force }
        }
        if (Test-Path -LiteralPath $fixtureRoot) {
            if (Get-ChildItem -LiteralPath $fixtureRoot -Recurse -Force -Attributes ReparsePoint) { throw 'the fixture contains a link' }
            Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
        }
    } catch {
        $cleanup = "fixture preserved at $fixtureRoot for diagnosis: $($_.Exception.Message)"
        Write-Warning $cleanup
    }
    $report.cleanup = $cleanup
    $report.seconds = [Math]::Round($timer.Elapsed.TotalSeconds, 1)
    $report.finishedAt = [DateTime]::UtcNow.ToString('o')
    New-Item -ItemType Directory -Force -Path $evidenceDir | Out-Null
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Host "Setup UI report: $reportPath"
}
Write-Host ("All setup UI checks passed ({0:F2}s)." -f $timer.Elapsed.TotalSeconds)
