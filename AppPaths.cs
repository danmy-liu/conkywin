using System;
using System.IO;

namespace ConkyWin
{
    /// <summary>
    /// 所有运行时路径的唯一入口。
    ///
    /// 便携版（zip 解压即用）：config.json / script.lua / 日志都在 exe 同目录，与旧版行为完全一致。
    /// 安装版（安装到 Program Files 等只读目录）：exe 同目录会有 installed.flag，
    /// 用户可改的文件改放 %LOCALAPPDATA%\ConkyWin，避免普通权限写不了 Program Files。
    /// 这样升级安装覆盖 exe 时，绝不会覆盖用户改过的配置和脚本。
    /// </summary>
    public static class AppPaths
    {
        public const string InstalledFlag = "installed.flag";
        public const string PortableFlag = "portable.flag";
        private const string InitFlag = ".conkywin-init";

        private static readonly object Gate = new object();
        private static string _installDir;
        private static string _dataDir;
        private static bool _installed;
        private static bool _ready;

        /// <summary>exe 所在目录（只读资源都在这里）。</summary>
        public static string InstallDir { get { Ensure(); return _installDir; } }

        /// <summary>用户可写目录：配置、脚本、日志、天气缓存都在这里。</summary>
        public static string DataDir { get { Ensure(); return _dataDir; } }

        /// <summary>是否为安装版（数据目录与安装目录分离）。</summary>
        public static bool IsInstalled { get { Ensure(); return _installed; } }

        public static string ConfigPath { get { return Path.Combine(DataDir, "config.json"); } }
        public static string ScriptPath { get { return Path.Combine(DataDir, "script.lua"); } }
        public static string BackupPath { get { return Path.Combine(DataDir, "config.json.bak"); } }
        public static string LogPath { get { return Path.Combine(DataDir, "debug.log"); } }
        public static string WeatherCachePath { get { return Path.Combine(DataDir, "weather.cache.json"); } }
        public static string SelfTestLogPath { get { return Path.Combine(DataDir, "selftest.log"); } }

        /// <summary>只读资源（天气图标等）：先找 exe 目录，再退到数据目录，方便用户放自定义图标。</summary>
        public static string AssetPath(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return null;
            if (Path.IsPathRooted(relative)) return relative;
            string a = Path.Combine(InstallDir, relative);
            if (File.Exists(a)) return a;
            string b = Path.Combine(DataDir, relative);
            return File.Exists(b) ? b : a;
        }

        private static void Ensure()
        {
            lock (Gate)
            {
                if (_ready) return;
                _installDir = AppDomain.CurrentDomain.BaseDirectory;
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConkyWin");
                _dataDir = ResolveDataDir(_installDir, root,
                    Environment.GetEnvironmentVariable("CONKYWIN_DATA_DIR"));
                _installed = !SamePath(_dataDir, _installDir);
                try { EnsureDataFiles(); } catch { }
                _ready = true;
            }
        }

        private static bool SamePath(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'),
                                 Path.GetFullPath(b).TrimEnd('\\', '/'),
                                 StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>纯逻辑，便于 --selftest 覆盖便携 / 安装 / 环境变量 / 只读目录几种形态。</summary>
        public static string ResolveDataDir(string installDir, string userDataRoot, string envOverride)
        {
            if (!string.IsNullOrWhiteSpace(envOverride)) return envOverride;
            if (File.Exists(Path.Combine(installDir, PortableFlag))) return installDir;
            if (File.Exists(Path.Combine(installDir, InstalledFlag))) return userDataRoot;
            return CanWrite(installDir) ? installDir : userDataRoot;
        }

        private static bool CanWrite(string dir)
        {
            try
            {
                string p = Path.Combine(dir, ".conkywin-write-test-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                File.WriteAllText(p, "x");
                File.Delete(p);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 安装版首次运行：把安装目录里的默认配置/脚本模板复制到数据目录，只做一次。
        /// 之后用户删掉 script.lua 想回内置面板也不会被自动恢复。
        /// </summary>
        private static void EnsureDataFiles()
        {
            if (!_installed) return;
            if (!Directory.Exists(_dataDir)) Directory.CreateDirectory(_dataDir);
            string init = Path.Combine(_dataDir, InitFlag);
            if (File.Exists(init)) return;
            foreach (string name in new[] { "config.json", "script.lua" })
            {
                string src = Path.Combine(_installDir, name);
                string dst = Path.Combine(_dataDir, name);
                if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
            }
            try { File.WriteAllText(init, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")); } catch { }
        }
    }
}
