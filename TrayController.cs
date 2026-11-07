using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Windows;
using WF = System.Windows.Forms;
using SD = System.Drawing;

namespace ConkyWin
{
    /// <summary>
    /// 托盘图标入口。主面板点击穿透、不进任务栏，用户没有任何可点的位置，
    /// 所以托盘同时承担「打开设置 / 重新加载 / 开机自启 / 退出」四个入口。
    /// </summary>
    public class TrayController : IDisposable
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ConkyWin";

        private readonly MainWindow _main;
        private WF.NotifyIcon _icon;
        private SettingsWindow _settings;

        public TrayController(MainWindow main)
        {
            _main = main;
            try
            {
                _icon = new WF.NotifyIcon
                {
                    Visible = true,
                    Text = "ConkyWin · 桌面监控挂件",
                    Icon = LoadIcon()
                };
                _icon.DoubleClick += (s, e) => ShowSettings();

                var menu = new WF.ContextMenuStrip();
                var open = new WF.ToolStripMenuItem("打开设置");
                open.Click += (s, e) => ShowSettings();
                menu.Items.Add(open);

                var reload = new WF.ToolStripMenuItem("重新加载配置与脚本");
                reload.Click += (s, e) => _main.ForceReload();
                menu.Items.Add(reload);

                var folder = new WF.ToolStripMenuItem("打开配置文件夹");
                folder.Click += (s, e) => OpenFolder();
                menu.Items.Add(folder);

                menu.Items.Add(new WF.ToolStripSeparator());

                var auto = new WF.ToolStripMenuItem("开机自启")
                {
                    CheckOnClick = true,
                    Checked = IsAutoStart()
                };
                auto.CheckedChanged += (s, e) => SetAutoStart(auto.Checked);
                menu.Items.Add(auto);

                // 只检查 + 提示，不自动下载或替换 exe（详见 README「关于自动更新」）
                var update = new WF.ToolStripMenuItem("检查更新");
                update.Click += async (s, e) => await CheckUpdateAsync();
                menu.Items.Add(update);

                menu.Items.Add(new WF.ToolStripSeparator());

                var exit = new WF.ToolStripMenuItem("退出 ConkyWin");
                exit.Click += (s, e) =>
                {
                    Dispose();
                    System.Windows.Application.Current.Shutdown();
                };
                menu.Items.Add(exit);

                _icon.ContextMenuStrip = menu;
            }
            catch (Exception ex) { MainWindow.LogStatic("托盘图标初始化失败: " + ex); }
        }

