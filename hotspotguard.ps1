# ============================================================================
# hotspotguard.ps1 - 屏幕触发热点守护程序
# ----------------------------------------------------------------------------
# 功能:
#   1. 检测目标屏幕(如 DELF145 / Dell E2423H)是否已连接
#   2. 目标屏连接   -> 自动开启 Windows 移动热点, 然后退出
#   3. 其他外接屏   -> 不做任何操作, 直接退出(不碰热点)
#   4. 托盘图标 + 事件驱动监听(占用低), 运行满 N 分钟自动退出
#   5. 图形化界面配置目标屏幕 (--config)
#   6. 支持纯命令行模式
#
# 用法:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File hotspotguard.ps1            # 托盘监控(默认)
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File hotspotguard.ps1 -Mode once # 检测一次并执行后退出
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File hotspotguard.ps1 -Mode status
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File hotspotguard.ps1 -Mode config
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File hotspotguard.ps1 -Mode start
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File hotspotguard.ps1 -Mode stop
#   提权操作(watch/once/start/stop)需要管理员权限, 非提权运行时自动请求 UAC。
# ============================================================================

param(
    [ValidateSet('watch', 'once', 'status', 'config', 'start', 'stop', 'help')]
    [string]$Mode = 'watch'
)

$ErrorActionPreference = 'Continue'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$LogFile = Join-Path $ScriptDir 'hotspotguard.log'
$ConfigFile = Join-Path $ScriptDir 'hotspotguard.json'

# ---------------------------------------------------------------------------
# 日志
# ---------------------------------------------------------------------------
function Write-Log {
    param([string]$Message)
    $line = '[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    try { Add-Content -Path $LogFile -Value $line -Encoding UTF8 } catch {}
    # 用 Write-Host 输出(不污染函数返回值/输出流, 避免 $r = Invoke-Evaluate 捕获日志行)
    Write-Host $line
}

# ---------------------------------------------------------------------------
# 通知(右下角气泡)
# ---------------------------------------------------------------------------
$script:Exiting = $false       # 正在退出, 停止重复判定

function Wait-NotificationDisplay {
    param([int]$Seconds = 5)
    # 保持消息泵运行, 让气泡通知能显示出来(阻塞式 Start-Sleep 会挡住消息循环)
    $end = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $end) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 100
    }
}

function Send-Notification {
    param([string]$Title, [string]$Text)
    try {
        if ($script:NotifyIcon) {
            # 托盘模式: 复用托盘图标的气泡
            $script:NotifyIcon.BalloonTipTitle = $Title
            $script:NotifyIcon.BalloonTipText = $Text
            $script:NotifyIcon.BalloonTipIcon = [System.Windows.Forms.ToolTipIcon]::Info
            $script:NotifyIcon.ShowBalloonTip(5000)
        } else {
            # 命令行模式: 临时创建一个托盘图标来弹气泡
            $tmp = New-Object System.Windows.Forms.NotifyIcon
            $tmp.Icon = [System.Drawing.SystemIcons]::Information
            $tmp.Visible = $true
            $tmp.BalloonTipTitle = $Title
            $tmp.BalloonTipText = $Text
            $tmp.BalloonTipIcon = [System.Windows.Forms.ToolTipIcon]::Info
            $tmp.ShowBalloonTip(5000)
            Wait-NotificationDisplay 5
            $tmp.Visible = $false
            $tmp.Dispose()
        }
        Write-Log ('通知: ' + $Title + ' - ' + $Text)
    } catch {
        Write-Log ('通知发送失败: ' + $_.Exception.Message)
    }
}

# ---------------------------------------------------------------------------
# 配置
# ---------------------------------------------------------------------------
$script:DefaultConfig = @{
    TargetPnP            = 'DELF145'   # 触发屏幕: PnP ID 或名称片段, 如 'DELF145' / 'E2423H'
    StartOnTarget        = $true       # 检测到目标屏 -> 开热点并退出
    ExitOnOtherExternal  = $true       # 检测到其他外接屏 -> 不做任何操作, 直接退出
    StopWhenNoTarget     = $false      # 目标屏不在(含无外接屏) -> 也关热点并退出(默认 false 不主动关)
    IgnoreIntegrated     = $true       # 忽略笔记本内置屏(不算"其他外接屏")
    MaxRunMinutes        = 5           # 最多运行 N 分钟后自动退出
    CheckIntervalSeconds = 10          # 兜底轮询间隔(设备事件之外的保险)
}

