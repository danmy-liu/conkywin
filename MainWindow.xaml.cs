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
        private PerformanceCounter _diskIoCounter;
        private ulong _totalMem;
        private long _lastRx, _lastTx;
        private DateTime _lastNetSample = DateTime.MinValue;
        private int _gpuTick = 0;
        private Config _cfg = new Config();
        private readonly DispatcherTimer _timer;
        private DispatcherTimer _posTimer;

        // 热重载：轮询配置文件/脚本的最后修改时间（比 FileSystemWatcher 可靠，
        // 编辑器"写临时文件再改名"的保存方式不会触发 Changed 事件）
        private string _cfgPath, _scriptPath;
        private DateTime _cfgStamp, _scriptStamp;

        // 内置面板的控件缓存：完整脚本模式会清空 RootPanel，退出脚本模式时要还原
        private List<UIElement> _builtinChildren;

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
                _cfg = LoadConfigLive() ?? new Config();
                Log("config 已加载 PanelWidth=" + _cfg.PanelWidth + " FontSize=" + _cfg.FontSize +
                    " RightGap=" + _cfg.Position.RightGap);
                var hwnd = new WindowInteropHelper(this).Handle;
                // 点击穿透 + 工具窗口（不抢焦点、不挡鼠标）
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
                // 先应用配置（含固定宽度），再落位（右对齐依赖宽度）
                ApplyConfig(_cfg);
                // 壁纸之上、图标之下（等效 Conky below）
                PlaceWindow();

                // 定期校正落位（防 explorer 重启 / Z 序被其他程序打乱）
                _posTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
                _posTimer.Tick += (s2, e2) => { try { PlaceWindow(); } catch { } };
                _posTimer.Start();

                // 内容高度变化（天气加载、行数增减）后重新做屏幕内钳制
                SizeChanged += (s2, e2) => { try { PlaceWindow(); } catch { } };

                // 内置面板控件缓存（完整脚本模式会清空 RootPanel，切回来时要还原）
                _builtinChildren = RootPanel.Children.Cast<UIElement>().ToList();

                // Lua 脚本（可选）：exe 同目录 script.lua，不存在则一切行为与内置版一致
                _scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "script.lua");
                _scriptHost.FontSize = _cfg.FontSize;
                _scriptHost.TimeFontSize = _cfg.TimeFontSize;
                _scriptHost.WeatherLat = _cfg.Weather.Lat;
                _scriptHost.WeatherLon = _cfg.Weather.Lon;
                _scriptHost.WeatherCity = _cfg.Weather.City;
                _scriptHost.Load(_scriptPath, _snap);
                if (_scriptHost.Loaded)
                    Log("script.lua 已加载，mode=" + (_scriptHost.FullMode ? "full(conky_rows)" : "enhance(hooks)"));

                // 配置 / 脚本热重载：记录当前修改时间，Update() 中每秒比一次
                _cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                _cfgStamp = FileStamp(_cfgPath);
                _scriptStamp = FileStamp(_scriptPath);
                Log("热重载已启用：config.json / script.lua（保存即生效，无需重启）");
            }
            catch { }

            try
            {
                _cpuCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
                _cpuCounter.NextValue(); // 预热
            }
            catch { }
            try { _procCounter = new PerformanceCounter("System", "Processes"); } catch { }
            try { _diskIoCounter = new PerformanceCounter("PhysicalDisk", "Disk Bytes/sec", "_Total"); _diskIoCounter.NextValue(); } catch { }

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
            _posTimer?.Stop();
            try { _cpuCounter?.Dispose(); } catch { }
            try { _procCounter?.Dispose(); } catch { }
            try { _diskIoCounter?.Dispose(); } catch { }
        }

        // ---------- 热重载（配置 / 脚本） ----------

        private static DateTime FileStamp(string path)
        {
            try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        /// <summary>
        /// 每秒比对 config.json / script.lua 的修改时间，变了就重新加载。
        /// 用轮询而不是 FileSystemWatcher：VSCode、部分编辑器保存时是"写临时文件→改名覆盖"，
        /// FileSystemWatcher 的 Changed 事件收不到（只会收到 Deleted/Renamed），表现为改了没反应。
        /// </summary>
        private void CheckHotReload()
        {
            try
            {
                var cs = FileStamp(_cfgPath);
                if (cs != _cfgStamp)
                {
                    _cfgStamp = cs;
                    var c = LoadConfigLive();
                    if (c != null)
                    {
                        _cfg = c;
                        ApplyConfig(c);
                        PlaceWindow();
                        Log("config.json 已重新加载（宽度/位置/字号/配色已生效）");
                    }
                    else Log("config.json 解析失败，保留原配置");
                }

                var ss = FileStamp(_scriptPath);
                if (ss != _scriptStamp)
                {
                    _scriptStamp = ss;
                    ReloadScript();
                }
            }
            catch { }
        }

        private void ReloadScript()
        {
            bool wasFull = _scriptHost.Loaded && _scriptHost.FullMode;
            _scriptHost.FontSize = _cfg.FontSize;
            _scriptHost.TimeFontSize = _cfg.TimeFontSize;
            _scriptHost.WeatherLat = _cfg.Weather.Lat;
            _scriptHost.WeatherLon = _cfg.Weather.Lon;
            _scriptHost.WeatherCity = _cfg.Weather.City;
            _scriptHost.Load(_scriptPath, _snap);

            if (_scriptHost.Loaded && _scriptHost.FullMode)
            {
                _dynRows.Clear(); _dynSigs.Clear();
                RootPanel.Children.Clear();
                Log("script.lua 已重新加载，mode=full(conky_rows)（曲线历史已重置）");
            }
            else
            {
                if (wasFull) ShowBuiltinPanel(); // 从完整脚本模式退回内置面板：还原控件
                if (_scriptHost.Loaded) Log("script.lua 已重新加载，mode=enhance(hooks)");
                else Log("script.lua 已移除或加载失败，回退内置面板");
            }
        }

        /// <summary>把 RootPanel 内容还原为内置面板（退出完整脚本模式时调用）。</summary>
        private void ShowBuiltinPanel()
        {
            _dynRows.Clear(); _dynSigs.Clear();
            RootPanel.Children.Clear();
            if (_builtinChildren == null) return;
            foreach (var el in _builtinChildren) RootPanel.Children.Add(el);
            ApplyConfig(_cfg);
        }

        private void Update()
        {
            FillSnapshot();
            CheckHotReload();

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
                s.NetTotalDownGb = rx / 1024.0 / 1024.0 / 1024.0; // 开机以来累计
                s.NetTotalUpGb = tx / 1024.0 / 1024.0 / 1024.0;
            }
            catch { }

            try { s.Proc = (int)_procCounter.NextValue(); } catch { s.Proc = -1; }

            try { if (_diskIoCounter != null) s.DiskIoKb = _diskIoCounter.NextValue() / 1024.0; } catch { }

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
            double w = Math.Max(0, Math.Min(100, pct)) / 100.0 * RootPanel.ActualWidth;
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
            public bool IsHr;
            public bool IsGap;
            public bool IsColumns;
            public Border Sep;
            public TextBlock Label, Value;
            public Image ValueIcon;
            public Border BarBg;
            public System.Windows.Shapes.Rectangle Bar;
            public Canvas GraphCanvas;
            public System.Windows.Shapes.Polygon GraphArea;   // 曲线图元复用（避免每帧重建控件导致闪烁）
            public System.Windows.Shapes.Polyline GraphLine;
            public string GraphColorStr;
            public double GraphH;
            public TextBlock[] CellLabels, CellTexts, CellSubs;
            public Image[] CellIcons;
        }

        private readonly List<DynRow> _dynRows = new List<DynRow>();
        private readonly List<string> _dynSigs = new List<string>();

        /// <summary>
        /// 行的控件结构签名——只包含影响"控件类型"的字段。
        /// 注意不能含 label/text 等动态内容：Top 进程行的 label 是进程名，采样排序一变
        /// 就会触发整面板重建（RootPanel.Clear），表现就是每隔几秒闪一次、曲线清空重画。
        /// </summary>
        private static string RowSig(ScriptHost.Row r)
        {
            if (r.Gap > 0) return "gap";
            if (r.Cells != null && r.Cells.Count > 0) return "cols" + r.Cells.Count;
            return (r.Hr ? "hr" : "row") + (r.Graph != null ? "|g" + r.GraphHeight : "") +
                   (r.Bar.HasValue ? "|b" : "") + (r.Icon != null ? "|i" : "");
        }

        private void RenderScriptPanel()
        {
            var rows = _scriptHost.GetRows();
            if (rows == null) return; // 脚本出错：保留上一帧画面

            // 行结构（数量/类型）变化才重建控件，否则只刷新内容
            bool changed = rows.Count != _dynRows.Count;
            if (!changed)
                for (int i = 0; i < rows.Count; i++)
                    if (_dynSigs[i] != RowSig(rows[i])) { changed = true; break; }

            if (changed)
            {
                _dynRows.Clear(); _dynSigs.Clear();
                RootPanel.Children.Clear();
                foreach (var r in rows) AddDynRow(r);
                PinWidth(); // 同步设置会被随后的布局周期顶回 XAML 默认值，布局完成后再钉一次
            }

            for (int i = 0; i < Math.Min(rows.Count, _dynRows.Count); i++)
                RefreshDynRow(_dynRows[i], rows[i]);
        }

        /// <summary>
        /// 把窗口宽度钉到配置值。WPF 的 SizeToContent=Height 在内容重建时会把
        /// 运行时设置的 Width 顶回 XAML 声明值（220），必须在布局周期结束后再设置一次。
        /// </summary>
        private void PinWidth()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_cfg.PanelWidth > 100 && Math.Abs(Width - _cfg.PanelWidth) > 0.5)
                    Width = _cfg.PanelWidth;
            }), DispatcherPriority.Loaded);
        }

        private void AddDynRow(ScriptHost.Row r)
        {
            bool first = RootPanel.Children.Count == 0;
            var dr = new DynRow { IsHr = r.Hr };

            if (r.Gap > 0) // 空白间隔行（分区留白）
            {
                RootPanel.Children.Add(new Border { Height = r.Gap, Background = Brushes.Transparent });
                dr.IsGap = true;
                _dynRows.Add(dr);
                _dynSigs.Add(RowSig(r));
                return;
            }

            if (r.Cells != null && r.Cells.Count > 0) // 多列布局（如三天预报：星期/图标/温度）
            {
                dr.IsColumns = true;
                var grid = new Grid { Margin = new Thickness(0, first ? 0 : 7, 0, 0) };
                for (int i = 0; i < r.Cells.Count; i++)
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                dr.CellLabels = new TextBlock[r.Cells.Count];
                dr.CellTexts = new TextBlock[r.Cells.Count];
                dr.CellSubs = new TextBlock[r.Cells.Count];
                dr.CellIcons = new Image[r.Cells.Count];
                for (int i = 0; i < r.Cells.Count; i++)
                {
                    var c = r.Cells[i];
                    var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                    var l = new TextBlock
                    {
                        Text = c.Label,
                        FontFamily = new FontFamily("Microsoft YaHei UI"),
                        FontSize = 11,
                        Foreground = Brush(_cfg.LabelColor),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 0, 0, 2)
                    };
                    var v = new TextBlock
                    {
                        Text = c.Text,
                        FontFamily = new FontFamily("Segoe UI Symbol, Segoe UI Emoji, Microsoft YaHei UI"),
                        FontSize = 20,
                        Foreground = Brush(c.Color ?? "#EAF2FC"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 0, 0, 2)
                    };
                    col.Children.Add(l);
                    col.Children.Add(v);
                    if (c.Icon != null)
                    {
                        var ci = new Image
                        {
                            Width = c.IconSize,
                            Height = c.IconSize,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(0, 1, 0, 3)
                        };
                        var src = LoadIcon(c.Icon);
                        if (src != null) ci.Source = src;
                        col.Children.Add(ci);
                        dr.CellIcons[i] = ci;
                        v.Visibility = Visibility.Collapsed; // 图片与文本符号二选一
                    }
                    var s = new TextBlock
                    {
                        Text = c.Sub,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11,
                        Foreground = Brush(c.Color ?? "#EAF2FC"),
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    col.Children.Add(s);
                    Grid.SetColumn(col, i);
                    grid.Children.Add(col);
                    dr.CellLabels[i] = l;
                    dr.CellTexts[i] = v;
                    dr.CellSubs[i] = s;
                }
                RootPanel.Children.Add(grid);
                _dynRows.Add(dr);
                _dynSigs.Add(RowSig(r));
                return;
            }

            if (r.Hr)
            {
                var sep = new Border
                {
                    Height = 2,
                    CornerRadius = new CornerRadius(1),
                    Background = Brush(r.Color ?? "#40FFFFFF"),
                    Margin = new Thickness(0, first ? 0 : 7, 0, 0)
                };
                RootPanel.Children.Add(sep);
                dr.Sep = sep;
            }
            else
            {
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
                    FontSize = r.FontSize ?? _cfg.FontSize,
                    FontWeight = _cfg.Bold ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = Brush(r.Color ?? "#EAF2FC"),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                grid.Children.Add(label);
                grid.Children.Add(value);
                dr.Label = label;
                dr.Value = value;
                if (r.Icon != null)
                {
                    var img = new Image
                    {
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 4, 0),
                        Width = r.IconSize,
                        Height = r.IconSize
                    };
                    var src = LoadIcon(r.Icon);
                    if (src != null) img.Source = src;
                    grid.Children.Add(img);
                    dr.ValueIcon = img;
                    value.Margin = new Thickness(0, 0, r.IconSize + 8, 0); // 给图标让位
                }
                RootPanel.Children.Add(grid);

                if (r.Graph != null)
                {
                    var canvas = new Canvas
                    {
                        Height = r.GraphHeight,
                        Margin = new Thickness(0, 3, 0, 0),
                        ClipToBounds = true
                    };
                    dr.GraphArea = new System.Windows.Shapes.Polygon { Fill = Brushes.Transparent };
                    dr.GraphLine = new System.Windows.Shapes.Polyline { StrokeThickness = 1 };
                    canvas.Children.Add(dr.GraphArea);
                    canvas.Children.Add(dr.GraphLine);
                    dr.GraphH = r.GraphHeight;
                    RootPanel.Children.Add(canvas);
                    dr.GraphCanvas = canvas;
                }
                else if (r.Bar.HasValue)
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
            }
            _dynRows.Add(dr);
            _dynSigs.Add(RowSig(r));
        }

        private void RefreshDynRow(DynRow dr, ScriptHost.Row r)
        {
            if (dr.IsHr || dr.IsGap) return; // 分隔线/间隔无动态内容
            if (dr.IsColumns && r.Cells != null)
            {
                for (int i = 0; i < Math.Min(dr.CellTexts.Length, r.Cells.Count); i++)
                {
                    var c = r.Cells[i];
                    dr.CellLabels[i].Text = c.Label;
                    dr.CellTexts[i].Text = c.Text;
                    dr.CellSubs[i].Text = c.Sub;
                    var brush = Brush(c.Color ?? "#EAF2FC");
                    dr.CellTexts[i].Foreground = brush;
                    dr.CellSubs[i].Foreground = brush;
                    if (dr.CellIcons[i] != null && c.Icon != null)
                    {
                        dr.CellIcons[i].Width = dr.CellIcons[i].Height = c.IconSize;
                        var src = LoadIcon(c.Icon);
                        if (src != null && dr.CellIcons[i].Source != src) dr.CellIcons[i].Source = src;
                    }
                }
                return;
            }
            dr.Label.Text = r.Label;
            dr.Value.Text = r.Text;
            dr.Value.Foreground = Brush(r.Color ?? "#EAF2FC");
            if (r.FontSize.HasValue) dr.Value.FontSize = r.FontSize.Value;
            if (dr.ValueIcon != null && r.Icon != null)
            {
                dr.ValueIcon.Width = dr.ValueIcon.Height = r.IconSize;
                var src = LoadIcon(r.Icon);
                if (src != null && dr.ValueIcon.Source != src) dr.ValueIcon.Source = src;
            }
            if (dr.GraphCanvas != null && r.Graph != null)
            {
                DrawGraph(dr, r.GraphColor ?? r.Color ?? "#FFFFFF", r.Graph);
            }
            else if (dr.Bar != null && r.Bar.HasValue)
            {
                double w = Math.Max(0, Math.Min(100, r.Bar.Value)) / 100.0 * dr.BarBg.ActualWidth;
                if (dr.Bar.Width != w) dr.Bar.Width = w;
                if (r.BarStart != null && r.BarEnd != null)
                    dr.Bar.Fill = Gradient(r.BarStart, r.BarEnd);
            }
        }

        /// <summary>
        /// 曲线绘制：复用同一组 Polygon/Polyline 图元，每帧只更新点集。
        /// 每帧 Clear+重建会产生可见的闪烁（控件经历"移除→添加→重排"两个渲染帧）。
        /// </summary>
        private void DrawGraph(DynRow dr, string colorHex, List<double> series)
        {
            var canvas = dr.GraphCanvas;
            double w = canvas.ActualWidth > 1 ? canvas.ActualWidth : Math.Max(10, _cfg.PanelWidth - 24);
            double h = canvas.ActualHeight > 1 ? canvas.ActualHeight : dr.GraphH;
            if (w <= 1 || h <= 1 || series == null || series.Count < 2) return;

            if (dr.GraphColorStr != colorHex)
            {
                var color = Brush(colorHex);
                dr.GraphLine.Stroke = color;
                dr.GraphArea.Fill = new SolidColorBrush(Color.FromArgb(0x40, color.Color.R, color.Color.G, color.Color.B));
                dr.GraphColorStr = colorHex;
            }

            var pts = dr.GraphLine.Points;
            pts.Clear();
            int n = series.Count;
            for (int i = 0; i < n; i++)
            {
                double x = w * i / (n - 1);
                double v = Math.Max(0, Math.Min(100, series[i]));
                pts.Add(new Point(x, h - 1 - v / 100.0 * (h - 2)));
            }
            // 面积 = 折线 + 底边两个点
            var fillPts = dr.GraphArea.Points;
            fillPts.Clear();
            foreach (var p in pts) fillPts.Add(p);
            fillPts.Add(new Point(w, h));
            fillPts.Add(new Point(0, h));
        }

        /// <summary>加载图标 PNG（相对路径基于 exe 目录；OnLoad 立即读入避免文件锁）。</summary>
        private static ImageSource LoadIcon(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                string full = Path.IsPathRooted(path) ? path : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
                if (!File.Exists(full)) return null;
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(full);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private void UpdateDynFonts(Config c)
        {
            foreach (var dr in _dynRows)
            {
                if (dr.IsHr || dr.IsGap) continue;
                if (dr.IsColumns)
                {
                    foreach (var l in dr.CellLabels) if (l != null) l.Foreground = Brush(c.LabelColor);
                    continue;
                }
                dr.Label.Foreground = Brush(c.LabelColor);
                dr.Value.FontSize = c.FontSize;
                dr.Value.FontWeight = c.Bold ? FontWeights.Bold : FontWeights.Normal;
                if (dr.BarBg != null) dr.BarBg.Background = Brush(c.BarBackground);
            }
        }

        /// <summary>
        /// 把面板放到"壁纸之上、桌面图标之下"：
        /// 找到承载桌面图标（SHELLDLL_DefView）的顶层窗口（Progman 或 WorkerW），插到它正下方。
        /// 不用 HWND_BOTTOM 硬压底——它有概率把窗口压到壁纸层之下（实测偶发，表现为面板完全不可见）。
        /// 位置同时做屏幕内钳制：内容高度变化（天气加载、行数变化）后不会超出工作区下边界。
        /// </summary>
        /// <summary>右对齐模式下根据面板宽度和 RightGap 计算 X，否则用配置 X。</summary>
        private int TargetX()
        {
            if (_cfg.Position.RightGap >= 0)
                return (int)(SystemParameters.WorkArea.Right - ActualWidth - _cfg.Position.RightGap);
            return _cfg.Position.X;
        }

        /// <summary>按配置 Y 定位，但保证面板完整落在工作区内（底部留 BottomGap 像素）。</summary>
        private int TargetY()
        {
            int y = _cfg.Position.Y;
            double waBottom = SystemParameters.WorkArea.Bottom;
            double waTop = SystemParameters.WorkArea.Top;
            double h = ActualHeight > 10 ? ActualHeight : 0;
            if (h > 10 && y + h > waBottom - _cfg.Position.BottomGap)
                y = (int)Math.Max(waTop, waBottom - _cfg.Position.BottomGap - h);
            return y;
        }

        private void PlaceWindow()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int x = TargetX(), y = TargetY();
            IntPtr iconLayer = FindIconLayer();
            if (iconLayer != IntPtr.Zero && iconLayer != hwnd)
                SetWindowPos(hwnd, iconLayer, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            else
                SetWindowPos(hwnd, (IntPtr)HWND_BOTTOM, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        private static IntPtr FindIconLayer()
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hwnd, lp) =>
            {
                var sb = new System.Text.StringBuilder(256);
                GetClassName(hwnd, sb, 256);
                string cls = sb.ToString();
                if (cls == "Progman" || cls == "WorkerW")
                {
                    if (FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                    {
                        found = hwnd;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        // ---------- 配置文件 ----------

        private static Config LoadConfig()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            return LoadConfigFile(path, out _);
        }

        /// <summary>加载配置并把原文带给脚本宿主（conky.config() 透传，含用户自定义键）。</summary>
        private Config LoadConfigLive()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            var c = LoadConfigFile(path, out string raw);
            if (c != null) _scriptHost.ConfigJson = raw;
            return c;
        }

        private static Config LoadConfigFile(string path, out string rawText)
        {
            rawText = "{}";
            try
            {
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path);
                    if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1); // 去掉记事本等编辑器加的 BOM
                    rawText = text;
                    var c = JsonSerializer.Deserialize<Config>(text);
                    return c; // 解析失败返回 null，调用方保留现有配置
                }
                var def = new Config();
                File.WriteAllText(path, JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = true }));
                return def;
            }
            catch (Exception ex) { Log("config.json 解析失败: " + ex.Message); return null; }
        }

        private void ApplyConfig(Config c)
        {
            try
            {
                if (c.PanelWidth > 100)
                {
                    Width = c.PanelWidth;
                    PinWidth();
                }
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
                bool wxChanged = _scriptHost.WeatherLat != c.Weather.Lat ||
                                 _scriptHost.WeatherLon != c.Weather.Lon ||
                                 _scriptHost.WeatherCity != (c.Weather.City ?? "");
                _scriptHost.WeatherLat = c.Weather.Lat;
                _scriptHost.WeatherLon = c.Weather.Lon;
                _scriptHost.WeatherCity = c.Weather.City ?? "";
                if (wxChanged) _scriptHost.InvalidateWeather(); // 改了城市/坐标立刻重抓
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
            public int PanelWidth { get; set; } = 220;   // 面板固定宽度（px），高度自适应
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
            public Wx Weather { get; set; } = new Wx();
        }

        public class Wx
        {
            /// <summary>城市名，如 "宝鸡"；填了就用 Open-Meteo 地理编码取坐标（优先于 IP 定位）。</summary>
            public string City { get; set; } = "";
            /// <summary>纬度/经度；999 = 自动（先试 City，再试 IP 定位）。</summary>
            public double Lat { get; set; } = 999;
            public double Lon { get; set; } = 999;
        }

        public class Pos
        {
            public int X { get; set; } = 1680;
            public int Y { get; set; } = 360;
            /// <summary>>=0 时右对齐：X = 屏幕右缘 - 面板宽 - RightGap；-1 表示禁用，直接用 X。</summary>
            public int RightGap { get; set; } = -1;
            /// <summary>面板底边与工作区底边的最小间距（内容超高时面板自动上移，保证不出屏）。</summary>
            public int BottomGap { get; set; } = 8;
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

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string lpszWindow);
    }
}