        private static SD.Icon LoadIcon()
        {
            try
            {
                string exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    SD.Icon ico = SD.Icon.ExtractAssociatedIcon(exe);
                    if (ico != null) return ico;
                }
            }
            catch { }
            // 兜底：运行时画一个 32x32 的小图标（自包含单文件下也能用）
            try
            {
                var bmp = new SD.Bitmap(32, 32);
                using (var g = SD.Graphics.FromImage(bmp))
                {
                    g.Clear(SD.Color.FromArgb(20, 30, 43));
                    var colors = new[] { SD.Color.FromArgb(77, 227, 224), SD.Color.FromArgb(47, 158, 255),
                                         SD.Color.FromArgb(155, 123, 255), SD.Color.FromArgb(255, 200, 92) };
                    var widths = new[] { 22, 15, 25, 11 };
                    for (int i = 0; i < 4; i++)
                        using (var br = new SD.SolidBrush(colors[i]))
                            g.FillRectangle(br, 5, 5 + i * 6, widths[i], 4);
                }
                return SD.Icon.FromHandle(bmp.GetHicon());
            }
            catch { return SD.SystemIcons.Application; }
        }

        public void ShowSettings()
        {
            try
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    if (_settings != null)
                    {
                        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
                        _settings.Activate();
                        return;
                    }
                    _settings = new SettingsWindow();
                    _settings.Closed += (s, e) => _settings = null;
                    _settings.Show();
                    _settings.Activate();
                });
            }
            catch (Exception ex) { MainWindow.LogStatic("打开设置窗口失败: " + ex); }
        }

        private static void OpenFolder()
        {
            try
            {
                string cfg = ConfigStore.ConfigPath;
                Process.Start("explorer.exe", "/select,\"" + cfg + "\"");
            }
            catch
            {
                try { Process.Start("explorer.exe", ConfigStore.BaseDir); } catch { }
            }
        }

        private static bool IsAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k != null && k.GetValue(ValueName) != null;
            }
            catch { return false; }
        }

        private static void SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on)
                    {
                        string exe = Environment.ProcessPath;
                        if (!string.IsNullOrEmpty(exe)) k.SetValue(ValueName, "\"" + exe + "\"");
                    }
                    else k.DeleteValue(ValueName, false);
                }
            }
            catch (Exception ex) { MainWindow.LogStatic("设置开机自启失败: " + ex); }
        }

        private const string GithubLatestApi = "https://api.github.com/repos/danmy-liu/conkywin/releases/latest";
        private const string GiteeReleasesApi = "https://gitee.com/api/v5/repos/danmyleo/conkywin/releases?per_page=1";
        private const string DownloadPage = "https://gitee.com/danmyleo/conkywin/releases";

        private static Version CurrentVersion()
        {
            try
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
            catch { return new Version(0, 0, 0); }
        }

        /// <summary>只读取最新 Release 的 tag_name，不做任何下载 / 校验 / 替换。</summary>
        private static async Task<string> LatestTagAsync()
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ConkyWin/" + CurrentVersion());
                foreach (string url in new[] { GithubLatestApi, GiteeReleasesApi })
                {
                    try
                    {
                        string body = await http.GetStringAsync(url).ConfigureAwait(false);
                        using (JsonDocument doc = JsonDocument.Parse(body))
                        {
                            JsonElement root = doc.RootElement;
                            if (root.ValueKind == JsonValueKind.Array)   // Gitee 返回数组
                            {
                                if (root.GetArrayLength() == 0) continue;
                                root = root[0];
                            }
                            if (root.TryGetProperty("tag_name", out JsonElement tag)) return tag.GetString();
                        }
                    }
                    catch { } // 单侧接口不通就换下一个
                }
            }
            return null;
        }

        private static async Task CheckUpdateAsync()
        {
            string tag = await LatestTagAsync();
            Version cur = CurrentVersion();
            string curText = "v" + cur.ToString(3);
            if (tag == null)
            {
                WF.MessageBox.Show("暂时无法检查更新（网络不可用或接口异常）。\n\n可稍后重试，或到 Gitee / GitHub 的 Releases 页面查看。",
                    "ConkyWin · 检查更新", WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Information);
                return;
            }
            bool newer = Version.TryParse(tag.TrimStart('v', 'V'), out Version latest) && latest > cur;
            if (!newer)
            {
                WF.MessageBox.Show("当前已是最新版本 " + curText + "。",
                    "ConkyWin · 检查更新", WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Information);
                return;
            }
            var r = WF.MessageBox.Show(
                "发现新版本 " + tag + "（当前 " + curText + "）。\n\n是否打开发布页下载？",
                "ConkyWin · 检查更新", WF.MessageBoxButtons.YesNo, WF.MessageBoxIcon.Information);
            if (r == WF.DialogResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo(DownloadPage) { UseShellExecute = true }); }
                catch { }
            }
        }

        public void Dispose()
        {
            try
            {
                if (_icon != null)
                {
                    _icon.Visible = false;
                    if (_icon.ContextMenuStrip != null) _icon.ContextMenuStrip.Dispose();
                    _icon.Dispose();
                    _icon = null;
                }
            }
            catch { }
            try { _settings?.Close(); } catch { }
        }
    }
}
