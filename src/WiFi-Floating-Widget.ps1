# Wi-Fi Network Analyzer Pro V5 - corner widget
# Windows PowerShell 5.1, built-in Windows Forms only. No admin rights needed to monitor.
#   Ctrl+Alt+W            show / hide from anywhere
#   Click the tray icon   show / hide
#   Drag the widget       it snaps to the nearest screen corner
param([switch]$AtLogin)   # set by the Startup shortcut
$ErrorActionPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$SafetyScript = Join-Path $Root 'NetSafety.ps1'
. $SafetyScript

# ---------- single instance ----------
$created = $false
$script:Mutex = New-Object System.Threading.Mutex($true, 'Local\WiFiAnalyzerProV5Widget', [ref]$created)
if (-not $created) {
    [System.Windows.Forms.MessageBox]::Show('The Wi-Fi widget is already running. Press Ctrl+Alt+W to show it.', 'Wi-Fi Analyzer Pro') | Out-Null
    return
}
if ($AtLogin) { Start-Sleep -Seconds 45 }   # just signed in: give Wi-Fi time to connect before the first checks

# ---------- native helper: borderless double-buffered form, rounded corners, global hotkey ----------
if (-not ('WifiWidget.LayeredForm' -as [type])) {
Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing -TypeDefinition @'
using System;
using System.Windows.Forms;
using System.Runtime.InteropServices;
namespace WifiWidget {
public class SmoothForm : Form {
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    public event EventHandler HotKeyPressed;
    bool shadow = true;
    public SmoothForm(bool withShadow) : this() { shadow = withShadow; }   // false = no drop shadow (see-through popups)
    public SmoothForm() {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
    }
    protected override CreateParams CreateParams {
        get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80; if (shadow) cp.ClassStyle |= 0x20000; return cp; }
    }
    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
    }
    protected override bool ShowWithoutActivation { get { return true; } }   // never steal focus from what you are typing
    public bool RegisterToggleKey(uint mod, uint vk) { return RegisterHotKey(Handle, 9001, mod, vk); }
    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x0312 && m.WParam.ToInt32() == 9001 && HotKeyPressed != null) HotKeyPressed(this, EventArgs.Empty);
        base.WndProc(ref m);
    }
    protected override void OnHandleDestroyed(EventArgs e) { UnregisterHotKey(Handle, 9001); base.OnHandleDestroyed(e); }
}
// Alert popup: real per-pixel transparency (UpdateLayeredWindow), so soft edges and rounded corners blend with the desktop
// and Windows draws no frame or border around it. Everything is painted into a 32-bit bitmap and pushed with SetBitmap.
public class LayeredForm : Form {
    [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; public PT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] struct SZ { public int W, H; public SZ(int w, int h) { W = w; H = h; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref PT pos, ref SZ size, IntPtr src, ref PT srcPos, int key, ref BLEND b, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    public LayeredForm() {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
    }
    protected override CreateParams CreateParams {
        get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x80; return cp; }   // WS_EX_LAYERED | WS_EX_TOOLWINDOW
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    public void SetBitmap(System.Drawing.Bitmap bmp) {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = IntPtr.Zero, old = IntPtr.Zero;
        try {
            hb = bmp.GetHbitmap(System.Drawing.Color.FromArgb(0)); old = SelectObject(mem, hb);
            PT pos = new PT(Left, Top), src = new PT(0, 0); SZ size = new SZ(bmp.Width, bmp.Height);
            BLEND b = new BLEND(); b.Op = 0; b.Flags = 0; b.Alpha = 255; b.Format = 1;   // AC_SRC_ALPHA
            UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref b, 2);   // ULW_ALPHA
        } finally {
            ReleaseDC(IntPtr.Zero, screen);
            if (hb != IntPtr.Zero) { SelectObject(mem, old); DeleteObject(hb); }
            DeleteDC(mem);
        }
    }
}
}
'@
}

# ---------- data files ----------
# When started from the EXE, the launcher sets AMBAAN_DATA to a writable folder (portable: next to the EXE).
$DataRoot = if ($env:AMBAAN_DATA) { $env:AMBAAN_DATA } else { $Root }
$DataDir = Join-Path $DataRoot 'MonitorData'
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
$KnownFile    = Join-Path $DataDir 'KnownDevices.json'
$EventCsv     = Join-Path $DataDir 'Alerts.csv'
$DeviceCsv    = Join-Path $DataDir 'DeviceHistory.csv'
$SettingsFile = Join-Path $DataDir 'WidgetSettings.json'
$Unnamed = 'Unknown device'

$script:Known = @{}
if (Test-Path $KnownFile) {
    try { (Get-Content $KnownFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $script:Known[$_.Name.ToUpper()] = [string]$_.Value } } catch {}
    $script:Known.Remove('00-00-00-00-00-00')   # junk entry from an unresolved ARP lookup (older build)
}
$Defaults = @{ AutoStart = $true; SlowMbps = 10; FastMbps = 50; FastMode = 'Daily'; SpeedEvery = 30; AlertAll = $false; Voice = $true; Character = 'Ambaan' }
$script:Cfg = @{ Corner = 'BottomRight'; Expanded = $false; ScreenX = $null; ScreenY = $null; LastFastDay = ''; AutoStartAsked = $false }
foreach ($k in $Defaults.Keys) { $script:Cfg[$k] = $Defaults[$k] }
if (Test-Path $SettingsFile) {
    try {
        $s = Get-Content $SettingsFile -Raw | ConvertFrom-Json
        if ($s.Corner) { $script:Cfg.Corner = $s.Corner }
        $script:Cfg.Expanded = [bool]$s.Expanded
        $script:Cfg.AlertAll = [bool]$s.AlertAll
        if ($s.SlowMbps) { $script:Cfg.SlowMbps = [double]$s.SlowMbps }
        if ($s.SpeedEvery) { $script:Cfg.SpeedEvery = [int]$s.SpeedEvery }
        if ($s.LastFastDay) { $script:Cfg.LastFastDay = [string]$s.LastFastDay }
        if ($null -ne $s.AutoStart) { $script:Cfg.AutoStart = [bool]$s.AutoStart }
        if ($null -ne $s.AutoStartAsked) { $script:Cfg.AutoStartAsked = [bool]$s.AutoStartAsked }
        if ($s.FastMbps) { $script:Cfg.FastMbps = [double]$s.FastMbps }
        if ($s.FastMode -in 'Daily', 'Every', 'Off') { $script:Cfg.FastMode = [string]$s.FastMode }
        if ($null -ne $s.Voice) { $script:Cfg.Voice = [bool]$s.Voice }
        if ($s.Character -in 'Ambaan', 'Dhamu') { $script:Cfg.Character = [string]$s.Character }
        if ($null -ne $s.ScreenX) { $script:Cfg.ScreenX = [int]$s.ScreenX; $script:Cfg.ScreenY = [int]$s.ScreenY }
    } catch {}
}
function Save-Known    { try { $script:Known | ConvertTo-Json | Set-Content -Encoding UTF8 $KnownFile } catch {} }
function Save-Settings { try { $script:Cfg | ConvertTo-Json | Set-Content -Encoding UTF8 $SettingsFile } catch {} }
if ($script:Cfg.FastMbps -le $script:Cfg.SlowMbps) { $script:Cfg.FastMbps = $script:Cfg.SlowMbps + 10 }
function Get-SpeedEvery { return [math]::Max(10, [int]$script:Cfg.SpeedEvery) }

# ---------- start with Windows: a shortcut in the user's Startup folder (no admin, shows in Task Manager > Startup apps) ----------
$StartupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'Ambaan WiFi Guard.lnk'
function Set-AutoStart([bool]$on) {
    try {
        if (-not $on) { if (Test-Path $StartupLink) { Remove-Item $StartupLink -Force }; return }
        $ws = New-Object -ComObject WScript.Shell
        $lnk = $ws.CreateShortcut($StartupLink)
        if ($env:AMBAAN_EXE -and (Test-Path $env:AMBAAN_EXE)) {
            # Started from the EXE: start the EXE at login (it unpacks / finds the scripts itself)
            $lnk.TargetPath = $env:AMBAAN_EXE
            $lnk.Arguments = '--atlogin'
            $lnk.WorkingDirectory = Split-Path -Parent $env:AMBAAN_EXE
            $lnk.IconLocation = $env:AMBAAN_EXE + ',0'
        } else {
            $lnk.TargetPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            $lnk.Arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + (Join-Path $Root 'WiFi-Floating-Widget.ps1') + '" -AtLogin'
            $lnk.WorkingDirectory = $Root
            $ico = Join-Path $Root 'assets\ambaan.ico'; if (Test-Path $ico) { $lnk.IconLocation = $ico }
        }
        $lnk.WindowStyle = 7
        $lnk.Description = 'Ambaan Wi-Fi Guard'
        $lnk.Save()
    } catch {}
}
Set-AutoStart ([bool]$script:Cfg.AutoStart)   # also re-points the shortcut if the folder was moved
function Write-Alert([string]$kind, [string]$detail) {
    try { [pscustomobject]@{ Timestamp = (Get-Date).ToString('s'); Event = $kind; Detail = $detail } | Export-Csv -Path $EventCsv -NoTypeInformation -Append -Encoding UTF8 } catch {}
}

# ---------- live state ----------
$script:Wifi = [pscustomobject]@{ Connected = $false; SSID = 'Wi-Fi'; Band = ''; Channel = ''; Signal = $null; RX = ''; TX = '' }
$script:Down = 0.0; $script:Up = 0.0
$script:DownHist = New-Object System.Collections.Generic.List[double]
$script:UpHist   = New-Object System.Collections.Generic.List[double]
$script:LastBytes = $null; $script:LastBytesAt = Get-Date
$script:RouterMs = $null; $script:NetMs = $null
$script:Gateway = Get-PrimaryGateway
$script:Devices = @(); $script:Scan = $null; $script:Scanning = $false; $script:ScanJob = $null; $script:ScanDue = [datetime]::MinValue
$script:Dns = $null; $script:DnsJob = $null; $script:DnsDue = [datetime]::MinValue
$script:Tick = 0
$script:Hits = New-Object System.Collections.Generic.List[object]
$script:Hover = $null
$script:DevScroll = 0
$script:Drag = $null
$script:SessionNew = @{}
$script:DevMap = @{}          # MAC -> @{ IP; LastSeen; LastAlive }
$script:LastJoinAlert = @{}   # MAC -> time of last reconnect alert
$script:OwnMacs = @{}         # this PC's adapters
Get-NetAdapter -Physical | ForEach-Object { if ($_.MacAddress) { $script:OwnMacs[$_.MacAddress.ToUpper()] = $_.Name } }
$script:GatewayMac = $null
$HintFile = Join-Path $DataDir 'DeviceHints.json'
$WatchLog = Join-Path $DataDir 'Watcher.log'
$script:HostNames = @{}       # MAC -> name the device announced (DHCP hostname / vendor)
if (Test-Path $HintFile) {
    try { (Get-Content $HintFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $script:HostNames[$_.Name.ToUpper()] = [string]$_.Value } } catch {}
}
function Save-Hints { try { $script:HostNames | ConvertTo-Json | Set-Content -Encoding UTF8 $HintFile } catch {} }
$script:Events = New-Object 'System.Collections.Concurrent.ConcurrentQueue[object]'
$script:WatchState = [hashtable]::Synchronized(@{ Stop = $false; Dhcp = 'starting'; Prefix = $null; Log = $WatchLog })

# ---------- look ----------
function C([string]$hex, [int]$a = 255) { [Drawing.Color]::FromArgb($a, [Drawing.ColorTranslator]::FromHtml($hex)) }
$Col = @{
    Top = C '#16133A'; Bottom = C '#2C2468'; Text = C '#F5F3FF'; Muted = C '#A9A3D9'
    Sky = C '#8EC5FF'; Lilac = C '#C9A7FF'; Green = C '#7CE8B8'; Amber = C '#FFD27A'; Red = C '#FF9C9C'
}
$Font = @{
    Title = New-Object Drawing.Font('Segoe UI Semibold', 11)
    Big   = New-Object Drawing.Font('Segoe UI Semibold', 17)
    Gauge = New-Object Drawing.Font('Segoe UI Semibold', 15)
    Body  = New-Object Drawing.Font('Segoe UI', 9)
    Bold  = New-Object Drawing.Font('Segoe UI Semibold', 9.5)
    Small = New-Object Drawing.Font('Segoe UI', 8)
    Tiny  = New-Object Drawing.Font('Segoe UI Semibold', 7.5)
    Icon  = New-Object Drawing.Font('Segoe UI Symbol', 10)
}
$W = 340; $HCompact = 122; $HExpanded = 660
$Margin = 14

function RF($x, $y, $w, $h) { New-Object Drawing.RectangleF([single]$x, [single]$y, [single]$w, [single]$h) }
function New-RoundPath([Drawing.RectangleF]$r, [single]$rad) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath; $d = $rad * 2
    $p.AddArc($r.X, $r.Y, $d, $d, 180, 90); $p.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $p.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90); $p.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}
