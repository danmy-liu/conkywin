using System;
using System.Diagnostics;
using System.IO;
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
