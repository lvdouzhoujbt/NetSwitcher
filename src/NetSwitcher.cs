// ============================================================================
// NetSwitcher - 网卡 IP(V4/V6) / DNS / 系统代理 一键切换工具
// 技术: C# WinForms (.NET Framework 4.x, Windows 10/11 内置, 零依赖)
// 编译: 见同目录 build.cmd
// 功能: 方案切换(IP方案/代理方案分离) / 托盘常驻 / 随系统启动 / 亮暗主题
//       网卡启停重启 / 快捷打开状态与IP设置窗口 / INI 方案导入备份
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetSwitcher
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // ------------------------------------------------------------------
    // INI 文件读写 (调用系统 API, 兼容中文)
    // ------------------------------------------------------------------
    class IniFile
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern long WritePrivateProfileString(string section, string key, string val, string filePath);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        static extern int GetPrivateProfileString(string section, string key, string def,
            StringBuilder retVal, int size, string filePath);

        string path;

        public IniFile(string path) { this.path = path; }

        public void Write(string section, string key, string val)
        {
            WritePrivateProfileString(section, key, val, path);
        }

        public string Read(string section, string key, string def)
        {
            StringBuilder sb = new StringBuilder(1024);
            GetPrivateProfileString(section, key, def, sb, 1024, path);
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------
    // 切换方案  (Type: IP=网卡IP方案 / Proxy=代理方案 / All=旧版完整方案)
    // ------------------------------------------------------------------
    class Profile
    {
        public string Name = "";
        public string Type = "IP";
        public string Adapter = "";
        // IPv4
        public bool Dhcp4 = true;
        public string IP4 = "", Mask4 = "", GW4 = "", DNS1 = "", DNS2 = "";
        // IPv6
        public bool Dhcp6 = true;
        public string IP6 = "", Prefix6 = "64", GW6 = "";
        // 系统代理
        public bool ProxyEnabled = false;
        public string ProxyServer = "";
        public bool BypassLocal = true;

        public void Save(string file)
        {
            IniFile ini = new IniFile(file);
            ini.Write("Profile", "Name", Name);
            ini.Write("Profile", "Type", Type);
            ini.Write("Adapter", "Name", Adapter);
            ini.Write("IPv4", "Mode", Dhcp4 ? "DHCP" : "Static");
            ini.Write("IPv4", "IP", IP4);
            ini.Write("IPv4", "Mask", Mask4);
            ini.Write("IPv4", "Gateway", GW4);
            ini.Write("IPv4", "DNS1", DNS1);
            ini.Write("IPv4", "DNS2", DNS2);
            ini.Write("IPv6", "Mode", Dhcp6 ? "Auto" : "Static");
            ini.Write("IPv6", "IP", IP6);
            ini.Write("IPv6", "Prefix", Prefix6);
            ini.Write("IPv6", "Gateway", GW6);
            ini.Write("Proxy", "Enabled", ProxyEnabled ? "1" : "0");
            ini.Write("Proxy", "Server", ProxyServer);
            ini.Write("Proxy", "BypassLocal", BypassLocal ? "1" : "0");
        }

        public static Profile Load(string file)
        {
            IniFile ini = new IniFile(file);
            Profile p = new Profile();
            p.Name = ini.Read("Profile", "Name", Path.GetFileNameWithoutExtension(file));
            p.Type = ini.Read("Profile", "Type", "All");   // 旧版方案无 Type, 视为完整方案
            p.Adapter = ini.Read("Adapter", "Name", "");
            p.Dhcp4 = ini.Read("IPv4", "Mode", "DHCP") != "Static";
            p.IP4 = ini.Read("IPv4", "IP", "");
            p.Mask4 = ini.Read("IPv4", "Mask", "");
            p.GW4 = ini.Read("IPv4", "Gateway", "");
            p.DNS1 = ini.Read("IPv4", "DNS1", "");
            p.DNS2 = ini.Read("IPv4", "DNS2", "");
            p.Dhcp6 = ini.Read("IPv6", "Mode", "Auto") != "Static";
            p.IP6 = ini.Read("IPv6", "IP", "");
            p.Prefix6 = ini.Read("IPv6", "Prefix", "64");
            p.GW6 = ini.Read("IPv6", "Gateway", "");
            p.ProxyEnabled = ini.Read("Proxy", "Enabled", "0") == "1";
            p.ProxyServer = ini.Read("Proxy", "Server", "");
            p.BypassLocal = ini.Read("Proxy", "BypassLocal", "1") == "1";
            return p;
        }
    }

    // ------------------------------------------------------------------
    // netsh 命令执行
    // ------------------------------------------------------------------
    static class Cmd
    {
        public static string Run(string args, out int exitCode)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "netsh.exe";
            psi.Arguments = args;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.Default;
            psi.StandardErrorEncoding = Encoding.Default;
            using (Process p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                exitCode = p.ExitCode;
                return output.Trim();
            }
        }

        public static string Q(string s) { return "\"" + s + "\""; }
    }

    // ------------------------------------------------------------------
    // Windows 系统代理 (WinINET / 注册表)
    // ------------------------------------------------------------------
    static class ProxyHelper
    {
        const int INTERNET_OPTION_REFRESH = 37;
        const int INTERNET_OPTION_SETTINGS_CHANGED = 39;

        [DllImport("wininet.dll", SetLastError = true)]
        static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        public static void Apply(bool enable, string server, bool bypassLocal)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true))
            {
                key.SetValue("ProxyEnable", enable ? 1 : 0, RegistryValueKind.DWord);
                if (enable)
                {
                    if (!string.IsNullOrEmpty(server)) key.SetValue("ProxyServer", server);
                    string ov = bypassLocal ? "<local>" : "";
                    key.SetValue("ProxyOverride", ov);
                }
            }
            // 通知系统立即生效, 否则要重启浏览器才生效
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
    }

    // ------------------------------------------------------------------
    // 随系统启动 (HKCU Run 注册表, 无需管理员)
    // ------------------------------------------------------------------
    static class AutostartHelper
    {
        const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValName = "NetSwitcher";

        public static bool Get()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                return k != null && k.GetValue(ValName) != null;
            }
        }

        public static void Set(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath, true))
            {
                if (k == null) return;
                if (on) k.SetValue(ValName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(ValName, false);
            }
        }
    }

    // ------------------------------------------------------------------
    // 主题: 亮/暗色跟随系统设置 (Win11 风格配色)
    // ------------------------------------------------------------------
    static class Theme
    {
        public static bool IsDark()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                if (key == null) return false;
                object v = key.GetValue("AppsUseLightTheme");
                return (v is int && (int)v == 0);
            }
        }
    }

    // ------------------------------------------------------------------
    // 网卡条目 (含连接状态 / 启禁用状态, 显示于下拉列表)
    // ------------------------------------------------------------------
    class NicItem
    {
        public string Name;       // netsh 使用的接口名
        public string Guid;       // NetCfgInstanceId, 用于定位属性窗口
        public bool Enabled;      // 管理状态 (启用/禁用)
        public string Status;     // 已连接 / 未连接 / 已禁用

        public NicItem(string name, string guid, bool enabled, string status)
        {
            Name = name; Guid = guid; Enabled = enabled; Status = status;
        }
        public override string ToString() { return Name + "  [" + Status + "]"; }
    }

    // ------------------------------------------------------------------
    // 原生 IP 地址输入框 (封装系统控件 SysIPAddress32)
    // 单框内自动分段: 框内自带分隔点、段间自动跳转、每段 0-255 校验、
    // 支持方向键/退格/粘贴编辑, 与 Windows 网络属性中的输入框外观一致
    // ------------------------------------------------------------------
    class IpField : Panel
    {
        IntPtr hIP = IntPtr.Zero;

        const int WS_CHILD = 0x40000000;
        const int WS_VISIBLE = 0x10000000;
        const int IPM_CLEARADDRESS = 0x0464;
        const int IPM_SETADDRESS = 0x0465;
        const int IPM_GETADDRESS = 0x0466;
        const int WM_SETFONT = 0x0030;

        struct INITCOMMONCONTROLSEX { public int dwSize; public int dwICC; }

        [DllImport("comctl32.dll")]
        static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr parent, IntPtr id, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref uint lParam);
        [DllImport("user32.dll")]
        static extern bool EnableWindow(IntPtr hWnd, bool enable);
        [DllImport("user32.dll")]
        static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("kernel32.dll")]
        static extern IntPtr GetModuleHandle(string name);

        public IpField()
        {
            this.Height = 26;
            this.Width = 152;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            INITCOMMONCONTROLSEX icc;
            icc.dwSize = Marshal.SizeOf(typeof(INITCOMMONCONTROLSEX));
            icc.dwICC = 0x80;   // ICC_INTERNET_CLASSES
            InitCommonControlsEx(ref icc);
            if (hIP != IntPtr.Zero) DestroyWindow(hIP);
            hIP = CreateWindowEx(0, "SysIPAddress32", null, WS_CHILD | WS_VISIBLE,
                1, 1, this.Width - 2, this.Height - 2, this.Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            IntPtr hFont = this.Font.ToHfont();
            SendMessage(hIP, WM_SETFONT, hFont, (IntPtr)1);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (hIP != IntPtr.Zero) EnableWindow(hIP, this.Enabled);
        }

        // 是否四段全部填写
        public bool IsComplete()
        {
            uint v = 0;
            return (int)SendMessage(hIP, IPM_GETADDRESS, IntPtr.Zero, ref v) == 4;
        }

        // 是否全部为空 (空 = 未做任何输入)
        public bool IsEmpty()
        {
            uint v = 0;
            return (int)SendMessage(hIP, IPM_GETADDRESS, IntPtr.Zero, ref v) == 0;
        }

        public new string Text
        {
            get
            {
                if (hIP == IntPtr.Zero) return "";
                uint v = 0;
                int n = (int)SendMessage(hIP, IPM_GETADDRESS, IntPtr.Zero, ref v);
                if (n == 0) return "";
                return string.Format("{0}.{1}.{2}.{3}",
                    (v >> 24) & 255, (v >> 16) & 255, (v >> 8) & 255, v & 255);
            }
            set
            {
                if (hIP == IntPtr.Zero) return;
                string[] seg = (value ?? "").Trim().Split('.');
                if (seg.Length != 4)
                {
                    SendMessage(hIP, IPM_CLEARADDRESS, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                uint a = 0;
                for (int i = 0; i < 4; i++)
                {
                    uint part;
                    uint.TryParse(seg[i], out part);
                    if (part > 255) part = 255;
                    a = (a << 8) | part;
                }
                SendMessage(hIP, IPM_SETADDRESS, IntPtr.Zero, (IntPtr)a);
            }
        }

        public void ClearBoxes()
        {
            if (hIP != IntPtr.Zero) SendMessage(hIP, IPM_CLEARADDRESS, IntPtr.Zero, IntPtr.Zero);
        }
    }

    // ------------------------------------------------------------------
    // 网卡状态窗口 (自绘信息面板, 不依赖 van.dll, 兼容精简系统)
    // ------------------------------------------------------------------
    class StatusForm : Form
    {
        public StatusForm(NicItem nic)
        {
            bool dark = Theme.IsDark();
            Color bg = dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);
            Color fg = dark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(27, 27, 27);
            Color gray = dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110);

            this.Font = new Font("微软雅黑", 10F);
            this.Text = "网卡状态 - " + nic.Name;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = bg;

            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ---- 收集信息行 ----
            List<KeyValuePair<string, string>> rows = new List<KeyValuePair<string, string>>();
            rows.Add(new KeyValuePair<string, string>("管理状态", nic.Enabled ? "已启用" : "已禁用"));
            rows.Add(new KeyValuePair<string, string>("连接状态", nic.Status.Contains("已连接") ? "已连接" : "未连接"));

            NetworkInterface ni = null;
            foreach (NetworkInterface x in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (x.Name == nic.Name) { ni = x; break; }
            }
            if (ni != null)
            {
                rows.Add(new KeyValuePair<string, string>("网卡类型", ni.NetworkInterfaceType.ToString()));
                try
                {
                    rows.Add(new KeyValuePair<string, string>("MAC 地址",
                        string.Join("-", ni.GetPhysicalAddress().GetAddressBytes()
                            .Select(b => b.ToString("X2")).ToArray())));
                }
                catch { }
                try
                {
                    rows.Add(new KeyValuePair<string, string>("链路速度",
                        (ni.Speed / 1000000.0).ToString("F0") + " Mbps"));
                }
                catch { }
                try
                {
                    IPInterfaceProperties p = ni.GetIPProperties();
                    foreach (UnicastIPAddressInformation u in p.UnicastAddresses)
                    {
                        if (u.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        if (u.Address.ToString().StartsWith("169.254.")) continue;
                        rows.Add(new KeyValuePair<string, string>("IPv4 地址", u.Address.ToString()));
                        try { rows.Add(new KeyValuePair<string, string>("子网掩码", u.IPv4Mask.ToString())); } catch { }
                        break;
                    }
                    string gw4 = "";
                    foreach (GatewayIPAddressInformation g in p.GatewayAddresses)
                    {
                        if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        { gw4 = g.Address.ToString(); break; }
                    }
                    rows.Add(new KeyValuePair<string, string>("IPv4 网关", gw4.Length > 0 ? gw4 : "无"));
                    try
                    {
                        IPv4InterfaceProperties p4 = p.GetIPv4Properties();
                        rows.Add(new KeyValuePair<string, string>("DHCP", p4 != null && p4.IsDhcpEnabled
                            ? "已启用 (自动获取)" : "未启用 (手动配置)"));
                    }
                    catch { }
                    string dns = "";
                    foreach (System.Net.IPAddress d in p.DnsAddresses)
                    {
                        if (d.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        dns += (dns.Length > 0 ? ", " : "") + d.ToString();
                    }
                    rows.Add(new KeyValuePair<string, string>("DNS 服务器", dns.Length > 0 ? dns : "无"));
                    int v6n = 0;
                    foreach (UnicastIPAddressInformation u in p.UnicastAddresses)
                    {
                        if (u.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) continue;
                        if (u.Address.IsIPv6LinkLocal) continue;
                        v6n++;
                        rows.Add(new KeyValuePair<string, string>("IPv6 地址 " + v6n, u.Address.ToString()));
                        if (v6n >= 2) break;
                    }
                    if (v6n == 0) rows.Add(new KeyValuePair<string, string>("IPv6 地址", "无"));
                    string gw6 = "";
                    foreach (GatewayIPAddressInformation g in p.GatewayAddresses)
                    {
                        if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                        { gw6 = g.Address.ToString(); break; }
                    }
                    rows.Add(new KeyValuePair<string, string>("IPv6 网关", gw6.Length > 0 ? gw6 : "无"));
                }
                catch { }
            }
            else
            {
                rows.Add(new KeyValuePair<string, string>("提示", "网卡已禁用, 无 TCP/IP 信息; 启用后可查看详情"));
            }

            // ---- 渲染 ----
            int y = 58;
            Label title = new Label();
            title.Text = nic.Name + "  [" + nic.Status + "]";
            title.Font = new Font("微软雅黑", 12F, FontStyle.Bold);
            title.Location = new Point(20, 15);
            title.Size = new Size(490, 32);
            title.ForeColor = nic.Enabled
                ? (dark ? Color.FromArgb(108, 203, 95) : Color.FromArgb(0, 128, 0))
                : gray;
            this.Controls.Add(title);

            foreach (KeyValuePair<string, string> row in rows)
            {
                Label k = new Label();
                k.Text = row.Key + ":";
                k.Location = new Point(20, y);
                k.Size = new Size(110, 26);
                k.ForeColor = gray;
                this.Controls.Add(k);
                Label v = new Label();
                v.Text = row.Value.Length > 0 ? row.Value : "-";
                v.Location = new Point(135, y);
                v.Size = new Size(375, 26);
                v.ForeColor = fg;
                this.Controls.Add(v);
                y += 30;
            }

            Button btnClose = new Button();
            btnClose.Text = "关闭";
            btnClose.Size = new Size(110, 34);
            btnClose.Location = new Point((520 - 110) / 2, y + 8);
            btnClose.DialogResult = DialogResult.OK;
            this.Controls.Add(btnClose);
            this.AcceptButton = btnClose;
            this.CancelButton = btnClose;

            this.ClientSize = new Size(520, y + 60);
        }
    }

    // ------------------------------------------------------------------
    // 主窗口
    // ------------------------------------------------------------------
    class MainForm : Form
    {
        ListBox lstProfiles;
        TextBox txtIpName, txtIP6, txtPrefix6, txtGW6, txtProxy, txtProxyName;
        IpField qIP4, qMask, qGW4, qDNS1, qDNS2;   // IPv4 各项均为原生框内自动分段输入
        ComboBox cmbMaskPreset, cmbAdapter, cmbType;
        RadioButton rbDhcp4, rbStatic4, rbAuto6, rbStatic6;
        CheckBox chkProxy, chkBypassLocal, chkAutoStart;
        Button btnApply, btnApplyProxy;
        Label lblNicStatus;                  // 网卡操作实时状态显示
        NotifyIcon tray;
        ContextMenuStrip trayMenu;
        System.Windows.Forms.Timer trayTimer;
        List<Label> grayLabels = new List<Label>();
        string profilesDir;
        string currentFile = null;   // 当前编辑器对应的方案文件路径
        bool suppressAutoStartEvent = false;
        bool exiting = false;
        bool balloonShown = false;

        public MainForm()
        {
            this.Font = new Font("微软雅黑", 10F);
            this.Text = "NetSwitcher - 网卡IP/代理一键切换工具";
            // DPI 缩放: 控件坐标随系统缩放比例等比放大, 避免 125%/150% 下溢出
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.AutoScaleDimensions = new SizeF(96F, 96F);
            this.ClientSize = new Size(1060, 652);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;

            profilesDir = Path.Combine(Application.StartupPath, "profiles");
            if (!Directory.Exists(profilesDir)) Directory.CreateDirectory(profilesDir);

            InitUi();
            // 整体放大 20%: 所有控件坐标与尺寸等比缩放, 间距同步放宽
            this.Scale(new SizeF(1.2F, 1.2F));
            this.ClientSize = new Size(1272, 786);
            ApplyTheme();
            RefreshNicList(null);
            ReloadProfiles(null);
            InitTray();
            ApplyWindowChrome();

            // 关闭窗口 = 缩到托盘; 退出仅通过托盘菜单
            this.FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (!exiting && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    HideToTray();
                }
            };
            this.Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized) HideToTray();
            };
        }

        // ================= 窗口外观增强 (Win11 圆角 / 暗色标题栏) =================

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void ApplyWindowChrome()
        {
            try
            {
                if (Environment.OSVersion.Version.Major >= 10)
                {
                    int round = 2;   // DWMWCP_ROUND: Win11 圆角窗口
                    DwmSetWindowAttribute(this.Handle, 33, ref round, 4);
                    if (Theme.IsDark())
                    {
                        int dark = 1;   // 暗色主题下标题栏也用深色
                        DwmSetWindowAttribute(this.Handle, 20, ref dark, 4);
                    }
                }
            }
            catch { /* 旧系统不支持该属性, 静默忽略 */ }
        }

        // ================= UI 构建 =================

        Label AddLabel(Control parent, string text, int x, int y, int w, bool gray)
        {
            // 固定宽度标签: 随 DPI 线性缩放, 避免缩放下与相邻控件叠加
            Label l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.Size = new Size(w, 22);
            l.TextAlign = ContentAlignment.MiddleLeft;
            if (gray) grayLabels.Add(l);
            parent.Controls.Add(l);
            return l;
        }

        TextBox AddTextBox(Control parent, int x, int y, int w)
        {
            TextBox t = new TextBox();
            t.Location = new Point(x, y);
            t.Width = w;
            parent.Controls.Add(t);
            return t;
        }

        IpField NewQuad(Control parent, int x, int y)
        {
            IpField q = new IpField();
            q.Location = new Point(x, y);
            parent.Controls.Add(q);
            return q;
        }

        Button AddButton(Control parent, string text, int x, int y, int w, int h, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, h);
            if (onClick != null) b.Click += onClick;
            parent.Controls.Add(b);
            return b;
        }

        GroupBox AddGroup(string title, int x, int y, int w, int h)
        {
            GroupBox g = new GroupBox();
            g.Text = title;
            g.Location = new Point(x, y);
            g.Size = new Size(w, h);
            this.Controls.Add(g);
            return g;
        }

        RadioButton MakeRadio(Control parent, string text, int x, int y)
        {
            RadioButton r = new RadioButton();
            r.Text = text;
            r.Location = new Point(x, y);
            r.AutoSize = true;
            parent.Controls.Add(r);
            return r;
        }

        void InitUi()
        {
            // ============ 左侧: 方案列表 ============
            GroupBox gLeft = AddGroup("方案列表 (双击载入)", 12, 12, 280, 612);

            AddLabel(gLeft, "方案类型:", 16, 32, 68, false);
            cmbType = new ComboBox();
            cmbType.Location = new Point(92, 28);
            cmbType.Width = 176;
            cmbType.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbType.Items.Add("网卡IP方案");
            cmbType.Items.Add("代理方案");
            cmbType.SelectedIndex = 0;
            cmbType.SelectedIndexChanged += delegate { currentFile = null; ReloadProfiles(null); };
            gLeft.Controls.Add(cmbType);

            lstProfiles = new ListBox();
            lstProfiles.Location = new Point(16, 62);
            lstProfiles.Size = new Size(248, 392);
            lstProfiles.IntegralHeight = false;
            lstProfiles.ItemHeight = 24;
            lstProfiles.DoubleClick += delegate { LoadSelectedToEditor(); };
            gLeft.Controls.Add(lstProfiles);

            AddButton(gLeft, "删除方案", 16, 464, 124, 32, OnBtnDelete);
            AddButton(gLeft, "导入方案", 152, 464, 124, 32, OnBtnImport);
            AddButton(gLeft, "备份全部", 16, 504, 124, 32, OnBtnBackup);
            AddButton(gLeft, "打开文件夹", 152, 504, 124, 32, delegate { Process.Start("explorer.exe", profilesDir); });
            AddLabel(gLeft, "保存 / 应用 / 清空按钮", 16, 552, 248, true);
            AddLabel(gLeft, "位于右侧对应功能区", 16, 576, 248, true);

            // ============ 右上: 基本信息 + 网卡操作 ============
            GroupBox gBasic = AddGroup("基本信息 / 网卡操作", 306, 12, 742, 104);
            AddLabel(gBasic, "目标网卡:", 16, 32, 78, false);
            cmbAdapter = new ComboBox();
            cmbAdapter.Location = new Point(102, 28);
            cmbAdapter.Width = 440;
            cmbAdapter.DropDownStyle = ComboBoxStyle.DropDownList;
            gBasic.Controls.Add(cmbAdapter);
            AddButton(gBasic, "刷新", 556, 26, 64, 28, delegate { RefreshNicList(null); });
            chkAutoStart = new CheckBox();
            chkAutoStart.Text = "随系统启动";
            chkAutoStart.Location = new Point(636, 30);
            chkAutoStart.AutoSize = true;
            chkAutoStart.CheckedChanged += delegate
            {
                if (!suppressAutoStartEvent) AutostartHelper.Set(chkAutoStart.Checked);
            };
            gBasic.Controls.Add(chkAutoStart);

            AddButton(gBasic, "状态", 16, 64, 62, 28, delegate { OpenNicStatus(); });
            AddButton(gBasic, "属性", 86, 64, 62, 28, delegate { OpenNicProps(); });
            AddButton(gBasic, "IP设置", 156, 64, 74, 28, delegate { OpenIpSettings(); });
            AddButton(gBasic, "启用", 250, 64, 62, 28, delegate { SetNicAdmin(true); });
            AddButton(gBasic, "禁用", 320, 64, 62, 28, delegate { SetNicAdmin(false); });
            AddButton(gBasic, "重启网卡", 390, 64, 100, 28, delegate { RestartNic(); });
            lblNicStatus = AddLabel(gBasic, "窗口入口 / 网卡管理", 510, 69, 230, true);
            cmbAdapter.SelectedIndexChanged += delegate { SetNicStatus("窗口入口 / 网卡管理", 0); };

            // ============ 右中: 网卡IP方案 / IPv4 ============
            GroupBox g4 = AddGroup("网卡IP方案 / IPv4 设置", 306, 124, 742, 200);
            AddLabel(g4, "方案名称:", 16, 32, 78, false);
            txtIpName = AddTextBox(g4, 102, 28, 220);
            AddButton(g4, "获取当前设置", 350, 26, 112, 28, delegate { CaptureIp(); });
            AddButton(g4, "保存", 472, 26, 72, 28, OnBtnSaveIp);
            btnApply = AddButton(g4, "应用", 554, 26, 72, 28, OnBtnApplyIp);
            AddButton(g4, "清空", 636, 26, 72, 28, OnBtnClearIp);

            rbDhcp4 = MakeRadio(g4, "自动获取 (DHCP)", 16, 66);
            rbDhcp4.CheckedChanged += delegate { Toggle4(); };
            rbStatic4 = MakeRadio(g4, "静态地址", 200, 66);

            AddLabel(g4, "IP 地址:", 16, 104, 78, false);
            qIP4 = NewQuad(g4, 102, 100);
            AddLabel(g4, "子网掩码:", 320, 104, 76, false);
            qMask = NewQuad(g4, 404, 100);
            cmbMaskPreset = new ComboBox();
            cmbMaskPreset.Location = new Point(566, 99);
            cmbMaskPreset.Width = 118;
            cmbMaskPreset.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbMaskPreset.Items.Add("预设掩码 ▼");
            foreach (string m in MaskPresets()) cmbMaskPreset.Items.Add(m);
            cmbMaskPreset.SelectedIndex = 0;
            cmbMaskPreset.SelectedIndexChanged += delegate
            {
                if (cmbMaskPreset.SelectedIndex > 0) qMask.Text = cmbMaskPreset.SelectedItem.ToString();
            };
            g4.Controls.Add(cmbMaskPreset);
            AddLabel(g4, "网 关:", 16, 138, 78, false);
            qGW4 = NewQuad(g4, 102, 134);
            AddLabel(g4, "DNS 1:", 320, 138, 76, false);
            qDNS1 = NewQuad(g4, 404, 134);
            AddLabel(g4, "DNS 2:", 16, 172, 78, false);
            qDNS2 = NewQuad(g4, 102, 168);
            AddLabel(g4, "DNS 留空 = DHCP 恢复自动获取; 静态模式保持不变", 320, 172, 390, true);

            // ============ 右中: IPv6 ============
            GroupBox g6 = AddGroup("IPv6 设置", 306, 332, 742, 140);
            rbAuto6 = MakeRadio(g6, "自动获取", 16, 26);
            rbAuto6.CheckedChanged += delegate { Toggle6(); };
            rbStatic6 = MakeRadio(g6, "静态地址", 200, 26);

            AddLabel(g6, "IPv6 地址:", 16, 62, 82, false);
            txtIP6 = AddTextBox(g6, 106, 58, 250);
            AddLabel(g6, "前缀长度:", 386, 62, 76, false);
            txtPrefix6 = AddTextBox(g6, 470, 58, 60);
            txtPrefix6.Text = "64";
            AddLabel(g6, "(默认 64)", 542, 62, 90, true);
            AddLabel(g6, "IPv6 网关:", 16, 96, 82, false);
            txtGW6 = AddTextBox(g6, 106, 92, 250);
            AddLabel(g6, "自动模式依赖路由器 RA / DHCPv6, 建议实测", 386, 96, 330, true);

            // ============ 右下: 系统代理方案 ============
            GroupBox gP = AddGroup("系统代理方案 (Windows 全局设置, 作用于当前用户)", 306, 480, 742, 158);
            AddLabel(gP, "方案名称:", 16, 32, 78, false);
            txtProxyName = AddTextBox(gP, 102, 28, 180);
            AddButton(gP, "获取当前设置", 350, 26, 112, 28, delegate { CaptureProxy(); });
            AddButton(gP, "保存", 472, 26, 72, 28, OnBtnSaveProxy);
            btnApplyProxy = AddButton(gP, "应用", 554, 26, 72, 28, OnBtnApplyProxy);
            AddButton(gP, "清空", 636, 26, 72, 28, OnBtnClearProxy);

            chkProxy = new CheckBox();
            chkProxy.Text = "启用系统代理";
            chkProxy.Location = new Point(16, 66);
            chkProxy.AutoSize = true;
            gP.Controls.Add(chkProxy);
            AddLabel(gP, "代理地址:", 170, 68, 78, false);
            txtProxy = AddTextBox(gP, 256, 64, 210);
            AddLabel(gP, "如 127.0.0.1:7890", 478, 68, 150, true);
            chkBypassLocal = new CheckBox();
            chkBypassLocal.Text = "不代理本地地址 (<local>)";
            chkBypassLocal.Location = new Point(16, 98);
            chkBypassLocal.AutoSize = true;
            gP.Controls.Add(chkBypassLocal);
            AddLabel(gP, "IP 方案与代理方案分开保存、分开应用, 互不干扰; 修改立即生效。", 16, 128, 460, true);

            rbDhcp4.Checked = true;
            rbAuto6.Checked = true;
            chkAutoStart.Checked = AutostartHelper.Get();
        }

        // ================= Win11 风格主题 (亮/暗随系统) =================

        void ApplyTheme()
        {
            bool dark = Theme.IsDark();
            Color formBg, text, grayText, border, btnBg, inputBg, accent, accentFg;
            if (!dark)
            {
                formBg = Color.FromArgb(243, 243, 243);
                text = Color.FromArgb(27, 27, 27);
                grayText = Color.FromArgb(110, 110, 110);
                border = Color.FromArgb(200, 200, 200);
                btnBg = Color.White;
                inputBg = Color.White;
                accent = Color.FromArgb(0, 103, 192);
                accentFg = Color.White;
            }
            else
            {
                formBg = Color.FromArgb(32, 32, 32);
                text = Color.FromArgb(240, 240, 240);
                grayText = Color.FromArgb(150, 150, 150);
                border = Color.FromArgb(85, 85, 85);
                btnBg = Color.FromArgb(45, 45, 45);
                inputBg = Color.FromArgb(39, 39, 39);
                accent = Color.FromArgb(76, 194, 255);
                accentFg = Color.Black;
            }
            this.BackColor = formBg;
            foreach (Control c in GetAllChildren(this))
            {
                if (c is GroupBox)
                {
                    c.BackColor = formBg;
                    c.ForeColor = text;
                }
                else if (c is Panel)
                {
                    c.BackColor = formBg;
                }
                else if (c is Button)
                {
                    Button b = (Button)c;
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = border;
                    // 悬停/按下高亮, 提升质感
                    if (b == btnApply || b == btnApplyProxy)
                    {
                        b.BackColor = accent; b.ForeColor = accentFg;
                        b.FlatAppearance.MouseOverBackColor = dark
                            ? Color.FromArgb(96, 205, 255) : Color.FromArgb(21, 119, 214);
                    }
                    else
                    {
                        b.BackColor = btnBg; b.ForeColor = text;
                        b.FlatAppearance.MouseOverBackColor = dark
                            ? Color.FromArgb(58, 58, 58) : Color.FromArgb(232, 241, 251);
                        b.FlatAppearance.MouseDownBackColor = dark
                            ? Color.FromArgb(70, 70, 70) : Color.FromArgb(201, 224, 247);
                    }
                }
                else if (c is TextBox)
                {
                    TextBox tb = (TextBox)c;
                    tb.BackColor = inputBg;
                    tb.ForeColor = text;
                    tb.BorderStyle = BorderStyle.FixedSingle;
                }
                else if (c is ListBox)
                {
                    ListBox lbx = (ListBox)c;
                    lbx.BackColor = inputBg;
                    lbx.ForeColor = text;
                    lbx.BorderStyle = BorderStyle.FixedSingle;
                }
                else if (c is ComboBox)
                {
                    c.BackColor = inputBg;
                    c.ForeColor = text;
                }
                else if (c is Label)
                {
                    // 先统一处理, 灰色标签稍后覆盖
                    c.ForeColor = text;
                }
                else
                {
                    c.ForeColor = text;
                }
            }
            foreach (Label l in grayLabels) l.ForeColor = grayText;
        }

        List<Control> GetAllChildren(Control root)
        {
            List<Control> list = new List<Control>();
            foreach (Control c in root.Controls)
            {
                list.Add(c);
                list.AddRange(GetAllChildren(c));
            }
            return list;
        }

        // ================= 托盘常驻 =================

        void InitTray()
        {
            Icon appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            this.Icon = appIcon;

            tray = new NotifyIcon();
            tray.Icon = appIcon;
            tray.Text = "NetSwitcher - 网卡IP/代理一键切换";
            tray.Visible = true;

            trayMenu = new ContextMenuStrip();
            ToolStripMenuItem miShow = new ToolStripMenuItem("显示主界面");
            miShow.Font = new Font(miShow.Font, FontStyle.Bold);
            miShow.Click += delegate { ShowMain(); };
            ToolStripMenuItem miIp = new ToolStripMenuItem("切换网卡IP方案");
            ToolStripMenuItem miProxy = new ToolStripMenuItem("切换代理方案");
            ToolStripMenuItem miAuto = new ToolStripMenuItem("随系统启动");
            miAuto.CheckOnClick = true;
            miAuto.Checked = AutostartHelper.Get();
            miAuto.Click += delegate
            {
                suppressAutoStartEvent = true;
                chkAutoStart.Checked = miAuto.Checked;
                suppressAutoStartEvent = false;
            };
            ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { ExitApp(); };
            trayMenu.Items.Add(miShow);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(miIp);
            trayMenu.Items.Add(miProxy);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(miAuto);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(miExit);

            trayMenu.Opening += delegate
            {
                miIp.DropDownItems.Clear();
                miProxy.DropDownItems.Clear();
                foreach (ItemHolder it in LoadAllHolders())
                {
                    string file = it.File;
                    ToolStripMenuItem mi = new ToolStripMenuItem(it.Text);
                    mi.Click += delegate { TrayApply(file); };
                    if (it.Type == "Proxy") miProxy.DropDownItems.Add(mi);
                    else miIp.DropDownItems.Add(mi);
                }
                if (miIp.DropDownItems.Count == 0) miIp.DropDownItems.Add("(暂无方案)").Enabled = false;
                if (miProxy.DropDownItems.Count == 0) miProxy.DropDownItems.Add("(暂无方案)").Enabled = false;
            };

            trayTimer = new System.Windows.Forms.Timer();
            trayTimer.Interval = SystemInformation.DoubleClickTime;
            trayTimer.Tick += delegate
            {
                trayTimer.Stop();
                ShowTrayMenu();
            };
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) ShowTrayMenu();
                else if (e.Button == MouseButtons.Left) trayTimer.Start();
            };
            tray.DoubleClick += delegate
            {
                trayTimer.Stop();
                ShowMain();
            };
        }

        void ShowTrayMenu()
        {
            SetForegroundWindow(this.Handle);
            trayMenu.Show(Cursor.Position);
        }

        void ShowMain()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();
        }

        void HideToTray()
        {
            this.Hide();
            if (!balloonShown)
            {
                balloonShown = true;
                tray.ShowBalloonTip(2000, "NetSwitcher",
                    "程序已常驻通知栏。单击图标弹菜单, 双击打开主界面。", ToolTipIcon.Info);
            }
        }

        void ExitApp()
        {
            exiting = true;
            tray.Visible = false;
            Application.Exit();
        }

        void TrayApply(string file)
        {
            Profile p = Profile.Load(file);
            if (p.Type != "Proxy" && string.IsNullOrEmpty(p.Adapter))
            {
                tray.ShowBalloonTip(2500, "NetSwitcher",
                    "方案 \"" + p.Name + "\" 未指定目标网卡, 请在主界面载入后选择。", ToolTipIcon.Warning);
                return;
            }
            ApplyProfile(p, file);
            tray.ShowBalloonTip(1500, "NetSwitcher", "已应用方案: " + p.Name, ToolTipIcon.Info);
        }

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        // ================= 网卡列表 (WMI, 可见禁用状态的网卡) =================

        void RefreshNicList(string selectName)
        {
            string sel = null;
            NicItem oldSel = cmbAdapter.SelectedItem as NicItem;
            if (selectName != null) sel = selectName;
            else if (oldSel != null) sel = oldSel.Name;

            List<NicItem> items = new List<NicItem>();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT NetConnectionID, NetEnabled, NetConnectionStatus, GUID FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL"))
                {
                    foreach (ManagementObject o in searcher.Get())
                    {
                        string name = Convert.ToString(o["NetConnectionID"]);
                        if (string.IsNullOrEmpty(name)) continue;
                        bool enabled = o["NetEnabled"] != null && Convert.ToInt32(o["NetEnabled"]) == 1;
                        int st = o["NetConnectionStatus"] == null ? -1 : Convert.ToInt32(o["NetConnectionStatus"]);
                        string status;
                        if (!enabled) status = "已禁用";
                        else if (st == 2) status = "已启用·已连接";
                        else status = "已启用·未连接";
                        string guid = Convert.ToString(o["GUID"]);
                        items.Add(new NicItem(name, guid, enabled, status));
                    }
                }
            }
            catch
            {
                // WMI 异常时退回基础枚举 (此时看不到禁用的网卡)
                foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    items.Add(new NicItem(n.Name, n.Id, true,
                        n.OperationalStatus == OperationalStatus.Up ? "已启用·已连接" : "已启用·未连接"));
                }
            }
            // 启用的排前面, 同状态按名称排序
            items.Sort(delegate(NicItem a, NicItem b)
            {
                if (a.Enabled != b.Enabled) return a.Enabled ? -1 : 1;
                return string.Compare(a.Name, b.Name);
            });
            cmbAdapter.Items.Clear();
            foreach (NicItem it in items) cmbAdapter.Items.Add(it);
            foreach (NicItem it in items)
            {
                if (it.Name == sel) { cmbAdapter.SelectedItem = it; break; }
            }
            if (cmbAdapter.SelectedIndex < 0 && cmbAdapter.Items.Count > 0)
                cmbAdapter.SelectedIndex = 0;
        }

        NicItem SelectedNic()
        {
            return cmbAdapter.SelectedItem as NicItem;
        }

        // ================= 系统版本判断 (Win7 兼容) =================

        static bool IsWin8Plus()
        {
            Version v = Environment.OSVersion.Version;
            return v.Major > 6 || (v.Major == 6 && v.Minor >= 2);
        }

        static bool IsWin10Plus()
        {
            return Environment.OSVersion.Version.Major >= 10;
        }

        // ================= 网卡快捷操作 =================

        void OpenNicStatus()
        {
            NicItem n = SelectedNic();
            if (n == null) { Warn("请先选择网卡。"); return; }
            // 自绘状态窗口: 不依赖 van.dll, 精简系统 / Win7 / Win10 / Win11 全可用
            new StatusForm(n).ShowDialog(this);
        }

        void OpenNicProps()
        {
            NicItem n = SelectedNic();
            if (n == null) { Warn("请先选择网卡。"); return; }
            // 通过 Shell COM 在"网络连接"文件夹中找到网卡项, 直接调用其"属性"动词
            // -> 弹出真正的网卡属性对话框
            try
            {
                object shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                Type shellType = shell.GetType();
                object folder = shellType.InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, shell,
                    new object[] { "::{7007ACC7-3202-11D1-AAD2-00805FC1270E}" });
                if (folder != null)
                {
                    Type folderType = folder.GetType();
                    object items = folderType.InvokeMember("Items", BindingFlags.InvokeMethod, null, folder, null);
                    foreach (object item in (System.Collections.IEnumerable)items)
                    {
                        Type itemType = item.GetType();
                        string name = (string)itemType.InvokeMember("Name", BindingFlags.GetProperty, null, item, null);
                        if (name != n.Name) continue;
                        object verbs = itemType.InvokeMember("Verbs", BindingFlags.InvokeMethod, null, item, null);
                        foreach (object verb in (System.Collections.IEnumerable)verbs)
                        {
                            Type verbType = verb.GetType();
                            string vn = (string)verbType.InvokeMember("Name", BindingFlags.GetProperty, null, verb, null);
                            if (string.IsNullOrEmpty(vn)) continue;
                            if (vn.Contains("属性") || vn.ToLower().Contains("properties"))
                            {
                                verbType.InvokeMember("DoIt", BindingFlags.InvokeMethod, null, verb, null);
                                return;
                            }
                        }
                    }
                }
            }
            catch { }
            // 回退: 在"网络连接"窗口中定位选中该网卡
            string guid = (n.Guid ?? "").Trim();
            if (guid.Length > 0 && !guid.StartsWith("{")) guid = "{" + guid + "}";
            if (guid.Length > 2)
            {
                try
                {
                    Process.Start("explorer.exe",
                        "/select,\"::{7007ACC7-3202-11D1-AAD2-00805FC1270E}\\::" + guid + "\"");
                    return;
                }
                catch { }
            }
            Process.Start("control.exe", "ncpa.cpl");
        }

        void OpenIpSettings()
        {
            NicItem n = SelectedNic();
            // 系统设置程序存在才走 ms-settings (精简系统常被裁剪)
            string sysSettings = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "ImmersiveControlPanel", "SystemSettings.exe");
            if (!IsWin10Plus() || !File.Exists(sysSettings))
            {
                Process.Start("control.exe", "ncpa.cpl");
                return;
            }
            string page = "ms-settings:network-ethernet";
            if (n != null)
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.Name == n.Name && ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    {
                        page = "ms-settings:network-wifi";
                        break;
                    }
                }
            }
            try
            {
                Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
            }
            catch
            {
                Process.Start("control.exe", "ncpa.cpl");
            }
        }

        // 实时状态: 0=常规提示 1=进行中 2=成功 3=失败
        void SetNicStatus(string text, int kind)
        {
            if (lblNicStatus == null) return;
            lblNicStatus.Text = text;
            bool dark = Theme.IsDark();
            if (kind == 1)
                lblNicStatus.ForeColor = dark ? Color.FromArgb(250, 192, 94) : Color.FromArgb(176, 108, 0);
            else if (kind == 2)
                lblNicStatus.ForeColor = dark ? Color.FromArgb(108, 203, 95) : Color.FromArgb(0, 128, 0);
            else if (kind == 3)
                lblNicStatus.ForeColor = dark ? Color.FromArgb(255, 107, 107) : Color.FromArgb(200, 0, 0);
            else
                lblNicStatus.ForeColor = dark ? Color.FromArgb(150, 150, 150) : Color.Gray;
            Application.DoEvents();
        }

        // 轮询 WMI 等待网卡达到目标状态, 用于确认 netsh 命令真实生效
        bool WaitNicState(string name, bool enabled, int timeoutMs)
        {
            int waited = 0;
            string wmiName = name.Replace("'", "''");
            while (waited < timeoutMs)
            {
                Thread.Sleep(300);
                waited += 300;
                Application.DoEvents();
                try
                {
                    using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                        "SELECT NetEnabled FROM Win32_NetworkAdapter WHERE NetConnectionID='" + wmiName + "'"))
                    {
                        foreach (ManagementObject o in s.Get())
                        {
                            // 禁用后 NetEnabled 为 null, 视作未启用
                            bool en = o["NetEnabled"] != null && Convert.ToInt32(o["NetEnabled"]) == 1;
                            if (en == enabled) return true;
                        }
                    }
                }
                catch { }
            }
            return false;
        }

        void SetButtonsBusy(bool busy)
        {
            foreach (Control c in GetAllChildren(this))
            {
                Button b = c as Button;
                if (b != null && (b.Text == "启用" || b.Text == "禁用" || b.Text == "重启网卡"))
                    b.Enabled = !busy;
            }
        }

        void SetNicAdmin(bool enable)
        {
            NicItem n = SelectedNic();
            if (n == null) { Warn("请先选择网卡。"); return; }
            string act = enable ? "启用" : "禁用";
            SetButtonsBusy(true);
            this.Cursor = Cursors.WaitCursor;
            SetNicStatus("正在" + act + "……", 1);
            int code;
            Cmd.Run("interface set interface name=" + Cmd.Q(n.Name) +
                (enable ? " admin=enable" : " admin=disable"), out code);
            bool ok = WaitNicState(n.Name, enable, 8000);
            this.Cursor = Cursors.Default;
            SetButtonsBusy(false);
            RefreshNicList(n.Name);
            if (ok)
            {
                SetNicStatus(act + "成功", 2);
                Info(act + "成功。");
            }
            else
            {
                SetNicStatus(act + "失败, 请检查权限或网卡状态", 3);
                Warn(act + "失败, 请检查权限或网卡状态。");
            }
        }

        void RestartNic()
        {
            NicItem n = SelectedNic();
            if (n == null) { Warn("请先选择网卡。"); return; }
            if (MessageBox.Show("确定重启网卡 \"" + n.Name + "\" 吗? (先禁用再启用, 网络会短暂中断)",
                "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            SetButtonsBusy(true);
            this.Cursor = Cursors.WaitCursor;
            int code;
            SetNicStatus("正在禁用……", 1);
            Cmd.Run("interface set interface name=" + Cmd.Q(n.Name) + " admin=disable", out code);
            WaitNicState(n.Name, false, 10000);
            SetNicStatus("正在启用……", 1);
            Cmd.Run("interface set interface name=" + Cmd.Q(n.Name) + " admin=enable", out code);
            bool ok = WaitNicState(n.Name, true, 15000);
            this.Cursor = Cursors.Default;
            SetButtonsBusy(false);
            RefreshNicList(n.Name);
            if (ok)
            {
                SetNicStatus("重启成功", 2);
                Info("网卡重启成功。");
            }
            else
            {
                SetNicStatus("重启失败, 请手动检查网卡状态", 3);
                Warn("网卡重启失败, 请手动检查网卡状态。");
            }
        }

        // ================= 方案列表 =================

        string TypeKey() { return cmbType.SelectedIndex == 1 ? "Proxy" : "IP"; }

        string DisplayType(string t)
        {
            if (t == "Proxy") return "[代理] ";
            if (t == "All") return "[完整] ";
            return "[IP] ";
        }

        class ItemHolder
        {
            public string Text;
            public string File;
            public string Type;
            public ItemHolder(string text, string file, string type)
            {
                Text = text; File = file; Type = type;
            }
            public override string ToString() { return Text; }
        }

        List<ItemHolder> LoadAllHolders()
        {
            List<ItemHolder> list = new List<ItemHolder>();
            var files = Directory.GetFiles(profilesDir, "*.ini")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                try
                {
                    Profile p = Profile.Load(f);
                    list.Add(new ItemHolder(DisplayType(p.Type) + p.Name, f, p.Type));
                }
                catch { /* 跳过损坏文件 */ }
            }
            return list;
        }

        void ReloadProfiles(string selectName)
        {
            lstProfiles.Items.Clear();
            string want = TypeKey();
            foreach (ItemHolder it in LoadAllHolders())
            {
                bool show;
                if (want == "Proxy") show = (it.Type == "Proxy" || it.Type == "All");
                else show = (it.Type != "Proxy");
                if (!show) continue;
                lstProfiles.Items.Add(it);
            }
            if (selectName != null)
            {
                foreach (ItemHolder it in lstProfiles.Items)
                {
                    if (it.Text == selectName) { lstProfiles.SelectedItem = it; break; }
                }
            }
        }

        void LoadSelectedToEditor()
        {
            ItemHolder it = lstProfiles.SelectedItem as ItemHolder;
            if (it == null) return;
            Profile p = Profile.Load(it.File);
            if (p.Type == "Proxy")
            {
                WriteProxyEditor(p);
            }
            else
            {
                WriteIpEditor(p);
                currentFile = it.File;
            }
        }

        // ================= 数据绑定 (IP 方案与代理方案分离) =================

        void WriteIpEditor(Profile p)
        {
            txtIpName.Text = p.Name;
            foreach (NicItem it in cmbAdapter.Items)
            {
                if (it.Name == p.Adapter) { cmbAdapter.SelectedItem = it; break; }
            }
            rbDhcp4.Checked = p.Dhcp4;
            rbStatic4.Checked = !p.Dhcp4;
            qIP4.Text = p.IP4; qMask.Text = p.Mask4; qGW4.Text = p.GW4;
            qDNS1.Text = p.DNS1; qDNS2.Text = p.DNS2;
            cmbMaskPreset.SelectedIndex = 0;
            rbAuto6.Checked = p.Dhcp6;
            rbStatic6.Checked = !p.Dhcp6;
            txtIP6.Text = p.IP6; txtPrefix6.Text = p.Prefix6; txtGW6.Text = p.GW6;
        }

        void WriteProxyEditor(Profile p)
        {
            txtProxyName.Text = p.Name;
            chkProxy.Checked = p.ProxyEnabled;
            txtProxy.Text = p.ProxyServer;
            chkBypassLocal.Checked = p.BypassLocal;
        }

        Profile ReadIpEditor()
        {
            Profile p = new Profile();
            p.Name = txtIpName.Text.Trim();
            p.Type = "IP";
            NicItem nic = cmbAdapter.SelectedItem as NicItem;
            p.Adapter = (nic == null) ? "" : nic.Name;
            p.Dhcp4 = rbDhcp4.Checked;
            p.IP4 = qIP4.Text; p.Mask4 = qMask.Text; p.GW4 = qGW4.Text;
            p.DNS1 = qDNS1.IsEmpty() ? "" : qDNS1.Text;
            p.DNS2 = qDNS2.IsEmpty() ? "" : qDNS2.Text;
            p.Dhcp6 = rbAuto6.Checked;
            p.IP6 = txtIP6.Text.Trim(); p.Prefix6 = txtPrefix6.Text.Trim(); p.GW6 = txtGW6.Text.Trim();
            return p;
        }

        Profile ReadProxyEditor()
        {
            Profile p = new Profile();
            p.Name = txtProxyName.Text.Trim();
            p.Type = "Proxy";
            p.ProxyEnabled = chkProxy.Checked;
            p.ProxyServer = txtProxy.Text.Trim();
            p.BypassLocal = chkBypassLocal.Checked;
            return p;
        }

        string SanitizeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        void Toggle4()
        {
            bool isStatic = rbStatic4.Checked;
            qIP4.Enabled = isStatic; qMask.Enabled = isStatic; cmbMaskPreset.Enabled = isStatic;
            qGW4.Enabled = isStatic;
            // DNS 在 DHCP 模式下也可手动指定(留空则恢复自动获取), 始终可编辑
        }

        void Toggle6()
        {
            bool isStatic = rbStatic6.Checked;
            txtIP6.Enabled = isStatic; txtPrefix6.Enabled = isStatic; txtGW6.Enabled = isStatic;
        }

        // ================= 获取当前设置 (IP 与 代理 分开获取) =================

        NetworkInterface FindSelectedNic()
        {
            NicItem nic = SelectedNic();
            if (nic == null) { Warn("请先选择目标网卡。"); return null; }
            foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.Name == nic.Name) return n;
            }
            Warn("该网卡当前未启用或不存在, 请先启用网卡。");
            return null;
        }

        void CaptureIp()
        {
            NetworkInterface target = FindSelectedNic();
            if (target == null) return;
            IPInterfaceProperties props = target.GetIPProperties();

            // ---- IPv4: DHCP 状态 / IP / 掩码 / 网关 / DNS ----
            try
            {
                IPv4InterfaceProperties p4 = props.GetIPv4Properties();
                bool dhcp4 = p4 != null && p4.IsDhcpEnabled;
                rbDhcp4.Checked = dhcp4;
                rbStatic4.Checked = !dhcp4;
            }
            catch { }
            foreach (UnicastIPAddressInformation u in props.UnicastAddresses)
            {
                if (u.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                string s = u.Address.ToString();
                if (s.StartsWith("169.254.")) continue;
                qIP4.Text = s;
                try { qMask.Text = u.IPv4Mask.ToString(); } catch { }
                break;
            }
            foreach (GatewayIPAddressInformation g in props.GatewayAddresses)
            {
                if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    qGW4.Text = g.Address.ToString();
                    break;
                }
            }
            int dnsIdx = 0;
            foreach (System.Net.IPAddress dns in props.DnsAddresses)
            {
                if (dns.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                if (dnsIdx == 0) qDNS1.Text = dns.ToString();
                else if (dnsIdx == 1) { qDNS2.Text = dns.ToString(); break; }
                dnsIdx++;
            }

            // ---- IPv6: 全局地址 / 前缀 / 网关 ----
            bool hasGlobal6 = false;
            foreach (UnicastIPAddressInformation u in props.UnicastAddresses)
            {
                if (u.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) continue;
                if (u.Address.IsIPv6LinkLocal || u.Address.IsIPv6SiteLocal) continue;
                string s = u.Address.ToString();
                int pct = s.IndexOf('%');
                if (pct >= 0) s = s.Substring(0, pct);
                txtIP6.Text = s;
                try { txtPrefix6.Text = u.PrefixLength.ToString(); } catch { }
                hasGlobal6 = true;
                break;
            }
            foreach (GatewayIPAddressInformation g in props.GatewayAddresses)
            {
                if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    txtGW6.Text = g.Address.ToString();
                    break;
                }
            }
            rbStatic6.Checked = hasGlobal6;
            rbAuto6.Checked = !hasGlobal6;

            Info("已读取当前 IP 设置到\"网卡IP方案\"编辑区。\r\n" +
                "获取后仍可任意修改各项数值, 确认后可\"应用\"或\"保存\"。\r\n" +
                "(若 IPv6 实际为自动获取, 请手动点回\"自动获取\")");
        }

        void CaptureProxy()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false))
            {
                if (key != null)
                {
                    object en = key.GetValue("ProxyEnable");
                    chkProxy.Checked = (en is int && (int)en == 1);
                    object sv = key.GetValue("ProxyServer");
                    txtProxy.Text = (sv == null) ? "" : sv.ToString();
                    object ov = key.GetValue("ProxyOverride");
                    chkBypassLocal.Checked = (ov != null && ov.ToString().IndexOf("<local>") >= 0);
                }
            }
            Info("已读取当前系统代理设置到\"系统代理方案\"编辑区, 获取后仍可任意修改。");
        }

        // ================= 方案保存 / 校验 =================

        string FileForName(Profile p)
        {
            return Path.Combine(profilesDir, SanitizeName(p.Name) + ".ini");
        }

        // 保存: 同名方案提示覆盖; 选"否"则要求修改名称, 不允许出现相同方案名
        void SaveProfileCore(Profile p)
        {
            if (p.Name.Length == 0) { Warn("方案名称不能为空, 请先填写方案名称。"); return; }
            string file = FileForName(p);
            if (File.Exists(file))
            {
                DialogResult r = MessageBox.Show("已存在同名方案 \"" + p.Name + "\", 是否覆盖?",
                    "确认覆盖", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes)
                {
                    Warn("已取消保存。请修改方案名称, 不允许存在相同名称的方案。");
                    return;
                }
            }
            p.Save(file);
            if (p.Type != "Proxy") currentFile = file;
            ReloadProfiles(DisplayType(p.Type) + p.Name);
            Info("已保存" + (p.Type == "Proxy" ? "代理" : "IP") + "方案: " + p.Name);
        }

        bool TryValidateIpProfile(Profile p)
        {
            if (p.Name.Length == 0) { Warn("方案名称不能为空。"); return false; }
            if (string.IsNullOrEmpty(p.Adapter)) { Warn("请选择目标网卡。"); return false; }
            if (!p.Dhcp4)
            {
                if (!IsValidIPv4(p.IP4)) { Warn("IPv4 地址不完整或格式不正确(每段 0-255)。"); return false; }
                if (!IsValidIPv4(p.Mask4)) { Warn("子网掩码不能为空且格式需正确。"); return false; }
                if (!IsValidIPv4(p.GW4)) { Warn("静态模式下网关不能为空且格式需正确。"); return false; }
            }
            if (p.DNS1.Length > 0 && !IsValidIPv4(p.DNS1))
            { Warn("DNS 1 格式不正确(每段 0-255), 不需要请留空。"); return false; }
            if (p.DNS2.Length > 0 && !IsValidIPv4(p.DNS2))
            { Warn("DNS 2 格式不正确(每段 0-255), 不需要请留空。"); return false; }
            if (!p.Dhcp6)
            {
                if (!IsValidIPv6(p.IP6)) { Warn("IPv6 地址格式不正确。"); return false; }
                int pre;
                if (!int.TryParse(p.Prefix6, out pre) || pre < 0 || pre > 128)
                { Warn("前缀长度必须是 0-128 的数字。"); return false; }
                if (!IsValidIPv6(p.GW6)) { Warn("静态模式下 IPv6 网关不能为空且格式需正确。"); return false; }
            }
            return true;
        }

        bool TryValidateProxyProfile(Profile p)
        {
            if (p.Name.Length == 0) { Warn("代理方案名称不能为空。"); return false; }
            if (p.ProxyEnabled && p.ProxyServer.Length == 0)
            { Warn("已启用代理, 但代理地址不能为空。"); return false; }
            return true;
        }

        static bool IsValidIPv4(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string[] seg = s.Split('.');
            if (seg.Length != 4) return false;
            foreach (string p in seg)
            {
                if (p.Length == 0 || p.Length > 3) return false;
                foreach (char c in p) { if (!char.IsDigit(c)) return false; }
                if (int.Parse(p) > 255) return false;
            }
            return true;
        }

        static bool IsValidIPv6(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            try
            {
                System.Net.IPAddress a = System.Net.IPAddress.Parse(s);
                return a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
            }
            catch { return false; }
        }

        static string[] MaskPresets()
        {
            return new string[] {
                "255.255.255.255", "255.255.255.252", "255.255.255.248", "255.255.255.240",
                "255.255.255.224", "255.255.255.192", "255.255.255.128", "255.255.255.0",
                "255.255.254.0", "255.255.252.0", "255.255.248.0", "255.255.240.0",
                "255.255.224.0", "255.255.192.0", "255.255.128.0", "255.255.0.0",
                "255.254.0.0", "255.252.0.0", "255.248.0.0", "255.240.0.0",
                "255.224.0.0", "255.192.0.0", "255.128.0.0", "255.0.0.0",
                "254.0.0.0", "252.0.0.0", "248.0.0.0", "240.0.0.0",
                "224.0.0.0", "192.0.0.0", "128.0.0.0"
            };
        }

        void OnBtnSaveIp(object sender, EventArgs e)
        {
            Profile p = ReadIpEditor();
            if (!TryValidateIpProfile(p)) return;
            SaveProfileCore(p);
        }

        void OnBtnSaveProxy(object sender, EventArgs e)
        {
            Profile p = ReadProxyEditor();
            if (!TryValidateProxyProfile(p)) return;
            SaveProfileCore(p);
        }

        void OnBtnDelete(object sender, EventArgs e)
        {
            ItemHolder it = lstProfiles.SelectedItem as ItemHolder;
            if (it == null) { Warn("请先选中要删除的方案。"); return; }
            if (MessageBox.Show("确定删除方案 \"" + it.Text + "\" 吗?", "确认",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            File.Delete(it.File);
            if (currentFile == it.File) currentFile = null;
            ReloadProfiles(null);
            Info("已删除。");
        }

        void OnBtnImport(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "导入方案文件";
                dlg.Filter = "方案文件 (*.ini)|*.ini|所有文件 (*.*)|*.*";
                dlg.Multiselect = true;
                if (dlg.ShowDialog() != DialogResult.OK) return;
                int count = 0;
                foreach (string src in dlg.FileNames)
                {
                    string dst = Path.Combine(profilesDir, Path.GetFileName(src));
                    File.Copy(src, dst, true);
                    count++;
                }
                ReloadProfiles(null);
                Info("已导入 " + count + " 个方案文件。");
            }
        }

        void OnBtnBackup(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择备份目录, 所有方案 INI 将复制到该目录";
                if (dlg.ShowDialog() != DialogResult.OK) return;
                var files = Directory.GetFiles(profilesDir, "*.ini");
                foreach (string f in files)
                    File.Copy(f, Path.Combine(dlg.SelectedPath, Path.GetFileName(f)), true);
                Info("已备份 " + files.Length + " 个方案到:\r\n" + dlg.SelectedPath);
            }
        }

        // ================= 应用 / 清空 =================

        void OnBtnApplyIp(object sender, EventArgs e)
        {
            Profile p = ReadIpEditor();
            if (!TryValidateIpProfile(p)) return;
            string f = FileForName(p);
            ApplyProfile(p, File.Exists(f) ? f : null);
        }

        void OnBtnApplyProxy(object sender, EventArgs e)
        {
            Profile p = ReadProxyEditor();
            if (!TryValidateProxyProfile(p)) return;
            ApplyProfile(p, null);
        }

        void OnBtnClearIp(object sender, EventArgs e)
        {
            txtIpName.Text = "";
            qIP4.ClearBoxes(); qMask.ClearBoxes(); qGW4.ClearBoxes();
            qDNS1.ClearBoxes(); qDNS2.ClearBoxes();
            cmbMaskPreset.SelectedIndex = 0;
            rbDhcp4.Checked = true;
            rbAuto6.Checked = true;
            txtIP6.Text = ""; txtPrefix6.Text = "64"; txtGW6.Text = "";
            currentFile = null;
            Info("已清空\"网卡IP方案\"编辑区 (目标网卡选择保留)。");
        }

        void OnBtnClearProxy(object sender, EventArgs e)
        {
            txtProxyName.Text = "";
            chkProxy.Checked = false;
            txtProxy.Text = "";
            chkBypassLocal.Checked = true;
            Info("已清空\"系统代理方案\"编辑区。");
        }

        string ApplyProfile(Profile p, string profileFile)
        {
            bool doIp = (p.Type != "Proxy");
            bool doProxy = (p.Type != "IP");

            if (doIp)
            {
                if (string.IsNullOrEmpty(p.Adapter)) return "未选择目标网卡";
                if (!p.Dhcp4 && p.IP4.Length == 0) return "IPv4 静态模式下未填写 IP 地址";
                if (!p.Dhcp6 && p.IP6.Length == 0) return "IPv6 静态模式下未填写 IPv6 地址";
            }

            StringBuilder log = new StringBuilder();
            int code;
            string nic = p.Adapter;

            // ---------- IPv4 / DNS / IPv6 ----------
            if (doIp)
            {
                if (p.Dhcp4)
                {
                    string outp = Cmd.Run("interface ipv4 set address name=" + Cmd.Q(nic) + " source=dhcp", out code);
                    log.AppendLine("[IPv4 DHCP] " + Result(code, outp));
                }
                else
                {
                    string args = "interface ipv4 set address name=" + Cmd.Q(nic) + " static " + p.IP4;
                    if (p.Mask4.Length > 0) args += " " + p.Mask4;
                    if (p.GW4.Length > 0) args += " " + p.GW4 + " 1";
                    string outp = Cmd.Run(args, out code);
                    log.AppendLine("[IPv4 静态] " + Result(code, outp));
                }
                if (p.DNS1.Length > 0)
                {
                    string outp = Cmd.Run("interface ipv4 set dnsservers name=" + Cmd.Q(nic) +
                        " static " + p.DNS1 + " primary", out code);
                    log.AppendLine("[DNS 1] " + Result(code, outp));
                    if (p.DNS2.Length > 0)
                    {
                        outp = Cmd.Run("interface ipv4 add dnsservers name=" + Cmd.Q(nic) +
                            " address=" + p.DNS2 + " index=2", out code);
                        log.AppendLine("[DNS 2] " + Result(code, outp));
                    }
                }
                else if (p.Dhcp4)
                {
                    string outp = Cmd.Run("interface ipv4 set dnsservers name=" + Cmd.Q(nic) + " source=dhcp", out code);
                    log.AppendLine("[DNS 自动] " + Result(code, outp));
                }

                if (p.Dhcp6)
                {
                    if (profileFile != null && File.Exists(profileFile))
                    {
                        string last = new IniFile(profileFile).Read("IPv6", "LastIP", "");
                        if (last.Length > 0)
                            Cmd.Run("interface ipv6 delete address interface=" + Cmd.Q(nic) + " address=" + last, out code);
                    }
                    string outp = Cmd.Run("interface ipv6 set interface interface=" + Cmd.Q(nic) +
                        " routerdiscovery=dhcp", out code);
                    if (code != 0)
                        outp = Cmd.Run("interface ipv6 set interface interface=" + Cmd.Q(nic) +
                            " routerdiscovery=enabled", out code);
                    log.AppendLine("[IPv6 自动] " + Result(code, outp));
                    if (profileFile != null && File.Exists(profileFile))
                        new IniFile(profileFile).Write("IPv6", "LastIP", "");
                }
                else
                {
                    string outp = Cmd.Run("interface ipv6 add address interface=" + Cmd.Q(nic) +
                        " address=" + p.IP6 + " prefixlength=" + p.Prefix6, out code);
                    if (code != 0)
                        outp = Cmd.Run("interface ipv6 set address interface=" + Cmd.Q(nic) +
                            " address=" + p.IP6 + " prefixlength=" + p.Prefix6, out code);
                    log.AppendLine("[IPv6 静态] " + Result(code, outp));
                    if (p.GW6.Length > 0)
                    {
                        outp = Cmd.Run("interface ipv6 set route ::/0 interface=" + Cmd.Q(nic) +
                            " nexthop=" + p.GW6, out code);
                        if (code != 0)
                            outp = Cmd.Run("interface ipv6 add route ::/0 interface=" + Cmd.Q(nic) +
                                " nexthop=" + p.GW6, out code);
                        log.AppendLine("[IPv6 网关] " + Result(code, outp));
                    }
                    if (profileFile != null && File.Exists(profileFile))
                        new IniFile(profileFile).Write("IPv6", "LastIP", p.IP6);
                }
            }

            // ---------- 系统代理 ----------
            if (doProxy)
            {
                try
                {
                    ProxyHelper.Apply(p.ProxyEnabled, p.ProxyServer, p.BypassLocal);
                    log.AppendLine("[系统代理] " + (p.ProxyEnabled
                        ? "已启用 " + p.ProxyServer : "已关闭"));
                }
                catch (Exception ex)
                {
                    log.AppendLine("[系统代理] 失败: " + ex.Message);
                }
            }

            string msg = log.ToString();
            if (this.Visible)
            {
                MessageBox.Show("切换完成, 明细如下:\r\n\r\n" + msg,
                    "NetSwitcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return msg;
        }

        string Result(int code, string output)
        {
            if (code == 0) return "成功";
            return "失败 -> " + output;
        }

        void Warn(string msg)
        {
            MessageBox.Show(msg, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void Info(string msg)
        {
            MessageBox.Show(msg, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