function Fill-Round($g, $r, $rad, $color) { $p = New-RoundPath $r $rad; $b = New-Object Drawing.SolidBrush($color); $g.FillPath($b, $p); $b.Dispose(); $p.Dispose() }
function Draw-Text($g, [string]$t, $font, $color, $x, $y, $w = 400, $h = 30, [string]$align = 'Near') {
    $sf = New-Object Drawing.StringFormat; $sf.Alignment = $align; $sf.LineAlignment = 'Near'; $sf.Trimming = 'EllipsisCharacter'
    $b = New-Object Drawing.SolidBrush($color); $g.DrawString($t, $font, $b, (RF $x $y $w $h), $sf); $b.Dispose(); $sf.Dispose()
}
function Get-TextWidth($g, [string]$t, $font) { return $g.MeasureString($t, $font).Width }
function Add-Hit($rect, [string]$action, $data = $null) { $script:Hits.Add([pscustomobject]@{ Rect = $rect; Action = $action; Data = $data }) }
function Draw-Chip($g, [string]$t, $x, $y, $color, [string]$action = '') {
    $tw = (Get-TextWidth $g $t $Font.Tiny) + 14
    $r = RF $x $y $tw 20
    $hov = ($script:Hover -and $action -and $script:Hover.Action -eq $action)
    Fill-Round $g $r 10 ([Drawing.Color]::FromArgb($(if ($hov) { 70 } else { 42 }), $color))
    Draw-Text $g $t $Font.Tiny $color ($x + 7) ($y + 3) $tw 18
    if ($action) { Add-Hit $r $action }
    return $x + $tw + 6
}
function Draw-Button($g, [string]$t, $x, $y, $w, $h, [string]$action, $color) {
    $r = RF $x $y $w $h
    $hov = ($script:Hover -and $script:Hover.Action -eq $action)
    Fill-Round $g $r 9 ([Drawing.Color]::FromArgb($(if ($hov) { 90 } else { 48 }), $color))
    Draw-Text $g $t $Font.Bold $Col.Text $x ($y + ($h - 18) / 2) $w 20 'Center'
    Add-Hit $r $action
}

# ---------- measurements ----------
function Update-Wifi {
    $txt = (netsh wlan show interfaces) -join "`n"
    $w = [pscustomobject]@{ Connected = $false; SSID = 'Wired'; Band = ''; Channel = ''; Signal = $null; RX = ''; TX = ''; Name = '' }
    if ($txt -match '(?im)^\s*State\s*:\s*connected') { $w.Connected = $true }
    if ($txt -match '(?im)^\s*Name\s*:\s*(.+)$') { $w.Name = $Matches[1].Trim() }
    if ($w.Connected) {
        if ($txt -match '(?im)^\s*SSID\s*:\s*(.+)$') { $w.SSID = $Matches[1].Trim() }
        if ($txt -match '(?im)^\s*Band\s*:\s*(.+)$') { $w.Band = $Matches[1].Trim() }
        if ($txt -match '(?im)^\s*Channel\s*:\s*(\d+)') { $w.Channel = $Matches[1] }
        if ($txt -match '(?im)^\s*Signal\s*:\s*(\d+)%') { $w.Signal = [int]$Matches[1] }
        if ($txt -match '(?im)^\s*Receive rate \(Mbps\)\s*:\s*([\d.]+)') { $w.RX = $Matches[1] }
        if ($txt -match '(?im)^\s*Transmit rate \(Mbps\)\s*:\s*([\d.]+)') { $w.TX = $Matches[1] }
    }
    $script:Wifi = $w
}
function Update-Traffic {
    # Total traffic across this PC's Wi-Fi and Ethernet adapters (what this PC is using right now).
    $rx = [long]0; $tx = [long]0
    foreach ($n in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($n.OperationalStatus -eq 'Up' -and ($n.NetworkInterfaceType -eq 'Ethernet' -or $n.NetworkInterfaceType -eq 'Wireless80211')) {
            try { $st = $n.GetIPStatistics(); $rx += $st.BytesReceived; $tx += $st.BytesSent } catch {}
        }
    }
    $now = Get-Date
    if ($script:LastBytes) {
        $dt = ($now - $script:LastBytesAt).TotalSeconds
        if ($dt -gt 0) {
            $script:Down = [math]::Round([math]::Max(0, ($rx - $script:LastBytes[0]) * 8 / 1e6 / $dt), 1)
            $script:Up   = [math]::Round([math]::Max(0, ($tx - $script:LastBytes[1]) * 8 / 1e6 / $dt), 1)
        }
    }
    $script:LastBytes = @($rx, $tx); $script:LastBytesAt = $now
    $script:DownHist.Add($script:Down); $script:UpHist.Add($script:Up)
    while ($script:DownHist.Count -gt 120) { $script:DownHist.RemoveAt(0); $script:UpHist.RemoveAt(0) }
}
function Get-Ping([string]$target, [int]$timeout) {
    if (-not $target) { return $null }
    try { $p = New-Object System.Net.NetworkInformation.Ping; $r = $p.Send($target, $timeout); $p.Dispose(); if ($r.Status -eq 'Success') { return [int]$r.RoundtripTime } } catch {}
    return $null
}

# ---------- background jobs (keep the widget smooth) ----------
function Start-Bg([string]$code) {
    $ps = [powershell]::Create(); [void]$ps.AddScript($code)
    return [pscustomobject]@{ PS = $ps; Handle = $ps.BeginInvoke() }
}
function Receive-Bg($job) {
    $out = $null
    try { $out = $job.PS.EndInvoke($job.Handle) } catch {}
    try { $job.PS.Dispose() } catch {}
    if ($out -and $out.Count) { return $out[0] }
    return $null
}
function Request-Scan { if (-not $script:Scanning) { $script:Scanning = $true; $script:ScanJob = Start-Bg ". '$SafetyScript'; Invoke-LanScan"; $form.Invalidate() } }
function Request-Dns  { if (-not $script:DnsJob) { $script:DnsJob = Start-Bg ". '$SafetyScript'; Get-DnsSafety"; $form.Invalidate() } }

function Get-DeviceHint([string]$mac) {
    if ($script:OwnMacs.ContainsKey($mac)) { return 'this computer' }
    if ($mac -eq $script:GatewayMac) { return 'main router' }
    if ($script:HostNames.ContainsKey($mac)) { return "calls itself: $($script:HostNames[$mac])" }
    if (Test-RandomMac $mac) { return 'private MAC - phone/laptop' }
    return 'fixed address'
}
function Register-Device([string]$mac, [string]$ip, [string]$source, [bool]$alive = $true) {
    # Called by the 20s sweep AND by the live watcher. Alerts the moment an unseen MAC appears.
    $mac = $mac.ToUpper()
    if ($mac -match '^(FF-FF|01-00-5E|33-33|00-00-00-00-00-00)') { return }   # broadcast, multicast, unresolved
    $now = Get-Date
    $prev = $script:DevMap[$mac]
    $offlineMin = if ($prev -and $prev.LastAlive -ne [datetime]::MinValue) { ($now - $prev.LastAlive).TotalMinutes } else { $null }
    if ($script:DevMap.ContainsKey($mac)) {
        $script:DevMap[$mac].LastSeen = $now
        if ($alive) { $script:DevMap[$mac].LastAlive = $now; if ($ip) { $script:DevMap[$mac].IP = $ip } }
    } else {
        $script:DevMap[$mac] = @{ IP = $(if ($ip) { $ip } else { 'connecting...' }); LastSeen = $now; LastAlive = $(if ($alive) { $now } else { [datetime]::MinValue }) }
    }
    $auto = $null
    if ($script:OwnMacs.ContainsKey($mac)) { $auto = "This PC - $($script:OwnMacs[$mac])" } elseif ($mac -eq $script:GatewayMac) { $auto = 'Router (gateway)' }
    if (-not $script:Known.ContainsKey($mac)) {
        $script:Known[$mac] = $(if ($auto) { $auto } else { $Unnamed }); Save-Known
        if (-not $auto) {
            $script:SessionNew[$mac] = $true
            $shownIp = $script:DevMap[$mac].IP
            Write-Alert 'New device detected' ("IP {0}; MAC {1}; via {2}" -f $shownIp, $mac, $source)
            try { [pscustomobject]@{ Timestamp = (Get-Date).ToString('s'); IP = $shownIp; MAC = $mac; DeviceName = $Unnamed } | Export-Csv -Path $DeviceCsv -NoTypeInformation -Append -Encoding UTF8 } catch {}
            $script:LastJoinAlert[$mac] = $now
            Show-JoinAlert $mac 'New'
            $script:ScanDue = (Get-Date).AddSeconds(5)   # quick sweep to fill in its IP
        }
    } else {
        if ($auto -and $script:Known[$mac] -eq $Unnamed) { $script:Known[$mac] = $auto; Save-Known }
        # Reconnect alert. A DHCP request = the device is joining right now.
        # From the sweep, only count it if it was gone 5+ min (phones that sleep briefly stop answering pings).
        $joined = $alive -and (($source -eq 'dhcp') -or ($null -ne $offlineMin -and $offlineMin -ge 5))
        $name = $script:Known[$mac]
        $trusted = ($name -ne $Unnamed)
        $last = $script:LastJoinAlert[$mac]
        $cool = (-not $last) -or (($now - $last).TotalSeconds -ge 10)   # a join sends 2-4 DHCP packets within a second or two: alert once
        if ($joined -and -not $auto -and $cool -and ((-not $trusted) -or $script:Cfg.AlertAll)) {
            $script:LastJoinAlert[$mac] = $now
            $shownIp = $script:DevMap[$mac].IP
            $who = if ($trusted) { "'$name'" } elseif ($script:HostNames.ContainsKey($mac)) { "Unnamed device '$($script:HostNames[$mac])'" } else { 'An unnamed device' }
            Write-Alert 'Device reconnected' ("{0}; IP {1}; MAC {2}; via {3}" -f $name, $shownIp, $mac, $source)
            if ($trusted) {
                $tray.BalloonTipTitle = 'Device reconnected'; $tray.BalloonTipText = "$who is back on Wi-Fi ($shownIp)."; $tray.ShowBalloonTip(6000)
            } else { Show-JoinAlert $mac 'Unnamed' }
        }
    }
}
function Build-DeviceList {
    # Online = answered within the last 50 s (two sweeps). Offline devices stay listed, dimmed, for 30 min.
    $now = Get-Date
    $list = @()
    foreach ($mac in @($script:DevMap.Keys)) {
        $e = $script:DevMap[$mac]
        $online = ($now - $e.LastAlive).TotalSeconds -le 50
        if (-not $online -and ($e.LastAlive -eq [datetime]::MinValue -or ($now - $e.LastAlive).TotalMinutes -gt 30)) { continue }
        $name = $script:Known[$mac]
        $ago = [int][math]::Max(1, ($now - $e.LastAlive).TotalMinutes)
        $hint = if ($online) { Get-DeviceHint $mac } else { "offline - last seen $ago min ago" }
        $list += [pscustomobject]@{ IP = $e.IP; MAC = $mac; Name = $name; Named = ($name -ne $Unnamed); Hint = $hint; New = $script:SessionNew.ContainsKey($mac); Online = $online }
    }
    $script:Devices = @($list | Sort-Object @{ Expression = { -not $_.Online } }, @{ Expression = { $p = $_.IP.Split('.'); if ($p.Count -eq 4) { [int]$p[3] } else { 999 } } })
}
function Merge-Scan($scan) {
    if (-not $scan) { return }
    $script:Scan = $scan
    if ($scan.GatewayMac) { $script:GatewayMac = $scan.GatewayMac }
    if ($scan.Gateway) { $script:WatchState.Prefix = (($scan.Gateway.Split('.'))[0..2] -join '.') + '.' }
    foreach ($r in $scan.Rows) { Register-Device $r.MAC $r.IP 'sweep' ([bool]$r.Alive) }
    Build-DeviceList
}
function Receive-WatchEvents {
    $ev = $null; $any = $false
    while ($script:Events.TryDequeue([ref]$ev)) {
        $any = $true
        $mac = $ev.MAC.ToUpper()
        $label = $null
        if ($ev.Host) { $label = $ev.Host }
        elseif ($ev.Vendor -match '^android') { $label = 'Android device' }
        elseif ($ev.Vendor -match '^MSFT') { $label = 'Windows PC' }
        $better = $ev.Host -or -not $script:HostNames.ContainsKey($mac)   # a real hostname beats a vendor guess
        if ($label -and $better -and $script:HostNames[$mac] -ne $label) { $script:HostNames[$mac] = $label; Save-Hints }
        # DHCP = it is joining right now. ARP = trust it only for a MAC we have never seen (old entries linger).
        $alive = ($ev.Source -eq 'dhcp') -or -not $script:DevMap.ContainsKey($mac)
        Register-Device $mac $ev.IP $ev.Source $alive
    }
    if ($any) { Build-DeviceList }
}

