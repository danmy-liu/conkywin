using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using MoonSharp.Interpreter;

namespace ConkyWin
{
    /// <summary>
    /// Lua 脚本宿主（基于 MoonSharp，纯托管实现，无原生 DLL 依赖）。
    ///
    /// 脚本文件：exe 同目录 script.lua（可选，不存在则一切行为与内置版一致）。
    /// 脚本在安全沙箱中运行（无 io/os/net 等危险模块），出错只记 debug.log，不影响主面板。
    ///
    /// 两种模式：
    /// 1) 完整模式：脚本定义 conky_rows() 返回行表，整个面板由脚本生成。行表字段：
    ///    { label="CPU", text="42%", color="#4DE3E0", bar=42, bar_start="#4DE3E0", bar_end="#2F9EFF" }
    ///    bar 为 0-100 数字（nil 则该行无进度条）；color/bar_start/bar_end 缺省用内置默认。
    /// 2) 增强模式：脚本定义若干钩子函数，覆盖对应行的文本/颜色：
    ///    conky_time_text()                        -> "23:59:01"
    ///    conky_cpu_text(pct)                      -> "42%"
    ///    conky_mem_text(usedGb, totalGb, pct)     -> "8.2/11.9G"
    ///    conky_disk_text(usedGb, totalGb, pct)    -> "96/111G"
    ///    conky_net_text(downKb, upKb)             -> "↓1.2M ↑45K"
    ///    conky_gpu_text(util, memUsedGb, temp)    -> "3% 0.9G 39℃"
    ///    conky_temp_text(temp)                    -> "--℃"（temp 为 nil）
    ///    conky_proc_text(count)                   -> "198"
    ///    conky_cpu_color(pct)                     -> "#FF6B6B"
    ///    conky_mem_color(pct) / conky_disk_color(pct) / conky_gpu_color(pct)
    ///    conky_net_color(downKb, upKb) / conky_temp_color(temp) / conky_proc_color(count)
    ///    conky_bar_color(name, pct)               -> "#FF6B6B" 或 {"#FF0000", "#FFC85C"}
    ///      name ∈ {"cpu","mem","disk","gpu"}，返回单色则渐变两端同色，返回表则覆盖渐变两端
    ///
    /// 脚本可用的 conky API（值为最近一次采样）：
    ///    conky.cpu()            0-100 占用
    ///    conky.mem_pct() / conky.mem_used_gb() / conky.mem_total_gb()
    ///    conky.disk_pct() / conky.disk_used_gb() / conky.disk_total_gb()
    ///    conky.net_down_kb() / conky.net_up_kb()
    ///    conky.gpu_util() / conky.gpu_temp() / conky.gpu_mem_used_gb() / conky.gpu_mem_total_gb()
    ///    conky.gpu_available()  GPU 是否可用
    ///    conky.proc()           进程数
    ///    conky.time(fmt)        按 .NET 格式串取当前时间，如 conky.time("HH:mm:ss")
    ///    conky.font_size() / conky.time_font_size()   从 config.json 读取的字号
    /// </summary>
    public class ScriptHost
    {
        public class Snapshot
        {
            public double Cpu;
            public double MemPct, MemUsedGb, MemTotalGb;
            public bool DiskAvailable;
            public double DiskPct, DiskUsedGb, DiskTotalGb;
            public List<DiskItem> Disks = new List<DiskItem>(); // 所有固定磁盘
            public double DiskIoKb;
            public double NetDownKb, NetUpKb;
            public double NetTotalDownGb, NetTotalUpGb; // 开机以来累计流量
            public double GpuUtil, GpuTemp, GpuMemUsedGb, GpuMemTotalGb;
            public bool GpuAvailable;
            public int Proc;
        }

        public class DiskItem
        {
            public string Name = "";    // "C:", "D:", ...
            public string Label = "";   // 卷标（可能为空）
            public double Pct;          // 已用百分比
            public double UsedGb, TotalGb;
        }

