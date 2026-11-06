using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WF = System.Windows.Forms;
using SD = System.Drawing;

namespace ConkyWin
{
    /// <summary>
    /// 可视化配置窗口。配置写 exe 同目录 config.json，靠 MainWindow 的 mtime 轮询自动生效，
    /// 所以这里不需要和主面板做任何 IPC，也不要求重启程序。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private enum Kind { Int, Double, Bool, Text, Color }

        private class Field
        {
            public string Path;
            public Kind Kind;
            public string Default;
            public string Loaded;
            public Func<string> Get;
            public Action<string> Set;
            public Action<bool> MarkValid;
            public Action<bool> SetEnabled;
        }

        private readonly List<Field> _fields = new List<Field>();
        private readonly DispatcherTimer _saveTimer;
        private readonly string[] _fontNames = SystemFontNames();
        private JsonObject _root;
        private bool _loading;
        private bool _configDirty;
        private bool _rawLoading, _rawDirty;
        private bool _luaLoading, _luaDirty;
        private string _luaSaved = "";
        private string _currentTheme = "color";

        public SettingsWindow()
        {
            InitializeComponent();
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _saveTimer.Tick += (s, e) =>
            {
                _saveTimer.Stop();
                if (_configDirty) SaveConfig();
            };
            Tabs.SelectionChanged += (s, e) =>
            {
                if (Tabs.SelectedIndex == 5 && !_rawDirty) RefreshRaw();
            };

            BuildGeneral();
            BuildColors();
            BuildFonts();
            BuildScriptParams();
            LoadConfig();
            LoadScript();
        }

        // ---------- 构建表单 ----------