# Live watcher, runs in the background:
#  1. listens for DHCP broadcasts: every phone/laptop sends one the moment it joins the Wi-Fi
#  2. reads the Windows ARP table every 3 s (catches devices that talk without DHCP)
$WatcherCode = @'
param($Queue, $State)
function Log([string]$t) { try { Add-Content -Path $State.Log -Value ("{0}  {1}" -f (Get-Date).ToString('s'), $t) } catch {} }
$udp = $null
try {
    $udp = New-Object System.Net.Sockets.UdpClient
    $udp.Client.SetSocketOption([System.Net.Sockets.SocketOptionLevel]::Socket, [System.Net.Sockets.SocketOptionName]::ReuseAddress, $true)
    $udp.Client.Bind((New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 67)))
    $udp.Client.ReceiveTimeout = 1000
    $State.Dhcp = 'listening'; Log 'DHCP listener started on UDP 67'
} catch { $State.Dhcp = 'off'; Log ("DHCP listener unavailable: " + $_.Exception.Message); $udp = $null }
$seen = @{}
$lastArp = [datetime]::MinValue
while (-not $State.Stop) {
    if ($udp) {
        try {
            $ep = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
            $b = $udp.Receive([ref]$ep)
            if ($b.Length -ge 240 -and $b[0] -eq 1 -and $b[236] -eq 99 -and $b[237] -eq 130 -and $b[238] -eq 83 -and $b[239] -eq 99) {
                $mac = ($b[28..33] | ForEach-Object { '{0:X2}' -f $_ }) -join '-'
                $hostName = $null; $ip = $null; $vendor = $null; $type = $null
                $ci = ($b[12..15]) -join '.'
                if ($ci -ne '0.0.0.0') { $ip = $ci }
                $i = 240
                while ($i -lt $b.Length) {
                    $code = $b[$i]
                    if ($code -eq 255) { break }
                    if ($code -eq 0) { $i++; continue }
                    if ($i + 1 -ge $b.Length) { break }
                    $len = [int]$b[$i + 1]
                    if ($len -gt 0 -and ($i + 1 + $len) -lt $b.Length) {
                        $val = $b[($i + 2)..($i + 1 + $len)]
                        switch ($code) {
                            53 { $type = $val[0] }
                            12 { $hostName = [Text.Encoding]::ASCII.GetString([byte[]]$val).Trim([char]0) }
                            50 { if ($len -eq 4) { $ip = $val -join '.' } }
                            60 { $vendor = [Text.Encoding]::ASCII.GetString([byte[]]$val).Trim([char]0) }
                        }
                    }
                    $i += 2 + $len
                }
                Log ("DHCP type {0} from {1} ip={2} host={3} vendor={4}" -f $type, $mac, $ip, $hostName, $vendor)
                $Queue.Enqueue([pscustomobject]@{ Source = 'dhcp'; MAC = $mac; IP = $ip; Host = $hostName; Vendor = $vendor })
            }
        } catch {}
    } else { Start-Sleep -Milliseconds 1000 }

    if ($State.Forget) { $seen.Remove($State.Forget); $State.Forget = $null }
    if (((Get-Date) - $lastArp).TotalSeconds -ge 3 -and $State.Prefix) {
        $lastArp = Get-Date
        try {
            Get-NetNeighbor -AddressFamily IPv4 -ErrorAction Stop |
                Where-Object { $_.IPAddress -like "$($State.Prefix)*" -and $_.State -notin @('Unreachable','Incomplete','Permanent') -and
                               $_.LinkLayerAddress -match '^([0-9A-Fa-f]{2}-){5}[0-9A-Fa-f]{2}$' } |
                ForEach-Object {
                    $m = $_.LinkLayerAddress.ToUpper()
                    if ($m -ne '00-00-00-00-00-00' -and -not $seen.ContainsKey($m)) {
                        $seen[$m] = $true
                        $Queue.Enqueue([pscustomobject]@{ Source = 'arp'; MAC = $m; IP = $_.IPAddress; Host = $null; Vendor = $null })
                    }
                }
        } catch {}
    }
}
if ($udp) { $udp.Close() }
'@
function Start-Watcher {
    $ps = [powershell]::Create()
    [void]$ps.AddScript($WatcherCode).AddArgument($script:Events).AddArgument($script:WatchState)
    $script:Watcher = [pscustomobject]@{ PS = $ps; Handle = $ps.BeginInvoke() }
}

# ---------- dialogs ----------
function Style-Dialog($f, [int]$w, [int]$h, [string]$title) {
    $f.Text = $title; $f.Size = New-Object Drawing.Size($w, $h); $f.FormBorderStyle = 'FixedToolWindow'
    $f.StartPosition = 'CenterScreen'; $f.TopMost = $true; $f.BackColor = $Col.Top; $f.ForeColor = $Col.Text; $f.Font = $Font.Body
}
function New-DlgButton([string]$t, $x, $y, $w, $color) {
    $b = New-Object Windows.Forms.Button; $b.Text = $t; $b.Location = New-Object Drawing.Point($x, $y); $b.Size = New-Object Drawing.Size($w, 32)
    $b.FlatStyle = 'Flat'; $b.FlatAppearance.BorderSize = 0; $b.BackColor = $color; $b.ForeColor = $Col.Text; $b.Font = $Font.Bold; return $b
}
function Show-Settings {
    $f = New-Object Windows.Forms.Form; Style-Dialog $f 430 550 'Ambaan settings'
    $mk = {
        param($t, $x, $y, $w, $h, $muted)
        $l = New-Object Windows.Forms.Label; $l.Text = $t; $l.Location = New-Object Drawing.Point($x, $y); $l.Size = New-Object Drawing.Size($w, $h)
        $l.ForeColor = $(if ($muted) { $Col.Muted } else { $Col.Text }); $l.BackColor = [Drawing.Color]::Transparent; $l
    }
    $now = & $mk '' 16 12 384 36 $true
    $now.Text = ('Now: slow below {0} Mbps, good at {1} Mbps, speed check every {2} min, start with Windows {3}' -f `
        $script:Cfg.SlowMbps, $script:Cfg.FastMbps, (Get-SpeedEvery), $(if ($script:Cfg.AutoStart) { 'on' } else { 'off' }))
    $mkNum = {
        param($x, $y, $min, $max)
        $n = New-Object Windows.Forms.NumericUpDown; $n.Location = New-Object Drawing.Point($x, $y); $n.Size = New-Object Drawing.Size(90, 26)
        $n.Minimum = $min; $n.Maximum = $max; $n.DecimalPlaces = 0; $n.Font = New-Object Drawing.Font('Segoe UI', 11)
        $n.BackColor = $Col.Bottom; $n.ForeColor = $Col.Text; $n.BorderStyle = 'FixedSingle'; $n
    }
    $mkCombo = {
        param($x, $y, $w, $items)
        $c = New-Object Windows.Forms.ComboBox; $c.DropDownStyle = 'DropDownList'; $c.FlatStyle = 'Flat'
        $c.Location = New-Object Drawing.Point($x, $y); $c.Size = New-Object Drawing.Size($w, 26); $c.Font = New-Object Drawing.Font('Segoe UI', 10)
        $c.BackColor = $Col.Bottom; $c.ForeColor = $Col.Text; [void]$c.Items.AddRange([object[]]$items); $c
    }
    $mkCheck = {
        param($t, $y)
        $c = New-Object Windows.Forms.CheckBox; $c.Text = $t; $c.Location = New-Object Drawing.Point(16, $y); $c.Size = New-Object Drawing.Size(384, 24)
        $c.ForeColor = $Col.Text; $c.Font = $Font.Bold; $c.FlatStyle = 'Flat'; $c
    }

    $hAlerts = & $mk 'SPEED ALERTS' 16 58 384 18 $true; $hAlerts.Font = $Font.Tiny
    $l1 = & $mk 'Slow alert when speed drops below' 16 86 260 22 $false
    $slow = & $mkNum 280 82 1 500;  $u1 = & $mk 'Mbps' 376 86 40 22 $true
    $l2 = & $mk 'Good-speed alert when speed reaches' 16 124 260 22 $false
    $fast = & $mkNum 280 120 2 2000; $u2 = & $mk 'Mbps' 376 124 40 22 $true
    $l3 = & $mk 'Good-speed alert' 16 162 160 22 $false
    $modes = @('Once a day', 'Every time', 'Off'); $modeKeys = @('Daily', 'Every', 'Off')
    $mode = & $mkCombo 200 158 200 $modes
    $l4 = & $mk 'Check speed every' 16 200 160 22 $false
    $mins = @(15, 30, 60, 120)
    $every = & $mkCombo 200 196 200 ($mins | ForEach-Object { "$_ minutes" })
    $hint = & $mk 'Between the two numbers Ambaan stays quiet. Each check downloads a small 5 MB file.' 16 232 384 34 $true

    $hOther = & $mk 'GENERAL' 16 280 384 18 $true; $hOther.Font = $Font.Tiny
    $l5 = & $mk 'Alert character' 16 310 160 22 $false
    $chars = @('Ambaan', 'Dhamu'); $charKeys = @('Ambaan', 'Dhamu')
    $char = & $mkCombo 200 306 120 $chars
    $pv = New-DlgButton 'Preview' 328 303 72 (C '#3A3270')
    $pv.Add_Click({
        $old = $script:Cfg.Character; $script:Cfg.Character = $charKeys[$char.SelectedIndex]
        $m = @($script:Devices | Where-Object { -not $_.Named } | Select-Object -ExpandProperty MAC -First 1)
        if (-not $m) { $m = @($script:DevMap.Keys | Select-Object -First 1) }
        if ($m) { Show-JoinAlert ([string]$m[0]) 'New' $true }
        $script:Cfg.Character = $old
    })
    $auto = & $mkCheck 'Start Ambaan when Windows starts' 344
    $voice = & $mkCheck 'Character voice on alerts' 376
    $all = & $mkCheck 'Alert when my named devices reconnect too' 408

    $load = {
        param($c)
        $slow.Value = [decimal][math]::Min(500, [math]::Max(1, [double]$c.SlowMbps))
        $fast.Value = [decimal][math]::Min(2000, [math]::Max(2, [double]$c.FastMbps))
        $mode.SelectedIndex = [math]::Max(0, [array]::IndexOf($modeKeys, [string]$c.FastMode))
        $i = [array]::IndexOf($mins, [int]$c.SpeedEvery); if ($i -lt 0) { $i = 1 }; $every.SelectedIndex = $i
        $char.SelectedIndex = [math]::Max(0, [array]::IndexOf($charKeys, [string]$c.Character))
        $auto.Checked = [bool]$c.AutoStart; $voice.Checked = [bool]$c.Voice; $all.Checked = [bool]$c.AlertAll
    }
    & $load $script:Cfg

    $ok = New-DlgButton 'Save' 16 458 100 (C '#5B4BC4')
    $rs = New-DlgButton 'Reset to default' 124 458 150 (C '#3A3270')
    $cn = New-DlgButton 'Cancel' 282 458 118 (C '#3A3270'); $cn.DialogResult = 'Cancel'
    $ok.Add_Click({
        if ($fast.Value -le $slow.Value) {
            [Windows.Forms.MessageBox]::Show($f, 'The good-speed number must be higher than the slow number.', 'Ambaan settings') | Out-Null
            $fast.Focus(); return
        }
        $f.DialogResult = 'OK'
    })
    $rs.Add_Click({ & $load $Defaults })
    $f.AcceptButton = $ok; $f.CancelButton = $cn
    $f.Controls.AddRange(@($now, $hAlerts, $l1, $slow, $u1, $l2, $fast, $u2, $l3, $mode, $l4, $every, $hint, $hOther, $l5, $char, $pv, $auto, $voice, $all, $ok, $rs, $cn))
    $res = if ($form.Visible) { $f.ShowDialog($form) } else { $f.ShowDialog() }
    if ($res -eq 'OK') {
        $oldEvery = Get-SpeedEvery
        $script:Cfg.SlowMbps = [double]$slow.Value; $script:Cfg.FastMbps = [double]$fast.Value
        $script:Cfg.FastMode = $modeKeys[$mode.SelectedIndex]; $script:Cfg.SpeedEvery = $mins[$every.SelectedIndex]
        $script:Cfg.Voice = $voice.Checked; $script:Cfg.AlertAll = $all.Checked; $script:Cfg.Character = $charKeys[$char.SelectedIndex]
        if ($script:Cfg.AutoStart -ne $auto.Checked) { $script:Cfg.AutoStart = $auto.Checked; Set-AutoStart $auto.Checked }
        $script:Cfg.AutoStartAsked = $true
        Save-Settings
        if ((Get-SpeedEvery) -ne $oldEvery -and -not $script:Speed.Job -and $script:Speed.SlowCount -eq 0) {
            $base = if ($script:Speed.LastAt) { $script:Speed.LastAt } else { Get-Date }
            $next = $base.AddMinutes((Get-SpeedEvery)); $soon = (Get-Date).AddMinutes(1)
            $script:Speed.NextTest = $(if ($next -lt $soon) { $soon } else { $next })
        }
        if ($form.Visible) { $form.Invalidate() }
    }
    $f.Dispose()
}
function Show-Rename($dev) {
    $f = New-Object Windows.Forms.Form; Style-Dialog $f 360 215 'Name this device'
    $l = New-Object Windows.Forms.Label; $l.Text = "$($dev.IP)   $($dev.MAC)`n$($dev.Hint)"; $l.ForeColor = $Col.Muted; $l.Location = '16,12'; $l.Size = '320,38'
    $t = New-Object Windows.Forms.TextBox; $t.Location = '16,56'; $t.Size = '312,26'; $t.Font = New-Object Drawing.Font('Segoe UI', 11)
    $t.BackColor = $Col.Bottom; $t.ForeColor = $Col.Text; $t.BorderStyle = 'FixedSingle'
    $t.Text = $(if ($dev.Named) { $dev.Name } elseif ($script:HostNames.ContainsKey($dev.MAC)) { $script:HostNames[$dev.MAC] } else { '' })
    $ok = New-DlgButton 'Save' 16 104 72 (C '#5B4BC4'); $ok.DialogResult = 'OK'
    $clr = New-DlgButton 'Clear name' 94 104 92 (C '#3A3270'); $clr.DialogResult = 'Retry'
    $fg = New-DlgButton 'Forget' 192 104 70 (C '#6B2F4F'); $fg.DialogResult = 'Abort'
    $cn = New-DlgButton 'Cancel' 268 104 60 (C '#3A3270'); $cn.DialogResult = 'Cancel'
    $tip = New-Object Windows.Forms.ToolTip; $tip.SetToolTip($fg, 'Forget this device: it will alert again as NEW the next time it connects')
    $f.AcceptButton = $ok; $f.CancelButton = $cn
    $f.Controls.AddRange(@($l, $t, $ok, $clr, $fg, $cn))
    $f.Add_Shown({ $t.Focus(); $t.SelectAll() })
    $res = $f.ShowDialog($form); $val = $t.Text.Trim(); $f.Dispose()
    if ($res -eq 'OK' -and $val) { $script:Known[$dev.MAC] = $val }
    elseif ($res -eq 'Retry') { $script:Known[$dev.MAC] = $Unnamed }
    elseif ($res -eq 'Abort') {
        $script:Known.Remove($dev.MAC); $script:DevMap.Remove($dev.MAC); $script:HostNames.Remove($dev.MAC); Save-Hints
        $script:WatchState.Forget = $dev.MAC   # let the ARP watcher report it again
    }
    else { return }
    $script:SessionNew.Remove($dev.MAC)
    Save-Known
    Build-DeviceList
    $form.Invalidate()
}
function Show-DnsFix {
    $f = New-Object Windows.Forms.Form; Style-Dialog $f 440 400 'DNS Shield'
    $info = New-Object Windows.Forms.Label; $info.Location = '16,12'; $info.Size = '400,74'; $info.ForeColor = $Col.Muted
    $lines = @()
    if ($script:Dns) { $lines += $script:Dns.Headline; $lines += $script:Dns.Issues }
    $lines += 'Choose what this PC should use for DNS:'
    $info.Text = ($lines -join "`n")
    $r1 = New-Object Windows.Forms.RadioButton; $r1.Text = 'Scam filter: Cloudflare 1.1.1.2 / 1.0.0.2  (blocks phishing + malware)'; $r1.Location = '16,92'; $r1.Size = '400,24'; $r1.Checked = $true
    $r2 = New-Object Windows.Forms.RadioButton; $r2.Text = 'Family filter: Cloudflare 1.1.1.3 / 1.0.0.3  (also blocks adult sites)'; $r2.Location = '16,120'; $r2.Size = '400,24'
    $r3 = New-Object Windows.Forms.RadioButton; $r3.Text = 'Automatic: use the router again (removes manual DNS)'; $r3.Location = '16,148'; $r3.Size = '400,24'
    foreach ($r in $r1, $r2, $r3) { $r.ForeColor = $Col.Text }
    $note = New-Object Windows.Forms.Label; $note.Location = '16,180'; $note.Size = '400,100'; $note.ForeColor = $Col.Amber
    $note.Text = "This changes DNS on THIS PC only (Wi-Fi + Ethernet) and asks for administrator permission.`n`nTo protect every phone and TV at home, set 1.1.1.2 and 1.0.0.2 as DNS on your router's admin page or app. Phones on mobile data need Private DNS: security.cloudflare-dns.com"
    $ok = New-DlgButton 'Apply' 16 300 120 (C '#5B4BC4'); $ok.DialogResult = 'OK'
    $cn = New-DlgButton 'Cancel' 144 300 100 (C '#3A3270'); $cn.DialogResult = 'Cancel'
    $f.AcceptButton = $ok; $f.CancelButton = $cn
    $f.Controls.AddRange(@($info, $r1, $r2, $r3, $note, $ok, $cn))
    $res = $f.ShowDialog($form)
    $choice = if ($r1.Checked) { 1 } elseif ($r2.Checked) { 2 } else { 3 }
    $f.Dispose()
    if ($res -ne 'OK') { return }
    $set = switch ($choice) {
        1 { "-ServerAddresses '1.1.1.2','1.0.0.2','2606:4700:4700::1112','2606:4700:4700::1002'" }
        2 { "-ServerAddresses '1.1.1.3','1.0.0.3','2606:4700:4700::1113','2606:4700:4700::1003'" }
        3 { '-ResetServerAddresses' }
    }
    $cmd = "Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | ForEach-Object { Set-DnsClientServerAddress -InterfaceIndex `$_.ifIndex $set }; Clear-DnsClientCache"
    $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cmd))
    try {
        $p = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList "-NoProfile -EncodedCommand $enc" -Wait -PassThru -ErrorAction Stop
        Write-Alert 'DNS changed' "Option $choice applied by user (exit $($p.ExitCode))"
    } catch {
        [Windows.Forms.MessageBox]::Show('No change made (administrator permission was not given).', 'DNS Shield') | Out-Null
        return
    }
    $script:Dns = $null; Request-Dns
}