        public class Row
        {
            public string Label = "";
            public string Text = "";
            public string Icon;         // 非 null → 行尾渲染图标（路径，相对 exe 目录或绝对路径）
            public double IconSize = 20;
            public string Color;        // null → 用默认色
            public double? Bar;         // null → 该行无进度条
            public string BarStart;
            public string BarEnd;
            public List<double> Graph;  // null → 无曲线图；否则为 0-100 的历史序列
            public double GraphHeight = 26;
            public string GraphColor;   // null → 用 Color
            public bool Hr;             // true → 该行渲染为分隔横线
            public double? FontSize;    // null → 用全局字号
            public double Gap;          // > 0 → 该行为空白间隔（px）
            public List<RowCell> Cells; // 非 null → 该行为多列布局（如三天预报）
        }

        public class RowCell
        {
            public string Label = "";   // 顶部小字
            public string Text = "";    // 中间大字（图标/数值）
            public string Icon;         // 非 null → 用图片替代 Text（路径）
            public double IconSize = 22;
            public string Sub = "";     // 底部小字
            public string Color;        // 整格颜色，null 用默认
        }

        private Script _script;
        private Table _api;
        private readonly Dictionary<string, Closure> _hooks = new Dictionary<string, Closure>();
        private static readonly string[] HookNames =
        {
            "conky_time_text", "conky_cpu_text", "conky_mem_text", "conky_disk_text",
            "conky_net_text", "conky_gpu_text", "conky_temp_text", "conky_proc_text",
            "conky_cpu_color", "conky_mem_color", "conky_disk_color", "conky_gpu_color",
            "conky_net_color", "conky_temp_color", "conky_proc_color", "conky_bar_color"
        };

        public bool Loaded { get; private set; }
        public bool FullMode { get; private set; }
        private string _lastError = "";
        private Snapshot _snap = new Snapshot();
        public int FontSize, TimeFontSize;

        /// <summary>config.json 原文，conky.config() 会解析成 Lua 表透传（含用户自定义键）。</summary>
        public string ConfigJson = "{}";
        private DynValue _cfgTable;
        private string _cfgTableSrc;

        // ---------- 进程 Top 列表（每 3 秒采样，供 conky.top_mem/top_cpu） ----------
        private class ProcInfo { public string Name; public double MemMb; public double Cpu; }
        private List<ProcInfo> _procs = new List<ProcInfo>();
        private DateTime _lastProcSample = DateTime.MinValue;
        private readonly Dictionary<int, double> _procCpuTicks = new Dictionary<int, double>();

        // ---------- 天气（Open-Meteo 免费 API，无 key；失败退避重试 + 本地缓存） ----------
        // 定位优先级：config 的 Weather.Lat/Lon > Weather.City（地理编码）> IP 定位（三源回退）
        public double WeatherLat = 999, WeatherLon = 999;
        public string WeatherCity = "";
        private WeatherData _wx;
        private readonly object _wxLock = new object();
        private DateTime _wxNextAttempt = DateTime.MinValue;  // 下一次允许抓取的时间
        private int _wxFail;
        private bool _wxCacheLoaded;
        private string _wxError = "";

        private static readonly HttpClient Http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            try { h.DefaultRequestHeaders.UserAgent.ParseAdd("ConkyWin/1.0"); } catch { }
            return h;
        }

        public class WeatherData
        {
            public double Temp { get; set; }
            public double Humidity { get; set; }
            public double Wind { get; set; }
            public double Pressure { get; set; }   // hPa 气压
            public double Code { get; set; }       // WMO 当前天气代码
            public string City { get; set; } = "";
            public List<double[]> Daily { get; set; } = new List<double[]>();
            public double Lat { get; set; }
            public double Lon { get; set; }
            public DateTime Updated { get; set; }
        }

        /// <summary>配置里的城市/坐标变了：清空当前数据并立刻重抓。</summary>
        public void InvalidateWeather()
        {
            lock (_wxLock) _wx = null;
            _wxFail = 0;
            _wxNextAttempt = DateTime.MinValue;
            _wxCacheLoaded = true; // 坐标已变，不再恢复旧缓存
        }

