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
- **点击穿透 + 压底**：`WS_EX_TRANSPARENT | WS_EX_LAYERED`，`HWND_BOTTOM` 嵌入桌面层，鼠标可正常操作下方图标
- **不抢焦点 / 不进任务栏**：`WS_EX_TOOLWINDOW`
- **配置热重载**：修改 exe 同目录的 `config.json` 保存后立即生效，无需重启；解析失败自动保留上一份有效配置
- **无 GPU 也能跑**：NVIDIA GPU 检测失败时静默降级，其余各行不受影响
- **Lua 脚本扩展**：可自定义每行文本/配色/进度条，甚至整面板由脚本生成（见下文）

## 运行要求

- Windows 10 / 11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- （可选）NVIDIA 显卡 + `nvidia-smi` 在 PATH 中，用于 GPU 行

## 使用

1. 下载 Release 或自行编译，得到 `ConkyWin.exe`
2. 双击运行，面板出现在屏幕右下（默认 1680,360），首次运行自动生成默认 `config.json`
3. 编辑 `config.json` 调整位置 / 字号 / 配色，保存即生效

### 开机自启（可选）

在注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下新建字符串值：

```
名称：ConkyWin
数据：<你的路径>\ConkyWin.exe
```

## 配置文件 config.json

```jsonc
{
  "Position": { "X": 1680, "Y": 360 },   // 面板位置（像素）
  "FontSize": 13,                        // 数值字号
  "TimeFontSize": 15,                    // 时间字号
  "Bold": true,                          // 数值是否加粗
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
  "Proc":  { "ValueColor": "#EAF2FC" }
}
```

颜色支持 `#RRGGBB` 与 `#AARRGGBB`（带透明度）。`Net` / `Temp` / `Proc` 没有进度条，只需 `ValueColor`。

## Lua 脚本（进阶）

参照 Linux Conky 的思路，ConkyWin 支持 Lua 脚本扩展（基于 [MoonSharp](https://github.com/moonsharp-devs/moonsharp)，纯托管实现，无原生 DLL 依赖）：

1. 把 [`script.example.lua`](script.example.lua) 复制到 exe 同目录并改名为 `script.lua`
2. 重启 ConkyWin 生效；删除 `script.lua` 即恢复内置面板

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

每行字段：`label` / `text` 必填；`color`（数值颜色）、`bar`（0-100 进度，nil 则无进度条）、`bar_start` / `bar_end`（渐变两端）可选。

### 脚本可用 API

| 函数 | 返回 |
|---|---|
| `conky.cpu()` | CPU 占用 0-100 |
| `conky.mem_pct()` / `mem_used_gb()` / `mem_total_gb()` | 内存 |
| `conky.disk_pct()` / `disk_used_gb()` / `disk_total_gb()` | C: 盘 |
| `conky.net_down_kb()` / `conky.net_up_kb()` | 网速 KB/s |
| `conky.gpu_util()` / `gpu_temp()` / `gpu_mem_used_gb()` / `gpu_mem_total_gb()` | GPU |
| `conky.gpu_available()` | GPU 是否可用 |
| `conky.proc()` | 进程数 |
| `conky.time(fmt)` | 当前时间（.NET 格式串，如 `"%H:%M:%S"`） |

## 编译

需要 .NET 8 SDK：

```powershell
dotnet build ConkyWin.csproj -c Release -o out
```

产物在 `out\ConkyWin.exe`。

## 已知限制

- **CPU 温度**：普通权限下 Windows 没有可用的读取通道（WMI `MSAcpi_ThermalZoneTemperature` 拒绝访问；真实温度需要 Ring0 内核驱动 + 管理员权限，而管理员权限会破坏本程序依赖的桌面嵌入机制）。TEMP 行因此固定显示 `--℃`。GPU 温度通过 `nvidia-smi`（普通权限可读）正常显示。
- **磁盘**：默认只监控 C: 盘。

## Roadmap（欢迎 PR）

- [x] Lua 脚本扩展（增强模式 + 完整模式）
- [x] 显示行开关（通过 Lua 完整模式即可自由增删行）
- [ ] 多盘监控
- [ ] 多屏位置记忆
- [ ] 管理员模式下读取 CPU 温度（LibreHardwareMonitor 方案探索）

## License

[MIT](LICENSE)