# ---------- join alert popup (animated character) ----------
$AlertGif = Join-Path $Root 'assets\new-device.gif'
$script:AlertImg = $null
if (Test-Path $AlertGif) { try { $script:AlertImg = [Drawing.Image]::FromFile($AlertGif) } catch {} }
# Characters: Ambaan = new-device.gif + new-device/slow-wifi/fast-wifi.mp3; Dhamu = dhamu.gif + dhamu-<same name>.mp3
$script:CharImg = @{ Ambaan = $script:AlertImg }
$DhamuGif = Join-Path $Root 'assets\dhamu.gif'
if (Test-Path $DhamuGif) { try { $script:CharImg.Dhamu = [Drawing.Image]::FromFile($DhamuGif) } catch {} }
$script:PopChar = 'Ambaan'; $script:CharTurn = 0
$script:Pop = $null
$script:PopMac = $null
$script:PopKind = 'New'
$script:PopHover = $null
$script:PopHits = New-Object System.Collections.Generic.List[object]
$PopW = 300; $PopH = 332                         # layout size (all drawing uses these units)
$PS = 0.78                                        # on-screen scale of the alert popup: 1.0 = original, smaller = smaller
$PopWs = [int]($PopW * $PS); $PopHs = [int]($PopH * $PS)
$PopCardTop = 172                                   # character (y 38-205) overlaps the card top by ~33 px
$SeeThrough = [Drawing.Color]::FromArgb(255, 1, 0, 1)     # near-black key: resized edge pixels blend into the dark outline, not a coloured fringe