        /// <summary>加载 script.lua；不存在或解析失败返回 false（Loaded=false）。</summary>
        public bool Load(string path, Snapshot snap)
        {
            _snap = snap;
            Loaded = false;
            FullMode = false;
            _hooks.Clear();
            _script = null;
            try
            {
                if (!File.Exists(path)) return false;
                _script = new Script(CoreModules.Preset_HardSandbox);
                BuildApi();
                _script.DoString(File.ReadAllText(path));
                // 完整模式：定义了 conky_rows()
                FullMode = _script.Globals.Get("conky_rows").Type == DataType.Function;
                // 缓存已定义的钩子
                foreach (var name in HookNames)
                {
                    var v = _script.Globals.Get(name);
                    if (v.Type == DataType.Function) _hooks[name] = v.Function;
                }
                Loaded = FullMode || _hooks.Count > 0;
                if (!Loaded) LogError("script.lua 已加载但既无 conky_rows 也无任何 conky_* 钩子，忽略");
            }
            catch (Exception ex)
            {
                Loaded = false;
                LogError("script.lua 加载失败: " + ex.Message);
            }
            return Loaded;
        }

        private void BuildApi()
        {
            _api = new Table(_script);
            _api["cpu"] = (Func<double>)(() => _snap.Cpu);
            _api["mem_pct"] = (Func<double>)(() => _snap.MemPct);
            _api["mem_used_gb"] = (Func<double>)(() => _snap.MemUsedGb);
            _api["mem_total_gb"] = (Func<double>)(() => _snap.MemTotalGb);
            _api["disk_pct"] = (Func<double>)(() => _snap.DiskPct);
            _api["disk_used_gb"] = (Func<double>)(() => _snap.DiskUsedGb);
            _api["disk_total_gb"] = (Func<double>)(() => _snap.DiskTotalGb);
            _api["disks"] = (Func<DynValue>)(() => GetDisks());
            _api["net_down_kb"] = (Func<double>)(() => _snap.NetDownKb);
            _api["net_up_kb"] = (Func<double>)(() => _snap.NetUpKb);
            _api["gpu_util"] = (Func<double>)(() => _snap.GpuUtil);
            _api["gpu_temp"] = (Func<double>)(() => _snap.GpuTemp);
            _api["gpu_mem_used_gb"] = (Func<double>)(() => _snap.GpuMemUsedGb);
            _api["gpu_mem_total_gb"] = (Func<double>)(() => _snap.GpuMemTotalGb);
            _api["gpu_available"] = (Func<bool>)(() => _snap.GpuAvailable);
            _api["proc"] = (Func<double>)(() => _snap.Proc);
            _api["time"] = (Func<string, string>)(fmt =>
            {
                try { return DateTime.Now.ToString(string.IsNullOrEmpty(fmt) ? "HH:mm:ss" : fmt); }
                catch { return DateTime.Now.ToString("HH:mm:ss"); }
            });
            _api["font_size"] = (Func<double>)(() => FontSize);
            _api["time_font_size"] = (Func<double>)(() => TimeFontSize);
            _api["disk_io_kb"] = (Func<double>)(() => _snap.DiskIoKb);
            _api["net_total_down_gb"] = (Func<double>)(() => _snap.NetTotalDownGb);
            _api["net_total_up_gb"] = (Func<double>)(() => _snap.NetTotalUpGb);
            _api["top_mem"] = (Func<double, DynValue>)(n => TopTable(false, n));
            _api["top_cpu"] = (Func<double, DynValue>)(n => TopTable(true, n));
            _api["config"] = (Func<DynValue>)(() => ConfigTable());
            _api["weather"] = (Func<DynValue>)(() => GetWeather());
            _api["weather_error"] = (Func<string>)(() => _wxError ?? "");
            _script.Globals["conky"] = _api;
        }

