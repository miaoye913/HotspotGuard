// ============================================================================
// HotspotGuard.cs - 屏幕 / USB 触发热点守护程序 (Windows)
// ----------------------------------------------------------------------------
// 功能:
//   1. 配置多个触发设备: 屏幕(DELF145 等) 与 USB(VID/PID) 严格分离, 互不混淆
//   2. 触发设备连接 -> 自动开启(或关闭) Windows 移动热点, 右下角通知, 退出
//   3. 检测到未配置的外接屏 -> 不做任何操作, 直接退出
//   4. 托盘图标 + 事件驱动监听(占用低), 运行满 N 分钟自动退出
//   5. 现代化暗色设置界面 (--config), 支持纯命令行模式
//
// 用法: HotspotGuard.exe [watch|once|status|config|start|stop|help]
// 编译: .NET Framework 4.x csc + GAC facade 程序集 + WinMetadata winmd
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace HotspotGuard
{
    // ============================ 原生 P/Invoke ============================
    internal static class Native
    {
        public static readonly Guid GUID_MONITOR = new Guid("4d36e96e-e325-11ce-bfc1-08002be10318");
        public static readonly Guid GUID_USB = new Guid("36fc9e60-c465-11cf-8056-444553540000");
        public const uint DIGCF_PRESENT = 0x2;
        public const uint SPDRP_DEVICEDESC = 1;
        public const uint SPDRP_FRIENDLYNAME = 12;
        public const uint DN_STARTED = 0x8;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, string Enumerator, IntPtr hwndParent, uint Flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInstanceId(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, StringBuilder buf, uint len, out uint need);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceRegistryProperty(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint Property, out uint RegDataType, StringBuilder buf, uint len, out uint need);
        [DllImport("cfgmgr32.dll", SetLastError = true)]
        public static extern int CM_Get_DevNode_Status(out uint ulStatus, out uint ulProblemNumber, uint dnDevInst, uint ulFlags);

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;   // 0=电池 1=交流电 255=未知
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte Reserved1;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS sps);
    }

    internal static class PowerState
    {
        // 测试开关: 设置环境变量 HSG_SIM_BATTERY=1 可模拟"未插电"状态
        private static bool SimBattery { get { return Environment.GetEnvironmentVariable("HSG_SIM_BATTERY") == "1"; } }

        public static bool IsOnAC()
        {
            if (SimBattery) return false;
            Native.SYSTEM_POWER_STATUS sps;
            if (Native.GetSystemPowerStatus(out sps))
                return sps.ACLineStatus == 1;
            return true; // 查询失败(如台式机/虚拟机)按已接电处理
        }

        public static string Describe()
        {
            if (SimBattery) return "使用电池 (模拟)";
            Native.SYSTEM_POWER_STATUS sps;
            if (!Native.GetSystemPowerStatus(out sps)) return "未知";
            if (sps.ACLineStatus == 1) return "已接通电源 (交流电)";
            if (sps.ACLineStatus == 0) return "使用电池";
            return "未知";
        }
    }

    public class DeviceInfo
    {
        public string Pnp;        // 屏幕: DELF145 ; USB: VID_1A86&PID_7523
        public string InstanceId;
        public string Name;
        public bool Present;
        public bool IsIntegrated;
    }

    internal static class DeviceQuery
    {
        private static string GetProp(IntPtr set, ref Native.SP_DEVINFO_DATA d, uint prop)
        {
            var sb = new StringBuilder(1024);
            uint rt, need;
            if (Native.SetupDiGetDeviceRegistryProperty(set, ref d, prop, out rt, sb, (uint)sb.Capacity, out need))
                return sb.ToString();
            return null;
        }

        public static List<DeviceInfo> Enumerate(Guid classGuid, bool presentOnly)
        {
            var list = new List<DeviceInfo>();
            IntPtr set = Native.SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, presentOnly ? Native.DIGCF_PRESENT : 0);
            if (set == IntPtr.Zero || set == (IntPtr)(-1))
                return list;
            try
            {
                uint idx = 0;
                while (true)
                {
                    var d = new Native.SP_DEVINFO_DATA();
                    d.cbSize = (uint)Marshal.SizeOf(typeof(Native.SP_DEVINFO_DATA));
                    if (!Native.SetupDiEnumDeviceInfo(set, idx, ref d)) break;
                    idx++;
                    var sb = new StringBuilder(512);
                    uint need;
                    Native.SetupDiGetDeviceInstanceId(set, ref d, sb, (uint)sb.Capacity, out need);
                    string inst = sb.ToString();
                    if (string.IsNullOrEmpty(inst)) continue;
                    string[] parts = inst.Split('\\');
                    string pnp = parts.Length > 1 ? parts[1] : inst;
                    string name = GetProp(set, ref d, Native.SPDRP_FRIENDLYNAME);
                    if (name == null) name = GetProp(set, ref d, Native.SPDRP_DEVICEDESC);
                    if (name == null) name = pnp;
                    uint status, problem;
                    int cr = Native.CM_Get_DevNode_Status(out status, out problem, d.DevInst, 0);
                    bool present = (cr == 0) && ((status & Native.DN_STARTED) != 0);
                    bool integ = name.IndexOf("Integrated", StringComparison.OrdinalIgnoreCase) >= 0
                              || name.IndexOf("\u5185\u7f6e", StringComparison.OrdinalIgnoreCase) >= 0; // "内置"
                    list.Add(new DeviceInfo { Pnp = pnp, InstanceId = inst, Name = name, Present = present, IsIntegrated = integ });
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(set);
            }
            return list;
        }
    }

    // ============================ WinRT 热点 ============================
    internal static class Hotspot
    {
        // 同步等待 WinRT 异步操作(避免依赖编译器的 WinRT await 支持)
        private static NetworkOperatorTetheringOperationResult AwaitResult(Windows.Foundation.IAsyncOperation<NetworkOperatorTetheringOperationResult> op)
        {
            var info = (Windows.Foundation.IAsyncInfo)op;
            while (info.Status == Windows.Foundation.AsyncStatus.Started)
                Thread.Sleep(50);
            if (info.Status == Windows.Foundation.AsyncStatus.Error)
                throw new Exception("异步操作失败: " + info.ErrorCode.Message);
            return op.GetResults();
        }

        public static bool Start()
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile == null) { Log.Write("无网络连接配置"); return false; }
                var mgr = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
                if (mgr.TetheringOperationalState.ToString() == "Off")
                {
                    var res = AwaitResult(mgr.StartTetheringAsync());
                    if (res.Status.ToString() == "Success") { Log.Write("热点已开启"); return true; }
                    Log.Write("开启热点失败: " + res.Status + " / " + res.AdditionalErrorMessage);
                    return false;
                }
                Log.Write("热点已在开启状态");
                return true;
            }
            catch (Exception ex) { Log.Write("开热点出错: " + ex.Message); return false; }
        }

        public static bool Stop()
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile == null) { Log.Write("无网络连接配置"); return false; }
                var mgr = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
                if (mgr.TetheringOperationalState.ToString() != "Off")
                {
                    var res = AwaitResult(mgr.StopTetheringAsync());
                    if (res.Status.ToString() == "Success") { Log.Write("热点已关闭"); return true; }
                    Log.Write("关闭热点失败: " + res.Status + " / " + res.AdditionalErrorMessage);
                    return false;
                }
                Log.Write("热点已在关闭状态");
                return true;
            }
            catch (Exception ex) { Log.Write("关热点出错: " + ex.Message); return false; }
        }

        public static string GetState()
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile == null) return "无网络配置";
                var mgr = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
                return mgr.TetheringOperationalState.ToString();
            }
            catch (Exception ex) { return "读取失败: " + ex.Message; }
        }
    }

    // ============================ 日志 ============================
    internal static class Log
    {
        private static readonly object LockObj = new object();
        private static string Path { get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hotspotguard.log"); } }

        public static void Write(string msg)
        {
            string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg;
            lock (LockObj)
            {
                try { System.IO.File.AppendAllText(Path, line + Environment.NewLine, Encoding.UTF8); } catch { }
            }
            try { Console.WriteLine(line); } catch { }
        }
    }

    // ============================ 通知 ============================
    internal static class Notify
    {
        public static NotifyIcon Tray;

        public static void Send(string title, string text)
        {
            try
            {
                if (Tray != null)
                {
                    Tray.BalloonTipTitle = title;
                    Tray.BalloonTipText = text;
                    Tray.BalloonTipIcon = ToolTipIcon.Info;
                    Tray.ShowBalloonTip(5000);
                }
                else
                {
                    var tmp = new NotifyIcon();
                    tmp.Icon = SystemIcons.Information;
                    tmp.Visible = true;
                    tmp.BalloonTipTitle = title;
                    tmp.BalloonTipText = text;
                    tmp.BalloonTipIcon = ToolTipIcon.Info;
                    tmp.ShowBalloonTip(5000);
                    Pump(5);
                    tmp.Visible = false;
                    tmp.Dispose();
                }
                Log.Write("通知: " + title + " - " + text);
            }
            catch (Exception ex) { Log.Write("通知发送失败: " + ex.Message); }
        }

        public static void Pump(int seconds)
        {
            DateTime end = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < end)
            {
                Application.DoEvents();
                Thread.Sleep(100);
            }
        }
    }

    // ============================ 配置 ============================
    [DataContract]
    public class TriggerItem
    {
        [DataMember] public string Name = "";
        [DataMember] public string Pnp = "";       // 屏幕: DELF145 ; USB: VID_xxxx&PID_xxxx
        [DataMember] public string Action = "start_hotspot"; // start_hotspot | stop_hotspot
    }

    [DataContract]
    public class Config
    {
        [DataMember] public List<TriggerItem> Screens = new List<TriggerItem>();  // 屏幕触发项
        [DataMember] public List<TriggerItem> Usb = new List<TriggerItem>();      // USB 触发项(与屏幕严格分离)
        [DataMember] public bool IgnoreIntegrated = true;
        [DataMember] public bool ExitOnOtherScreen = true;
        [DataMember] public bool StopWhenNoTrigger = false;
        [DataMember] public bool RequireACPower = true;  // 仅在接通电源时触发; 未插电则后台待机
        [DataMember] public int MaxRunMinutes = 5;
        [DataMember] public int CheckIntervalSeconds = 10;
    }

    internal static class ConfigStore
    {
        public static string Path { get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hotspotguard.json"); } }

        public static Config Load()
        {
            var cfg = new Config();
            try
            {
                if (File.Exists(Path))
                {
                    string json = File.ReadAllText(Path, Encoding.UTF8);
                    if (json.Length > 0 && json[0] == '\uFEFF') // 剥离 UTF-8 BOM
                        json = json.Substring(1);
                    var ser = new DataContractJsonSerializer(typeof(Config));
                    using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    {
                        var loaded = ser.ReadObject(ms) as Config;
                        if (loaded != null)
                        {
                            cfg = loaded;
                            // DCJS 不执行字段初始化器: 旧配置缺少的字段需手动补默认值
                            if (json.IndexOf("\"RequireACPower\"", StringComparison.OrdinalIgnoreCase) < 0)
                                cfg.RequireACPower = true; // 默认开启"仅接电触发"
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write("读取配置失败, 使用默认配置: " + ex.Message); }
            return cfg;
        }

        public static void Save(Config cfg)
        {
            var ser = new DataContractJsonSerializer(typeof(Config));
            using (var fs = File.Create(Path))
                ser.WriteObject(fs, cfg);
            Log.Write("配置已保存: " + Path);
        }
    }

    // ============================ 核心判定 ============================
    internal static class Evaluator
    {
        // 只保留最终通知: 触发成功执行动作 / 其他屏退出 / 超时自动退出; 失败与启动均不通知

        private static bool Matches(TriggerItem t, DeviceInfo d)
        {
            return d.Pnp.IndexOf(t.Pnp, StringComparison.OrdinalIgnoreCase) >= 0
                || d.Name.IndexOf(t.Pnp, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // 返回: "start"/"stop"/"exit"/"none"
        public static string Evaluate(Config cfg)
        {
            var screens = DeviceQuery.Enumerate(Native.GUID_MONITOR, false);
            var usb = DeviceQuery.Enumerate(Native.GUID_USB, true);

            // 1) 屏幕触发项(只匹配显示器)
            foreach (var t in cfg.Screens)
            {
                var hit = screens.FirstOrDefault(s => s.Present && Matches(t, s));
                if (hit != null)
                {
                    bool stop = t.Action == "stop_hotspot";
                    Log.Write("检测到屏幕触发: " + hit.Name + " [" + hit.Pnp + "] -> " + (stop ? "关热点" : "开热点"));
                    bool ok = stop ? Hotspot.Stop() : Hotspot.Start();
                    if (ok)
                    {
                        Notify.Send("屏幕触发热点", stop ? "触发设备已连接, 已关闭移动热点" : "触发设备已连接, 移动热点已开启");
                        return stop ? "stop" : "start";
                    }
                    return "none"; // 失败不通知, 静默重试
                }
            }

            // 2) USB 触发项(只匹配 USB 设备, 与屏幕严格分离)
            foreach (var t in cfg.Usb)
            {
                var hit = usb.FirstOrDefault(u => u.Present && Matches(t, u));
                if (hit != null)
                {
                    bool stop = t.Action == "stop_hotspot";
                    Log.Write("检测到 USB 触发: " + hit.Name + " [" + hit.Pnp + "] -> " + (stop ? "关热点" : "开热点"));
                    bool ok = stop ? Hotspot.Stop() : Hotspot.Start();
                    if (ok)
                    {
                        Notify.Send("屏幕触发热点", stop ? "触发设备已连接, 已关闭移动热点" : "触发设备已连接, 移动热点已开启");
                        return stop ? "stop" : "start";
                    }
                    return "none"; // 失败不通知, 静默重试
                }
            }

            // 3) 未检测到任何触发设备时是否强制关热点
            if (cfg.StopWhenNoTrigger)
            {
                Log.Write("未检测到任何触发设备 (StopWhenNoTrigger=true), 关闭热点");
                bool ok = Hotspot.Stop();
                if (ok) { Notify.Send("屏幕触发热点", "未检测到触发设备, 已关闭移动热点"); return "stop"; }
                return "none";
            }

            // 4) 检测到未配置的外接屏 -> 不做任何操作, 直接退出
            if (cfg.ExitOnOtherScreen)
            {
                var other = screens.FirstOrDefault(s => s.Present && !(cfg.IgnoreIntegrated && s.IsIntegrated)
                    && !cfg.Screens.Any(t => Matches(t, s)));
                if (other != null)
                {
                    Log.Write("检测到其他外接屏: " + other.Name + " [" + other.Pnp + "], 不做任何操作, 直接退出");
                    Notify.Send("屏幕触发热点", "检测到其他外接屏, 未操作热点, 程序已退出");
                    return "exit";
                }
            }

            Log.Write("未检测到触发设备, 继续监控");
            return "none";
        }
    }

    // ============================ 设备变化监听窗口 ============================
    internal class DevChangeForm : Form
    {
        public event EventHandler DeviceChanged;
        public event EventHandler PowerChanged;

        public DevChangeForm()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1, 1);
            Opacity = 0;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0219) // WM_DEVICECHANGE
            {
                int w = (int)m.WParam.ToInt64();
                if (w == 0x0007 || w == 0x8000 || w == 0x8004) // DBT_DEVNODES_CHANGED / ARRIVAL / REMOVECOMPLETE
                {
                    if (DeviceChanged != null) DeviceChanged(this, EventArgs.Empty);
                }
            }
            else if (m.Msg == 0x0218) // WM_POWERBROADCAST
            {
                int w = (int)m.WParam.ToInt64();
                if (w == 0x000A) // PBT_APMPOWERSTATUSCHANGE
                {
                    if (PowerChanged != null) PowerChanged(this, EventArgs.Empty);
                }
            }
            base.WndProc(ref m);
        }
    }

    // ============================ 托盘监控 ============================
    internal static class TrayApp
    {
        private static Config Cfg;
        private static NotifyIcon Tray;
        private static bool Exiting;
        private static bool WaitingForAC;      // 未插电待机中
        private static System.Windows.Forms.Timer ExitTimer; // 自动退出计时(待机时不计时)

        private static void EvaluateAndMaybeExit()
        {
            if (Exiting) return;
            try
            {
                Cfg = ConfigStore.Load(); // 每次重新读取, 设置即时生效

                // 电源门控: 开启"仅接电触发"且当前用电池 -> 后台待机, 直到接通电源
                if (Cfg.RequireACPower && !PowerState.IsOnAC())
                {
                    if (!WaitingForAC)
                    {
                        WaitingForAC = true;
                        Log.Write("未接通电源(使用电池), 后台待机, 接通电源后自动恢复监控");
                        if (Tray != null) Tray.Text = "屏幕/USB 触发热点 - 待机(未插电)";
                        Notify.Send("屏幕/USB 触发热点", "未接通电源(使用电池)，后台待机中。接通电源后自动恢复监控。");
                    }
                    return; // 待机: 不执行任何动作, 不退出
                }
                if (WaitingForAC)
                {
                    WaitingForAC = false;
                    Log.Write("已接通电源, 恢复监控");
                    if (Tray != null) Tray.Text = "屏幕/USB 触发热点";
                    if (ExitTimer != null) { ExitTimer.Stop(); ExitTimer.Start(); } // 重新计时
                }

                string r = Evaluator.Evaluate(Cfg);
                if (r != "none") { Exiting = true; Notify.Pump(5); Tray.Visible = false; Environment.Exit(0); }
            }
            catch (Exception ex) { Log.Write("判定出错: " + ex.Message); }
        }

        public static void Run()
        {
            using (var mutex = new Mutex(false, "Local\\HotspotGuard"))
            {
                if (!mutex.WaitOne(0)) { Log.Write("已有实例在运行, 退出"); return; }
                Cfg = ConfigStore.Load();

                try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

                // 生成托盘图标: 绿色圆 + 白色 H
                var bmp = new Bitmap(16, 16);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    using (var b = new SolidBrush(Color.ForestGreen)) g.FillEllipse(b, 2, 2, 12, 12);
                    using (var b2 = new SolidBrush(Color.White))
                    using (var f = new Font("Arial", 8, FontStyle.Bold)) g.DrawString("H", f, b2, 5, 3);
                }
                IntPtr hIcon = bmp.GetHicon();
                Icon icon = Icon.FromHandle(hIcon);

                Tray = new NotifyIcon();
                Tray.Icon = icon;
                Tray.Text = "屏幕/USB 触发热点";
                Tray.Visible = true;
                Notify.Tray = Tray;

                var menu = new ContextMenuStrip();
                menu.Items.Add("立即开启热点", null, (s, e) => { Hotspot.Start(); });
                menu.Items.Add("立即关闭热点", null, (s, e) => { Hotspot.Stop(); });
                menu.Items.Add("设置(触发设备)...", null, (s, e) => { using (var f = new SettingsForm()) f.ShowDialog(); });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("退出", null, (s, e) => { Exiting = true; Tray.Visible = false; Application.Exit(); });
                Tray.ContextMenuStrip = menu;

                var devForm = new DevChangeForm();
                devForm.DeviceChanged += (s, e) => { if (!Exiting) { Log.Write("检测到设备变化"); EvaluateAndMaybeExit(); } };
                devForm.PowerChanged += (s, e) => { if (!Exiting) { Log.Write("电源状态变化"); EvaluateAndMaybeExit(); } };
                devForm.Load += (s, e) => { if (!Exiting) EvaluateAndMaybeExit(); }; // 启动时先判定一次

                var tCheck = new System.Windows.Forms.Timer();
                tCheck.Interval = Math.Max(5, Cfg.CheckIntervalSeconds) * 1000;
                tCheck.Tick += (s, e) => EvaluateAndMaybeExit();
                tCheck.Start();

                ExitTimer = new System.Windows.Forms.Timer();
                ExitTimer.Interval = Math.Max(1, Cfg.MaxRunMinutes) * 60 * 1000;
                ExitTimer.Tick += (s, e) =>
                {
                    if (Exiting) return;
                    if (WaitingForAC) { ExitTimer.Stop(); ExitTimer.Start(); return; } // 待机中不计时, 接通电源后重新计时
                    Log.Write("运行满 " + Cfg.MaxRunMinutes + " 分钟, 自动退出");
                    Notify.Send("屏幕触发热点", "监控 " + Cfg.MaxRunMinutes + " 分钟未检测到触发设备, 程序已自动退出");
                    Exiting = true;
                    Notify.Pump(5);
                    Tray.Visible = false;
                    Environment.Exit(0);
                };
                ExitTimer.Start();

                Log.Write("托盘监控启动: 屏幕触发 " + Cfg.Screens.Count + " 项, USB 触发 " + Cfg.Usb.Count + " 项, 最长运行 " + Cfg.MaxRunMinutes + " 分钟");

                Application.Run(devForm);
            }
        }
    }

    // ============================ 设置界面 (现代暗色) ============================
    internal class SettingsForm : Form
    {
        private Config Cfg;
        private readonly Color Bg = Color.FromArgb(32, 33, 36);
        private readonly Color PanelBg = Color.FromArgb(43, 45, 50);
        private readonly Color Accent = Color.FromArgb(76, 139, 245);
        private readonly Color AccentDark = Color.FromArgb(58, 108, 196);
        private readonly Color Fg = Color.FromArgb(232, 234, 237);
        private readonly Color Sub = Color.FromArgb(154, 160, 166);

        private ListBox lbScreensAvail, lbScreensTrig, lbUsbAvail, lbUsbTrig;
        private ComboBox cmbScreensAction, cmbUsbAction;
        private Button btnTabScreens, btnTabUsb, btnTabGeneral;
        private Panel panelScreens, panelUsb, panelGeneral;
        private CheckBox chkIgnore, chkExitOther, chkStopNoTrig, chkAC;
        private NumericUpDown numMinutes;
        private List<DeviceInfo> screensAll, usbPresent;
        private List<TriggerItem> screenTriggers, usbTriggers;
        private bool loading;

        public SettingsForm()
        {
            Cfg = ConfigStore.Load();
            screenTriggers = new List<TriggerItem>(Cfg.Screens);
            usbTriggers = new List<TriggerItem>(Cfg.Usb);
            screensAll = DeviceQuery.Enumerate(Native.GUID_MONITOR, false);
            usbPresent = DeviceQuery.Enumerate(Native.GUID_USB, true);
            Build();
        }

        private static Button FlatBtn(string text, Color bg, Color fg)
        {
            var b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = bg;
            b.ForeColor = fg;
            b.Font = new Font("Segoe UI", 9f);
            b.Cursor = Cursors.Hand;
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(bg, 0.15f);
            return b;
        }

        private static ListBox DarkList()
        {
            var lb = new ListBox();
            lb.BackColor = Color.FromArgb(28, 29, 32);
            lb.ForeColor = Color.FromArgb(232, 234, 237);
            lb.BorderStyle = BorderStyle.FixedSingle;
            lb.Font = new Font("Segoe UI", 9f);
            lb.IntegralHeight = false;
            return lb;
        }

        private void Build()
        {
            Text = "屏幕/USB 触发热点 - 设置";
            BackColor = Bg;
            ForeColor = Fg;
            Font = new Font("Segoe UI", 9.5f);
            Size = new Size(700, 600);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var lblTitle = new Label();
            lblTitle.Text = "屏幕 / USB 触发热点";
            lblTitle.Font = new Font("Segoe UI", 15f, FontStyle.Bold);
            lblTitle.ForeColor = Fg;
            lblTitle.Location = new Point(22, 14);
            lblTitle.AutoSize = true;

            var lblSub = new Label();
            lblSub.Text = "配置触发设备: 检测到连接后自动执行动作。屏幕与 USB 相互独立。";
            lblSub.Font = new Font("Segoe UI", 9f);
            lblSub.ForeColor = Sub;
            lblSub.Location = new Point(24, 46);
            lblSub.AutoSize = true;

            // 分段式页签
            btnTabScreens = FlatBtn("  屏幕触发  ", PanelBg, Fg);
            btnTabScreens.Location = new Point(22, 76);
            btnTabScreens.Size = new Size(150, 34);
            btnTabUsb = FlatBtn("  USB 触发  ", PanelBg, Fg);
            btnTabUsb.Location = new Point(178, 76);
            btnTabUsb.Size = new Size(150, 34);
            btnTabGeneral = FlatBtn("  通用设置  ", PanelBg, Fg);
            btnTabGeneral.Location = new Point(334, 76);
            btnTabGeneral.Size = new Size(150, 34);
            btnTabScreens.Click += (s, e) => SelectTab(0);
            btnTabUsb.Click += (s, e) => SelectTab(1);
            btnTabGeneral.Click += (s, e) => SelectTab(2);

            // ---- 屏幕触发面板 ----
            panelScreens = new Panel();
            panelScreens.BackColor = Bg;
            panelScreens.Location = new Point(16, 118);
            panelScreens.Size = new Size(668, 408);

            var l1 = new Label();
            l1.Text = "可用屏幕 (含历史连接, [已连接] 为当前插入):";
            l1.ForeColor = Fg; l1.Location = new Point(8, 8); l1.AutoSize = true;
            lbScreensAvail = DarkList();
            lbScreensAvail.Location = new Point(8, 30);
            lbScreensAvail.Size = new Size(310, 300);
            foreach (var m in screensAll.OrderByDescending(x => x.Present).ThenBy(x => x.Name))
            {
                string mark = m.Present ? "[已连接]" : "[历史]";
                string integ = m.IsIntegrated ? " (内置屏)" : "";
                lbScreensAvail.Items.Add(m.Name + integ + "  " + mark + "  " + m.Pnp);
            }

            var l2 = new Label();
            l2.Text = "当前屏幕触发项:";
            l2.ForeColor = Fg; l2.Location = new Point(344, 8); l2.AutoSize = true;
            lbScreensTrig = DarkList();
            lbScreensTrig.Location = new Point(344, 30);
            lbScreensTrig.Size = new Size(316, 250);
            lbScreensTrig.SelectedIndexChanged += (s, e) => SyncActionCombo(0);

            var bAddS = FlatBtn("▶ 添加为触发", Accent, Color.White);
            bAddS.Location = new Point(8, 344); bAddS.Size = new Size(130, 32);
            bAddS.Click += (s, e) => AddTrigger(0);
            var bDelS = FlatBtn("◀ 移除", PanelBg, Fg);
            bDelS.Location = new Point(148, 344); bDelS.Size = new Size(90, 32);
            bDelS.Click += (s, e) => RemoveTrigger(0);

            var laS = new Label();
            laS.Text = "动作:"; laS.ForeColor = Sub; laS.Location = new Point(8, 386); laS.AutoSize = true;
            cmbScreensAction = new ComboBox();
            cmbScreensAction.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbScreensAction.Items.AddRange(new object[] { "开热点", "关热点" });
            cmbScreensAction.Location = new Point(58, 383); cmbScreensAction.Size = new Size(110, 24);
            cmbScreensAction.BackColor = PanelBg; cmbScreensAction.ForeColor = Fg;
            cmbScreensAction.SelectedIndexChanged += (s, e) => ApplyActionCombo(0);
            var bApplyS = FlatBtn("应用到选中项", PanelBg, Fg);
            bApplyS.Location = new Point(180, 382); bApplyS.Size = new Size(120, 27);
            bApplyS.Click += (s, e) => ApplyActionCombo(0);

            panelScreens.Controls.AddRange(new Control[] { l1, lbScreensAvail, l2, lbScreensTrig, bAddS, bDelS, laS, cmbScreensAction, bApplyS });

            // ---- USB 触发面板 ----
            panelUsb = new Panel();
            panelUsb.BackColor = Bg;
            panelUsb.Location = new Point(16, 118);
            panelUsb.Size = new Size(668, 408);

            var lu1 = new Label();
            lu1.Text = "可用 USB 设备 (仅当前已插入; 请先插上目标设备再选择):";
            lu1.ForeColor = Fg; lu1.Location = new Point(8, 8); lu1.AutoSize = true;
            lbUsbAvail = DarkList();
            lbUsbAvail.Location = new Point(8, 30);
            lbUsbAvail.Size = new Size(310, 300);
            foreach (var u in usbPresent.OrderBy(x => x.Name))
                lbUsbAvail.Items.Add(u.Name + "  [" + u.Pnp + "]");

            var lu2 = new Label();
            lu2.Text = "当前 USB 触发项:";
            lu2.ForeColor = Fg; lu2.Location = new Point(344, 8); lu2.AutoSize = true;
            lbUsbTrig = DarkList();
            lbUsbTrig.Location = new Point(344, 30);
            lbUsbTrig.Size = new Size(316, 250);
            lbUsbTrig.SelectedIndexChanged += (s, e) => SyncActionCombo(1);

            var bAddU = FlatBtn("▶ 添加为触发", Accent, Color.White);
            bAddU.Location = new Point(8, 344); bAddU.Size = new Size(130, 32);
            bAddU.Click += (s, e) => AddTrigger(1);
            var bDelU = FlatBtn("◀ 移除", PanelBg, Fg);
            bDelU.Location = new Point(148, 344); bDelU.Size = new Size(90, 32);
            bDelU.Click += (s, e) => RemoveTrigger(1);

            var laU = new Label();
            laU.Text = "动作:"; laU.ForeColor = Sub; laU.Location = new Point(8, 386); laU.AutoSize = true;
            cmbUsbAction = new ComboBox();
            cmbUsbAction.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbUsbAction.Items.AddRange(new object[] { "开热点", "关热点" });
            cmbUsbAction.Location = new Point(58, 383); cmbUsbAction.Size = new Size(110, 24);
            cmbUsbAction.BackColor = PanelBg; cmbUsbAction.ForeColor = Fg;
            cmbUsbAction.SelectedIndexChanged += (s, e) => ApplyActionCombo(1);
            var bApplyU = FlatBtn("应用到选中项", PanelBg, Fg);
            bApplyU.Location = new Point(180, 382); bApplyU.Size = new Size(120, 27);
            bApplyU.Click += (s, e) => ApplyActionCombo(1);

            panelUsb.Controls.AddRange(new Control[] { lu1, lbUsbAvail, lu2, lbUsbTrig, bAddU, bDelU, laU, cmbUsbAction, bApplyU });

            // ---- 通用面板 ----
            panelGeneral = new Panel();
            panelGeneral.BackColor = Bg;
            panelGeneral.Location = new Point(16, 118);
            panelGeneral.Size = new Size(668, 408);

            chkIgnore = new CheckBox();
            chkIgnore.Text = "忽略笔记本内置屏 (不视为\"其他外接屏\")";
            chkIgnore.ForeColor = Fg; chkIgnore.Checked = Cfg.IgnoreIntegrated;
            chkIgnore.Location = new Point(24, 30); chkIgnore.AutoSize = true;

            chkExitOther = new CheckBox();
            chkExitOther.Text = "检测到未配置的外接屏时直接退出 (不做任何操作)";
            chkExitOther.ForeColor = Fg; chkExitOther.Checked = Cfg.ExitOnOtherScreen;
            chkExitOther.Location = new Point(24, 66); chkExitOther.AutoSize = true;

            chkStopNoTrig = new CheckBox();
            chkStopNoTrig.Text = "未检测到任何触发设备时关闭热点并退出";
            chkStopNoTrig.ForeColor = Fg; chkStopNoTrig.Checked = Cfg.StopWhenNoTrigger;
            chkStopNoTrig.Location = new Point(24, 102); chkStopNoTrig.AutoSize = true;

            chkAC = new CheckBox();
            chkAC.Text = "仅在接通电源(插电)时触发动作; 未插电时后台待机直到接通电源";
            chkAC.ForeColor = Fg; chkAC.Checked = Cfg.RequireACPower;
            chkAC.Location = new Point(24, 138); chkAC.AutoSize = true;

            var lp = new Label();
            lp.Text = "当前电源: " + PowerState.Describe();
            lp.ForeColor = Fg; lp.Location = new Point(24, 172); lp.AutoSize = true;
            lp.Font = new Font("Segoe UI", 9f);

            var lg = new Label();
            lg.Text = "最长运行(分钟后自动退出):";
            lg.ForeColor = Fg; lg.Location = new Point(24, 206); lg.AutoSize = true;
            numMinutes = new NumericUpDown();
            numMinutes.Location = new Point(220, 202);
            numMinutes.Minimum = 1; numMinutes.Maximum = 120;
            numMinutes.Value = Math.Max(1, Cfg.MaxRunMinutes);
            numMinutes.BackColor = PanelBg; numMinutes.ForeColor = Fg;

            var info = new Label();
            info.Text = "触发设备连接 -> 执行动作(开/关热点) -> 通知 -> 退出。\r\n屏幕触发项只匹配显示器, USB 触发项只匹配 USB 设备, 互不混淆。\r\n配置保存在 HotspotGuard.exe 同目录的 hotspotguard.json。";
            info.ForeColor = Sub; info.Location = new Point(24, 240); info.Size = new Size(600, 60);
            info.Font = new Font("Segoe UI", 8.5f);

            panelGeneral.Controls.AddRange(new Control[] { chkIgnore, chkExitOther, chkStopNoTrig, chkAC, lp, lg, numMinutes, info });

            // ---- 底部按钮 ----
            var btnOk = FlatBtn("保存", Accent, Color.White);
            btnOk.Location = new Point(430, 540); btnOk.Size = new Size(110, 38);
            btnOk.Click += (s, e) => SaveAndClose();
            var btnCancel = FlatBtn("取消", PanelBg, Fg);
            btnCancel.Location = new Point(556, 540); btnCancel.Size = new Size(110, 38);
            btnCancel.Click += (s, e) => Close();

            Controls.AddRange(new Control[] { lblTitle, lblSub, btnTabScreens, btnTabUsb, btnTabGeneral, panelScreens, panelUsb, panelGeneral, btnOk, btnCancel });

            SelectTab(0);
            RefreshTriggerLists();
            loading = true;
            SyncActionCombo(0);
            SyncActionCombo(1);
            loading = false;
        }

        private void SelectTab(int idx)
        {
            panelScreens.Visible = idx == 0;
            panelUsb.Visible = idx == 1;
            panelGeneral.Visible = idx == 2;
            btnTabScreens.BackColor = idx == 0 ? Accent : PanelBg;
            btnTabUsb.BackColor = idx == 1 ? Accent : PanelBg;
            btnTabGeneral.BackColor = idx == 2 ? Accent : PanelBg;
        }

        private void RefreshTriggerLists()
        {
            lbScreensTrig.Items.Clear();
            foreach (var t in screenTriggers)
                lbScreensTrig.Items.Add(t.Name + "  [" + t.Pnp + "]  ->  " + (t.Action == "stop_hotspot" ? "关热点" : "开热点"));
            lbUsbTrig.Items.Clear();
            foreach (var t in usbTriggers)
                lbUsbTrig.Items.Add(t.Name + "  [" + t.Pnp + "]  ->  " + (t.Action == "stop_hotspot" ? "关热点" : "开热点"));
        }

        private void AddTrigger(int kind)
        {
            if (kind == 0)
            {
                int i = lbScreensAvail.SelectedIndex;
                if (i < 0) { MessageBox.Show("请先在左侧选择一台屏幕", "提示"); return; }
                var d = screensAll[i];
                if (screenTriggers.Any(t => t.Pnp.Equals(d.Pnp, StringComparison.OrdinalIgnoreCase))) { MessageBox.Show("该屏幕已在触发项中", "提示"); return; }
                screenTriggers.Add(new TriggerItem { Name = d.Name, Pnp = d.Pnp, Action = "start_hotspot" });
            }
            else
            {
                int i = lbUsbAvail.SelectedIndex;
                if (i < 0) { MessageBox.Show("请先在左侧选择一个 USB 设备", "提示"); return; }
                var d = usbPresent[i];
                if (usbTriggers.Any(t => t.Pnp.Equals(d.Pnp, StringComparison.OrdinalIgnoreCase))) { MessageBox.Show("该 USB 设备已在触发项中", "提示"); return; }
                usbTriggers.Add(new TriggerItem { Name = d.Name, Pnp = d.Pnp, Action = "start_hotspot" });
            }
            RefreshTriggerLists();
        }

        private void RemoveTrigger(int kind)
        {
            ListBox lb = kind == 0 ? lbScreensTrig : lbUsbTrig;
            List<TriggerItem> list = kind == 0 ? screenTriggers : usbTriggers;
            int i = lb.SelectedIndex;
            if (i < 0) { MessageBox.Show("请先在右侧选择要移除的触发项", "提示"); return; }
            list.RemoveAt(i);
            RefreshTriggerLists();
        }

        private void SyncActionCombo(int kind)
        {
            if (loading) return;
            ComboBox cmb = kind == 0 ? cmbScreensAction : cmbUsbAction;
            ListBox lb = kind == 0 ? lbScreensTrig : lbUsbTrig;
            List<TriggerItem> list = kind == 0 ? screenTriggers : usbTriggers;
            int i = lb.SelectedIndex;
            if (i >= 0 && i < list.Count)
                cmb.SelectedIndex = list[i].Action == "stop_hotspot" ? 1 : 0;
        }

        private void ApplyActionCombo(int kind)
        {
            if (loading) return;
            ComboBox cmb = kind == 0 ? cmbScreensAction : cmbUsbAction;
            ListBox lb = kind == 0 ? lbScreensTrig : lbUsbTrig;
            List<TriggerItem> list = kind == 0 ? screenTriggers : usbTriggers;
            int i = lb.SelectedIndex;
            if (i >= 0 && i < list.Count && cmb.SelectedIndex >= 0)
            {
                list[i].Action = cmb.SelectedIndex == 1 ? "stop_hotspot" : "start_hotspot";
                RefreshTriggerLists();
                lb.SelectedIndex = i;
            }
        }

        private void SaveAndClose()
        {
            Cfg.Screens = screenTriggers;
            Cfg.Usb = usbTriggers;
            Cfg.IgnoreIntegrated = chkIgnore.Checked;
            Cfg.ExitOnOtherScreen = chkExitOther.Checked;
            Cfg.StopWhenNoTrigger = chkStopNoTrig.Checked;
            Cfg.RequireACPower = chkAC.Checked;
            Cfg.MaxRunMinutes = (int)numMinutes.Value;
            ConfigStore.Save(Cfg);
            MessageBox.Show("已保存, 下次运行生效。", "完成");
            Close();
        }
    }

    // ============================ 主入口 ============================
    internal static class Program
    {
        private static bool IsAdmin()
        {
            var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var p = new System.Security.Principal.WindowsPrincipal(id);
            return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        private static void PrintHelp()
        {
            Console.WriteLine("屏幕/USB 触发热点 - HotspotGuard");
            Console.WriteLine();
            Console.WriteLine("用法: HotspotGuard.exe [模式]");
            Console.WriteLine("  (默认) watch   托盘监控: 触发设备连接->执行动作->通知->退出; 满N分钟自动退出");
            Console.WriteLine("  once           检测一次并执行后退出");
            Console.WriteLine("  status         查看当前屏幕/USB 设备与热点状态");
            Console.WriteLine("  config         打开图形化设置(选择触发设备)");
            Console.WriteLine("  start          立即开启热点");
            Console.WriteLine("  stop           立即关闭热点");
            Console.WriteLine();
            Console.WriteLine("watch/once/start/stop 需要管理员权限, 非管理员时自动请求提权。");
        }

        private static void ShowStatus()
        {
            Console.WriteLine();
            Console.WriteLine("已识别的屏幕(含历史):");
            foreach (var m in DeviceQuery.Enumerate(Native.GUID_MONITOR, false))
            {
                string s = m.Present ? "已连接" : "未连接";
                string i = m.IsIntegrated ? " [内置屏]" : "";
                Console.WriteLine("  {0,-30} {1,-10} {2}{3}", m.Name, s, m.Pnp, i);
            }
            Console.WriteLine();
            Console.WriteLine("当前已插入的 USB 设备:");
            foreach (var u in DeviceQuery.Enumerate(Native.GUID_USB, true))
                Console.WriteLine("  {0,-30} {1}", u.Name, u.Pnp);
            Console.WriteLine();
            Console.WriteLine("电源状态: " + PowerState.Describe());
            Console.WriteLine("热点状态: " + Hotspot.GetState());
            var cfg = ConfigStore.Load();
            Console.WriteLine("屏幕触发项: " + cfg.Screens.Count + " 项, USB 触发项: " + cfg.Usb.Count + " 项, 仅接电触发: " + cfg.RequireACPower);
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string mode = "watch";
            if (args.Length > 0)
            {
                mode = args[0].Trim().TrimStart('-', '/').ToLowerInvariant();
                if (mode == "h" || mode == "?") mode = "help";
            }

            Log.Write("==== HotspotGuard 启动, Mode=" + mode + " ====");

            bool needAdmin = mode == "watch" || mode == "once" || mode == "start" || mode == "stop";
            if (needAdmin && !IsAdmin())
            {
                Console.WriteLine("需要管理员权限, 正在请求提权...");
                try
                {
                    var psi = new ProcessStartInfo(Application.ExecutablePath);
                    psi.Verb = "runas";
                    psi.UseShellExecute = true;
                    psi.Arguments = string.Join(" ", args);
                    Process.Start(psi);
                }
                catch { Console.WriteLine("提权被取消"); }
                return;
            }

            switch (mode)
            {
                case "help":
                    PrintHelp();
                    break;
                case "status":
                    ShowStatus();
                    break;
                case "config":
                    using (var f = new SettingsForm()) f.ShowDialog();
                    break;
                case "start":
                    Hotspot.Start();
                    break;
                case "stop":
                    Hotspot.Stop();
                    break;
                case "once":
                {
                    var cfg = ConfigStore.Load();
                    if (cfg.RequireACPower && !PowerState.IsOnAC())
                    {
                        Log.Write("未接通电源(使用电池), 跳过本次检测");
                        break;
                    }
                    string r = Evaluator.Evaluate(cfg);
                    Log.Write("once 模式结束, 结果: " + r);
                    break;
                }
                default:
                    TrayApp.Run();
                    break;
            }
        }
    }
}
