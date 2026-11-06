# ConkyWin

Windows 版 [Conky](https://github.com/brndnmtthws/conky) 式桌面系统监控挂件。背景 100% 透明、数据直接悬浮在桌面上，**嵌入桌面层**（不置顶、不挡鼠标、不抢焦点），支持开机自启。

纯 C# / WPF 实现，零第三方监控软件依赖（不需要 Rainmeter、LiteMonitor 等），单文件运行，体积小。

![screenshot](docs/screenshot.png)

## 功能

| 行 | 内容 | 数据来源 |
|---|---|---|
| SYSTEM | 时钟 HH:mm:ss | 系统时间 |
| CPU | 占用率 + 渐变进度条 | Performance Counter |
| MEM | 已用 / 总量 + 进度条 | `GlobalMemoryStatusEx` |
| DISK | C: 已用 / 总量 + 进度条 | `DriveInfo` |
| NET | ↓下行 ↑上行 速率（>1M 自动显示 M） | `NetworkInterface` 流量差分 |
| GPU | 占用率 / 显存 / 温度 + 进度条 | `nvidia-smi`（每 2 秒采样） |
| TEMP | CPU 温度（普通权限不可读，显示 `--℃`） | 见下方"已知限制" |
| PROC | 进程数 | Performance Counter |

## 特性

- **真透明**：WPF per-pixel alpha，无任何底板
- **点击穿透 + 压底**：`WS_EX_TRANSPARENT | WS_EX_LAYERED`，嵌入桌面层（壁纸之上、图标之下），鼠标可正常操作下方图标；每 15 秒自动校正 Z 序，防止被其他程序顶掉
- **不抢焦点 / 不进任务栏**：`WS_EX_TOOLWINDOW`
- **配置热重载**：exe 同目录的 `config.json` 和 `script.lua` 保存后立即生效，无需重启（轮询修改时间实现，兼容"写临时文件再改名"的编辑器保存方式）；解析失败自动保留上一份有效配置
- **无 GPU 也能跑**：NVIDIA GPU 检测失败时静默降级，其余各行不受影响
- **Lua 脚本扩展**：可自定义每行文本/配色/进度条，甚至整面板由脚本生成（见下文）
- **配置透传**：config.json 整表（含任意自定义键）通过 `conky.config()` 供脚本读取——改配置即改脚本参数，不用动代码

## 运行要求

- Windows 10 / 11（x64）
- **无需安装 .NET**：发行版是自包含单文件，.NET 8 运行时已打包进 exe（约 69 MB），解压即用
- （可选）NVIDIA 显卡 + `nvidia-smi`，用于 GPU 那一行；没有则自动跳过，其余功能不受影响

> 从源码编译运行才需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

## 快速开始

1. 下载最新的 `ConkyWin-x.x.x-win-x64.zip`（国内推荐 Gitee，速度更快）：
   - Gitee：[gitee.com/danmyleo/conkywin/releases](https://gitee.com/danmyleo/conkywin/releases)
   - GitHub：[github.com/danmy-liu/conkywin/releases](https://github.com/danmy-liu/conkywin/releases)
2. 解压到任意目录（建议固定位置，如 `D:\Tools\ConkyWin`——开机自启要用这个路径）
3. 双击 `ConkyWin.exe`，面板出现在屏幕右侧，托盘区出现 ConkyWin 图标
4. **双击托盘图标**打开可视化设置窗口，改样式所见即所得；也可以直接编辑 `config.json` / `script.lua`，保存即生效
5. 不想要脚本版面板：在设置窗口的「Lua 脚本」页清空内容，或删除 `script.lua`，立刻回到内置简洁面板（7 行，带进度条）

## 图形化设置（推荐）

托盘图标是主程序唯一的交互入口 —— 主面板点击穿透、不进任务栏，本身点不到。Windows 11 可能把图标收在托盘的「^」里，点开就能看到。

**双击图标**打开设置窗口；**右键**是菜单：

| 菜单项 | 作用 |
|---|---|
| 打开设置 | 打开可视化配置窗口 |
| 重新加载配置与脚本 | 不等轮询，立刻重读 `config.json` / `script.lua` |
| 打开配置文件夹 | 在资源管理器里定位 `config.json` |
| 开机自启 | 勾选/取消，自动维护 `HKCU\...\Run` 注册表项（指向当前运行的 exe） |
| 退出 ConkyWin | 正常退出并清理托盘图标 |

设置窗口六个页签：

- **常规**：水平对齐 / 垂直对齐、X/Y、边缘留白、面板宽度、天气城市与经纬度
- **配色**：标签色、进度条底色、7 个模块数值色、CPU/MEM/DISK/GPU 渐变两端；带系统取色器与预览色块，透明度前缀会保留
- **字体**：全局正文字体 / 标签字体 / 时间字体 / 多列字体（下拉直接列出系统已安装字体）及各自字号；下方「分区块覆盖」表格按 `script.lua` 里的 `style` 标记自动生成，每个区块可单独设主字号、标签字号、字体
- **脚本参数**：`GraphHeight` / `TopProcesses` / `ScaleFloorKb` / `TextColor` / `AccentColor`（Harmattan 脚本的自定义键）
- **Lua 脚本**：主题 `color` / `mono` 切换、语法检查、保存、恢复内置默认脚本
- **原始 JSON**：直接编辑完整 `config.json`，任意自定义键都能改，校验通过才应用

**位置怎么摆**：水平、垂直各有一个对齐下拉，可选 `绝对坐标` / `左对齐` / `水平居中` / `右对齐` 和 `绝对坐标` / `顶部对齐` / `垂直居中` / `底部对齐`。X/Y 永远可填：选「绝对坐标」时是屏幕像素坐标；选对齐方式时变成相对基准的微调偏移（正数向右 / 下，可为负）。从绝对坐标切到对齐方式时窗口会自动把偏移归零，避免旧坐标把面板顶出屏幕。旧配置只有 `RightGap` / `BottomGap`，打开设置窗口时会自动推断出对齐方式，位置不会跳。

**字体与字号怎么改**：全局项是所有区块的默认值，字体下拉列的是本机已安装的字体族，留空表示跟随上一级。想单独调某个区块，在「分区块覆盖」表格里找到它改主字号 / 标签字号 / 字体即可，字号填 `0` 表示跟随全局。表格的区块名是**自动从 `script.lua` 扫描** `style = "xxx"` 得到的 —— 你给脚本里任意一行加上自己的 `style` 名，设置窗口就会自动多出对应的一行，不用改 C#。底层写的是 `config.json` 的 `Styles` 表：

```json
"Styles": {
  "weather_city":     { "Size": 30, "LabelSize": 13, "Font": "KaiTi" },
  "weather_info":     { "Size": 14, "LabelSize": 12 },
  "weather_forecast": { "CellSize": 22, "CellLabelSize": 12, "CellSubSize": 12 },
  "graph":            { "LabelSize": 12 }
}
```

可用键：`Size` / `LabelSize` / `CellSize` / `CellLabelSize` / `CellSubSize` / `Font` / `LabelFont` / `CellFont`。也可以在脚本的行里直接写 `font_size` / `label_font_size` / `font_family` 等字段，行内显式值优先。

**配色以谁为准**：使用 `script.lua` 时，文字和曲线颜色由脚本顶部的 `C` 表决定（`text` 是数值、`label` 是左侧小标签、`cpu`/`mem`/`disk` 是曲线色）。在「配色」「脚本参数」页改颜色，窗口会同步改写 `C` 表；直接在「Lua 脚本」页改 `C` 表同样立即生效，两条路等价。`config.json` 里的 `LabelColor`/`Cpu.ValueColor` 等只用于删掉脚本后的内置简洁面板。

改动在停止操作 700ms 后自动保存，约 1 秒后面板生效；也可以点「保存并应用」立刻写盘。写盘走临时文件 + 原子替换，不会让每秒轮询读到半个 JSON。首次保存会生成 `config.json.bak`，「原始 JSON」页可一键还原。**未知键一律原样保留**，你自己加进 config.json 的自定义键不会被控件删掉。

> 设置窗口开着的时候，如果又在外部编辑器改了 `config.json`，请先点「重新载入」或关掉窗口重开 —— 窗口保存时以它自己载入的那份内容为准。

给桌面快捷方式加 `--settings` 参数，可以只打开设置窗口而不启动面板：`ConkyWin.exe --settings`。开发自测用 `ConkyWin.exe --selftest`，会在 exe 同目录写 `selftest.log`。

### 开机自启（可选）

最简单的做法是勾托盘菜单里的「开机自启」。想手工设置也行，在注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下新建字符串值：

```
名称：ConkyWin
数据：<你的路径>\ConkyWin.exe
```

## 配置文件 config.json

```jsonc
{
  "Position": {
    "X": 0,                               // 绝对坐标下的屏幕横坐标；对齐模式下是相对基准的偏移
    "Y": 0,                               // 绝对坐标下的屏幕纵坐标；对齐模式下是相对基准的偏移
    "HAlign": "right",                    // absolute / left / center / right（留空 = 旧版 RightGap 语义）
    "VAlign": "bottom",                   // absolute / top / center / bottom（留空 = 旧版 BottomGap 钳制）
    "RightGap": 0,                        // 右对齐时距屏幕右缘的留白
    "BottomGap": 8                        // 底部对齐时距屏幕下缘的留白
  },
  "PanelWidth": 220,                     // 面板固定宽度（px），高度自适应内容
  "FontSize": 13,                        // 全局数值字号
  "TimeFontSize": 15,                    // 时间字号
  "Bold": true,                          // 数值是否加粗
  "FontFamily": "Consolas",              // 全局正文字体（填系统已安装的字体名）
  "LabelFontFamily": "Microsoft YaHei UI", // 全局左侧标签字体
  "LabelFontSize": 11,                   // 全局左侧标签字号
  "CellFontSize": 20,                    // 多列区块（三天预报）数值字号
  "CellLabelFontSize": 11,               // 多列区块顶部小字字号
  "CellSubFontSize": 11,                 // 多列区块底部小字字号
  "Styles": {},                          // 分区块覆盖，见「字体与字号怎么改」
  "LabelColor": "#7A90AC",               // 左侧标签颜色（CPU/MEM/...）
  "BarBackground": "#26FFFFFF",          // 进度条底色（半透明）
  "Cpu": {
    "ValueColor": "#4DE3E0",             // 数值颜色
    "BarStart": "#4DE3E0",               // 进度条渐变起点
    "BarEnd": "#2F9EFF"                  // 进度条渐变终点
  },
  "Mem":   { "ValueColor": "#7FB0FF", "BarStart": "#6FA8FF", "BarEnd": "#9B7BFF" },
  "Disk":  { "ValueColor": "#FFC85C", "BarStart": "#FFC85C", "BarEnd": "#FF8A5C" },
  "Net":   { "ValueColor": "#8FD8FF" },
  "Gpu":   { "ValueColor": "#B78CFF", "BarStart": "#B78CFF", "BarEnd": "#7A5CFF" },
  "Temp":  { "ValueColor": "#FF6B6B" },
  "Proc":  { "ValueColor": "#EAF2FC" },
  "Weather": {
    "City": "",                          // 城市名（支持中文，如 "西安"），填了优先用地理编码定位
    "Lat": 999,                          // 经纬度；999 = 自动（先 City，再 IP 定位）
    "Lon": 999
  }
}
```

颜色支持 `#RRGGBB` 与 `#AARRGGBB`（带透明度）。`Net` / `Temp` / `Proc` 没有进度条，只需 `ValueColor`。`Weather` 仅在 Lua 脚本调用 `conky.weather()` 时使用。

## Lua 脚本（进阶）

参照 Linux Conky 的思路，ConkyWin 支持 Lua 脚本扩展（基于 [MoonSharp](https://github.com/moonsharp-devs/moonsharp)，纯托管实现，无原生 DLL 依赖）：

1. 把 [`script.example.lua`](script.example.lua)（最小示例）或 [`script.harmattan.lua`](script.harmattan.lua)（复刻 Linux Harmattan 主题：天气大字/图标/三列预报 + 历史曲线 + 累计流量 + Top 进程列表；顶部 `THEME = "color"/"mono"` 一键换肤）复制到 exe 同目录并改名为 `script.lua`
2. 保存即生效（热重载，无需重启）；删除 `script.lua` 即恢复内置面板

脚本在**安全沙箱**中运行（无 io/os 等模块），出错只写 `debug.log` 并自动回退内置面板，不会导致挂件崩溃。

### 两种模式

**增强模式**——不定义 `conky_rows()`，只定义钩子函数，覆盖内置面板中对应行的文本/颜色/进度条：

```lua
-- CPU 超 90% 数值变红
function conky_cpu_color(pct)
    if pct > 90 then return "#FF5C5C" end
end

-- 内存改显示百分比
function conky_mem_text(used_gb, total_gb, pct)
    return string.format("%.0f%%", pct)
end

-- CPU 进度条超 80% 后渐变转红
function conky_bar_color(name, pct)
    if name == "cpu" and pct > 80 then return {"#FFC85C", "#FF5C5C"} end
end
```

可用钩子：`conky_time_text` / `conky_cpu_text` / `conky_mem_text` / `conky_disk_text` / `conky_net_text` / `conky_gpu_text` / `conky_temp_text` / `conky_proc_text`，以及对应的 `conky_*_color` 与 `conky_bar_color(name, pct)`。

**完整模式**——定义 `conky_rows()` 返回行表，整面板由脚本生成，WPF 按行表动态渲染：

```lua
function conky_rows()
    return {
        { label = "S Y S T E M", text = conky.time("%H:%M:%S"), color = "#8FD8FF" },
        { label = "CPU", text = string.format("%.0f%%", conky.cpu()),
          color = "#4DE3E0", bar = conky.cpu(), bar_start = "#4DE3E0", bar_end = "#2F9EFF" },
        -- ...
    }
end
```

每行字段：`label` / `text` 必填；`color`、`bar`（0-100 进度）、`bar_start` / `bar_end`（渐变两端）、`graph`（0-100 数值数组，历史曲线面积图）、`graph_height`（曲线高度）、`hr`（分隔横线）、`gap`（空白间隔 px，用于分区留白）、`font_size`（本行字号）、`icon`（行尾图片路径，相对 exe 目录或绝对路径）、`icon_size`（默认 20）、`cells`（多列布局，见下）可选。`cells` 的每格同样支持 `icon` / `icon_size`（默认 22）。

仓库自带 [`assets/icons/`](assets/icons) 一套白色单色天气图标（经典 conky weather 图标集，按 weather.com 代码 0-47 编号），脚本可直接引用：`icon = "assets/icons/32.png"`（晴）、`26.png`（多云）、`4.png`（雷雨）等。

`cells` 渲染为一行等宽多列，每格 `{label, text, sub, color}` 三层（顶部小字 / 中间大字 / 底部小字），适合三天预报（星期 / 图标 / 温度）这类并排内容：

```lua
rows[#rows + 1] = {
    cells = {
        { label = "今天", text = "☁︎", sub = "8°/18°" },
        { label = "明天", text = "⛅︎", sub = "6°/16°" },
        { label = "后天", text = "🌧︎", sub = "7°/11°" },
    }
}
```

速率类曲线建议**自适应满格量**（峰值放大、滞后收缩），否则流量几 K 时曲线是条直线——参考 `script.harmattan.lua` 里的 `autoscale()` 实现。

### 自定义配置：conky.config() 透传

config.json 会整体解析成 Lua 表，**包括任何你在里面自定义的键**。脚本可以直接把配置当参数用——改配置即改脚本行为，完全不用动代码：

```lua
local cfg = conky.config()
local floor_kb = cfg.ScaleFloorKb or 16   -- 在 config.json 里加 "ScaleFloorKb": 32 即可覆盖
local top_n    = cfg.TopProcesses or 5
local my_note  = cfg.MyNote               -- 任何键都能读
```

### 脚本可用 API

| 函数 | 返回 |
|---|---|
| `conky.cpu()` | CPU 占用 0-100 |
| `conky.mem_pct()` / `mem_used_gb()` / `mem_total_gb()` | 内存 |
| `conky.disk_pct()` / `disk_used_gb()` / `disk_total_gb()` | C: 盘 |
| `conky.disks()` | 所有固定磁盘数组 `{name="C:", label="系统", pct=62, used_gb, total_gb}` |
| `conky.disk_io_kb()` | 全盘读写速率 KB/s |
| `conky.net_down_kb()` / `conky.net_up_kb()` | 网速 KB/s |
| `conky.net_total_down_gb()` / `net_total_up_gb()` | 开机以来累计收/发流量 GB |
| `conky.top_mem(n)` / `top_cpu(n)` | 进程列表 `{name, mem_mb, cpu}`（每 3 秒采样） |
| `conky.gpu_util()` / `gpu_temp()` / `gpu_mem_used_gb()` / `gpu_mem_total_gb()` | GPU |
| `conky.gpu_available()` | GPU 是否可用 |
| `conky.proc()` | 进程数 |
| `conky.time(fmt)` | 当前时间（.NET 格式串，如 `"%H:%M:%S"`） |
| `conky.config()` | config.json 整表（含自定义键） |
| `conky.weather()` | 天气表 `{city, temp, humidity, wind, pressure, code, daily[3]={code,min,max}}`，数据来自 [Open-Meteo](https://open-meteo.com)（免费无 key）；定位顺序：config 的 Lat/Lon > City > IP 自动定位；成功后有本地缓存，重启不用重等 |
| `conky.weather_error()` | 最近一次天气获取失败的原因（成功后为空串） |
| `conky.font_size()` / `conky.time_font_size()` | config.json 中的字号 |

## 编译

需要 .NET 8 SDK：

```powershell
dotnet build ConkyWin.csproj -c Release -o out
```

产物在 `out\ConkyWin.exe`（依赖已安装的 .NET 8 运行时，体积仅 1 MB 左右）。

## 打包发行版（自包含，别人无需装 .NET）

```powershell
powershell -ExecutionPolicy Bypass -File .\build-release.ps1
```

产物 `dist/ConkyWin-<版本>-win-x64.zip`（约 69 MB），内含：

```
ConkyWin.exe     自包含单文件（.NET 8 运行时已打包进去）
config.json      推荐配置（268px 宽 Harmattan 布局）
script.lua       Harmattan 主题脚本（删掉即回内置面板）
assets\icons\    天气图标
README.md / LICENSE
```

把 zip 作为附件上传到发行版即可：Gitee 单个附件上限 100 MB，GitHub 上限 2 GB，69 MB 都在范围内。

### 发版流程

推一个 `v*` 标签，GitHub Actions 会自动在 Windows 上编译、打包并创建 Release（附件自动上传），工作流见 `.github/workflows/release.yml`：

```powershell
# 1. 改 ConkyWin.csproj 里的 <Version>
# 2. 提交并打标签
git commit -am "发布 v1.0.1"
git tag v1.0.1
git push --follow-tags        # 有 github 远程时同步推送
```

Gitee 侧不支持这种自动构建，需要本地跑一次 `build-release.ps1` 再手动上传附件。

## 已知限制

- **CPU 温度**：普通权限下 Windows 没有可用的读取通道（WMI `MSAcpi_ThermalZoneTemperature` 拒绝访问；真实温度需要 Ring0 内核驱动 + 管理员权限，而管理员权限会破坏本程序依赖的桌面嵌入机制）。TEMP 行因此固定显示 `--℃`。GPU 温度通过 `nvidia-smi`（普通权限可读）正常显示。
- **磁盘**：默认只监控 C: 盘。

## Roadmap（欢迎 PR）

- [x] Lua 脚本扩展（增强模式 + 完整模式）
- [x] 显示行开关（通过 Lua 完整模式即可自由增删行）
- [x] 历史曲线图 / 磁盘 IO / 天气（`script.harmattan.lua`）
- [x] 速率曲线自适应满格量
- [ ] 多盘监控
- [ ] 多屏位置记忆
- [ ] 管理员模式下读取 CPU 温度（LibreHardwareMonitor 方案探索）

## License

[MIT](LICENSE)