function Get-Config {
    $cfg = $script:DefaultConfig.Clone()
    if (Test-Path $ConfigFile) {
        try {
            $saved = Get-Content $ConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($k in $saved.PSObject.Properties) { $cfg[$k.Name] = $k.Value }
        } catch { Write-Log ("读取配置失败, 使用默认配置: " + $_.Exception.Message) }
    }
    return $cfg
}

function Save-Config {
    param($Cfg)
    $Cfg | ConvertTo-Json | Set-Content -Path $ConfigFile -Encoding UTF8
    Write-Log ("配置已保存: " + $ConfigFile)
}

# ---------------------------------------------------------------------------
# 原生辅助: SetupAPI 枚举显示器 + 设备变化监听窗口 (C# 内嵌, 仅 P/Invoke, 无 WinRT)
# ---------------------------------------------------------------------------
$script:NativeReady = $false
try {
    Add-Type -ReferencedAssemblies 'System.Core', 'System.Windows.Forms', 'System.Drawing' -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

public class NativeMonitor
{
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, string Enumerator, IntPtr hwndParent, uint Flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, StringBuilder buf, uint len, out uint need);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint Property, out uint RegDataType, StringBuilder buf, uint len, out uint need);
    [DllImport("cfgmgr32.dll", SetLastError = true)]
    private static extern int CM_Get_DevNode_Status(out uint ulStatus, out uint ulProblemNumber, uint dnDevInst, uint ulFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    private static Guid GUID_MONITOR = new Guid("4d36e96e-e325-11ce-bfc1-08002be10318");
    private const uint DIGCF_PRESENT = 0x2;
    private const uint SPDRP_DEVICEDESC = 0x1;
    private const uint SPDRP_FRIENDLYNAME = 0xC;
    private const uint DN_STARTED = 0x8;

    public class MonitorInfo
    {
        public string PnP;
        public string InstanceId;
        public string Name;
        public bool Present;
        public bool IsIntegrated;
    }

    private static string GetProp(IntPtr set, ref SP_DEVINFO_DATA d, uint prop)
    {
        var sb = new StringBuilder(1024);
        uint rt, need;
        if (SetupDiGetDeviceRegistryProperty(set, ref d, prop, out rt, sb, (uint)sb.Capacity, out need))
            return sb.ToString();
        return null;
    }

    public static List<MonitorInfo> Enumerate(bool presentOnly)
    {
        var list = new List<MonitorInfo>();
        IntPtr set = SetupDiGetClassDevs(ref GUID_MONITOR, null, IntPtr.Zero, presentOnly ? DIGCF_PRESENT : 0);
        if (set == IntPtr.Zero || set == (IntPtr)(-1))
            return list;
        try
        {
            uint idx = 0;
            while (true)
            {
                var d = new SP_DEVINFO_DATA();
                d.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                if (!SetupDiEnumDeviceInfo(set, idx, ref d)) break;
                idx++;
                var sb = new StringBuilder(512);
                uint need;
                SetupDiGetDeviceInstanceId(set, ref d, sb, (uint)sb.Capacity, out need);
                string inst = sb.ToString();
                if (string.IsNullOrEmpty(inst) || !inst.StartsWith("DISPLAY\\", StringComparison.OrdinalIgnoreCase)) continue;
                string pnp = inst.Split('\\')[1];
                string name = GetProp(set, ref d, SPDRP_FRIENDLYNAME);
                if (name == null) name = GetProp(set, ref d, SPDRP_DEVICEDESC);
                if (name == null) name = pnp;
                uint status, problem;
                int cr = CM_Get_DevNode_Status(out status, out problem, d.DevInst, 0);
                bool present = (cr == 0) && ((status & DN_STARTED) != 0);
                bool integ = (name.IndexOf("Integrated", StringComparison.OrdinalIgnoreCase) >= 0)
                          || (name.IndexOf("内置", StringComparison.OrdinalIgnoreCase) >= 0);
                list.Add(new MonitorInfo { PnP = pnp, InstanceId = inst, Name = name, Present = present, IsIntegrated = integ });
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return list;
    }
}

public class DevChangeForm : Form
{
    public event EventHandler DeviceChanged;
    public DevChangeForm()
    {
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new System.Drawing.Point(-32000, -32000);
        Size = new System.Drawing.Size(1, 1);
        Opacity = 0;
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0219) // WM_DEVICECHANGE
        {
            int w = (int)m.WParam.ToInt64();
            if (w == 0x0007 || w == 0x8000 || w == 0x8004) // DBT_DEVNODES_CHANGED / ARRIVAL / REMOVECOMPLETE
            {
                if (DeviceChanged != null)
                    DeviceChanged(this, EventArgs.Empty);
            }
        }
        base.WndProc(ref m);
    }
}
'@ -ErrorAction Stop
    $script:NativeReady = $true
} catch {
    Write-Log ("加载原生辅助组件失败: " + $_.Exception.Message)
}

