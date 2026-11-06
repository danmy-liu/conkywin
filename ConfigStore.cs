using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;

namespace ConkyWin
{
    /// <summary>
    /// 配置读写的公共逻辑（不依赖 WPF，便于命令行自测）。
    ///
    /// 关键约定：
    /// 1. 用 JsonNode 读写，只改有控件的键，用户自定义键（ScaleFloorKb / City / 随便加的键）原样保留；
    ///    绝不能用强类型 Config 整体反序列化再序列化覆盖，那会丢掉所有未知键。
    /// 2. 写文件走「临时文件 + File.Replace」，因为主程序每秒轮询 mtime，直接覆写可能读到半个 JSON。
    /// 3. 统一 UTF-8 无 BOM；读取时剥掉记事本等编辑器加的 BOM。
    /// </summary>
    public static class ConfigStore
    {
        public static string BaseDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        public static string ConfigPath { get { return Path.Combine(BaseDir, "config.json"); } }
        public static string ScriptPath { get { return Path.Combine(BaseDir, "script.lua"); } }
        public static string BackupPath { get { return Path.Combine(BaseDir, "config.json.bak"); } }

        /// <summary>读文本并剥掉 BOM；文件不存在返回 null。</summary>
        public static string ReadText(string path)
        {
            if (!File.Exists(path)) return null;
            string t = File.ReadAllText(path);
            if (t.Length > 0 && t[0] == '\uFEFF') t = t.Substring(1);
            return t;
        }