        private static void AddSection(Panel p, string title, string note)
        {
            p.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4D, 0xE3, 0xE0)),
                Margin = new Thickness(0, 14, 0, 3)
            });
            if (!string.IsNullOrEmpty(note))
                p.Children.Add(new TextBlock
                {
                    Text = note,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x7E, 0x93, 0xAB)),
                    Margin = new Thickness(0, 0, 0, 6),
                    TextWrapping = TextWrapping.Wrap
                });
        }

        private static StackPanel NewRow(Panel p)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            p.Children.Add(row);
            return row;
        }

        private static TextBlock NewLabel(string text)
        {
            return new TextBlock { Text = text, Width = 168, FontSize = 13 };
        }

        private static TextBlock NewHint(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7E, 0x93, 0xAB)),
                Margin = new Thickness(12, 0, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 300
            };
        }

        private Field Register(Field f)
        {
            _fields.Add(f);
            return f;
        }

        private Field AddNumber(Panel p, string label, string path, string def, Kind kind, string hint)
        {
            var row = NewRow(p);
            row.Children.Add(NewLabel(label));
            var tb = new TextBox { Width = 110 };
            tb.TextChanged += (s, e) => { if (!_loading) Changed(); };
            row.Children.Add(tb);
            row.Children.Add(NewHint(hint));
            return Register(new Field
            {
                Path = path, Kind = kind, Default = def,
                Get = () => tb.Text.Trim(),
                Set = v => tb.Text = v,
                SetEnabled = on => tb.IsEnabled = on,
                MarkValid = ok => tb.BorderBrush = new SolidColorBrush(
                    ok ? Color.FromRgb(0x33, 0x46, 0x5C) : Color.FromRgb(0xE2, 0x4B, 0x4A))
            });
        }

        /// <summary>
        /// 整数值下拉：把「人话」选项映射到配置里的整数值（如 RightGap 0 / -1）。
        /// 配置里出现选项以外的值时，动态补一项「自定义值」，避免保存时被悄悄改掉。
        /// </summary>
        private Field AddComboNum(Panel p, string label, string path, int def,
                                  Tuple<string, int>[] options, string hint, Action<int> onValue = null)
        {
            var row = NewRow(p);
            row.Children.Add(NewLabel(label));
            var cb = new ComboBox { Width = 230, FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center };
            foreach (var o in options) cb.Items.Add(new ComboBoxItem { Content = o.Item1, Tag = o.Item2 });
            cb.SelectionChanged += (s, e) =>
            {
                if (_loading) return;
                Changed();
                var it = cb.SelectedItem as ComboBoxItem;
                if (onValue != null && it != null) onValue((int)it.Tag);
            };
            row.Children.Add(cb);
            row.Children.Add(NewHint(hint));
            return Register(new Field
            {
                Path = path, Kind = Kind.Int, Default = def.ToString(CultureInfo.InvariantCulture),
                Get = () =>
                {
                    var it = cb.SelectedItem as ComboBoxItem;
                    return it != null && it.Tag != null
                        ? it.Tag.ToString() : def.ToString(CultureInfo.InvariantCulture);
                },
                Set = v =>
                {
                    ComboBoxItem found = null;
                    foreach (ComboBoxItem it in cb.Items)
                        if (it.Tag != null && it.Tag.ToString() == v) { found = it; break; }
                    if (found == null)
                    {
                        int n;
                        if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) n = def;
                        found = new ComboBoxItem { Content = "自定义值 (" + v + ")", Tag = n };
                        cb.Items.Add(found);
                    }
                    cb.SelectedItem = found;
                    if (onValue != null) onValue((int)found.Tag);
                }
            });
        }

        /// <summary>枚举系统已安装字体族，供字体下拉使用；读不到时给几个安全兜底。</summary>
        private static string[] SystemFontNames()
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in System.Windows.Media.Fonts.SystemFontFamilies)
                {
                    string n = f.Source;
                    if (!string.IsNullOrWhiteSpace(n)) set.Add(n.Trim());
                }
            }
            catch { }
            if (set.Count == 0)
            {
                set.Add("Consolas"); set.Add("Microsoft YaHei UI");
                set.Add("Segoe UI"); set.Add("Segoe UI Symbol");
            }
            var arr = new string[set.Count];
            set.CopyTo(arr);
            return arr;
        }

        private static Action<bool> BorderFlag(Control ctl)
        {
            return ok => ctl.BorderBrush = new SolidColorBrush(
                ok ? Color.FromRgb(0x33, 0x46, 0x5C) : Color.FromRgb(0xE2, 0x4B, 0x4A));
        }

        private static string ComboTag(ComboBox cb, string def)
        {
            var it = cb.SelectedItem as ComboBoxItem;
            return it != null && it.Tag != null ? it.Tag.ToString() : def;
        }

        private static void SelectCombo(ComboBox cb, string v, string def)
        {
            v = v ?? def;
            foreach (ComboBoxItem it in cb.Items)
                if (it.Tag != null && it.Tag.ToString() == v) { cb.SelectedItem = it; return; }
            var extra = new ComboBoxItem { Content = "自定义 (" + v + ")", Tag = v };
            cb.Items.Add(extra);
            cb.SelectedItem = extra;
        }

        private void FillFontCombo(ComboBox cb)
        {
            cb.Items.Add(new ComboBoxItem { Content = "跟随全局", Tag = "" });
            foreach (string f in _fontNames)
                cb.Items.Add(new ComboBoxItem { Content = f, Tag = f });
        }

        /// <summary>字符串值下拉；切换时可用 onValue(新值, 旧值) 联动其它控件。</summary>
        private Field AddComboText(Panel p, string label, string path, string def,
                                   Tuple<string, string>[] options, string hint,
                                   Action<string, string> onValue = null)
        {
            var row = NewRow(p);
            row.Children.Add(NewLabel(label));
            var cb = new ComboBox
            {
                Width = 250, FontSize = 13,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            foreach (var o in options) cb.Items.Add(new ComboBoxItem { Content = o.Item1, Tag = o.Item2 });
            string prev = def;
            cb.SelectionChanged += (s, e) =>
            {
                if (_loading) return;
                string v = ComboTag(cb, def);
                Changed();
                if (onValue != null) onValue(v, prev);
                prev = v;
            };
            row.Children.Add(cb);
            row.Children.Add(NewHint(hint));
            return Register(new Field
            {
                Path = path, Kind = Kind.Text, Default = def,
                Get = () => ComboTag(cb, def),
                Set = v => { SelectCombo(cb, v, def); prev = v ?? def; }
            });
        }

        private Field AddFontCombo(Panel p, string label, string path, string def, string hint)
        {
            var row = NewRow(p);
            row.Children.Add(NewLabel(label));
            var cb = new ComboBox
            {
                Width = 250, FontSize = 13, IsTextSearchEnabled = true,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            FillFontCombo(cb);
            cb.SelectionChanged += (s, e) => { if (!_loading) Changed(); };
            row.Children.Add(cb);
            row.Children.Add(NewHint(hint));
            return Register(new Field
            {
                Path = path, Kind = Kind.Text, Default = def,
                Get = () => ComboTag(cb, def),
                Set = v => SelectCombo(cb, v, def),
                MarkValid = BorderFlag(cb)
            });
        }

        /// <summary>
        /// 一个区块一行：主字号 / 标签字号 / 字体。字号填 0 表示跟随全局，
        /// 字体选「跟随全局」写空串。三个控件各自注册成 Field，保存时只写改动过的键。
        /// </summary>
        private void AddStyleRow(Panel p, string key, string name, double defSize, double defLabel, bool cells)
        {
            var row = NewRow(p);
            row.Children.Add(new TextBlock
            {
                Text = name, Width = 168, FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xE6, 0xF5)),
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
            });
            var tbSize = new TextBox
            {
                Width = 56, TextAlignment = TextAlignment.Center,
                ToolTip = "主字号；填 0 = 跟随全局"
            };
            var tbLab = new TextBox
            {
                Width = 56, Margin = new Thickness(6, 0, 0, 0), TextAlignment = TextAlignment.Center,
                ToolTip = "左侧标签字号；填 0 = 跟随全局"
            };
            var cb = new ComboBox
            {
                Width = 190, Margin = new Thickness(8, 0, 0, 0), FontSize = 12,
                IsTextSearchEnabled = true, VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "该区块使用的字体"
            };
            FillFontCombo(cb);
            tbSize.TextChanged += (s, e) => { if (!_loading) Changed(); };
            tbLab.TextChanged += (s, e) => { if (!_loading) Changed(); };
            cb.SelectionChanged += (s, e) => { if (!_loading) Changed(); };
            row.Children.Add(tbSize);
            row.Children.Add(tbLab);
            row.Children.Add(cb);

            string kSize = cells ? "CellSize" : "Size";
            string kLab = cells ? "CellLabelSize" : "LabelSize";
            Register(new Field
            {
                Path = "Styles." + key + "." + kSize, Kind = Kind.Double,
                Default = defSize.ToString(CultureInfo.InvariantCulture),
                Get = () => tbSize.Text.Trim(), Set = v => tbSize.Text = v,
                MarkValid = BorderFlag(tbSize)
            });
            Register(new Field
            {
                Path = "Styles." + key + "." + kLab, Kind = Kind.Double,
                Default = defLabel.ToString(CultureInfo.InvariantCulture),
                Get = () => tbLab.Text.Trim(), Set = v => tbLab.Text = v,
                MarkValid = BorderFlag(tbLab)
            });
            Register(new Field
            {
                Path = "Styles." + key + ".Font", Kind = Kind.Text, Default = "",
                Get = () => ComboTag(cb, ""), Set = v => SelectCombo(cb, v, ""),
                MarkValid = BorderFlag(cb)
            });
        }

        private void AddSlider(Panel p, string label, string path, int def, int min, int max, string hint)
        {
            var row = NewRow(p);
            row.Children.Add(NewLabel(label));
            var tb = new TextBox { Width = 58, TextAlignment = TextAlignment.Center };
            var sl = new Slider
            {
                Minimum = min, Maximum = max, Width = 190,
                TickFrequency = 1, IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            sl.ValueChanged += (s, e) =>
            {
                if (_loading) return;
                string v = ((int)sl.Value).ToString(CultureInfo.InvariantCulture);
                if (tb.Text != v) tb.Text = v;
                Changed();
            };
            tb.TextChanged += (s, e) =>
            {
                if (_loading) return;
                int v;
                if (int.TryParse(tb.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)
                    && v >= min && v <= max && (int)sl.Value != v) sl.Value = v;
                Changed();
            };
            row.Children.Add(tb);
            row.Children.Add(sl);
            row.Children.Add(NewHint(hint));
            Register(new Field
            {
                Path = path, Kind = Kind.Int, Default = def.ToString(CultureInfo.InvariantCulture),
                Get = () => tb.Text.Trim(),
                Set = v =>
                {
                    tb.Text = v;
                    int n;
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)
                        && n >= min && n <= max) sl.Value = n;
                },
                MarkValid = ok => tb.BorderBrush = new SolidColorBrush(
                    ok ? Color.FromRgb(0x33, 0x46, 0x5C) : Color.FromRgb(0xE2, 0x4B, 0x4A))
            });
        }

        private void AddCheck(Panel p, string label, string path, bool def, string hint)
        {
            var row = NewRow(p);
            row.Children.Add(NewLabel(label));
            var cb = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
            cb.Checked += (s, e) => { if (!_loading) Changed(); };
            cb.Unchecked += (s, e) => { if (!_loading) Changed(); };
            row.Children.Add(cb);
            row.Children.Add(NewHint(hint));
            Register(new Field
            {
                Path = path, Kind = Kind.Bool,
                Default = def ? "true" : "false",
                Get = () => cb.IsChecked == true ? "true" : "false",
                Set = v => cb.IsChecked = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
            });
        }

        private void AddText(Panel p, string label, string path, string def, string hint)
        {
            var row = NewRow(p);
            row.Children.Add(NewLabel(label));
            var tb = new TextBox { Width = 220 };
            tb.TextChanged += (s, e) => { if (!_loading) Changed(); };
            row.Children.Add(tb);
            row.Children.Add(NewHint(hint));
            Register(new Field
            {
                Path = path, Kind = Kind.Text, Default = def,
                Get = () => tb.Text,
                Set = v => tb.Text = v
            });
        }

        private void AddColor(Panel p, string label, string path, string def, string hint)
        {
            var row = NewRow(p);
            row.Children.Add(NewLabel(label));
            var tb = new TextBox { Width = 110, FontFamily = new FontFamily("Consolas") };
            var sw = new Border
            {
                Width = 30, Height = 22, Margin = new Thickness(8, 0, 0, 0),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x46, 0x5C)),
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent
            };
            var btn = new Button { Content = "取色", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(9, 4, 9, 4) };
            btn.Click += (s, e) => PickColor(tb, sw);
            tb.TextChanged += (s, e) =>
            {
                sw.Background = HexToBrush(tb.Text);
                if (!_loading) Changed();
            };
            row.Children.Add(tb);
            row.Children.Add(sw);
            row.Children.Add(btn);
            if (!string.IsNullOrEmpty(hint)) row.Children.Add(NewHint(hint));
            Register(new Field
            {
                Path = path, Kind = Kind.Color, Default = def,
                Get = () => tb.Text.Trim(),
                Set = v => { tb.Text = v; sw.Background = HexToBrush(v); },
                MarkValid = ok => tb.BorderBrush = new SolidColorBrush(
                    ok ? Color.FromRgb(0x33, 0x46, 0x5C) : Color.FromRgb(0xE2, 0x4B, 0x4A))
            });
        }

        private void BuildGeneral()
        {
            var p = GeneralPanel;
            AddSection(p, "位置", "X / Y 始终可填：选「绝对坐标」时是屏幕像素坐标；选对齐方式时是相对基准的微调偏移（正数向右 / 下，可为负）。从绝对坐标切到对齐方式时会自动把偏移归零，避免旧坐标把面板顶出屏幕。");
            Field fx = AddNumber(p, "X（像素）", "Position.X", "1652", Kind.Int, "屏幕横坐标或水平偏移");
            Field fy = AddNumber(p, "Y（像素）", "Position.Y", "300", Kind.Int, "屏幕纵坐标或垂直偏移");
            AddComboText(p, "水平对齐", "Position.HAlign", "absolute", new[]
            {
                Tuple.Create("绝对坐标（X 即屏幕横坐标）", "absolute"),
                Tuple.Create("左对齐（贴工作区左缘）", "left"),
                Tuple.Create("水平居中", "center"),
                Tuple.Create("右对齐（贴工作区右缘）", "right")
            }, "选对齐方式后 X 变成相对偏移",
                (v, old) => { if (v != "absolute" && old == "absolute") fx.Set("0"); });
            AddComboText(p, "垂直对齐", "Position.VAlign", "absolute", new[]
            {
                Tuple.Create("绝对坐标（Y 即屏幕纵坐标）", "absolute"),
                Tuple.Create("顶部对齐", "top"),
                Tuple.Create("垂直居中", "center"),
                Tuple.Create("底部对齐", "bottom")
            }, "选对齐方式后 Y 变成相对偏移",
                (v, old) => { if (v != "absolute" && old == "absolute") fy.Set("0"); });
            AddNumber(p, "右缘留白", "Position.RightGap", "0", Kind.Int, "靠右时距屏幕右边缘的像素");
            AddNumber(p, "下缘留白", "Position.BottomGap", "8", Kind.Int, "靠下时距屏幕下边缘的像素");

            AddSection(p, "字体与尺寸", "改动会在 1 秒后直接反映到桌面面板上。");
            AddSlider(p, "面板宽度", "PanelWidth", 268, 120, 800, "像素");
            AddSlider(p, "正文字号", "FontSize", 13, 8, 28, "磅");
            AddSlider(p, "时间字号", "TimeFontSize", 15, 8, 32, "磅");
            AddCheck(p, "粗体", "Bold", true, "数值与时间文字加粗");

            AddSection(p, "天气定位", "留空城市名并保持 999/999 时走 IP 自动定位；也可手动填经纬度。");
            AddText(p, "城市名", "Weather.City", "", "例如 Beijing，会走地理编码");
            AddNumber(p, "纬度 Lat", "Weather.Lat", "999", Kind.Double, "999 = 自动定位");
            AddNumber(p, "经度 Lon", "Weather.Lon", "999", Kind.Double, "999 = 自动定位");
        }

        private void BuildColors()
        {
            var p = ColorsPanel;
            AddSection(p, "全局", "支持 #RRGGBB 与带透明度的 #AARRGGBB。使用 Harmattan 脚本时，这里的颜色会同步写入脚本 C 表，所以界面取色和直接改脚本都有效；进度条渐变只作用于内置面板。");
            AddColor(p, "标签颜色", "LabelColor", "#A6BAD1", "CPU / MEM 等标签");
            AddColor(p, "进度条底色", "BarBackground", "#26FFFFFF", "半透明槽底");

            AddSection(p, "各模块数值与进度条", "BarStart / BarEnd 是进度条渐变两端；Net / Temp / Proc 只有数值颜色。");
            AddColor(p, "CPU 数值", "Cpu.ValueColor", "#4DE3E0", "");
            AddColor(p, "CPU 渐变起", "Cpu.BarStart", "#4DE3E0", "");
            AddColor(p, "CPU 渐变止", "Cpu.BarEnd", "#2F9EFF", "");
            AddColor(p, "MEM 数值", "Mem.ValueColor", "#7FB0FF", "");
            AddColor(p, "MEM 渐变起", "Mem.BarStart", "#6FA8FF", "");
            AddColor(p, "MEM 渐变止", "Mem.BarEnd", "#9B7BFF", "");
            AddColor(p, "DISK 数值", "Disk.ValueColor", "#FFC85C", "");
            AddColor(p, "DISK 渐变起", "Disk.BarStart", "#FFC85C", "");
            AddColor(p, "DISK 渐变止", "Disk.BarEnd", "#FF8A5C", "");
            AddColor(p, "NET 数值", "Net.ValueColor", "#8FD8FF", "");
            AddColor(p, "GPU 数值", "Gpu.ValueColor", "#B78CFF", "");
            AddColor(p, "GPU 渐变起", "Gpu.BarStart", "#B78CFF", "");
            AddColor(p, "GPU 渐变止", "Gpu.BarEnd", "#7A5CFF", "");
            AddColor(p, "TEMP 数值", "Temp.ValueColor", "#FF6B6B", "");
            AddColor(p, "PROC 数值", "Proc.ValueColor", "#EAF2FC", "");
        }

        // ---------- 字体与字号：区块名自动从脚本识别 ----------

        private static readonly Dictionary<string, string> StyleNames = new Dictionary<string, string>
        {
            { "weather_city", "天气 · 城市名 / 大字温度" },
            { "weather_desc", "天气 · 天气描述" },
            { "weather_info", "天气 · 湿度 / 风速 / 气压" },
            { "weather_forecast", "天气 · 三天预报" },
            { "weather_error", "天气 · 加载提示" },
            { "graph", "CPU / 内存 / 磁盘 IO / 网速曲线" },
            { "disk", "磁盘用量条" },
            { "total", "累计流量" },
            { "proc", "进程总数" },
            { "top", "Top 进程列表" },
        };
        private static readonly Dictionary<string, double> StyleSizes = new Dictionary<string, double>
        {
            { "weather_city", 26 }, { "weather_desc", 15 }, { "disk", 12 },
            { "top", 12 }, { "weather_forecast", 20 },
        };
        private static readonly Dictionary<string, double> StyleLabelSizes = new Dictionary<string, double>
        {
            { "weather_forecast", 11 },
        };
        private List<string> DiscoverStyles()
        {
            var found = new List<string>();
            string lua = LuaEditor != null ? LuaEditor.Text : "";
            if (string.IsNullOrEmpty(lua)) lua = ConfigStore.ReadText(ConfigStore.ScriptPath) ?? "";
            foreach (string k in ConfigStore.FindStyleTags(lua))
                if (!found.Contains(k)) found.Add(k);
            foreach (string k in StyleNames.Keys) if (!found.Contains(k)) found.Add(k);
            return found;
        }

        private void BuildFonts()
        {
            var p = FontsPanel;
            AddSection(p, "全局字体与字号", "所有区块的默认值。字体下拉直接列出系统已安装字体；留空表示跟随上一级默认。改动约 1 秒后生效。");
            AddFontCombo(p, "正文字体", "FontFamily", "Consolas", "数值、时间等主要文字");
            AddSlider(p, "正文字号", "FontSize", 13, 8, 36, "磅");
            AddFontCombo(p, "标签字体", "LabelFontFamily", "Microsoft YaHei UI", "左侧小标签：城市名 / 湿度 / CPU…");
            AddSlider(p, "标签字号", "LabelFontSize", 11, 7, 30, "磅");
            AddFontCombo(p, "时间字体", "TimeFontFamily", "", "内置简洁面板的时间；留空跟随正文字体");
            AddSlider(p, "时间字号", "TimeFontSize", 15, 8, 40, "磅");
            AddFontCombo(p, "多列字体", "CellFontFamily", "", "三天预报数值；留空用天气符号字体");
            AddSlider(p, "多列数值字号", "CellFontSize", 20, 8, 40, "磅");
            AddSlider(p, "多列小字字号", "CellSubFontSize", 11, 7, 30, "磅");
            AddSlider(p, "多列标题字号", "CellLabelFontSize", 11, 7, 30, "磅");

            AddSection(p, "分区块覆盖", "区块名自动从 script.lua 识别：在脚本里给行加 style = \"xxx\"，这里就会多出一行。主字号 / 标签字号填 0 = 跟随全局。改完约 1 秒生效，不用动脚本。");
            var head = NewRow(p);
            head.Children.Add(NewLabel("区块"));
            head.Children.Add(new TextBlock
            {
                Text = "主字号", Width = 56, FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7E, 0x93, 0xAB))
            });
            head.Children.Add(new TextBlock
            {
                Text = "标签字号", Width = 56, Margin = new Thickness(6, 0, 0, 0), FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7E, 0x93, 0xAB))
            });
            head.Children.Add(new TextBlock
            {
                Text = "字体（跟随全局 = 空）", Margin = new Thickness(8, 0, 0, 0), FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7E, 0x93, 0xAB))
            });
            foreach (string key in DiscoverStyles())
            {
                string name;
                if (!StyleNames.TryGetValue(key, out name)) name = key;
                double ds, dl;
                if (!StyleSizes.TryGetValue(key, out ds)) ds = 0;
                if (!StyleLabelSizes.TryGetValue(key, out dl)) dl = 0;
                AddStyleRow(p, key, name, ds, dl, key == "weather_forecast");
            }
        }

        private void BuildScriptParams()
        {
            var p = ScriptPanel;
            AddSection(p, "Harmattan 脚本参数", "这些是 script.lua 通过 conky.config() 读取的自定义键。换成别的脚本时它们会被忽略，不会报错。");
            AddSlider(p, "曲线高度", "GraphHeight", 30, 10, 80, "像素");
            AddSlider(p, "Top 进程条数", "TopProcesses", 5, 1, 15, "内存与 CPU 两个列表各自的行数");
            AddSlider(p, "速率曲线下限", "ScaleFloorKb", 16, 4, 1024, "KB/s，自适应满格量的下限");
            AddSection(p, "脚本文字配色", "会同步写入脚本的 C.text / C.accent（两个主题分支都写）。也可以直接在「Lua 脚本」页改 C 表，改完保存立即生效。");
            AddColor(p, "正文颜色", "TextColor", "#F2F7FF", "");
            AddColor(p, "强调色", "AccentColor", "#4DE3E0", "");
        }

        // ---------- 颜色工具 ----------

        private static bool IsValidHex(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return false;
            string h = v.Trim().TrimStart('#');
            if (h.Length != 6 && h.Length != 8) return false;
            foreach (char c in h) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        private static Brush HexToBrush(string hex)
        {
            if (!IsValidHex(hex)) return Brushes.Transparent;
            try { return (Brush)new BrushConverter().ConvertFromString(hex.StartsWith("#") ? hex : "#" + hex); }
            catch { return Brushes.Transparent; }
        }

        private static SD.Color HexToDrawing(string hex, out string alpha)
        {
            alpha = "";
            if (!IsValidHex(hex)) return SD.Color.FromArgb(0x4D, 0xE3, 0xE0);
            string h = hex.Trim().TrimStart('#');
            if (h.Length == 8) { alpha = h.Substring(0, 2); h = h.Substring(2); }
            try
            {
                return SD.Color.FromArgb(
                    int.Parse(h.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    int.Parse(h.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    int.Parse(h.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
            catch { return SD.Color.FromArgb(0x4D, 0xE3, 0xE0); }
        }

        private static void PickColor(TextBox hex, Border sw)
        {
            string alpha;
            SD.Color start = HexToDrawing(hex.Text, out alpha);
            using (var dlg = new WF.ColorDialog { FullOpen = true, AnyColor = true, Color = start })
            {
                if (dlg.ShowDialog() != WF.DialogResult.OK) return;
                hex.Text = string.Format("#{0}{1:X2}{2:X2}{3:X2}",
                    alpha, dlg.Color.R, dlg.Color.G, dlg.Color.B);
            }
            sw.Background = HexToBrush(hex.Text);
        }

        // ---------- 配置读写 ----------

        private void Changed()
        {
            if (_loading) return;
            _configDirty = true;
            StatusText.Text = "有修改，停止操作后自动保存…";
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void LoadConfig()
        {
            string text = ConfigStore.ReadText(ConfigStore.ConfigPath);
            if (text == null) text = ConfigStore.DefaultConfig;
            try { _root = ConfigStore.Parse(text); }
            catch (Exception ex)
            {
                MessageBox.Show("config.json 解析失败，已载入默认配置。\r\n" + ex.Message,
                    "ConkyWin 设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                _root = ConfigStore.Parse(ConfigStore.DefaultConfig);
            }
            if (_root == null) _root = new JsonObject();
            NormalizeAlign();
            BindFields();
            RefreshRaw();
            StatusText.Text = "已载入 " + Path.GetFileName(ConfigStore.ConfigPath);
        }

        /// <summary>
        /// 老配置只有 RightGap / BottomGap，对齐下拉无法如实显示实际状态。
        /// 这里按旧值推断 HAlign / VAlign 填进内存里的 _root（不立即写盘），
        /// 同时把 X / Y 归一化成偏移，保证第一次改动时面板不会突然跳到屏幕外。
        /// </summary>
        private void NormalizeAlign()
        {
            ConfigStore.NormalizeAlign(_root);
        }

        private void BindFields()
        {
            _loading = true;
            foreach (Field f in _fields)
            {
                string v = ConfigStore.Get(_root, f.Path);
                if (v == null) v = f.Default;
                f.Set(v);
                f.Loaded = v;
                f.MarkValid?.Invoke(true);
            }
            _loading = false;
            _configDirty = false;
        }

        private static JsonNode ToNode(string v, Kind kind)
        {
            switch (kind)
            {
                case Kind.Int:
                    int i;
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return JsonValue.Create(i);
                    return null;
                case Kind.Double:
                    double d;
                    if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return JsonValue.Create(d);
                    return null;
                case Kind.Bool:
                    bool b;
                    if (bool.TryParse(v, out b)) return JsonValue.Create(b);
                    return null;
                case Kind.Color:
                    if (!IsValidHex(v)) return null;
                    return JsonValue.Create(v);
                default:
                    return JsonValue.Create(v ?? "");
            }
        }

        private void SaveConfig()
        {
            if (_root == null) return;
            _saveTimer.Stop();
            JsonObject cand = ConfigStore.Parse(ConfigStore.ToJson(_root));
            bool bad = false;
            var palette = new Dictionary<string, string>();
            foreach (Field f in _fields)
            {
                string cur = f.Get();
                if (cur == f.Loaded) continue;
                JsonNode node = ToNode(cur, f.Kind);
                if (node == null) { bad = true; f.MarkValid?.Invoke(false); continue; }
                f.MarkValid?.Invoke(true);
                f.Loaded = cur;
                ConfigStore.Set(cand, f.Path, node);
                if (f.Kind == Kind.Color)
                {
                    string key = PaletteKey(f.Path);
                    if (key != null) palette[key] = cur;
                }
            }
            if (bad)
            {
                StatusText.Text = "有输入不合法（红框），未保存，请修正后重试";
                return;
            }
            try
            {
                ConfigStore.Backup();
                ConfigStore.AtomicWrite(ConfigStore.ConfigPath, ConfigStore.ToJson(cand));
                _root = cand;
                _configDirty = false;
                RefreshRaw();
                SyncPalette(palette);
                StatusText.Text = "已保存 · " + DateTime.Now.ToString("HH:mm:ss") + " · 约 1 秒后面板生效";
            }
            catch (Exception ex)
            {
                StatusText.Text = "保存失败：" + ex.Message;
                MessageBox.Show("写入 config.json 失败：\r\n" + ex.Message,
                    "ConkyWin 设置", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 完整脚本模式的渲染只认脚本里的 C 表，所以颜色控件光写 config.json 会「改了没反应」。
        /// 这里把本次真正改动的字段同步写入 C 表（两个主题分支都写），再保存脚本触发热重载。
        /// </summary>
        private void SyncPalette(Dictionary<string, string> palette)
        {
            if (palette.Count == 0 || !ConfigStore.HasPalette(LuaEditor.Text)) return;
            string next = ConfigStore.SetPalette(LuaEditor.Text, palette);
            if (next == LuaEditor.Text) return;
            try
            {
                ConfigStore.AtomicWrite(ConfigStore.ScriptPath, next);
                _luaLoading = true;
                LuaEditor.Text = next;
                _luaLoading = false;
                _luaSaved = next;
                _luaDirty = false;
                LuaStatus.Text = "配色已同步到脚本 C 表 · 约 1 秒后生效";
            }
            catch (Exception ex) { StatusText.Text = "颜色同步到脚本失败：" + ex.Message; }
        }

        /// <summary>配置键 → 脚本 C 表键；返回 null 表示该键只作用于内置面板。</summary>
        private static string PaletteKey(string path)
        {
            switch (path)
            {
                case "LabelColor": return "label";
                case "TextColor": return "text";
                case "AccentColor": return "accent";
                case "Cpu.ValueColor": return "cpu";
                case "Mem.ValueColor": return "mem";
                case "Disk.ValueColor": return "disk";
                case "Net.ValueColor": return "net_down";
                case "Gpu.ValueColor": return "gpu";
                default: return null;
            }
        }

        private void RefreshRaw()
        {
            if (_rawDirty && RawEditor.IsFocused) return;
            _rawLoading = true;
            RawEditor.Text = ConfigStore.ToJson(_root);
            _rawLoading = false;
            _rawDirty = false;
        }

        private void ReplaceRoot(string text, string successNote)
        {
            JsonObject parsed;
            try { parsed = ConfigStore.Parse(text); }
            catch (Exception ex)
            {
                MessageBox.Show("JSON 解析失败：\r\n" + ex.Message, "ConkyWin 设置",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (parsed == null)
            {
                MessageBox.Show("JSON 顶层必须是对象 { }", "ConkyWin 设置",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                ConfigStore.Backup();
                ConfigStore.AtomicWrite(ConfigStore.ConfigPath, ConfigStore.ToJson(parsed));
                _root = parsed;
                BindFields();
                RefreshRaw();
                StatusText.Text = successNote;
            }
            catch (Exception ex) { StatusText.Text = "写入失败：" + ex.Message; }
        }

        // ---------- Lua 脚本 ----------

        private void LoadScript()
        {
            _luaLoading = true;
            _luaSaved = ConfigStore.ReadText(ConfigStore.ScriptPath) ?? "";
            LuaEditor.Text = _luaSaved;
            _currentTheme = ConfigStore.GetTheme(_luaSaved);
            ThemeBox.SelectedIndex = _currentTheme == "mono" ? 1 : 0;
            LuaStatus.Text = string.IsNullOrEmpty(_luaSaved)
                ? "当前没有脚本（使用内置简洁面板）"
                : "已载入 script.lua · " + _luaSaved.Length + " 字符";
            _luaLoading = false;
            _luaDirty = false;
        }

        private void SaveLua()
        {
            string text = LuaEditor.Text;
            string err = ConfigStore.ValidateLua(text);
            if (err != null)
            {
                var r = MessageBox.Show("Lua 语法检查未通过：\r\n" + err + "\r\n\r\n仍要保存吗？",
                    "ConkyWin 设置", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }
            try
            {
                ConfigStore.AtomicWrite(ConfigStore.ScriptPath, text);
                _luaSaved = text;
                _luaDirty = false;
                LuaStatus.Text = "已保存 · 约 1 秒后重载脚本" + (err == null ? " · 语法通过" : " · 语法有误");
            }
            catch (Exception ex)
            {
                MessageBox.Show("写入 script.lua 失败：\r\n" + ex.Message,
                    "ConkyWin 设置", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_luaLoading || _root == null) return;
            var item = ThemeBox.SelectedItem as ComboBoxItem;
            if (item == null) return;
            string theme = item.Tag as string ?? "color";
            if (theme == _currentTheme) return;
            string next = ConfigStore.SetTheme(LuaEditor.Text, theme);
            if (next == LuaEditor.Text)
            {
                MessageBox.Show("当前脚本里没有找到 THEME 行（可能是自定义脚本），主题切换已忽略。\r\n" +
                                "要切换主题请直接在 Lua 编辑器里修改。",
                    "ConkyWin 设置", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            LuaEditor.Text = next;
            _currentTheme = theme;
            SaveLua();
        }

        private void OnLuaTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_luaLoading) return;
            _luaDirty = true;
            LuaStatus.Text = "脚本有未保存的修改";
        }

        private void OnCheckLua(object sender, RoutedEventArgs e)
        {
            string err = ConfigStore.ValidateLua(LuaEditor.Text);
            if (err == null)
                MessageBox.Show("语法检查通过。", "ConkyWin 设置", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show("语法错误：\r\n" + err, "ConkyWin 设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void OnSaveLua(object sender, RoutedEventArgs e) { SaveLua(); }

        private void OnRestoreLua(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("用内置的 Harmattan 脚本覆盖当前 script.lua？\r\n当前脚本会先备份到 config.json.bak 之外（不覆盖配置备份），可通过版本历史找回。",
                    "ConkyWin 设置", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            LuaEditor.Text = ConfigStore.DefaultScript;
            _currentTheme = ConfigStore.GetTheme(LuaEditor.Text);
            _luaLoading = true;
            ThemeBox.SelectedIndex = _currentTheme == "mono" ? 1 : 0;
            _luaLoading = false;
            SaveLua();
        }

        // ---------- 原始 JSON ----------

        private void OnRawTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_rawLoading) return;
            _rawDirty = true;
            StatusText.Text = "原始 JSON 有未应用的修改";
        }

        private void OnApplyRaw(object sender, RoutedEventArgs e) { ReplaceRoot(RawEditor.Text, "原始 JSON 已应用"); }
        private void OnReloadRaw(object sender, RoutedEventArgs e) { _rawDirty = false; RefreshRaw(); }

        private void OnRestoreBackup(object sender, RoutedEventArgs e)
        {
            if (!File.Exists(ConfigStore.BackupPath))
            {
                MessageBox.Show("还没有备份文件（首次保存配置时会自动生成 config.json.bak）。",
                    "ConkyWin 设置", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show("用 config.json.bak 覆盖当前配置？", "ConkyWin 设置",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            ReplaceRoot(ConfigStore.ReadText(ConfigStore.BackupPath), "已从备份还原");
        }

        // ---------- 底部按钮 / 关闭 ----------

        private void OnSave(object sender, RoutedEventArgs e) { SaveConfig(); }

        private void OnRestoreDefault(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("恢复内置推荐配置？会覆盖当前 config.json（原文件先存为 config.json.bak）。",
                    "ConkyWin 设置", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            ReplaceRoot(ConfigStore.DefaultConfig, "已恢复默认配置");
        }

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            try { Process.Start("explorer.exe", "/select,\"" + ConfigStore.ConfigPath + "\""); }
            catch { try { Process.Start("explorer.exe", ConfigStore.BaseDir); } catch { } }
        }

        private void OnClose(object sender, RoutedEventArgs e) { Close(); }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_configDirty) SaveConfig();
            if (_luaDirty)
            {
                var r = MessageBox.Show("Lua 脚本有未保存的修改，保存后再关闭吗？\r\n选「否」会丢弃修改。",
                    "ConkyWin 设置", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
                if (r == MessageBoxResult.Yes) SaveLua();
            }
            _saveTimer.Stop();
        }
    }
}