# ---------------------------------------------------------------------------
# WinRT 热点操作 (仅 Windows PowerShell 5.1)
# ---------------------------------------------------------------------------
$script:WinRtReady = $false
try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $null = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType = WindowsRuntime]
    $null = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType = WindowsRuntime]
    $script:AsTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
    $script:WinRtReady = $true
} catch {
    Write-Log ("加载 WinRT 组件失败: " + $_.Exception.Message)
}

function Await-WinRt {
    param($WinRtTask, [Type]$ResultType)
    $asTask = $script:AsTaskGeneric.MakeGenericMethod($ResultType)
    $netTask = $asTask.Invoke($null, @($WinRtTask))
    $netTask.Wait(-1) | Out-Null
    $netTask.Result
}

function Get-HotspotState {
    if (-not $script:WinRtReady) { return 'N/A' }
    try {
        $profile = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
        if (-not $profile) { return '无网络配置' }
        $mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile)
        return $mgr.TetheringOperationalState.ToString()
    } catch { return ('读取失败: ' + $_.Exception.Message) }
}

function Start-Hotspot {
    if (-not $script:WinRtReady) { Write-Log 'ERROR: WinRT 组件未就绪'; return $false }
    try {
        $profile = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
        if (-not $profile) { Write-Log 'ERROR: 未找到网络连接配置'; return $false }
        $mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile)
        if ($mgr.TetheringOperationalState.ToString() -eq 'Off') {
            $res = Await-WinRt ($mgr.StartTetheringAsync()) ([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult])
            if ($res.Status.ToString() -eq 'Success') { Write-Log '热点已开启'; return $true }
            Write-Log ('开启热点失败: ' + $res.Status + ' / ' + $res.AdditionalErrorMessage)
            return $false
        }
        Write-Log '热点已在开启状态'
        return $true
    } catch {
        Write-Log ('开热点出错: ' + $_.Exception.Message)
        return $false
    }
}

function Stop-Hotspot {
    if (-not $script:WinRtReady) { Write-Log 'ERROR: WinRT 组件未就绪'; return $false }
    try {
        $profile = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
        if (-not $profile) { Write-Log 'ERROR: 未找到网络连接配置'; return $false }
        $mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile)
        if ($mgr.TetheringOperationalState.ToString() -ne 'Off') {
            $res = Await-WinRt ($mgr.StopTetheringAsync()) ([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult])
            if ($res.Status.ToString() -eq 'Success') { Write-Log '热点已关闭'; return $true }
            Write-Log ('关闭热点失败: ' + $res.Status + ' / ' + $res.AdditionalErrorMessage)
            return $false
        }
        Write-Log '热点已在关闭状态'
        return $true
    } catch {
        Write-Log ('关热点出错: ' + $_.Exception.Message)
        return $false
    }
}

