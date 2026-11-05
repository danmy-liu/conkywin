using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ConkyWin
{
    public partial class MainWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int HWND_BOTTOM = 1;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOACTIVATE = 0x0010;
        private const int SWP_NOOWNERZORDER = 0x0200;

        private PerformanceCounter _cpuCounter;
        private PerformanceCounter _procCounter;
        private ulong _totalMem;
        private long _lastRx, _lastTx;
        private DateTime _lastNetSample = DateTime.MinValue;
        private int _gpuTick = 0;
        private Config _cfg = new Config();
        private FileSystemWatcher _cfgWatcher;
        private readonly DispatcherTimer _timer;

        // Lua 脚本宿主（script.lua 可选）与数据快照（每秒由 FillSnapshot 填充）
        private readonly ScriptHost _scriptHost = new ScriptHost();
        private readonly ScriptHost.Snapshot _snap = new ScriptHost.Snapshot();

        public MainWindow()
        {
            InitializeComponent();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => Update();
            Loaded += (s, e) => _timer.Start();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 加载配置（exe 同目录 config.json，不存在则自动生成默认）
                _cfg = LoadConfig() ?? new Config();
                var hwnd = new WindowInteropHelper(this).Handle;
                // 点击穿透 + 工具窗口（不抢焦点、不挡鼠标）
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
                // 压到桌面层（等效 Conky below），位置取配置
                SetWindowPos(hwnd, (IntPtr)HWND_BOTTOM, _cfg.Position.X, _cfg.Position.Y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                ApplyConfig(_cfg);

                // Lua 脚本（可选）：exe 同目录 script.lua，不存在则一切行为与内置版一致
                _scriptHost.FontSize = _cfg.FontSize;
                _scriptHost.TimeFontSize = _cfg.TimeFontSize;
                _scriptHost.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "script.lua"), _snap);
                if (_scriptHost.Loaded)
                    Log("script.lua 已加载，mode=" + (_scriptHost.FullMode ? "full(conky_rows)" : "enhance(hooks)"));

                // 配置热重载：改 config.json 保存后自动生效
                string cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                try
                {
                    _cfgWatcher = new FileSystemWatcher(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                    DateTime lastReload = DateTime.MinValue;
                    _cfgWatcher.Changed += (s, ev) =>
                    {
                        try
                        {
                            // 编辑器保存时会连发多次 Changed 事件，200ms 内只处理一次
                            if ((DateTime.Now - lastReload).TotalMilliseconds < 200) return;
                            lastReload = DateTime.Now;
                            Dispatcher.Invoke(() =>
                            {
                                var c = LoadConfig();
                                if (c == null) return;
                                _cfg = c;
                                SetWindowPos(hwnd, (IntPtr)HWND_BOTTOM, c.Position.X, c.Position.Y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                                ApplyConfig(c);
                            });
                        }
                        catch { }
                    };
                    _cfgWatcher.EnableRaisingEvents = true;
                }
                catch { }
            }
            catch { }

            try
            {
                _cpuCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
                _cpuCounter.NextValue(); // 预热
            }
            catch { }
            try { _procCounter = new PerformanceCounter("System", "Processes"); } catch { }

            var m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
            if (GlobalMemoryStatusEx(ref m)) _totalMem = m.ullTotalPhys;

            // CPU 温度：普通权限无法读取内核温度（Ring0 驱动需管理员），显示占位
            TempVal.Text = "--℃";

            Update();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _timer?.Stop();
            try { _cfgWatcher?.Dispose(); } catch { }
            try { _cpuCounter?.Dispose(); } catch { }
            try { _procCounter?.Dispose(); } catch { }
        }

        private void Update()
        {
            FillSnapshot();

            // 完整脚本模式：整面板由 script.lua 的 conky_rows() 生成
            if (_scriptHost.Loaded && _scriptHost.FullMode)
            {
                RenderScriptPanel();
                return;
            }

            try
            {
                var s = _snap;

                // 时间
                TimeVal.Text = H("conky_time_text", DateTime.Now.ToString("HH:mm:ss"));

                // CPU
                CpuVal.Text = H("conky_cpu_text", s.Cpu.ToString("0") + "%", s.Cpu);
                SetBar(CpuBar, s.Cpu);
                CpuVal.Foreground = Brush(HC("conky_cpu_color", _cfg.Cpu.ValueColor, s.Cpu));
                ApplyBarColor(CpuBar, "cpu", s.Cpu);

                // 内存
                if (s.MemTotalGb > 0)
                {
                    MemVal.Text = H("conky_mem_text", s.MemUsedGb.ToString("0.0") + "/" + s.MemTotalGb.ToString("0.0") + "G",
                        s.MemUsedGb, s.MemTotalGb, s.MemPct);
                    SetBar(MemBar, s.MemPct);
                    MemVal.Foreground = Brush(HC("conky_mem_color", _cfg.Mem.ValueColor, s.MemPct));
                    ApplyBarColor(MemBar, "mem", s.MemPct);
                }

                // 磁盘 C:
                if (s.DiskAvailable)
                {
                    DiskVal.Text = H("conky_disk_text", s.DiskUsedGb.ToString("0") + "/" + s.DiskTotalGb.ToString("0") + "G",
                        s.DiskUsedGb, s.DiskTotalGb, s.DiskPct);
                    SetBar(DiskBar, s.DiskPct);
                    DiskVal.Foreground = Brush(HC("conky_disk_color", _cfg.Disk.ValueColor, s.DiskPct));
                    ApplyBarColor(DiskBar, "disk", s.DiskPct);
                }

                // 网络
                NetVal.Text = H("conky_net_text", "↓" + FormatRate(s.NetDownKb) + " ↑" + FormatRate(s.NetUpKb),
                    s.NetDownKb, s.NetUpKb);
                NetVal.Foreground = Brush(HC("conky_net_color", _cfg.Net.ValueColor, s.NetDownKb, s.NetUpKb));

                // 进程数
                if (s.Proc >= 0)
                {
                    ProcVal.Text = H("conky_proc_text", s.Proc.ToString(), s.Proc);
                    ProcVal.Foreground = Brush(HC("conky_proc_color", _cfg.Proc.ValueColor, (double)s.Proc));
                }

                // GPU（nvidia-smi 每 2 秒采样）
                _gpuTick++;
                if (_gpuTick % 2 == 0) UpdateGpu();
                if (s.GpuAvailable)
                {
                    double barPct = GpuBarPct(s);
                    GpuVal.Text = H("conky_gpu_text",
                        s.GpuUtil.ToString("0") + "% " + s.GpuMemUsedGb.ToString("0.0") + "G " + s.GpuTemp.ToString("0") + "℃",
                        s.GpuUtil, s.GpuMemUsedGb, s.GpuTemp);
                    SetBar(GpuBar, barPct);
                    GpuVal.Foreground = Brush(HC("conky_gpu_color", _cfg.Gpu.ValueColor, s.GpuUtil));
                    ApplyBarColor(GpuBar, "gpu", barPct);
                }

                // TEMP 行：普通权限无读取通道，默认占位（GPU 温度在 GPU 行显示）
                TempVal.Text = H("conky_temp_text", "--℃", (object)null);
                TempVal.Foreground = Brush(HC("conky_temp_color", _cfg.Temp.ValueColor, (object)null));
            }
            catch { }
        }

        private static double GpuBarPct(ScriptHost.Snapshot s)
        {
            double memPct = s.GpuMemTotalGb > 0 ? s.GpuMemUsedGb / s.GpuMemTotalGb * 100 : 0;
            return Math.Max(s.GpuUtil, Math.Min(100, memPct));
        }

        /// <summary>每秒采集一次数据快照，供内置渲染与 Lua 脚本共用。</summary>
        private void FillSnapshot()
        {
            var s = _snap;

            double cpu = 0;
            try { cpu = Math.Max(0, Math.Min(100, _cpuCounter.NextValue())); } catch { }
            s.Cpu = cpu;

            if (_totalMem > 0)
            {
                try
                {
                    var m = new MEMORYSTATUSEX();
                    m.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
                    if (GlobalMemoryStatusEx(ref m))
                    {
                        s.MemUsedGb = (_totalMem - m.ullAvailPhys) / 1024.0 / 1024.0 / 1024.0;
                        s.MemTotalGb = _totalMem / 1024.0 / 1024.0 / 1024.0;
                        s.MemPct = Math.Min(100, Math.Max(0, (double)(_totalMem - m.ullAvailPhys) / _totalMem * 100));
                    }
                }
                catch { }
            }

            try
            {
                var di = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.Name.StartsWith("C"));
                if (di != null)
                {
                    s.DiskAvailable = true;
                    s.DiskTotalGb = di.TotalSize / 1024.0 / 1024.0 / 1024.0;
                    s.DiskUsedGb = s.DiskTotalGb - di.TotalFreeSpace / 1024.0 / 1024.0 / 1024.0;
                    s.DiskPct = Math.Min(100, Math.Max(0, s.DiskUsedGb / s.DiskTotalGb * 100));
                }
                else { Log("disk: no C drive found; drives=" + string.Join(",", DriveInfo.GetDrives().Select(d => d.Name + ":" + d.IsReady + ":" + d.DriveType))); }
            }
            catch (Exception ex) { Log("disk error: " + ex); }

            // 网络（下行/上行 KB/s 差分）
            try
            {
                var nics = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                (n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                                 n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                                 n.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet))
                    .ToList();
                long rx = 0, tx = 0;
                foreach (var n in nics)
                {
                    var st = n.GetIPv4Statistics();
                    rx += st.BytesReceived;
                    tx += st.BytesSent;
                }
                if (_lastNetSample != DateTime.MinValue)
                {
                    double sec = (DateTime.Now - _lastNetSample).TotalSeconds;
                    if (sec > 0)
                    {
                        s.NetDownKb = Math.Max(0, (rx - _lastRx) / sec / 1024.0);
                        s.NetUpKb = Math.Max(0, (tx - _lastTx) / sec / 1024.0);
                    }
                }
                _lastRx = rx; _lastTx = tx; _lastNetSample = DateTime.Now;
            }
            catch { }

            try { s.Proc = (int)_procCounter.NextValue(); } catch { s.Proc = -1; }

            // GPU 数据由 UpdateGpu（每 2 秒）填充
        }

        private void UpdateGpu()
        {
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi",
                    "--query-gpu=temperature.gpu,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    string line = p.StandardOutput.ReadToEnd().Trim();
                    var parts = line.Split(',');
                    if (parts.Length >= 4)
                    {
                        // nvidia-smi 输出与系统区域设置无关，但用 InvariantCulture 更稳妥
                        var ci = CultureInfo.InvariantCulture;
                        _snap.GpuTemp = double.Parse(parts[0].Trim(), ci);
                        _snap.GpuUtil = Math.Min(100, Math.Max(0, double.Parse(parts[1].Trim(), ci)));
                        _snap.GpuMemUsedGb = double.Parse(parts[2].Trim(), ci) / 1024.0;
                        _snap.GpuMemTotalGb = double.Parse(parts[3].Trim(), ci) / 1024.0;
                        _snap.GpuAvailable = true;
                    }
                }
            }
            catch { }
        }

        private static string FormatRate(double kbPerSec)
        {
            if (kbPerSec >= 1024) return (kbPerSec / 1024).ToString("0.0") + "M";
            return kbPerSec.ToString("0") + "K";
        }

        private static void Log(string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log"),
                    DateTime.Now.ToString("HH:mm:ss") + " " + msg + Environment.NewLine);
            }
            catch { }
        }

        private void SetBar(System.Windows.Shapes.Rectangle bar, double pct)
        {
            double w = Math.Max(0, Math.Min(100, pct)) / 100.0 * ActualWidth;
            if (bar.Width != w) bar.Width = w;
        }

        // ---------- Lua 增强模式辅助 ----------

        /// <summary>调用文本钩子；脚本未定义或返回 nil 时用内置 fallback。</summary>
        private string H(string hook, string fallback, params object[] args)
        {
            if (!_scriptHost.Loaded) return fallback;
            return _scriptHost.Text(hook, args) ?? fallback;
        }

        /// <summary>调用颜色钩子；脚本未定义或返回 nil 时用内置 fallback。</summary>
        private string HC(string hook, string fallback, params object[] args)
        {
            if (!_scriptHost.Loaded) return fallback;
            return _scriptHost.Color(hook, args) ?? fallback;
        }

        /// <summary>conky_bar_color(name, pct)：返回单色或 {起点,终点} 表时覆盖进度条渐变。</summary>
        private void ApplyBarColor(System.Windows.Shapes.Rectangle bar, string name, double pct)
        {
            if (!_scriptHost.Loaded) return;
            var (start, end) = _scriptHost.BarColor(name, pct);
            if (start != null && end != null) bar.Fill = Gradient(start, end);
        }

        // ---------- Lua 完整模式（conky_rows）动态面板 ----------

        private class DynRow
        {
            public TextBlock Label, Value;
            public Border BarBg;
            public System.Windows.Shapes.Rectangle Bar;
        }

        private readonly List<DynRow> _dynRows = new List<DynRow>();
        private readonly List<string> _dynLabels = new List<string>();
        private readonly List<bool> _dynHasBar = new List<bool>();

        private void RenderScriptPanel()
        {
            var rows = _scriptHost.GetRows();
            if (rows == null) return; // 脚本出错：保留上一帧画面

            // 行结构（数量/标签/有无进度条）变化才重建控件，否则只刷新内容
            bool changed = rows.Count != _dynRows.Count;
            if (!changed)
                for (int i = 0; i < rows.Count; i++)
                    if (_dynLabels[i] != rows[i].Label || _dynHasBar[i] != rows[i].Bar.HasValue) { changed = true; break; }

            if (changed)
            {
                _dynRows.Clear(); _dynLabels.Clear(); _dynHasBar.Clear();
                RootPanel.Children.Clear();
                foreach (var r in rows) AddDynRow(r);
            }

            for (int i = 0; i < Math.Min(rows.Count, _dynRows.Count); i++)
                RefreshDynRow(_dynRows[i], rows[i]);
        }

        private void AddDynRow(ScriptHost.Row r)
        {
            bool first = RootPanel.Children.Count == 0;
            var grid = new Grid { Margin = new Thickness(0, first ? 0 : 7, 0, 0) };
            var label = new TextBlock
            {
                Text = r.Label,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 11,
                Foreground = Brush(_cfg.LabelColor),
                VerticalAlignment = VerticalAlignment.Center
            };
            var value = new TextBlock
            {
                Text = r.Text,
                FontFamily = new FontFamily("Consolas"),
                FontSize = _cfg.FontSize,
                FontWeight = _cfg.Bold ? FontWeights.Bold : FontWeights.Normal,
                Foreground = Brush(r.Color ?? "#EAF2FC"),
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(label);
            grid.Children.Add(value);
            RootPanel.Children.Add(grid);

            var dr = new DynRow { Label = label, Value = value };
            if (r.Bar.HasValue)
            {
                var bg = new Border
                {
                    Height = 4,
                    CornerRadius = new CornerRadius(2),
                    Background = Brush(_cfg.BarBackground),
                    Margin = new Thickness(0, 3, 0, 0)
                };
                var bar = new System.Windows.Shapes.Rectangle
                {
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = Gradient(r.BarStart ?? "#EAF2FC", r.BarEnd ?? "#EAF2FC"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = 0
                };
                bg.Child = bar;
                RootPanel.Children.Add(bg);
                dr.BarBg = bg;
                dr.Bar = bar;
            }
            _dynRows.Add(dr);
            _dynLabels.Add(r.Label);
            _dynHasBar.Add(r.Bar.HasValue);
        }

        private void RefreshDynRow(DynRow dr, ScriptHost.Row r)
        {
            dr.Value.Text = r.Text;
            dr.Value.Foreground = Brush(r.Color ?? "#EAF2FC");
            if (dr.Bar != null && r.Bar.HasValue)
            {
                double w = Math.Max(0, Math.Min(100, r.Bar.Value)) / 100.0 * dr.BarBg.ActualWidth;
                if (dr.Bar.Width != w) dr.Bar.Width = w;
                if (r.BarStart != null && r.BarEnd != null)
                    dr.Bar.Fill = Gradient(r.BarStart, r.BarEnd);
            }
        }

        private void UpdateDynFonts(Config c)
        {
            foreach (var dr in _dynRows)
            {
                dr.Label.Foreground = Brush(c.LabelColor);
                dr.Value.FontSize = c.FontSize;
                dr.Value.FontWeight = c.Bold ? FontWeights.Bold : FontWeights.Normal;
                if (dr.BarBg != null) dr.BarBg.Background = Brush(c.BarBackground);
            }
        }

        // ---------- 配置文件 ----------

        private static Config LoadConfig()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            try
            {
                if (File.Exists(path))
                {
                    var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(path));
                    return c; // 解析失败返回 null，调用方保留现有配置
                }
                var def = new Config();
                File.WriteAllText(path, JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = true }));
                return def;
            }
            catch { return null; }
        }

        private void ApplyConfig(Config c)
        {
            try
            {
                TimeVal.FontSize = c.TimeFontSize;
                foreach (var tb in new[] { CpuVal, MemVal, DiskVal, NetVal, GpuVal, TempVal, ProcVal })
                    tb.FontSize = c.FontSize;
                var bold = c.Bold ? FontWeights.Bold : FontWeights.Normal;
                foreach (var tb in new[] { CpuVal, MemVal, DiskVal, NetVal, GpuVal, TempVal, ProcVal, TimeVal })
                    tb.FontWeight = bold;

                var label = Brush(c.LabelColor);
                foreach (var l in new[] { LabelCpu, LabelMem, LabelDisk, LabelNet, LabelGpu, LabelTemp, LabelProc })
                    l.Foreground = label;

                CpuVal.Foreground = Brush(c.Cpu.ValueColor);
                MemVal.Foreground = Brush(c.Mem.ValueColor);
                DiskVal.Foreground = Brush(c.Disk.ValueColor);
                NetVal.Foreground = Brush(c.Net.ValueColor);
                GpuVal.Foreground = Brush(c.Gpu.ValueColor);
                TempVal.Foreground = Brush(c.Temp.ValueColor);
                ProcVal.Foreground = Brush(c.Proc.ValueColor);

                CpuBar.Fill = Gradient(c.Cpu.BarStart, c.Cpu.BarEnd);
                MemBar.Fill = Gradient(c.Mem.BarStart, c.Mem.BarEnd);
                DiskBar.Fill = Gradient(c.Disk.BarStart, c.Disk.BarEnd);
                GpuBar.Fill = Gradient(c.Gpu.BarStart, c.Gpu.BarEnd);

                var barBg = Brush(c.BarBackground);
                CpuBarBg.Background = MemBarBg.Background = DiskBarBg.Background = GpuBarBg.Background = barBg;

                // 同步到 Lua 宿主；完整模式下动态行也要跟随字号/配色
                _scriptHost.FontSize = c.FontSize;
                _scriptHost.TimeFontSize = c.TimeFontSize;
                if (_scriptHost.Loaded && _scriptHost.FullMode) UpdateDynFonts(c);
            }
            catch { }
        }

        private static SolidColorBrush Brush(string hex)
        {
            try { return (SolidColorBrush)new BrushConverter().ConvertFromString(hex); }
            catch { return Brushes.White; }
        }

        private static LinearGradientBrush Gradient(string c1, string c2)
        {
            try
            {
                var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                b.GradientStops.Add(new GradientStop(Brush(c1).Color, 0));
                b.GradientStops.Add(new GradientStop(Brush(c2).Color, 1));
                return b;
            }
            catch { return new LinearGradientBrush(Colors.White, Colors.White, 0); }
        }

        public class Config
        {
            public Pos Position { get; set; } = new Pos();
            public int FontSize { get; set; } = 13;
            public int TimeFontSize { get; set; } = 15;
            public bool Bold { get; set; } = true;
            public string LabelColor { get; set; } = "#7A90AC";
            public string BarBackground { get; set; } = "#26FFFFFF";
            public Item Cpu { get; set; } = new Item("#4DE3E0", "#4DE3E0", "#2F9EFF");
            public Item Mem { get; set; } = new Item("#7FB0FF", "#6FA8FF", "#9B7BFF");
            public Item Disk { get; set; } = new Item("#FFC85C", "#FFC85C", "#FF8A5C");
            public Item Net { get; set; } = new Item("#8FD8FF", "", "");
            public Item Gpu { get; set; } = new Item("#B78CFF", "#B78CFF", "#7A5CFF");
            public Item Temp { get; set; } = new Item("#FF6B6B", "", "");
            public Item Proc { get; set; } = new Item("#EAF2FC", "", "");
        }

        public class Pos
        {
            public int X { get; set; } = 1680;
            public int Y { get; set; } = 360;
        }

        public class Item
        {
            public Item() { }
            public Item(string vc, string bs, string be) { ValueColor = vc; BarStart = bs; BarEnd = be; }
            public string ValueColor { get; set; } = "#EAF2FC";
            public string BarStart { get; set; } = "";
            public string BarEnd { get; set; } = "";
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }
}
