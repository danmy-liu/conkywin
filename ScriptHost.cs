using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
            public double NetDownKb, NetUpKb;
            public double GpuUtil, GpuTemp, GpuMemUsedGb, GpuMemTotalGb;
            public bool GpuAvailable;
            public int Proc;
        }

        public class Row
        {
            public string Label = "";
            public string Text = "";
            public string Color;        // null → 用默认色
            public double? Bar;         // null → 该行无进度条
            public string BarStart;
            public string BarEnd;
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
            _script.Globals["conky"] = _api;
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
                    };
                    var bar = t.Get("bar");
                    if (bar.Type == DataType.Number)
                        r.Bar = Math.Max(0, Math.Min(100, bar.Number));
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