# ---------------------------------------------------------------------------
# 核心判定
# ---------------------------------------------------------------------------
function Test-IsAdmin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
        IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-Evaluate {
    # 返回: 'start' 开热点并退出 / 'stop' 关热点并退出 / 'exit' 检测到其他屏, 不做操作直接退出 / 'none' 继续监控
    $cfg = Get-Config
    if (-not $script:NativeReady) {
        Write-Log 'ERROR: 无法枚举显示器(原生组件未加载)'
        return 'none'
    }
    $monitors = [NativeMonitor]::Enumerate($false)

    # 1) 目标屏已连接?
    $target = @($monitors | Where-Object {
        $_.Present -and ($_.PnP -like "*$($cfg.TargetPnP)*" -or $_.Name -like "*$($cfg.TargetPnP)*")
    })
    if ($target.Count -gt 0) {
        if ($cfg.StartOnTarget) {
            Write-Log ('检测到目标屏: ' + $target[0].Name + ' [' + $target[0].PnP + '], 开启热点')
            $ok = Start-Hotspot
            if ($ok) {
                Send-Notification '屏幕触发热点' '目标屏已连接，移动热点已开启'
                return 'start'
            }
            Write-Log '热点未成功开启(可能网络未就绪), 继续监控等待重试'
            return 'none'
        } else {
            Write-Log ('检测到目标屏但配置为不开启热点: ' + $target[0].Name)
        }
        return 'start'
    }

    # 2) 目标屏不在时是否直接关热点
    if ($cfg.StopWhenNoTarget) {
        Write-Log '目标屏未连接 (StopWhenNoTarget=true), 关闭热点'
        $ok = Stop-Hotspot
        if ($ok) {
            Send-Notification '屏幕触发热点' '目标屏未连接，已关闭移动热点'
            return 'stop'
        }
        Write-Log '热点未成功关闭, 继续监控等待重试'
        return 'none'
    }

    # 3) 检测到其他外接屏? -> 不做任何操作, 直接退出(不碰热点)
    if ($cfg.ExitOnOtherExternal) {
        $ext = @($monitors | Where-Object {
            $_.Present -and -not ($cfg.IgnoreIntegrated -and $_.IsIntegrated)
        })
        if ($ext.Count -gt 0) {
            Write-Log ('检测到其他外接屏: ' + $ext[0].Name + ' [' + $ext[0].PnP + '], 不做任何操作, 直接退出')
            Send-Notification '屏幕触发热点' '检测到其他外接屏，未操作热点，程序已退出'
            return 'exit'
        }
    }

    Write-Log '未检测到目标屏, 继续监控'
    return 'none'
}