        /// <summary>conky.config()：config.json 解析成 Lua 表（含任意自定义键），改配置即改脚本参数。</summary>
        private DynValue ConfigTable()
        {
            if (_cfgTable == null || _cfgTableSrc != ConfigJson)
            {
                _cfgTableSrc = ConfigJson;
                try
                {
                    using var doc = JsonDocument.Parse(ConfigJson);
                    _cfgTable = JsonToLua(doc.RootElement);
                }
                catch { _cfgTable = DynValue.NewTable(new Table(_script)); }
            }
            return _cfgTable;
        }

        private DynValue JsonToLua(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    var t = new Table(_script);
                    foreach (var p in el.EnumerateObject()) t[p.Name] = JsonToLua(p.Value);
                    return DynValue.NewTable(t);
                case JsonValueKind.Array:
                    var a = new Table(_script);
                    int i = 0;
                    foreach (var item in el.EnumerateArray()) a[++i] = JsonToLua(item);
                    return DynValue.NewTable(a);
                case JsonValueKind.String: return DynValue.NewString(el.GetString());
                case JsonValueKind.Number: return DynValue.NewNumber(el.GetDouble());
                case JsonValueKind.True:
                case JsonValueKind.False: return DynValue.NewBoolean(el.GetBoolean());
                default: return DynValue.Nil;
            }
        }

        /// <summary>conky.top_mem(n) / top_cpu(n)：返回 {name, mem_mb, cpu} 列表。</summary>
        private DynValue TopTable(bool byCpu, double n)
        {
            SampleProcesses();
            var src = byCpu
                ? _procs.OrderByDescending(p => p.Cpu).Take(Math.Max(1, (int)n))
                : _procs.OrderByDescending(p => p.MemMb).Take(Math.Max(1, (int)n));
            var t = new Table(_script);
            int i = 0;
            foreach (var p in src)
            {
                var e = new Table(_script);
                e["name"] = p.Name;
                e["mem_mb"] = p.MemMb;
                e["cpu"] = p.Cpu;
                t[++i] = DynValue.NewTable(e);
            }
            return DynValue.NewTable(t);
        }

        private void SampleProcesses()
        {
            var now = DateTime.Now;
            if (_procs.Count > 0 && (now - _lastProcSample).TotalSeconds < 3) return; // 3 秒内用缓存
            double wall = _lastProcSample == DateTime.MinValue ? 0 : (now - _lastProcSample).TotalSeconds;
            _lastProcSample = now;
            var list = new List<ProcInfo>();
            var newTicks = new Dictionary<int, double>();
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        var pi = new ProcInfo { Name = p.ProcessName, MemMb = p.WorkingSet64 / 1024.0 / 1024.0 };
                        double total = p.TotalProcessorTime.TotalSeconds;
                        newTicks[p.Id] = total;
                        if (wall > 0 && _procCpuTicks.TryGetValue(p.Id, out double prev))
                            pi.Cpu = Math.Min(100, Math.Max(0, (total - prev) / wall * 100)); // 单核归一
                        list.Add(pi);
                    }
                    catch { } // 进程可能刚好退出
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
            _procCpuTicks.Clear();
            foreach (var kv in newTicks) _procCpuTicks[kv.Key] = kv.Value;
            if (list.Count > 0) _procs = list;
        }

        // ---------- 增强模式钩子 ----------

        private DynValue CallHook(string name, params object[] args)
        {
            if (!_hooks.TryGetValue(name, out var fn)) return null;
            try { return _script.Call(fn, args); }
            catch (Exception ex) { LogError(name + " 执行出错: " + ex.Message); return null; }
        }

        private static string GetString(DynValue v) => v?.Type == DataType.String ? v.String : null;

        /// <summary>调用文本钩子，返回 null 表示用默认文本。</summary>
        public string Text(string hook, params object[] args) => GetString(CallHook(hook, args));

        /// <summary>调用颜色钩子，返回 null 表示用默认颜色。</summary>
        public string Color(string hook, params object[] args) => GetString(CallHook(hook, args));

        /// <summary>conky_bar_color(name, pct)：返回 null 用默认渐变；返回 {a,b} 表则覆盖渐变两端。</summary>
        public (string Start, string End) BarColor(string rowName, double pct)
        {
            var v = CallHook("conky_bar_color", rowName, pct);
            if (v == null) return (null, null);
            if (v.Type == DataType.String) return (v.String, v.String);
            if (v.Type == DataType.Tuple && v.Tuple.Length >= 2 &&
                v.Tuple[0].Type == DataType.String && v.Tuple[1].Type == DataType.String)
                return (v.Tuple[0].String, v.Tuple[1].String);
            if (v.Type == DataType.Table)
            {
                var s = v.Table.Get(1);
                var e = v.Table.Get(2);
                if (s.Type == DataType.String && e.Type == DataType.String) return (s.String, e.String);
            }
            return (null, null);
        }

        // ---------- 完整模式 conky_rows() ----------

        /// <summary>调用 conky_rows() 取行列表；出错返回 null（保留上一帧）。</summary>
        public List<Row> GetRows()
        {
            try
            {
                var v = _script.Call(_script.Globals.Get("conky_rows").Function);
                if (v.Type != DataType.Table) return null;
                var rows = new List<Row>();
                foreach (var item in v.Table.Values)
                {
                    if (item.Type != DataType.Table) continue;
                    var t = item.Table;
                    var r = new Row
                    {
                        Label = GetStr(t.Get("label")),
                        Text = GetStr(t.Get("text")),
                        Color = GetOptStr(t.Get("color")),
                        BarStart = GetOptStr(t.Get("bar_start")),
                        BarEnd = GetOptStr(t.Get("bar_end")),
                        GraphColor = GetOptStr(t.Get("graph_color")),
                    };
                    var bar = t.Get("bar");
                    if (bar.Type == DataType.Number)
                        r.Bar = Math.Max(0, Math.Min(100, bar.Number));
                    var g = t.Get("graph");
                    if (g.Type == DataType.Table)
                    {
                        r.Graph = new List<double>();
                        foreach (var gv in g.Table.Values)
                            if (gv.Type == DataType.Number) r.Graph.Add(gv.Number);
                    }
                    var gh = t.Get("graph_height");
                    if (gh.Type == DataType.Number) r.GraphHeight = gh.Number;
                    var hr = t.Get("hr");
                    if (hr.Type == DataType.Boolean && hr.Boolean) r.Hr = true;
                    var fs = t.Get("font_size");
                    if (fs.Type == DataType.Number) r.FontSize = fs.Number;
                    var icon = t.Get("icon");
                    if (icon.Type == DataType.String) r.Icon = icon.String;
                    var isz = t.Get("icon_size");
                    if (isz.Type == DataType.Number) r.IconSize = Math.Max(8, isz.Number);
                    var gap = t.Get("gap");
                    if (gap.Type == DataType.Number) r.Gap = Math.Max(0, gap.Number);
                    var cells = t.Get("cells");
                    if (cells.Type == DataType.Table)
                    {
                        r.Cells = new List<RowCell>();
                        foreach (var cv in cells.Table.Values)
                        {
                            if (cv.Type != DataType.Table) continue;
                            var ct = cv.Table;
                            var cell = new RowCell
                            {
                                Label = GetStr(ct.Get("label")),
                                Text = GetStr(ct.Get("text")),
                                Sub = GetStr(ct.Get("sub")),
                                Color = GetOptStr(ct.Get("color")),
                                Icon = GetOptStr(ct.Get("icon")),
                            };
                            var cisz = ct.Get("icon_size");
                            if (cisz.Type == DataType.Number) cell.IconSize = Math.Max(8, cisz.Number);
                            r.Cells.Add(cell);
                        }
                    }
                    rows.Add(r);
                }
                return rows;
            }
            catch (Exception ex) { LogError("conky_rows 执行出错: " + ex.Message); return null; }
        }

        private static string GetStr(DynValue v) => v.Type == DataType.String ? v.String
            : v.Type == DataType.Number ? v.Number.ToString(CultureInfo.InvariantCulture) : "";

        private static string GetOptStr(DynValue v) => v.Type == DataType.String ? v.String
            : v.Type == DataType.Number ? v.Number.ToString(CultureInfo.InvariantCulture) : null;

        /// <summary>所有固定磁盘：[{name="C:", label="系统", pct=62, used_gb=..., total_gb=...}, ...]</summary>
        private DynValue GetDisks()
        {
            var t = new Table(_script);
            int i = 0;
            foreach (var d in _snap.Disks)
            {
                var e = new Table(_script);
                e["name"] = d.Name;
                e["label"] = d.Label;
                e["pct"] = d.Pct;
                e["used_gb"] = d.UsedGb;
                e["total_gb"] = d.TotalGb;
                t[++i] = DynValue.NewTable(e);
            }
            return DynValue.NewTable(t);
        }

        private DynValue GetWeather()
        {
            if (!_wxCacheLoaded)
            {
                _wxCacheLoaded = true;
                LoadWeatherCache(); // 上次成功的数据先顶上，重启后不再"加载中"
            }
            if (DateTime.Now >= _wxNextAttempt)
            {
                _wxNextAttempt = DateTime.Now.AddSeconds(30); // 占位，避免抓取期间每秒重复起线程
                double lat = WeatherLat, lon = WeatherLon;
                string city = WeatherCity ?? "";
                Task.Run(() => FetchWeatherAsync(lat, lon, city));
            }
            lock (_wxLock)
            {
                if (_wx == null) return DynValue.Nil;
                var t = new Table(_script);
                t["temp"] = _wx.Temp;
                t["humidity"] = _wx.Humidity;
                t["wind"] = _wx.Wind;
                t["pressure"] = _wx.Pressure;
                t["code"] = _wx.Code;
                t["city"] = _wx.City;
                var daily = new Table(_script);
                for (int i = 0; i < _wx.Daily.Count; i++)
                {
                    var d = new Table(_script);
                    d["code"] = _wx.Daily[i][0];
                    d["min"] = _wx.Daily[i][1];
                    d["max"] = _wx.Daily[i][2];
                    daily[i + 1] = DynValue.NewTable(d);
                }
                t["daily"] = daily;
                return DynValue.NewTable(t);
            }
        }

        private async Task FetchWeatherAsync(double lat, double lon, string cityCfg)
        {
            try
            {
                string city = (cityCfg ?? "").Trim();
                if (lat > 900 || lon > 900)
                {
                    if (city.Length > 0)
                    {
                        var g = await GeocodeAsync(city).ConfigureAwait(false);
                        if (g.HasValue) { lat = g.Value.Lat; lon = g.Value.Lon; }
                    }
                    if (lat > 900 || lon > 900)
                    {
                        var ip = await LocateByIpAsync().ConfigureAwait(false);
                        if (ip.HasValue)
                        {
                            lat = ip.Value.Lat; lon = ip.Value.Lon;
                            if (city.Length == 0) city = ip.Value.City;
                        }
                    }
                }
                if (lat > 900 || lon > 900)
                    throw new Exception("无法定位，请在 config.json 的 Weather.City 里填城市名（如 \"宝鸡\"）");

                string latS = lat.ToString("0.####", CultureInfo.InvariantCulture);
                string lonS = lon.ToString("0.####", CultureInfo.InvariantCulture);
                string url = "https://api.open-meteo.com/v1/forecast?latitude=" + latS + "&longitude=" + lonS +
                             "&current=temperature_2m,relative_humidity_2m,wind_speed_10m,surface_pressure,weather_code" +
                             "&daily=weathercode,temperature_2m_max,temperature_2m_min&forecast_days=3&timezone=auto";
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url).ConfigureAwait(false));
                var cur = doc.RootElement.GetProperty("current");
                var wx = new WeatherData
                {
                    City = city,
                    Temp = cur.GetProperty("temperature_2m").GetDouble(),
                    Humidity = cur.GetProperty("relative_humidity_2m").GetDouble(),
                    Wind = cur.GetProperty("wind_speed_10m").GetDouble(),
                    Pressure = cur.TryGetProperty("surface_pressure", out var pr) ? pr.GetDouble() : 0,
                    Code = cur.TryGetProperty("weather_code", out var wc) ? wc.GetDouble() : 0,
                    Lat = lat,
                    Lon = lon,
                    Updated = DateTime.Now,
                };
                var daily = doc.RootElement.GetProperty("daily");
                var codes = daily.GetProperty("weathercode");
                var mins = daily.GetProperty("temperature_2m_min");
                var maxs = daily.GetProperty("temperature_2m_max");
                for (int i = 0; i < codes.GetArrayLength(); i++)
                    wx.Daily.Add(new[] { codes[i].GetDouble(), mins[i].GetDouble(), maxs[i].GetDouble() });

                lock (_wxLock) _wx = wx;
                _wxFail = 0;
                _wxError = "";
                _wxNextAttempt = DateTime.Now.AddMinutes(10);
                SaveWeatherCache(wx);
                LogError("weather ok: " + city + " " + wx.Temp + "°C");
            }
            catch (Exception ex)
            {
                _wxFail++;
                int wait = Math.Min(300, 20 << Math.Min(_wxFail - 1, 4)); // 20s→40s→80s→160s→300s 封顶
                _wxNextAttempt = DateTime.Now.AddSeconds(wait);
                _wxError = ex.Message;
                LogError("weather 获取失败（" + wait + "s 后重试）: " + ex.Message);
            }
        }

        /// <summary>城市名 → 经纬度（Open-Meteo 地理编码，支持中文名）。</summary>
        private static async Task<(double Lat, double Lon)?> GeocodeAsync(string city)
        {
            try
            {
                string url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=zh&format=json&name=" +
                             Uri.EscapeDataString(city);
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url).ConfigureAwait(false));
                if (!doc.RootElement.TryGetProperty("results", out var arr) || arr.GetArrayLength() == 0) return null;
                var r = arr[0];
                return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble());
            }
            catch { return null; }
        }

        /// <summary>IP 定位，三个源依次尝试（ip-api 在国内不稳，放最后）。</summary>
        private static async Task<(double Lat, double Lon, string City)?> LocateByIpAsync()
        {
            try // 1) ipwho.is（https）
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://ipwho.is/").ConfigureAwait(false));
                var r = doc.RootElement;
                if (r.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True &&
                    r.TryGetProperty("latitude", out var la) && la.ValueKind == JsonValueKind.Number)
                    return (la.GetDouble(), r.GetProperty("longitude").GetDouble(), Str(r, "city"));
            }
            catch { }
            try // 2) freeipapi.com（https）
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://freeipapi.com/api/json").ConfigureAwait(false));
                var r = doc.RootElement;
                if (r.TryGetProperty("latitude", out var la) && la.ValueKind == JsonValueKind.Number)
                    return (la.GetDouble(), r.GetProperty("longitude").GetDouble(), Str(r, "cityName"));
            }
            catch { }
            try // 3) ip-api.com
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync("http://ip-api.com/json/?fields=status,lat,lon,city").ConfigureAwait(false));
                var r = doc.RootElement;
                if (r.TryGetProperty("lat", out var la) && la.ValueKind == JsonValueKind.Number)
                    return (la.GetDouble(), r.GetProperty("lon").GetDouble(), Str(r, "city"));
            }
            catch { }
            return null;
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static string WeatherCachePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "weather.cache.json");

        private void LoadWeatherCache()
        {
            try
            {
                if (!File.Exists(WeatherCachePath)) return;
                var d = JsonSerializer.Deserialize<WeatherData>(File.ReadAllText(WeatherCachePath));
                if (d == null || string.IsNullOrEmpty(d.City) || d.Updated == default) return;
                lock (_wxLock) { if (_wx == null) _wx = d; }
            }
            catch { }
        }

        private static void SaveWeatherCache(WeatherData wx)
        {
            try { File.WriteAllText(WeatherCachePath, JsonSerializer.Serialize(wx)); } catch { }
        }

        private void LogError(string msg)
        {
            if (msg == _lastError) return; // 同一错误每秒都发生，只记一次
            _lastError = msg;
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log"),
                    DateTime.Now.ToString("HH:mm:ss") + " [lua] " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
