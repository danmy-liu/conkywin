using System;
using System.IO;
using System.Windows;

namespace ConkyWin
{
    public partial class App : Application
    {
        /// <summary>
        /// 自己创建主窗口（不用 StartupUri），这样命令行参数可以直接改成自测 / 只开设置窗口。
        /// 正常双击运行走最后分支，行为与原来一致。
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            foreach (string a in e.Args)
            {
                // --selftest：不打开任何窗口，跑配置读写 / 脚本语法检查并写 selftest.log
                if (string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase))
                {
                    string result;
                    try { result = ConfigStore.SelfTest(); }
                    catch (Exception ex) { result = "自测本身异常：\r\n" + ex; }
                    try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.log"), result); }
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

            var w = new MainWindow();
            MainWindow = w;
            w.Show();
        }
    }
}