# ---------------------------------------------------------------------------
# 设置界面 (--config)
# ---------------------------------------------------------------------------
function Show-SettingsDialog {
    [System.Windows.Forms.Application]::EnableVisualStyles()
    $cfg = Get-Config

    $form = New-Object System.Windows.Forms.Form
    $form.Text = '屏幕触发热点 - 设置'
    $form.Size = New-Object System.Drawing.Size(520, 460)
    $form.StartPosition = 'CenterScreen'
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text = '选择触发屏幕(会列出历史上连接过的所有屏幕):'
    $lbl.Location = New-Object System.Drawing.Point(15, 12)
    $lbl.AutoSize = $true

    $lb = New-Object System.Windows.Forms.ListBox
    $lb.Location = New-Object System.Drawing.Point(15, 38)
    $lb.Size = New-Object System.Drawing.Size(475, 180)
    $lbPnp = @()
    if ($script:NativeReady) {
        $mons = [NativeMonitor]::Enumerate($false)
        foreach ($m in ($mons | Sort-Object { -not $_.Present }, Name)) {
            $mark = if ($m.Present) { '[已连接]' } else { '[历史]' }
            $integ = if ($m.IsIntegrated) { ' (内置屏)' } else { '' }
            $null = $lb.Items.Add(("$($m.Name) $integ  $mark  $($m.PnP)").Trim())
            $lbPnp += $m.PnP
        }
    } else {
        $null = $lb.Items.Add('(无法枚举显示器)')
        $lbPnp += ''
    }
    $sel = -1
    for ($i = 0; $i -lt $lbPnp.Count; $i++) {
        if ($lbPnp[$i] -like "*$($cfg.TargetPnP)*") { $sel = $i; break }
    }
    if ($sel -ge 0) { $lb.SelectedIndex = $sel }

    $lbl2 = New-Object System.Windows.Forms.Label
    $lbl2.Text = '或手动输入 PnP ID / 名称片段 (留空则用上面选择):'
    $lbl2.Location = New-Object System.Drawing.Point(15, 230)
    $lbl2.AutoSize = $true

    $txt = New-Object System.Windows.Forms.TextBox
    $txt.Location = New-Object System.Drawing.Point(15, 254)
    $txt.Size = New-Object System.Drawing.Size(240, 23)
    $txt.Text = $cfg.TargetPnP

    $chk1 = New-Object System.Windows.Forms.CheckBox
    $chk1.Text = '忽略笔记本内置屏(不当作"其他外接屏")'
    $chk1.Location = New-Object System.Drawing.Point(15, 290)
    $chk1.AutoSize = $true
    $chk1.Checked = $cfg.IgnoreIntegrated

    $chk2 = New-Object System.Windows.Forms.CheckBox
    $chk2.Text = '目标屏不在时也自动关闭热点'
    $chk2.Location = New-Object System.Drawing.Point(15, 320)
    $chk2.AutoSize = $true
    $chk2.Checked = $cfg.StopWhenNoTarget

    $lbl3 = New-Object System.Windows.Forms.Label
    $lbl3.Text = '最多运行(分钟后自动退出):'
    $lbl3.Location = New-Object System.Drawing.Point(15, 352)
    $lbl3.AutoSize = $true

    $num = New-Object System.Windows.Forms.NumericUpDown
    $num.Location = New-Object System.Drawing.Point(200, 348)
    $num.Minimum = 1
    $num.Maximum = 120
    $num.Value = [Math]::Max(1, [int]$cfg.MaxRunMinutes)

    $btnOk = New-Object System.Windows.Forms.Button
    $btnOk.Text = '保存'
    $btnOk.Location = New-Object System.Drawing.Point(310, 380)
    $btnOk.Size = New-Object System.Drawing.Size(85, 30)
    $btnOk.DialogResult = 'OK'

    $btnCancel = New-Object System.Windows.Forms.Button
    $btnCancel.Text = '取消'
    $btnCancel.Location = New-Object System.Drawing.Point(405, 380)
    $btnCancel.Size = New-Object System.Drawing.Size(85, 30)
    $btnCancel.DialogResult = 'Cancel'

    $form.Controls.AddRange(@($lbl, $lb, $lbl2, $txt, $chk1, $chk2, $lbl3, $num, $btnOk, $btnCancel))
    $form.AcceptButton = $btnOk
    $form.CancelButton = $btnCancel

    $r = $form.ShowDialog()
    if ($r -eq [System.Windows.Forms.DialogResult]::OK) {
        $newPnp = $txt.Text.Trim()
        if (-not $newPnp -and $lb.SelectedIndex -ge 0 -and $lbPnp[$lb.SelectedIndex]) {
            $newPnp = $lbPnp[$lb.SelectedIndex]
        }
        if (-not $newPnp) {
            [System.Windows.Forms.MessageBox]::Show('请选择或输入触发屏幕', '提示')
            return $false
        }
        $cfg.TargetPnP = $newPnp
        $cfg.IgnoreIntegrated = $chk1.Checked
        $cfg.StopWhenNoTarget = $chk2.Checked
        $cfg.MaxRunMinutes = [int]$num.Value
        Save-Config $cfg
        Write-Log ('目标屏幕已设为: ' + $cfg.TargetPnP)
        [System.Windows.Forms.MessageBox]::Show('已保存, 下次运行生效。' + "`r`n" + '目标屏幕: ' + $cfg.TargetPnP, '完成')
        return $true
    }
    return $false
}

