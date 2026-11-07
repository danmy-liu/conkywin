using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace ConkyWin
{
    public partial class App : Application
    {
        // 静态字段保活到进程退出；Local\ 限定当前登录会话，不同 Windows 用户互不干扰
        private static Mutex _panelMutex;

        /// <summary>
        /// 只允许一个「桌面面板」实例。--selftest（CI 自测）与 --settings（只开设置窗口）
        /// 不受此限制，否则已有面板时自测会被挡住、设置快捷方式也打不开。
        /// </summary>
        private static bool AcquirePanelMutex()
        {
            try
            {
                _panelMutex = new Mutex(true, @"Local\ConkyWin.Panel.v1", out bool createdNew);
                return createdNew;
            }
            catch { return true; } // 拿不到 Mutex 也照常启动，宁可偶尔多开不能起不来
        }
        /// <summary>
        /// 自己创建主窗口（不用 StartupUri），这样命令行参数可以直接改成自测 / 只开设置窗口。
        /// 正常双击运行走最后分支，行为与原来一致。
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            foreach (string a in e.Args)
            {
                // --selftest-core：只跑纯逻辑断言，不构造任何 WPF 窗口，供无桌面的 CI 环境当发版门禁
                if (string.Equals(a, "--selftest-core", StringComparison.OrdinalIgnoreCase))
                {
                    string core;
                    try { core = ConfigStore.SelfTest(); }
                    catch (Exception ex) { core = "自测本身异常：\r\n" + ex; }
                    try { File.WriteAllText(ConkyWin.AppPaths.SelfTestLogPath, core); }
                    catch { }
                    Shutdown();
                    return;
                }
                // --selftest：不打开任何窗口，跑配置读写 / 脚本语法检查并写 selftest.log
                if (string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase))
                {
                    string result;
                    try { result = ConfigStore.SelfTest(); }
                    catch (Exception ex) { result = "自测本身异常：\r\n" + ex; }

                    // 设置窗口构造 + 字段路径检查：确认每类文字都有独立接口，且没有重复路径互相覆盖
                    try
                    {
                        var probeWin = new SettingsWindow();
                        var fi = typeof(SettingsWindow).GetField("_fields",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        var paths = new System.Collections.Generic.List<string>();
                        foreach (var item in (System.Collections.IEnumerable)fi.GetValue(probeWin))
                        {
                            var pf = item.GetType().GetField("Path");
                            paths.Add((string)pf.GetValue(item));
                        }
                        string[] need =
                        {
                            "Styles.weather_city.LabelSize", "Styles.weather_city.Size",
                            "Styles.weather_city.LabelFont", "Styles.weather_city.Font",
                            "Styles.weather_info.LabelSize", "Styles.weather_forecast.CellSubSize",
                            "UiTheme"
                        };
                        bool missing = false;
                        foreach (string n in need) if (!paths.Contains(n)) missing = true;
                        var uniq = new System.Collections.Generic.HashSet<string>(paths);
                        var dups = new System.Collections.Generic.List<string>();
                        foreach (string p in uniq)
                        {
                            int n = 0;
                            foreach (string q in paths) if (q == p) n++;
                            if (n > 1) dups.Add(p + "×" + n);
                        }
                        result += "\r\n设置窗口字段检查: " +
                                  (!missing && uniq.Count == paths.Count ? "PASS" : "FAIL") +
                                  "（共 " + paths.Count + " 个字段，缺项=" + missing +
                                  "，重复=" + string.Join(", ", dups.ToArray()) + "）\r\n";

                        // 外观主题：浅色 / 深色切换是否真的改变了窗口画刷（每次重新取资源对象）
                        System.Func<string> bgColor = () =>
                            ((System.Windows.Media.SolidColorBrush)probeWin.Resources["UiBg"]).Color.ToString();
                        string lightBg = bgColor();
                        probeWin.ApplyTheme("dark");
                        string darkBg = bgColor();
                        probeWin.ApplyTheme("light");
                        string backBg = bgColor();
                        result += "外观主题检查: " +
                                  (lightBg != darkBg && lightBg == backBg ? "PASS" : "FAIL") +
                                  "（浅色=" + lightBg + "，深色=" + darkBg + "）\r\n";
                    }
                    catch (Exception ex) { result += "\r\n设置窗口构造失败：\r\n" + ex + "\r\n"; }

                    try { File.WriteAllText(ConkyWin.AppPaths.SelfTestLogPath, result); }
                    catch { }
                    Shutdown();
                    return;
                }
                // --settings：只开设置窗口（桌面快捷方式也能直接用）
                if (string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase))
                {
                    var s = new SettingsWindow();
                    MainWindow = s;
                    s.Show();
                    return;
                }
            }

            // 已有面板实例：静默退出（托盘图标已经在那里，不再弹窗打扰）
            if (!AcquirePanelMutex()) { Shutdown(); return; }

            var w = new MainWindow();
            MainWindow = w;
            w.Show();
        }
    }
}