        public static JsonObject Parse(string text)
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) as JsonObject;
        }

        /// <summary>路径形如 "Position.X" / "Cpu.ValueColor"；不存在返回 null。</summary>
        public static string Get(JsonObject root, string path)
        {
            JsonNode n = Find(root, path, false);
            if (n == null) return null;
            if (n is JsonValue v)
            {
                string s;
                if (v.TryGetValue<string>(out s)) return s;
                return v.ToJsonString();
            }
            return n.ToJsonString();
        }

        /// <summary>按点分路径写入，自动补建中间对象。</summary>
        public static void Set(JsonObject root, string path, JsonNode value)
        {
            string[] segs = path.Split('.');
            JsonObject cur = root;
            for (int i = 0; i < segs.Length - 1; i++)
            {
                JsonObject next = cur[segs[i]] as JsonObject;
                if (next == null) { next = new JsonObject(); cur[segs[i]] = next; }
                cur = next;
            }
            cur[segs[segs.Length - 1]] = value;
        }

        private static JsonNode Find(JsonObject root, string path, bool create)
        {
            string[] segs = path.Split('.');
            JsonObject cur = root;
            for (int i = 0; i < segs.Length - 1; i++)
            {
                JsonObject next = cur[segs[i]] as JsonObject;
                if (next == null)
                {
                    if (!create) return null;
                    next = new JsonObject();
                    cur[segs[i]] = next;
                }
                cur = next;
            }
            return cur[segs[segs.Length - 1]];
        }

        public static string ToJson(JsonObject root)
        {
            return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>原子替换（同目录临时文件 → File.Replace），失败时退化为覆盖写。</summary>
        public static void AtomicWrite(string path, string text)
        {
            string dir = Path.GetDirectoryName(path);
            string tmp = Path.Combine(dir, Path.GetFileName(path) + ".tmp");
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch
            {
                File.Copy(tmp, path, true);
                try { File.Delete(tmp); } catch { }
            }
        }

        public static void Backup()
        {
            try { if (File.Exists(ConfigPath)) File.Copy(ConfigPath, BackupPath, true); }
            catch { }
        }

        // ---------- 内置默认模板（编译进 exe，发行包里不必额外附带） ----------

        public static string Embedded(string logicalName)
        {
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName);
            if (s == null) return null;
            using (s)
            using (var r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        public static string DefaultConfig { get { return Embedded("ConkyWin.Defaults.config.json") ?? "{}"; } }
        public static string DefaultScript { get { return Embedded("ConkyWin.Defaults.script.lua") ?? ""; } }

        // ---------- Lua 脚本辅助 ----------

        /// <summary>只编译不执行，用于保存前的语法检查；返回 null 表示通过。</summary>
        public static string ValidateLua(string code)
        {
            try
            {
                var s = new Script(CoreModules.Preset_HardSandbox);
                s.LoadString(code ?? "", null, "script.lua");
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        private static readonly Regex ThemeLine = new Regex(
            "(?m)^[ \\t]*local[ \\t]+THEME[ \\t]*=[ \\t]*[\"'](color|mono)[\"']",
            RegexOptions.Compiled);

        public static string GetTheme(string lua)
        {
            Match m = ThemeLine.Match(lua ?? "");
            return m.Success ? m.Groups[1].Value : "color";
        }

        /// <summary>替换脚本里的 THEME 行；脚本没有该行时原样返回（用户自定义脚本不吃这套）。</summary>
        public static string SetTheme(string lua, string theme)
        {
            if (lua == null) return lua;
            if (!ThemeLine.IsMatch(lua)) return lua;
            return ThemeLine.Replace(lua, "local THEME = \"" + theme + "\"", 1);
        }

        // ---------- 脚本 C 表配色同步 ----------

        private static readonly Regex PaletteBlock = new Regex(
            @"(?<head>\bC\s*=\s*\{)(?<body>.*?)(?<tail>\})",
            RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>脚本是否含有可同步的 C 配色表（自定义脚本可能没有）。</summary>
        public static bool HasPalette(string lua)
        {
            return !string.IsNullOrEmpty(lua) && PaletteBlock.IsMatch(lua);
        }

        /// <summary>
        /// 把配置窗口里改过的颜色写回脚本的每个 C = { ... } 表（color / mono 两个分支都改）。
        /// 只替换表中已存在的键，找不到就原样返回，绝不破坏自定义脚本。
        /// </summary>
        public static string SetPalette(string lua, Dictionary<string, string> changes)
        {
            if (string.IsNullOrEmpty(lua) || changes == null || changes.Count == 0) return lua;
            return PaletteBlock.Replace(lua, m =>
            {
                string body = m.Groups["body"].Value;
                foreach (var kv in changes)
                {
                    if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                    var re = new Regex(@"(\b" + Regex.Escape(kv.Key) + @"\s*=\s*)""[^""]*""",
                                       RegexOptions.IgnoreCase);
                    body = re.Replace(body, mm => mm.Groups[1].Value + "\"" + kv.Value + "\"");
                }
                return m.Groups["head"].Value + body + m.Groups["tail"].Value;
            });
        }

        /// <summary>命令行自测：ConkyWin.exe --selftest，结果写 exe 同目录 selftest.log。</summary>
        public static string SelfTest()
        {
            var sb = new StringBuilder();
            int pass = 0, fail = 0;
            Action<string, bool, string> check = (name, ok, detail) =>
            {
                if (ok) { pass++; sb.AppendLine("PASS  " + name); }
                else { fail++; sb.AppendLine("FAIL  " + name + "  " + detail); }
            };

            string dir = Path.Combine(Path.GetTempPath(), "ConkyWin-Test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                string cfg = Path.Combine(dir, "config.json");
                string raw = "{\"Position\":{\"Y\":360},\"MyCustom\":{\"Deep\":7},\"GraphHeight\":30}";
                File.WriteAllText(cfg, "\uFEFF" + raw, new UTF8Encoding(false));

                string text = ReadText(cfg);
                check("剥离 BOM", text != null && text.Length > 0 && text[0] != '\uFEFF', "首字符=" + (text ?? "")[0]);

                JsonObject root = Parse(text);
                check("解析 JSON", root != null, "");
                check("读取嵌套键", Get(root, "Position.Y") == "360", "实际=" + Get(root, "Position.Y"));
                check("读取自定义键", Get(root, "MyCustom.Deep") == "7", "实际=" + Get(root, "MyCustom.Deep"));
                check("缺失键返回 null", Get(root, "Nope.Nope") == null, "");

                Set(root, "GraphHeight", JsonValue.Create(42));
                Set(root, "NewKey", JsonValue.Create("hello"));
                string json = ToJson(root);
                check("写入已有键", Get(Parse(json), "GraphHeight") == "42", "");
                check("新建顶级键", Get(Parse(json), "NewKey") == "hello", "");
                check("保留自定义键", Get(Parse(json), "MyCustom.Deep") == "7", json);
                check("保留原有键", Get(Parse(json), "Position.Y") == "360", "");

                AtomicWrite(cfg, json);
                check("原子写回可读", Get(Parse(ReadText(cfg)), "GraphHeight") == "42", "");
                check("无残留临时文件", !File.Exists(cfg + ".tmp"), "");
                check("输出无 BOM", new FileInfo(cfg).Length > 0 &&
                    !(File.ReadAllBytes(cfg)[0] == 0xEF && File.ReadAllBytes(cfg)[1] == 0xBB), "");

                check("Lua 合法语法通过", ValidateLua("local x = 1\nreturn x") == null, "");
                check("Lua 错误语法被拦截", ValidateLua("local x = (") != null, "");

                string def = DefaultScript;
                check("内嵌默认脚本非空", !string.IsNullOrWhiteSpace(def) && def.Contains("conky_rows"),
                    "长度=" + (def ?? "").Length);
                check("内嵌默认配置非空", DefaultConfig.Contains("PanelWidth"), "");
                check("识别 THEME", GetTheme(def) == "color", "实际=" + GetTheme(def));
                string mono = SetTheme(def, "mono");
                check("切换 THEME", GetTheme(mono) == "mono" && mono.Contains("local THEME = \"mono\""), "");
                check("THEME 切换不动其它行", mono.Length > def.Length - 20, "");

                check("识别 C 配色表", HasPalette(def) && !HasPalette("local x = 1"), "");
                var pal = new Dictionary<string, string> { { "text", "#FF2020" }, { "label", "#20FF20" } };
                string colored = SetPalette(def, pal);
                check("C 表写入正文色", colored.Contains("text = \"#FF2020\""), "");
                check("C 表写入标签色", colored.Contains("label = \"#20FF20\""), "");
                check("C 表两分支同步", colored.Split(new[] { "text = \"#FF2020\"" },
                      StringSplitOptions.None).Length - 1 == 2, "");
                check("C 表同步不破坏脚本", colored.Contains("conky_rows") &&
                      colored.Length > def.Length - 20, "");
                check("自定义脚本颜色同步为空操作",
                      SetPalette("local x = 1", pal) == "local x = 1", "");
            }
            catch (Exception ex) { check("自测过程无异常", false, ex.ToString()); }
            finally { try { Directory.Delete(dir, true); } catch { } }

            sb.Insert(0, "ConkyWin ConfigStore 自测  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" +
                         "通过 " + pass + " / 失败 " + fail + "\r\n\r\n");
            return sb.ToString();
        }
    }
}