function Get-DevForMac([string]$mac) {
    $e = $script:DevMap[$mac]
    $name = $script:Known[$mac]
    [pscustomobject]@{ IP = $(if ($e) { $e.IP } else { '?' }); MAC = $mac; Name = $name; Named = ($name -ne $Unnamed); Hint = (Get-DeviceHint $mac) }
}
function Show-BlockHelp([string]$mac) {
    try { [Windows.Forms.Clipboard]::SetText($mac) } catch {}
    $dev = Get-DevForMac $mac
    $msg = "MAC address copied:  $mac`n`n" +
           "To block it:`n" +
           "1. Open your router's app or admin page`n" +
           "2. Go to Connected devices (or Clients / Device list)`n" +
           "3. Find the device with this MAC (or IP $($dev.IP)) and tap Block`n`n" +
           "Not sure which one it is? Change the Wi-Fi password there: every device that is not yours gets disconnected.`n`n" +
           "This PC cannot remove devices from Wi-Fi. Only the router can."
    [Windows.Forms.MessageBox]::Show($msg, 'Block this device', 'OK', 'Information') | Out-Null
}
function Close-JoinAlert {
    if ($script:Pop) { $popTimer.Stop(); $talkTimer.Stop(); $script:Pop.Hide() }
}
function Get-PopLocation {
    $wa = [Windows.Forms.Screen]::FromControl($form).WorkingArea
    if ($form.Visible -and $form.Opacity -gt 0) {
        $x = $form.Left + ($form.Width - $PopWs)
        if ($script:Cfg.Corner -like '*Left') { $x = $form.Left }
        if ($script:Cfg.Corner -like 'Bottom*') { $y = $form.Top - $PopHs - 10 } else { $y = $form.Bottom + 10 }
        if ($y -lt $wa.Top -or ($y + $PopHs) -gt $wa.Bottom) {          # no room above/below: sit beside the widget
            $y = [math]::Max($wa.Top + $Margin, [math]::Min($form.Top, $wa.Bottom - $PopHs - $Margin))
            $x = if ($script:Cfg.Corner -like '*Left') { $form.Right + 10 } else { $form.Left - $PopWs - 10 }
        }
        return New-Object Drawing.Point([int]$x, [int]$y)
    }
    $x = if ($script:Cfg.Corner -like '*Left') { $wa.Left + $Margin } else { $wa.Right - $PopWs - $Margin }
    $y = if ($script:Cfg.Corner -like 'Top*') { $wa.Top + $Margin } else { $wa.Bottom - $PopHs - $Margin }
    return New-Object Drawing.Point([int]$x, [int]$y)
}
function Draw-PopCard($g) {
    $w = $PopW; $h = $PopH
    $script:PopHits.Clear()
    $g.ScaleTransform([single]$PS, [single]$PS)   # draw in layout units, shown at $PS size
    $ct = $PopCardTop
    $cardPath = New-RoundPath (RF 0 $ct ($w - 1) ($h - $ct - 1)) 16
    $bg = New-Object Drawing.Drawing2D.LinearGradientBrush((RF 0 $ct $w ($h - $ct)), $Col.Top, $Col.Bottom, [single]70)
    $g.FillPath($bg, $cardPath); $bg.Dispose()
    $accent = if ($script:PopKind -eq 'New') { $Col.Red } elseif ($script:PopKind -eq 'Slow') { $Col.Sky } elseif ($script:PopKind -eq 'Fast') { $Col.Green } elseif ($script:PopKind -eq 'Ok') { $Col.Lilac } else { $Col.Amber }
    $cardPath.Dispose()   # no outline: a clean card like the Mac version
    $ap = New-Object Drawing.Pen($accent, 3); $g.DrawLine($ap, 18, $ct + 1, $w - 18, $ct + 1); $ap.Dispose()
    $g.SmoothingMode = 'AntiAlias'
    # close button
    $cr = RF ($w - 32) ($PopCardTop + 8) 24 24
    if ($script:PopHover -eq 'close') { Fill-Round $g $cr 12 ([Drawing.Color]::FromArgb(50, 255, 255, 255)) }
    Draw-Text $g ([string][char]0x2715) $Font.Icon $Col.Muted ($w - 32) ($PopCardTop + 9) 24 22 'Center'
    $script:PopHits.Add([pscustomobject]@{ Rect = $cr; Action = 'close' })
    if ($script:PopKind -in 'Slow', 'Fast', 'Ok') {
        $sp = $script:Speed
        $ttl = if ($script:PopKind -eq 'Fast') { 'Wi-Fi is flying today!' } elseif ($script:PopKind -eq 'Ok') { 'Speed is OK, not super fast' } else { 'Wi-Fi is slow again!' }
        Draw-Text $g $ttl $Font.Title $accent 0 216 $w 24 'Center'
        $shown = $sp.Last
        $now = if ($null -ne $shown) { "$shown Mbps" } else { 'No internet' }
        Draw-Text $g $now $Font.Big $Col.Text 16 240 ($w - 32) 34 'Center'
        $b1 = if ($script:PopKind -ne 'Slow') { @{ T = 'Test again'; A = 'retest'; X = 16; C = $Col.Lilac } } else { @{ T = 'Full test'; A = 'fulltest'; X = 16; C = $Col.Lilac } }
        foreach ($b in @($b1, @{ T = 'OK'; A = 'close'; X = 156; C = $Col.Sky })) {
            $r = RF $b.X 284 128 34
            Fill-Round $g $r 10 ([Drawing.Color]::FromArgb($(if ($script:PopHover -eq $b.A) { 150 } else { 95 }), $b.C))
            Draw-Text $g $b.T $Font.Bold $Col.Text $b.X 291 128 20 'Center'
            $script:PopHits.Add([pscustomobject]@{ Rect = $r; Action = $b.A })
        }
        return
    }
    $dev = Get-DevForMac $script:PopMac
    $title = if ($script:PopKind -eq 'New') { 'New device on your Wi-Fi!' } else { 'Unknown device is back!' }
    Draw-Text $g $title $Font.Title $accent 0 216 $w 24 'Center'
    $who = if ($script:HostNames.ContainsKey($dev.MAC)) { "$($script:HostNames[$dev.MAC])  -  $($dev.IP)" } else { $dev.IP }
    Draw-Text $g $who $Font.Bold $Col.Text 16 246 ($w - 32) 20 'Center'
    foreach ($b in @(@{ T = "It's mine"; A = 'mine'; X = 16; C = $Col.Green }, @{ T = 'Block...'; A = 'block'; X = 156; C = $Col.Red })) {
        $r = RF $b.X 284 128 34
        Fill-Round $g $r 10 ([Drawing.Color]::FromArgb($(if ($script:PopHover -eq $b.A) { 150 } else { 95 }), $b.C))
        Draw-Text $g $b.T $Font.Bold $Col.Text $b.X 291 128 20 'Center'
        $script:PopHits.Add([pscustomobject]@{ Rect = $r; Action = $b.A })
    }
}
function Update-PopCard {
    if ($script:PopCardBmp) { $script:PopCardBmp.Dispose() }
    $bmp = New-Object Drawing.Bitmap($PopWs, $PopHs, [Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $g = [Drawing.Graphics]::FromImage($bmp); $g.Clear([Drawing.Color]::Transparent)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    try { Draw-PopCard $g } catch {} finally { $g.Dispose() }
    $script:PopCardBmp = $bmp
}
function Render-Pop {
    $p = $script:Pop
    if (-not $p -or -not $p.Visible -or -not $script:PopCardBmp) { return }
    $bmp = New-Object Drawing.Bitmap($PopWs, $PopHs, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp); $g.Clear([Drawing.Color]::Transparent)
    $g.DrawImageUnscaled($script:PopCardBmp, 0, 0)
    $img = $script:PopImg
    if ($img) {
        $fr = $script:PopFrames
        if ($fr -and $fr.Count -gt 1 -and $fr.Total -gt 0) {   # our own GIF clock: frame by elapsed time
            $t = $script:PopClock.ElapsedMilliseconds % $fr.Total; $i = 0; $acc = 0
            while ($i -lt $fr.Count - 1) { $acc += $fr.Delays[$i]; if ($t -lt $acc) { break }; $i++ }
            [void]$img.SelectActiveFrame($fr.Dim, $i)
        }
        $amp = $script:PopAmp; $sc = 1 + 0.14 * $amp
        $bw = 178 * $sc * $PS; $bh = 167 * $sc * $PS
        $bx = ($PopWs - $bw) / 2 + $script:PopJx; $by = 205 * $PS - $bh - 6 * $amp + $script:PopJy
        $r = [math]::Min($bw / $img.Width, $bh / $img.Height); $dw = $img.Width * $r; $dh = $img.Height * $r
        $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
        $g.DrawImage($img, (RF ($bx + ($bw - $dw) / 2) ($by + ($bh - $dh) / 2) $dw $dh))
    }
    $g.Dispose()
    try { $p.SetBitmap($bmp) } catch {}
    $bmp.Dispose()
}
function Set-PopImage($img) {
    $script:PopImg = $img; $script:PopFrames = $null
    if ($img) {
        try {
            $fd = New-Object Drawing.Imaging.FrameDimension($img.FrameDimensionsList[0])
            $n = $img.GetFrameCount($fd); $d = @()
            if ($n -gt 1) {
                $raw = $img.GetPropertyItem(0x5100).Value
                for ($i = 0; $i -lt $n; $i++) { $v = [BitConverter]::ToInt32($raw, $i * 4) * 10; if ($v -le 0) { $v = 100 }; $d += $v }
            }
            $script:PopFrames = @{ Dim = $fd; Count = $n; Delays = $d; Total = [int](($d | Measure-Object -Sum).Sum) }
        } catch {}
    }
    $script:PopClock = [Diagnostics.Stopwatch]::StartNew()
}
$script:PopAmp = 0.0; $script:PopJx = 0; $script:PopJy = 0; $script:PopCardBmp = $null; $script:PopImg = $null
function New-JoinPopup {
    $p = New-Object WifiWidget.LayeredForm
    $p.Text = 'New device'
    $p.ClientSize = New-Object Drawing.Size($PopWs, $PopHs)
    $p.Add_MouseMove({
        param($s, $e)
        $hit = $null; foreach ($h in $script:PopHits) { if ($h.Rect.Contains([single]($e.X / $PS), [single]($e.Y / $PS))) { $hit = $h.Action } }
        if ($hit -ne $script:PopHover) { $script:PopHover = $hit; $script:Pop.Cursor = $(if ($hit) { 'Hand' } else { 'Default' }); Update-PopCard; Render-Pop }
        $popTimer.Stop(); $popTimer.Start()      # keep it open while the mouse is on it
    })
    $p.Add_MouseUp({
        param($s, $e)
        $hit = $null; foreach ($h in $script:PopHits) { if ($h.Rect.Contains([single]($e.X / $PS), [single]($e.Y / $PS))) { $hit = $h.Action } }
        switch ($hit) {
            'close' { Close-JoinAlert }
            'mine'  { $m = $script:PopMac; Close-JoinAlert; Show-Rename (Get-DevForMac $m) }
            'block' { $m = $script:PopMac; Show-BlockHelp $m }
            'fulltest' { Close-JoinAlert; Start-Process (Join-Path $Root 'Run-WiFi-Analyzer.bat') }
            'retest'   { Close-JoinAlert; Request-ManualSpeedTest }
        }
    })
    $p.Add_FormClosing({ param($s, $e) $e.Cancel = $true; Close-JoinAlert })
    return $p
}
function Get-VoiceEnvelope([string]$audio) {
    # Loudness of the clip every 40 ms (0..1), cached next to the audio as <name>.env.json. Needs ffmpeg once.
    $json = [IO.Path]::ChangeExtension($audio, '.env.json')
    $ff = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
    if ((Test-Path $json) -and (-not $ff -or (Get-Item $json).LastWriteTime -ge (Get-Item $audio).LastWriteTime)) {
        try { return [double[]](Get-Content $json -Raw | ConvertFrom-Json) } catch {}   # shipped file: used even without ffmpeg
    }
    if (-not $ff) { return $null }
    $tmp = [IO.Path]::GetTempFileName()
    & $ff -v quiet -y -i $audio -ac 1 -ar 8000 -f s16le $tmp
    $bytes = [IO.File]::ReadAllBytes($tmp); Remove-Item $tmp -ErrorAction SilentlyContinue
    $n = [int]($bytes.Length / 2); $win = 320
    $env = New-Object System.Collections.Generic.List[double]
    for ($i = 0; $i -lt $n; $i += $win) {
        $sum = 0.0; $c = 0
        for ($j = $i; $j -lt [math]::Min($n, $i + $win); $j++) { $s = [BitConverter]::ToInt16($bytes, $j * 2); $sum += [double]$s * $s; $c++ }
        $env.Add([math]::Sqrt($sum / [math]::Max(1, $c)))
    }
    $max = 1.0; foreach ($v in $env) { if ($v -gt $max) { $max = $v } }
    $out = @(); $prev = 0.0
    foreach ($v in $env) { $x = [math]::Min(1.0, $v / ($max * 0.8)); $prev = [math]::Max($x, $prev * 0.6); $out += [math]::Round($prev, 3) }   # fast attack, soft release
    try { ConvertTo-Json @($out) -Compress | Set-Content -Encoding UTF8 $json } catch {}
    return [double[]]$out
}
$script:VoiceEnv = $null
$script:PopAmp = 0.0
$script:Rand = New-Object System.Random
$talkTimer = New-Object Windows.Forms.Timer; $talkTimer.Interval = 33
$talkTimer.Add_Tick({
    if (-not $script:Pop -or -not $script:Pop.Visible) { $talkTimer.Stop(); return }
    $amp = 0.0
    if ($script:Player -and $script:VoiceEnv) {
        $idx = [int][math]::Floor($script:Player.Position.TotalSeconds / 0.04)
        if ($idx -ge 0 -and $idx -lt $script:VoiceEnv.Count) { $amp = $script:VoiceEnv[$idx] }
    }
    $amp = [math]::Max($amp, $script:PopAmp * 0.7)          # ease back to rest
    if ($amp -lt 0.01) { $amp = 0.0 }
    $script:PopAmp = $amp
    $script:PopJx = 0; $script:PopJy = 0
    if ($amp -gt 0.45) { $script:PopJx = $script:Rand.Next(-3, 4) * $amp; $script:PopJy = $script:Rand.Next(-2, 3) * $amp }   # panic shake on loud words
    Render-Pop
})
function Get-ClipNames([string]$kind) {
    if ($kind -eq 'New') { @('new-device') } elseif ($kind -eq 'Slow') { @('slow-wifi') } elseif ($kind -eq 'Fast') { @('fast-wifi') } else { @('rejoin', 'new-device') }
}
function Find-Clip([string]$prefix, [string]$kind) {
    foreach ($n in (Get-ClipNames $kind)) { foreach ($ext in 'mp3', 'wav', 'm4a') { $f = Join-Path $Root "assets\$prefix$n.$ext"; if (Test-Path $f) { return $f } } }
    return $null
}
function Select-AlertChar([string]$kind) {
    $ch = [string]$script:Cfg.Character
    if ($ch -eq 'Dhamu' -and -not $script:CharImg.Dhamu) { $ch = 'Ambaan' }   # no Dhamu image: fall back. No Dhamu clip for this alert: Dhamu shows, plays a short beep
    $script:PopChar = $ch; $script:PopCharKind = $kind
}
function Set-PopChar {
    $img = $script:CharImg[$script:PopChar]; if (-not $img) { $img = $script:AlertImg }
    # A per-alert GIF (e.g. assets\dhamu-new-device.gif) is timed to its voice clip: load it fresh so it starts from frame 1.
    if ($script:PopChar -eq 'Dhamu') {
        foreach ($n in (Get-ClipNames $script:PopCharKind)) {
            $gf = Join-Path $Root "assets\dhamu-$n.gif"
            if (Test-Path $gf) {
                try {
                    $fresh = [Drawing.Image]::FromStream((New-Object IO.MemoryStream(,[IO.File]::ReadAllBytes($gf))))
                    Set-PopImage $fresh
                    if ($script:TimedImg) { $script:TimedImg.Dispose() }
                    $script:TimedImg = $fresh; return
                } catch {}
            }
        }
    }
    Set-PopImage $img
}
function Play-AlertVoice([string]$kind) {
    Select-AlertChar $kind
    if (-not $script:Cfg.Voice -or $kind -eq 'Ok') { return }   # voice switched off in Settings
    # Drop your clips in assets\: new-device.mp3 (first time) and rejoin.mp3 (unnamed device back). .wav also works.
    # slow-wifi.mp3 for the slow-speed alert (no fallback to the device clip: the words would be wrong).
    $file = Find-Clip $(if ($script:PopChar -eq 'Dhamu') { 'dhamu-' } else { '' }) $kind
    if (-not $file) { try { [System.Media.SystemSounds]::Exclamation.Play() } catch {}; return }
    try {
        if (-not $script:Player) { Add-Type -AssemblyName PresentationCore; $script:Player = New-Object System.Windows.Media.MediaPlayer }
        $script:VoiceEnv = Get-VoiceEnvelope $file
        $script:Player.Stop(); $script:Player.Open([uri]$file); $script:Player.Volume = 1.0; $script:Player.Play()
        if ($script:VoiceEnv) { $talkTimer.Start() }
    } catch { try { [System.Media.SystemSounds]::Exclamation.Play() } catch {} }
}
function Show-JoinAlert([string]$mac, [string]$kind, [bool]$preview = $false) {
    Play-AlertVoice $kind
    $script:PopMac = $mac; $script:PopKind = $kind; $script:PopHover = $null
    if (-not $script:Pop) { $script:Pop = New-JoinPopup }
    Set-PopChar
    $script:Pop.Location = Get-PopLocation
    $script:Pop.Show(); Update-PopCard; Render-Pop; $talkTimer.Start()
    $popTimer.Stop(); $popTimer.Start()
    # also log to Windows notification history
    $dev = Get-DevForMac $mac
    $tray.BalloonTipTitle = $(if ($kind -eq 'New') { 'New device joined your Wi-Fi' } else { 'Unnamed device joined your Wi-Fi' })
    $tray.BalloonTipText = "$($dev.IP) connected. Open the widget to name it or block it."
    if (-not $preview) { $tray.ShowBalloonTip(5000) }
}
function Show-PopCard([string]$kind) {
    if ($kind -eq 'Ok') { Select-AlertChar $kind }
    $script:PopKind = $kind; $script:PopHover = $null
    if (-not $script:Pop) { $script:Pop = New-JoinPopup }
    Set-PopChar
    $script:Pop.Location = Get-PopLocation
    $script:Pop.Show(); Update-PopCard; Render-Pop; $talkTimer.Start()
    $popTimer.Stop(); $popTimer.Start()
}

# ---------- slow Wi-Fi watch ----------
# Every 60 s: 4 pings over Wi-Fi (tiny). Every SpeedEvery min, or when pings look bad: 5 MB quick download over Wi-Fi.
# Slow = below SlowMbps twice in a row (re-test after 20 s). Then the popup + slow-wifi.mp3, at most once per 15 min.
$script:Speed = @{ Last = $null; LastAt = $null; Usual = $null; Hist = New-Object System.Collections.Generic.List[double]
                   Job = $null; PingJob = $null; NextTest = (Get-Date).AddMinutes(2); NextPing = (Get-Date).AddSeconds(30)
                   SlowCount = 0; LastAlert = [datetime]::MinValue; PingAvg = $null; PingLoss = $null; Diag = ''; Via = '' }
$SpeedCode = @'
param($ip, $bytes)
$curl = Join-Path $env:SystemRoot 'System32\curl.exe'
# Cloudflare sometimes answers 403 when tests come close together: that is a refusal, not slow internet.
# So retry smaller; only "000" (no connection at all) counts as no internet.
$code = '000'
foreach ($size in @($bytes, 5000000, 2000000)) {
    $a = @('-s', '-o', 'NUL', '-w', '%{speed_download} %{http_code}', '--max-time', '20')
    if ($ip) { $a += @('--interface', $ip) }
    $a += ("https://speed.cloudflare.com/__down?bytes=$size&r=" + [guid]::NewGuid().ToString('N'))
    $r = "$(& $curl @a)".Trim().Split(' ')
    $code = if ($r.Count -ge 2) { $r[1] } else { '000' }
    if ($code -eq '200') { return [pscustomobject]@{ Mbps = [math]::Round([double]$r[0] * 8 / 1e6, 1); Ok = $true; Refused = $false } }
    if ($code -eq '000') { return [pscustomobject]@{ Mbps = $null; Ok = $false; Refused = $false } }
    Start-Sleep -Seconds 2
}
[pscustomobject]@{ Mbps = $null; Ok = $false; Refused = $true }
'@
$PingCode = @'
param($ip)
$a = @('-n', '4', '-w', '1000'); if ($ip) { $a += @('-S', $ip) }; $a += '1.1.1.1'
$t = (& ping.exe @a) -join "`n"
$loss = if ($t -match '\((\d+)% loss\)') { [int]$Matches[1] } else { 100 }
$avg = if ($t -match 'Average = (\d+)ms') { [int]$Matches[1] } else { $null }
[pscustomobject]@{ Avg = $avg; Loss = $loss }
'@
function Get-WifiIp {
    if (-not $script:Wifi.Connected -or -not $script:Wifi.Name) { return $null }
    $a = Get-NetIPAddress -InterfaceAlias $script:Wifi.Name -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($a) { return $a.IPAddress } else { return $null }
}
function Start-SpeedTest([bool]$manual = $false) {
    if ($script:Speed.Job) { if ($manual) { $script:Speed.Manual = $true }; return }
    $ip = Get-WifiIp
    $script:Speed.Via = $(if ($ip) { 'Wi-Fi' } else { 'cable' })
    $script:Speed.Manual = $manual
    $bytes = if ($manual) { 10000000 } else { 5000000 }      # button: bigger file = truer number; background: light
    $ps = [powershell]::Create(); [void]$ps.AddScript($SpeedCode).AddArgument($ip).AddArgument($bytes)
    $script:Speed.Job = [pscustomobject]@{ PS = $ps; Handle = $ps.BeginInvoke() }
    if ($form.Visible) { $form.Invalidate() }
}
function Request-ManualSpeedTest {
    if (-not $form.Visible) { Show-Widget }
    Start-SpeedTest $true
}
function Show-FastAlert {
    Play-AlertVoice 'Fast'
    Show-PopCard 'Fast'
}
function Start-PingProbe {
    if ($script:Speed.PingJob) { return }
    $ps = [powershell]::Create(); [void]$ps.AddScript($PingCode).AddArgument((Get-WifiIp))
    $script:Speed.PingJob = [pscustomobject]@{ PS = $ps; Handle = $ps.BeginInvoke() }
}
function Get-SlowDiagnosis {
    $sig = $script:Wifi.Signal
    if ($null -ne $sig -and $sig -lt 40) { return "Weak Wi-Fi signal ($sig%). Move closer to the router or switch to 5 GHz." }
    if ($null -ne $script:RouterMs -and $script:RouterMs -gt 50) { return "The router is slow to answer ($($script:RouterMs) ms). Too many devices busy, or restart the router." }
    if ($null -ne $sig) { return "Signal is fine ($sig%) and the router answers quickly, so the internet line itself is slow. If it lasts, contact your internet provider." }
    return 'The internet line itself looks slow.'
}
function Get-FastDetail {
    $bits = @()
    if ($null -ne $script:Wifi.Signal) { $bits += "Signal $($script:Wifi.Signal)%" }
    if ($null -ne $script:RouterMs) { $bits += "router $($script:RouterMs) ms" }
    if ($null -ne $script:Speed.PingAvg) { $bits += "internet $($script:Speed.PingAvg) ms" }
    $bits += "tested over $($script:Speed.Via)"
    return ($bits -join '  -  ')
}
function Update-SlowWatch {
    $sp = $script:Speed; $now = Get-Date
    if ($sp.PingJob -and $sp.PingJob.Handle.IsCompleted) {
        $r = Receive-Bg $sp.PingJob; $sp.PingJob = $null
        if ($r) {
            $sp.PingAvg = $r.Avg; $sp.PingLoss = $r.Loss
            if (($r.Loss -ge 50 -or ($r.Avg -and $r.Avg -gt 250)) -and $now -lt $sp.NextTest) { $sp.NextTest = $now }   # looks bad: test now
        }
    }
    if ($sp.Job -and $sp.Job.Handle.IsCompleted) {
        $r = Receive-Bg $sp.Job; $sp.Job = $null
        if ($r -and $r.Refused) {
            # Test server said no: we learned nothing, so don't call it slow. Try again later.
            if ($sp.Manual) { $sp.Manual = $false; $tray.BalloonTipTitle = 'Speed test'; $tray.BalloonTipText = 'The test server is busy. Try again in a minute.'; $tray.ShowBalloonTip(4000) }
            $sp.NextTest = $now.AddMinutes(3)
            return
        }
        $mbps = if ($r -and $r.Ok) { $r.Mbps } else { $null }
        $sp.Last = $mbps; $sp.LastAt = $now
        $limit = [double]$script:Cfg.SlowMbps
        $isSlow = ($null -eq $mbps -or $mbps -lt $limit)
        if (-not $isSlow) {
            $sp.Hist.Add($mbps); while ($sp.Hist.Count -gt 12) { $sp.Hist.RemoveAt(0) }
            $sorted = @($sp.Hist | Sort-Object); $sp.Usual = [math]::Round($sorted[[int][math]::Floor($sorted.Count / 2)])
        }
        $sp.Diag = $(if ($isSlow) { Get-SlowDiagnosis } else { Get-FastDetail })
        if ($sp.Manual) {
            # You pressed Speed test: always answer, slow voice or happy voice.
            $sp.Manual = $false
            Write-Alert 'Speed test' ("{0} Mbps via {1}" -f $mbps, $sp.Via)
            if ($isSlow) { $sp.WasSlow = $true; Show-SlowAlert }
            elseif ($mbps -ge [double]$script:Cfg.FastMbps) { $sp.WasSlow = $false; $sp.SlowCount = 0; Show-FastAlert }
            else { $sp.WasSlow = $false; $sp.SlowCount = 0; Show-PopCard 'Ok' }                  # between the two limits
            $sp.NextTest = $now.AddMinutes((Get-SpeedEvery))
        }
        elseif ($isSlow) {
            $sp.SlowCount++
            if ($sp.SlowCount -eq 1) { $sp.NextTest = $now.AddSeconds(20) }          # confirm before shouting
            elseif (($now - $sp.LastAlert).TotalMinutes -ge 15) {
                $sp.LastAlert = $now; $sp.WasSlow = $true
                Write-Alert 'Slow Wi-Fi' ("{0} Mbps via {1}; signal {2}%; router {3} ms" -f $mbps, $sp.Via, $script:Wifi.Signal, $script:RouterMs)
                Show-SlowAlert
                $sp.NextTest = $now.AddMinutes(5)
            } else { $sp.NextTest = $now.AddMinutes(5) }
        } else {
            $sp.SlowCount = 0
            # Happy voice only at or above the good-speed limit (Settings): every time, once a day / back after slow, or never.
            $today = $now.ToString('yyyy-MM-dd')
            if ($mbps -ge [double]$script:Cfg.FastMbps -and $script:Cfg.FastMode -ne 'Off') {
                if ($script:Cfg.FastMode -eq 'Every' -or $sp.WasSlow -or $script:Cfg.LastFastDay -ne $today) {
                    $sp.WasSlow = $false; $script:Cfg.LastFastDay = $today; Save-Settings
                    Show-FastAlert
                }
            }
            $sp.NextTest = $now.AddMinutes((Get-SpeedEvery))
        }
    }
    if (-not $sp.PingJob -and $now -ge $sp.NextPing) { $sp.NextPing = $now.AddSeconds(60); Start-PingProbe }
    if (-not $sp.Job -and $now -ge $sp.NextTest) { $sp.NextTest = $now.AddMinutes((Get-SpeedEvery)); Start-SpeedTest }
}
function Show-SlowAlert {
    Play-AlertVoice 'Slow'
    Show-PopCard 'Slow'
    $tray.BalloonTipTitle = 'Wi-Fi is slow'
    $mb = $script:Speed.Last
    $tray.BalloonTipText = $(if ($null -ne $mb) { "Speed test: $mb Mbps. $($script:Speed.Diag)" } else { 'No internet response.' })
    $tray.ShowBalloonTip(5000)
}

$popTimer = New-Object Windows.Forms.Timer; $popTimer.Interval = 20000   # auto-hide after 20 s without mouse
$popTimer.Add_Tick({ Close-JoinAlert })

# ---------- form ----------
$form = New-Object WifiWidget.SmoothForm
$form.Text = 'Wi-Fi Analyzer Pro'
$form.BackColor = $Col.Top
$form.Opacity = 0.0
$form.ClientSize = New-Object Drawing.Size($W, $(if ($script:Cfg.Expanded) { $HExpanded } else { $HCompact }))

function Get-CornerPoint([int]$h) {
    $wa = [Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    if ($null -ne $script:Cfg.ScreenX) { $wa = [Windows.Forms.Screen]::FromPoint((New-Object Drawing.Point($script:Cfg.ScreenX, $script:Cfg.ScreenY))).WorkingArea }
    $x = if ($script:Cfg.Corner -like '*Left') { $wa.Left + $Margin } else { $wa.Right - $W - $Margin }
    $y = if ($script:Cfg.Corner -like 'Top*') { $wa.Top + $Margin } else { $wa.Bottom - $h - $Margin }
    return New-Object Drawing.Point($x, $y)
}
$script:Slide = $null
$script:Fade = 0
function Move-ToCorner([bool]$animate = $true) {
    $target = Get-CornerPoint $form.Height
    if (-not $animate) { $form.Location = $target; return }
    $script:Slide = @{ From = $form.Location; To = $target; Step = 0 }
    $animTimer.Start()
}
function Set-Expanded([bool]$on) {
    $script:Cfg.Expanded = $on; Save-Settings
    $form.ClientSize = New-Object Drawing.Size($W, $(if ($on) { $HExpanded } else { $HCompact }))
    Move-ToCorner $false
    if ($on -and -not $script:Dns) { Request-Dns }
    $form.Invalidate()
}
function Show-Widget { $form.Show(); Move-ToCorner $false; $form.Activate(); $script:Fade = 1; $animTimer.Start() }
function Hide-Widget { $script:Fade = -1; $animTimer.Start() }
function Toggle-Widget { if ($form.Visible -and $script:Fade -ge 0) { Hide-Widget } else { Show-Widget } }

# ---------- painting ----------
$form.Add_Paint({
    param($s, $e)
    $g = $e.Graphics
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'ClearTypeGridFit'
    $w = $form.ClientSize.Width; $h = $form.ClientSize.Height
    $script:Hits.Clear()

    # background: indigo gradient + two soft glows
    $bg = New-Object Drawing.Drawing2D.LinearGradientBrush((RF 0 0 $w $h), $Col.Top, $Col.Bottom, [single]65)
    $g.FillRectangle($bg, 0, 0, $w, $h); $bg.Dispose()
    $gb = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(26, $Col.Sky)); $g.FillEllipse($gb, $w - 150, -90, 240, 190); $gb.Dispose()
    $gb = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(22, $Col.Lilac)); $g.FillEllipse($gb, -80, $h - 140, 230, 200); $gb.Dispose()
    $bp = New-Object Drawing.Pen([Drawing.Color]::FromArgb(40, 255, 255, 255)); $g.DrawRectangle($bp, 0, 0, $w - 1, $h - 1); $bp.Dispose()

    # header buttons (expand / hide)
    $glyph = if ($script:Cfg.Expanded) { [string][char]0x25B4 } else { [string][char]0x25BE }
    $bolt = if ($script:Speed.Job) { [string][char]0x231B } else { [string][char]0x26A1 }    # hourglass while testing, lightning otherwise
    $hdrBtns = @(@{ X = $w - 116; T = [string][char]0x2699; A = 'settings' }, @{ X = $w - 88; T = $bolt; A = 'speedtest' }, @{ X = $w - 60; T = $glyph; A = 'toggle' }, @{ X = $w - 32; T = [string][char]0x2715; A = 'hide' })
    foreach ($btn in $hdrBtns) {
        $r = RF $btn.X 10 24 24
        if ($script:Hover -and $script:Hover.Action -eq $btn.A) { Fill-Round $g $r 12 ([Drawing.Color]::FromArgb(50, 255, 255, 255)) }
        Draw-Text $g $btn.T $Font.Icon $Col.Muted $btn.X 11 24 22 'Center'
        Add-Hit $r $btn.A
    }

    # signal gauge
    $wf = $script:Wifi
    $gr = RF 16 18 84 84
    $track = New-Object Drawing.Pen([Drawing.Color]::FromArgb(34, 255, 255, 255), 8); $track.StartCap = 'Round'; $track.EndCap = 'Round'
    $g.DrawArc($track, $gr, 135, 270); $track.Dispose()
    $sig = if ($null -ne $wf.Signal) { $wf.Signal } else { 0 }
    if ($sig -gt 0) {
        $lg = New-Object Drawing.Drawing2D.LinearGradientBrush($gr, $Col.Sky, $Col.Lilac, [single]0)
        $vp = New-Object Drawing.Pen($lg, 8); $vp.StartCap = 'Round'; $vp.EndCap = 'Round'
        $g.DrawArc($vp, $gr, 135, [single](270 * $sig / 100)); $vp.Dispose(); $lg.Dispose()
    }
    $gt = if ($wf.Connected) { "$sig%" } else { 'LAN' }
    Draw-Text $g $gt $Font.Gauge $Col.Text 16 38 84 30 'Center'
    Draw-Text $g $(if ($wf.Connected) { 'signal' } else { 'cable' }) $Font.Small $Col.Muted 16 66 84 18 'Center'

    # name + band
    Draw-Text $g $wf.SSID $Font.Title $Col.Text 112 12 ($w - 234) 24
    $sub = if ($wf.Connected) { "$($wf.Band)  ch $($wf.Channel)" } else { 'Wi-Fi not connected' }
    Draw-Text $g $sub $Font.Small $Col.Muted 112 34 200 18

    # live speed
    $dt = [string][char]0x2193 + ' ' + ('{0:0.0}' -f $script:Down)
    Draw-Text $g $dt $Font.Big $Col.Sky 110 50 130 34
    $dx = 112 + (Get-TextWidth $g $dt $Font.Big) + 2
    $ut = [string][char]0x2191 + ' ' + ('{0:0.0}' -f $script:Up)
    Draw-Text $g $ut $Font.Big $Col.Lilac $dx 50 130 34
    $mx = $dx + (Get-TextWidth $g $ut $Font.Big)
    Draw-Text $g 'Mbps' $Font.Small $Col.Muted $mx 62 40 16

    # status chips
    $x = 112
    $rt = if ($null -ne $script:RouterMs) { "Router $($script:RouterMs) ms" } else { 'Router --' }
    $rc = if ($null -eq $script:RouterMs) { $Col.Red } elseif ($script:RouterMs -gt 30) { $Col.Amber } else { $Col.Green }
    $x = Draw-Chip $g $rt $x 92 $rc
    $onlineDevs = @($script:Devices | Where-Object { $_.Online })
    $unnamed = @($onlineDevs | Where-Object { -not $_.Named }).Count
    $dtxt = if ($script:Devices.Count -eq 0) { 'Devices ...' } elseif ($unnamed) { "$unnamed unnamed" } else { "$($onlineDevs.Count) online" }
    $x = Draw-Chip $g $dtxt $x 92 $(if ($unnamed) { $Col.Amber } else { $Col.Green }) 'expand'
    $dn = $script:Dns
    $dnsTxt = if (-not $dn) { 'DNS ...' } elseif ($dn.Shield -eq 'On') { 'Shield ON' } else { 'Shield OFF' }
    $dnsCol = if (-not $dn) { $Col.Muted } elseif ($dn.Status -eq 'Good') { $Col.Green } elseif ($dn.Status -eq 'Bad') { $Col.Red } else { $Col.Amber }
    $null = Draw-Chip $g $dnsTxt $x 92 $dnsCol 'expand'

    if (-not $script:Cfg.Expanded) { return }

    # ----- expanded: live traffic graph -----
    $y = 128
    Fill-Round $g (RF 14 $y ($w - 28) 84) 14 ([Drawing.Color]::FromArgb(22, 255, 255, 255))
    Draw-Text $g 'LIVE TRAFFIC  -  last 2 min' $Font.Tiny $Col.Muted 26 ($y + 8) 220 16
    $peak = 1.0
    foreach ($v in $script:DownHist) { if ($v -gt $peak) { $peak = $v } }
    foreach ($v in $script:UpHist) { if ($v -gt $peak) { $peak = $v } }
    Draw-Text $g ('peak {0:0.0} Mbps' -f $peak) $Font.Tiny $Col.Muted ($w - 140) ($y + 8) 112 16 'Far'
    $gx = 24; $gy = $y + 28; $gw2 = $w - 48; $gh = 48
    foreach ($series in @(@{ D = $script:DownHist; C = $Col.Sky; Fill = $true }, @{ D = $script:UpHist; C = $Col.Lilac; Fill = $false })) {
        $data = $series.D
        if ($data.Count -lt 2) { continue }
        $pts = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
        for ($i = 0; $i -lt $data.Count; $i++) {
            $px = $gx + $gw2 * ($i + (120 - $data.Count)) / 119
            $py = $gy + $gh - ($gh * $data[$i] / $peak)
            $pts.Add((New-Object Drawing.PointF([single]$px, [single]$py)))
        }
        if ($series.Fill) {
            $poly = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
            $poly.AddRange($pts)
            $poly.Add((New-Object Drawing.PointF($pts[$pts.Count - 1].X, [single]($gy + $gh))))
            $poly.Add((New-Object Drawing.PointF($pts[0].X, [single]($gy + $gh))))
            $fb = New-Object Drawing.Drawing2D.LinearGradientBrush((RF $gx ($gy - 1) $gw2 ($gh + 2)), [Drawing.Color]::FromArgb(90, $series.C), [Drawing.Color]::FromArgb(0, $series.C), [single]90)
            $g.FillPolygon($fb, $poly.ToArray()); $fb.Dispose()
        }
        $lp = New-Object Drawing.Pen($series.C, 1.8); $g.DrawLines($lp, $pts.ToArray()); $lp.Dispose()
    }

    # ----- stat tiles -----
    $y = 222
    $tw = ($w - 28 - 16) / 3
    $tiles = @(
        @{ L = 'ROUTER'; V = $(if ($null -ne $script:RouterMs) { "$($script:RouterMs) ms" } else { '--' }) },
        @{ L = 'INTERNET'; V = $(if ($null -ne $script:NetMs) { "$($script:NetMs) ms" } else { '--' }) },
        @{ L = $(if ($script:Speed.Job) { 'SPEED  testing...' } else { 'SPEED (quick)' }); V = $(if ($null -ne $script:Speed.Last) { "$($script:Speed.Last) Mbps" } elseif ($script:Speed.LastAt) { 'no response' } else { 'soon...' }) }
    )
    for ($i = 0; $i -lt 3; $i++) {
        $tx = 14 + $i * ($tw + 8)
        $tile = $tiles[$i]
        Fill-Round $g (RF $tx $y $tw 52) 12 ([Drawing.Color]::FromArgb(22, 255, 255, 255))
        Draw-Text $g $tile.L $Font.Tiny $Col.Muted ($tx + 10) ($y + 7) ($tw - 12) 14
        Draw-Text $g $tile.V $Font.Bold $Col.Text ($tx + 10) ($y + 24) ($tw - 12) 20
        if ($i -eq 2) { Add-Hit (RF $tx $y $tw 52) 'speedtest' }    # tap the speed tile to test
    }

    # ----- DNS shield card -----
    $y = 284
    Fill-Round $g (RF 14 $y ($w - 28) 96) 14 ([Drawing.Color]::FromArgb(22, 255, 255, 255))
    Draw-Text $g 'DNS SHIELD' $Font.Tiny $Col.Muted 26 ($y + 9) 120 16
    if ($script:DnsJob) { Draw-Text $g 'checking...' $Font.Tiny $Col.Muted ($w - 130) ($y + 9) 100 16 'Far' }
    elseif ($dn) { Draw-Text $g ('checked ' + $dn.CheckedAt.ToString('HH:mm')) $Font.Tiny $Col.Muted ($w - 130) ($y + 9) 100 16 'Far' }
    if ($dn) {
        $dot = New-Object Drawing.SolidBrush($dnsCol); $g.FillEllipse($dot, 26, $y + 32, 10, 10); $dot.Dispose()
        Draw-Text $g $dn.Headline $Font.Bold $Col.Text 42 ($y + 27) ($w - 70) 20
        $detail = if (@($dn.Issues).Count) { @($dn.Issues)[0] } else { $dn.Detail }
        Draw-Text $g $detail $Font.Small $Col.Muted 26 ($y + 50) ($w - 160) 34
    } else {
        Draw-Text $g 'Checking DNS servers...' $Font.Body $Col.Muted 26 ($y + 30) 220 20
    }
    $fixLabel = if ($dn -and $dn.Status -eq 'Good') { 'Change' } else { 'Fix it' }
    Draw-Button $g $fixLabel ($w - 124) ($y + 54) 52 30 'dnsfix' $Col.Lilac
    Draw-Button $g ([string][char]0x21BB) ($w - 66) ($y + 54) 40 30 'dnscheck' $Col.Sky

    # ----- devices -----
    $y = 392
    $dcount = @($script:Devices).Count
    $live = if ($script:WatchState.Dhcp -eq 'listening') { 'live watch ON' } else { 'checks every 20s' }
    Draw-Text $g "DEVICES  -  $($onlineDevs.Count) online   |   $live" $Font.Tiny $Col.Muted 26 $y ($w - 130) 16
    $scanTxt = if ($script:Scanning) { 'scanning...' } else { 'Scan now' }
    $sr = RF ($w - 100) ($y - 3) 76 20
    if ($script:Hover -and $script:Hover.Action -eq 'scan') { Fill-Round $g $sr 10 ([Drawing.Color]::FromArgb(50, $Col.Sky)) }
    Draw-Text $g $scanTxt $Font.Tiny $Col.Sky ($w - 100) $y 76 16 'Center'
    Add-Hit $sr 'scan'

    $listTop = $y + 22; $rowH = 38; $listBottom = $h - 56
    $visible = [math]::Floor(($listBottom - $listTop) / $rowH)
    $maxScroll = [math]::Max(0, $dcount - $visible)
    if ($script:DevScroll -gt $maxScroll) { $script:DevScroll = $maxScroll }
    for ($i = $script:DevScroll; $i -lt [math]::Min($dcount, $script:DevScroll + $visible); $i++) {
        $d = $script:Devices[$i]
        $ry = $listTop + ($i - $script:DevScroll) * $rowH
        $rr = RF 14 $ry ($w - 28) ($rowH - 4)
        $hov = ($script:Hover -and $script:Hover.Action -eq 'device' -and $script:Hover.Data.MAC -eq $d.MAC)
        Fill-Round $g $rr 10 ([Drawing.Color]::FromArgb($(if ($hov) { 44 } else { 18 }), 255, 255, 255))
        $dc = if (-not $d.Online) { [Drawing.Color]::FromArgb(110, $Col.Muted) } elseif ($d.New) { $Col.Red } elseif (-not $d.Named) { $Col.Amber } elseif ($d.Name -like 'This PC*' -or $d.Name -like 'Router*') { $Col.Sky } else { $Col.Green }
        $tc = if ($d.Online) { $Col.Text } else { [Drawing.Color]::FromArgb(140, $Col.Text) }
        $db = New-Object Drawing.SolidBrush($dc); $g.FillEllipse($db, 26, $ry + 13, 9, 9); $db.Dispose()
        $nm = if ($d.Named) { $d.Name } else { 'Unnamed device' }
        Draw-Text $g $nm $Font.Bold $tc 44 ($ry + 3) ($w - 140) 18
        Draw-Text $g "$($d.IP)  -  $($d.Hint)" $Font.Small $Col.Muted 44 ($ry + 19) ($w - 110) 16
        $tag = if ($hov) { 'Rename' } elseif ($d.New) { 'NEW' } else { '' }
        Draw-Text $g $tag $Font.Tiny $(if ($d.New -and -not $hov) { $Col.Red } else { $Col.Sky }) ($w - 90) ($ry + 11) 64 16 'Far'
        Add-Hit $rr 'device' $d
    }
    if ($dcount -eq 0) { Draw-Text $g $(if ($script:Scanning) { 'Looking for devices...' } else { 'No devices yet' }) $Font.Body $Col.Muted 26 ($listTop + 8) 260 20 }
    if ($maxScroll -gt 0) { Draw-Text $g "scroll for more ($dcount total)" $Font.Tiny $Col.Muted 26 ($listBottom) 200 14 }

    # ----- footer -----
    $fy = $h - 40
    $stTxt = if ($script:Speed.Job) { 'Testing...' } else { 'Speed test' }
    Draw-Button $g $stTxt 14 $fy 118 28 'speedtest' $Col.Green
    Draw-Button $g 'Full test' 138 $fy 88 28 'analyzer' $Col.Lilac
    Draw-Button $g 'Dashboard' 232 $fy 94 28 'dashboard' $Col.Sky
})

# ---------- mouse ----------
function Find-Hit($pt) { foreach ($h in $script:Hits) { if ($h.Rect.Contains([single]$pt.X, [single]$pt.Y)) { return $h } }; return $null }
$form.Add_MouseMove({
    param($s, $e)
    if ($script:Drag) {
        $p = [Windows.Forms.Cursor]::Position
        $form.Location = New-Object Drawing.Point(($p.X - $script:Drag.X), ($p.Y - $script:Drag.Y))
        $script:Drag.Moved = $true
        return
    }
    $h = Find-Hit $e.Location
    $same = ($h -and $script:Hover -and $h.Action -eq $script:Hover.Action -and $h.Data -eq $script:Hover.Data) -or (-not $h -and -not $script:Hover)
    if (-not $same) { $script:Hover = $h; $form.Cursor = $(if ($h) { 'Hand' } else { 'Default' }); $form.Invalidate() }
})
$form.Add_MouseLeave({ if ($script:Hover) { $script:Hover = $null; $form.Invalidate() } })
$form.Add_MouseDown({
    param($s, $e)
    if ($e.Button -ne 'Left') { return }
    if (-not (Find-Hit $e.Location)) { $script:Drag = @{ X = $e.X; Y = $e.Y; Moved = $false } }
})
$form.Add_MouseUp({
    param($s, $e)
    if ($script:Drag) {
        $moved = $script:Drag.Moved; $script:Drag = $null
        if ($moved) {
            $c = New-Object Drawing.Point([int]($form.Left + $form.Width / 2), [int]($form.Top + $form.Height / 2))
            $wa = [Windows.Forms.Screen]::FromPoint($c).WorkingArea
            $v = if ($c.Y -lt ($wa.Top + $wa.Height / 2)) { 'Top' } else { 'Bottom' }
            $hz = if ($c.X -lt ($wa.Left + $wa.Width / 2)) { 'Left' } else { 'Right' }
            $script:Cfg.Corner = "$v$hz"; $script:Cfg.ScreenX = $c.X; $script:Cfg.ScreenY = $c.Y; Save-Settings
            Move-ToCorner $true
        }
        return
    }
    $h = Find-Hit $e.Location
    if (-not $h -or $e.Button -ne 'Left') { return }
    switch ($h.Action) {
        'toggle'    { Set-Expanded (-not $script:Cfg.Expanded) }
        'expand'    { if (-not $script:Cfg.Expanded) { Set-Expanded $true } }
        'hide'      { Close-JoinAlert; $tray.Visible = $false; $form.Close() }   # X closes the app (Ctrl+Alt+W still hides/shows)
        'scan'      { Request-Scan }
        'dnscheck'  { $script:Dns = $null; Request-Dns }
        'dnsfix'    { Show-DnsFix }
        'speedtest' { Request-ManualSpeedTest }
        'settings'  { Show-Settings }
        'device'    { Show-Rename $h.Data }
        'dashboard' {
            $dash = Get-ChildItem (Join-Path $DataRoot 'Reports') -Filter 'WiFiDashboard-*.html' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($dash) { Start-Process $dash.FullName } else { [Windows.Forms.MessageBox]::Show('Run "Full test" first to create a dashboard.', 'Dashboard') | Out-Null }
        }
        'analyzer'  { Start-Process (Join-Path $Root 'Run-WiFi-Analyzer.bat') }
    }
})
$form.Add_MouseWheel({
    param($s, $e)
    if (-not $script:Cfg.Expanded) { return }
    $script:DevScroll = [math]::Max(0, $script:DevScroll - [math]::Sign($e.Delta)); $form.Invalidate()
})

# ---------- tray ----------
$bmp = New-Object Drawing.Bitmap 32, 32
$ig = [Drawing.Graphics]::FromImage($bmp); $ig.SmoothingMode = 'AntiAlias'
$ib = New-Object Drawing.SolidBrush($Col.Bottom); $ig.FillEllipse($ib, 0, 0, 31, 31)
foreach ($k in 0..2) { $ip = New-Object Drawing.Pen($Col.Sky, 2.6); $sz = 8 + $k * 7; $ig.DrawArc($ip, 16 - $sz / 2, 21 - $sz / 2, $sz, $sz, 225, 90); $ip.Dispose() }
$ib2 = New-Object Drawing.SolidBrush($Col.Lilac); $ig.FillEllipse($ib2, 14, 19, 4, 4); $ig.Dispose()
$tray = New-Object Windows.Forms.NotifyIcon
$tray.Icon = [Drawing.Icon]::FromHandle($bmp.GetHicon())
$tray.Text = 'Wi-Fi Analyzer Pro - Ctrl+Alt+W to show/hide'
$tray.Visible = $true
$menu = New-Object Windows.Forms.ContextMenuStrip
[void]$menu.Items.Add('Show / hide   (Ctrl+Alt+W)', $null, { Toggle-Widget })
[void]$menu.Items.Add('Expand / collapse', $null, { if (-not $form.Visible) { Show-Widget }; Set-Expanded (-not $script:Cfg.Expanded) })
$cornerMenu = New-Object Windows.Forms.ToolStripMenuItem 'Move to corner'
foreach ($cn in 'TopLeft', 'TopRight', 'BottomLeft', 'BottomRight') {
    $it = New-Object Windows.Forms.ToolStripMenuItem $cn
    $it.Tag = $cn
    $it.Add_Click({ param($s2, $e2) $script:Cfg.Corner = $s2.Tag; $script:Cfg.ScreenX = $null; $script:Cfg.ScreenY = $null; Save-Settings; if (-not $form.Visible) { Show-Widget } else { Move-ToCorner $true } })
    [void]$cornerMenu.DropDownItems.Add($it)
}
[void]$menu.Items.Add($cornerMenu)
[void]$menu.Items.Add('Settings...', $null, { Show-Settings })
[void]$menu.Items.Add('Speed test now', $null, { Request-ManualSpeedTest })
[void]$menu.Items.Add('Run full test', $null, { Start-Process (Join-Path $Root 'Run-WiFi-Analyzer.bat') })
[void]$menu.Items.Add('-')
[void]$menu.Items.Add('Exit', $null, { $tray.Visible = $false; $form.Close() })
$tray.ContextMenuStrip = $menu
$tray.Add_MouseClick({ param($s, $e) if ($e.Button -eq 'Left') { Toggle-Widget } })
$tray.Add_BalloonTipClicked({ Show-Widget; Set-Expanded $true })

# ---------- timers ----------
$animTimer = New-Object Windows.Forms.Timer; $animTimer.Interval = 15
$animTimer.Add_Tick({
    $busy = $false
    if ($script:Fade -eq 1) {
        $form.Opacity = [math]::Min(0.97, $form.Opacity + 0.12); if ($form.Opacity -ge 0.97) { $script:Fade = 0 } else { $busy = $true }
    } elseif ($script:Fade -eq -1) {
        $form.Opacity = [math]::Max(0, $form.Opacity - 0.14)
        if ($form.Opacity -le 0) { $script:Fade = 0; $form.Hide() } else { $busy = $true }
    }
    if ($script:Slide) {
        $sl = $script:Slide; $sl.Step++
        $t = [math]::Min(1.0, $sl.Step / 14.0); $ease = 1 - [math]::Pow(1 - $t, 3)
        $form.Location = New-Object Drawing.Point([int]($sl.From.X + ($sl.To.X - $sl.From.X) * $ease), [int]($sl.From.Y + ($sl.To.Y - $sl.From.Y) * $ease))
        if ($t -ge 1) { $script:Slide = $null } else { $busy = $true }
    }
    if (-not $busy) { $animTimer.Stop() }
})

$timer = New-Object Windows.Forms.Timer; $timer.Interval = 1000
$timer.Add_Tick({
    $script:Tick++
    try {
        Receive-WatchEvents
        Update-SlowWatch
        Update-Traffic
        if ($script:Tick % 2 -eq 1) { Update-Wifi; $script:RouterMs = Get-Ping $script:Gateway 400 }
        if ($script:Tick % 10 -eq 1) { $ms = Get-Ping '1.1.1.1' 800; if ($null -eq $ms) { $ms = Get-Ping '8.8.8.8' 800 }; $script:NetMs = $ms }
        if ($script:Tick % 30 -eq 1) { $gw = Get-PrimaryGateway; if ($gw) { $script:Gateway = $gw } }
        if ((Get-Date) -ge $script:ScanDue) { $script:ScanDue = (Get-Date).AddSeconds(20); Request-Scan }
        if ((Get-Date) -ge $script:DnsDue) { $script:DnsDue = (Get-Date).AddMinutes(10); Request-Dns }
        if ($script:Scanning -and $script:ScanJob -and $script:ScanJob.Handle.IsCompleted) {
            $res = Receive-Bg $script:ScanJob; $script:Scanning = $false; Merge-Scan $res
        }
        if ($script:DnsJob -and $script:DnsJob.Handle.IsCompleted) {
            $res = Receive-Bg $script:DnsJob; $script:DnsJob = $null
            if ($res) {
                $prev = $script:Dns; $script:Dns = $res
                if ($res.Status -ne 'Good' -and (-not $prev -or $prev.Status -eq 'Good')) {
                    $tray.BalloonTipTitle = 'DNS Shield'; $tray.BalloonTipText = $res.Headline; $tray.ShowBalloonTip(6000)
                }
            }
        }
    } catch {}
    if ($form.Visible) { $form.Invalidate() }
})

$form.add_HotKeyPressed({ Toggle-Widget })
$form.Add_Shown({
    if (-not $form.RegisterToggleKey(0x4003, 0x57)) { $tray.Text = 'Wi-Fi Analyzer Pro - click to show/hide' }   # Ctrl+Alt+W
    Move-ToCorner $false; $script:Fade = 1; $animTimer.Start(); $timer.Start()
    if (-not $script:Cfg.AutoStartAsked) {
        $script:Cfg.AutoStartAsked = $true; Save-Settings
        $tray.BalloonTipTitle = 'Ambaan will start with Windows'
        $tray.BalloonTipText = 'You can turn this off in Settings (the gear icon on the widget).'
        $tray.ShowBalloonTip(8000)
    }
    $gw = $script:Gateway; if ($gw) { $script:WatchState.Prefix = (($gw.Split('.'))[0..2] -join '.') + '.' }
    Start-Watcher
})
$form.Add_FormClosing({ param($s, $e) if ($tray.Visible) { $e.Cancel = $true; Hide-Widget } })
$form.Add_FormClosed({ $script:WatchState.Stop = $true; $timer.Stop(); $tray.Visible = $false; $tray.Dispose(); try { $script:Mutex.ReleaseMutex() } catch {} })

[void][Windows.Forms.Application]::Run($form)