# ---------------------------------------------------------------------------
# 托盘监控模式 (默认 / watch)
# ---------------------------------------------------------------------------
function Start-Watch {
    if (-not (Test-IsAdmin) -and -not $env:HSG_NO_ELEV) {
        Write-Host 'watch 模式需要管理员权限, 正在请求提权...'
        Start-Process powershell.exe -Verb RunAs -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
            '-File', "`"$PSCommandPath`"", '-Mode', 'watch'
        )
        return
    }

    [System.Windows.Forms.Application]::EnableVisualStyles()
    Write-Log 'step: 界面初始化完成'
    $cfg = Get-Config
    Write-Log 'step: 配置已读取'

    # 单实例
    $script:Mutex = New-Object System.Threading.Mutex($false, 'Local\HotspotGuard')
    if (-not $script:Mutex.WaitOne(0)) {
        Write-Log '已有实例在运行, 退出'
        return
    }

    # 降低自身优先级/占用
    try { (Get-Process -Id $PID).PriorityClass = 'BelowNormal'; Write-Log 'step: 优先级已调整' } catch { Write-Log ('step: 优先级调整失败 ' + $_.Exception.Message) }

    # 托盘图标
    $bmp = New-Object System.Drawing.Bitmap(16, 16)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $b = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::ForestGreen)
    $g.FillEllipse($b, 2, 2, 12, 12)
    $b2 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $font = New-Object System.Drawing.Font('Arial', 8, [System.Drawing.FontStyle]::Bold)
    $g.DrawString('H', $font, $b2, 5, 3)
    $g.Dispose(); $b.Dispose(); $b2.Dispose(); $font.Dispose()
    $hicon = $bmp.GetHicon()
    $icon = [System.Drawing.Icon]::FromHandle($hicon)
    Write-Log 'step: 托盘图标已创建'

    $notify = New-Object System.Windows.Forms.NotifyIcon
    $notify.Icon = $icon
    $notify.Text = '屏幕触发热点'
    $notify.Visible = $true
    $script:NotifyIcon = $notify
    Write-Log 'step: 托盘图标已显示'

    $menu = New-Object System.Windows.Forms.ContextMenuStrip
    $miStart = $menu.Items.Add('立即开启热点')
    $miStop = $menu.Items.Add('立即关闭热点')
    $miCfg = $menu.Items.Add('设置(选择触发屏幕)...')
    $null = $menu.Items.Add('-')
    $miExit = $menu.Items.Add('退出')
    $notify.ContextMenuStrip = $menu

    $miStart.Add_Click({ Write-Log '手动开启热点'; $null = Start-Hotspot })
    $miStop.Add_Click({ Write-Log '手动关闭热点'; $null = Stop-Hotspot })
    $miCfg.Add_Click({ $null = Show-SettingsDialog })
    $miExit.Add_Click({ Write-Log '用户退出'; $notify.Visible = $false; [Environment]::Exit(0) })

    # 设备变化事件 -> 立即判定
    $devForm = New-Object DevChangeForm
    $devForm.Add_DeviceChanged({
        if ($script:Exiting) { return }
        try { Write-Log '检测到设备变化'; $r = Invoke-Evaluate; if ($r -ne 'none') { $script:Exiting = $true; Wait-NotificationDisplay 5; [Environment]::Exit(0) } } catch { Write-Log ('判定出错: ' + $_.Exception.Message) }
    })
    Write-Log 'step: 设备监听已挂载'

    # 兜底轮询
    $tCheck = New-Object System.Windows.Forms.Timer
    $tCheck.Interval = [Math]::Max(5, [int]$cfg.CheckIntervalSeconds) * 1000
    $tCheck.Add_Tick({
        if ($script:Exiting) { return }
        try { $r = Invoke-Evaluate; if ($r -ne 'none') { $script:Exiting = $true; Wait-NotificationDisplay 5; [Environment]::Exit(0) } } catch { Write-Log ('判定出错: ' + $_.Exception.Message) }
    })
    $tCheck.Start()

    # 自动退出计时
    $tExit = New-Object System.Windows.Forms.Timer
    $tExit.Interval = [Math]::Max(1, [int]$cfg.MaxRunMinutes) * 60 * 1000
    $tExit.Add_Tick({
        if ($script:Exiting) { return }
        Write-Log ('运行满 ' + $cfg.MaxRunMinutes + ' 分钟, 自动退出')
        Send-Notification '屏幕触发热点' ('监控 ' + $cfg.MaxRunMinutes + ' 分钟未检测到目标屏，程序已自动退出')
        $script:Exiting = $true
        Wait-NotificationDisplay 5
        $notify.Visible = $false
        [Environment]::Exit(0)
    })
    $tExit.Start()
    Write-Log 'step: 定时器已启动'

    Write-Log ('托盘监控启动, 目标屏: ' + $cfg.TargetPnP + ' , 最长运行 ' + $cfg.MaxRunMinutes + ' 分钟')

    # 启动时先判定一次
    try {
        $r = Invoke-Evaluate
        if ($r -ne 'none') { $script:Exiting = $true; Wait-NotificationDisplay 5; $notify.Visible = $false; [Environment]::Exit(0) }
    } catch { Write-Log ('启动判定出错: ' + $_.Exception.Message) }

    Write-Log 'step: 进入消息循环'
    try {
        [System.Windows.Forms.Application]::Run($devForm)
        Write-Log 'step: 消息循环已退出(Run返回)'
    } catch {
        Write-Log ('消息循环异常: ' + $_.Exception.ToString())
    }
    Write-Log 'step: Start-Watch 结束'
}

# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------
Write-Log ('==== hotspotguard 启动, Mode=' + $Mode + ' ====')

switch ($Mode) {
    'help' {
        Get-Help $MyInvocation.MyCommand.Path -Detailed
        Write-Host @'

模式说明:
  (默认)  watch  托盘监控: 目标屏连接->开热点并退出; 其他外接屏->不做操作直接退出; 满N分钟自动退出
  once    检测一次并执行后退出
  status  查看当前屏幕清单与热点状态
  config  打开图形化设置(选择触发屏幕)
  start   立即开启热点
  stop    立即关闭热点
'@
    }
    'status' {
        if ($script:NativeReady) {
            Write-Host ''
            Write-Host '已识别的屏幕(含历史):'
            [NativeMonitor]::Enumerate($false) | ForEach-Object {
                $s = if ($_.Present) { '已连接' } else { '未连接' }
                $i = if ($_.IsIntegrated) { ' [内置屏]' } else { '' }
                Write-Host ("  {0,-28} {1,-10} {2}{3}" -f $_.Name, $s, $_.PnP, $i)
            }
        } else {
            Write-Host '无法枚举显示器'
        }
        Write-Host ('热点状态: ' + (Get-HotspotState))
        $cfg = Get-Config
        Write-Host ('当前配置目标屏: ' + $cfg.TargetPnP)
    }
    'config' {
        $null = Show-SettingsDialog
    }
    'once' {
        if (-not (Test-IsAdmin) -and -not $env:HSG_NO_ELEV) {
            Write-Host '需要管理员权限, 正在请求提权...'
            Start-Process powershell.exe -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', "`"$PSCommandPath`"", '-Mode', 'once')
        } else {
            $r = Invoke-Evaluate
            Write-Log ('once 模式结束, 结果: ' + $r)
            [Environment]::Exit(0)
        }
    }
    'start' {
        if (-not (Test-IsAdmin) -and -not $env:HSG_NO_ELEV) {
            Write-Host '需要管理员权限, 正在请求提权...'
            Start-Process powershell.exe -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', "`"$PSCommandPath`"", '-Mode', 'start')
        } else {
            $null = Start-Hotspot
        }
    }
    'stop' {
        if (-not (Test-IsAdmin) -and -not $env:HSG_NO_ELEV) {
            Write-Host '需要管理员权限, 正在请求提权...'
            Start-Process powershell.exe -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', "`"$PSCommandPath`"", '-Mode', 'stop')
        } else {
            $null = Stop-Hotspot
        }
    }
    default {
        # watch
        if (-not (Test-IsAdmin) -and -not $env:HSG_NO_ELEV) {
            Write-Host '需要管理员权限, 正在请求提权...'
            Start-Process powershell.exe -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', "`"$PSCommandPath`"", '-Mode', 'watch')
        } else {
            Start-Watch
        }
    }
}
