-- Harmattan 风格布局（复刻 Linux conky 主题，配合 config.json: PanelWidth=268, Position.RightGap=0）
--
-- 主题切换：把下面的 THEME 改成 "color"（彩色）或 "mono"（白色单色，原版风格），保存即生效（热重载）。
-- 配色只认下面的 C 表：text = 数值文字，label = 左侧小标签，cpu/mem/disk... = 曲线与进度条。
-- 直接改这里、或在设置窗口「配色 / 脚本参数」页取色，两边都会同步，改完保存立即生效。
-- 参数（曲线高度、Top 条数等）走 conky.config()，可以用自定义键写进 config.json（见文末示例）。
local THEME = "color"

local C
if THEME == "mono" then
    C = {
        text = "#FFFFFF", label = "#C8C8C8", hr = "#55FFFFFF",
        cpu = "#FFFFFF", mem = "#FFFFFF", disk = "#FFFFFF",
        net_down = "#FFFFFF", net_up = "#FFFFFF", gpu = "#FFFFFF",
        accent = "#FFFFFF", hot = "#FFFFFF", warn = "#FFFFFF",
    }
else
    C = {
        text = "#F2F7FF", label = "#9FB6D0", hr = "#38FFFFFF",
        cpu = "#2F9EFF", mem = "#9B7BFF", disk = "#FFC85C",
        net_down = "#4DE3E0", net_up = "#8FD8FF", gpu = "#B78CFF",
        accent = "#4DE3E0",
        hot = "#FFA94D",   -- 磁盘 >= 75%
        warn = "#FF6B6B",  -- 磁盘 >= 90%
    }
end

-- config.json 透传示例：任意自定义键都能在脚本里读（保存 config 即生效，不用改脚本）
local cfg = conky.config()
local SCALE_FLOOR_KB = cfg.ScaleFloorKb or 16   -- 速率曲线最低满格量（KB/s）
local GRAPH_H        = cfg.GraphHeight or 30    -- 曲线高度
local TOP_N          = cfg.TopProcesses or 5    -- 进程列表条数

-- ============ 字体与字号：按区块覆盖 ============
-- 每行用 style = "区块名" 标记；config.json 的 "Styles" 表里可以针对任意区块单独设：
--   "Styles": { "weather_city": { "Size": 30, "LabelSize": 13, "Font": "等线" } }
-- 可用键：Size / LabelSize / CellSize / CellLabelSize / CellSubSize / Font / LabelFont / CellFont
-- 不填就回退到 config 的全局 FontSize / FontFamily / LabelFontSize 等（设置窗口「字体」页）。
-- 想加新区块：在行上加一个 style = "my_block"，设置窗口会自动列出它，不用改 C#。
local STY = cfg.Styles or {}
local function num(v) return (type(v) == "number" and v > 0) and v or nil end
local function txt(v) return (type(v) == "string" and v ~= "") and v or nil end

-- 没有在 config 里覆盖时，沿用原版 Harmattan 的特殊字号；其余区块跟随全局 FontSize。
local DEFAULT_STY = {
    weather_city     = { Size = 26 },
    weather_desc     = { Size = 15 },
    weather_forecast = { CellSize = 20, CellLabelSize = 11, CellSubSize = 11 },
    disk             = { Size = 12 },
    top              = { Size = 12 },
}
local function merge_style(name)
    local out = nil
    local d = DEFAULT_STY[name]
    if d then out = {}; for k, v in pairs(d) do out[k] = v end end
    local u = STY[name]
    if u then
        if not out then out = {} end
        for k, v in pairs(u) do out[k] = v end
    end
    return out
end

-- 说明：config.json 里的 LabelColor / Cpu.ValueColor 等只用于「内置简洁面板」（删掉本脚本时）。
-- 使用本脚本时，配色以 C 表为准，避免两处配置互相覆盖导致「改了没反应」。
-- 设置窗口保存颜色时会同步改写 C 表，所以在界面里取色同样有效。

-- ============ 速率曲线自适应满格量 ============
-- 固定满格量（比如 2MB/s）在平时几 K 的流量下会画成一条直线，看不出起伏。
-- 峰值走高立刻放大满格量，峰值回落到一半以下再缓慢收缩：
-- 无论是 3K 还是 5M/s，曲线都能占满整个高度。
local function ceil_nice(v)
    if v <= 0 then return SCALE_FLOOR_KB end
    local mag = 10 ^ math.floor(math.log(v) / math.log(10) + 1e-9)
    local n = v / mag
    local step
    if n <= 1 then step = 1
    elseif n <= 2 then step = 2
    elseif n <= 5 then step = 5
    else step = 10 end
    return step * mag
end

local function peak(hist)
    local m = 0
    for i = 1, #hist do if hist[i] > m then m = hist[i] end end
    return m
end

local function autoscale(scale, p)
    if p > scale then
        return ceil_nice(p * 1.15)              -- 立刻放大，留 15% 余量
    elseif p * 2 < scale then
        local target = math.max(SCALE_FLOOR_KB, ceil_nice(p * 2.2))
        return math.max(target, scale * 0.75)   -- 缓慢收缩
    end
    return scale
end

local function to_pct(hist, scale)
    local out = {}
    for i = 1, #hist do
        local v = hist[i] / scale * 100
        if v > 100 then v = 100 end
        out[i] = v
    end
    return out
end

local function push(t, v)
    t[#t + 1] = v
    if #t > 60 then table.remove(t, 1) end
    return t
end

local cpu_hist, mem_hist = {}, {}
local io_raw, down_raw, up_raw = {}, {}, {}
local io_scale, net_scale = SCALE_FLOOR_KB, SCALE_FLOOR_KB

local function fmt_rate(kb)
    if kb >= 1024 then return string.format("%.1fM", kb / 1024) end
    return string.format("%.0fK", kb)
end

local function fmt_gb(gb)
    if gb >= 1 then return string.format("%.1fG", gb) end
    return string.format("%.0fM", gb * 1024)
end

-- WMO 天气代码 → 中文描述
local WMO = {
    [0]="晴",[1]="少云",[2]="多云",[3]="阴",[45]="雾",[48]="雾凇",
    [51]="毛毛雨",[53]="毛毛雨",[55]="毛毛雨",[56]="冻雨",[57]="冻雨",
    [61]="小雨",[63]="中雨",[65]="大雨",[66]="冻雨",[67]="冻雨",
    [71]="小雪",[73]="中雪",[75]="大雪",[77]="雪粒",
    [80]="阵雨",[81]="阵雨",[82]="强阵雨",[85]="阵雪",[86]="阵雪",
    [95]="雷雨",[96]="雷雨冰雹",[99]="雷雨冰雹",
}

-- WMO 代码 → 本地 PNG 图标（assets/icons/*.png，经典 conky weather 图标集，weather.com 代码编号）
local WICON = {
    [0]=32,[1]=34,[2]=28,[3]=26,
    [45]=20,[48]=20,
    [51]=11,[53]=11,[55]=11,[56]=10,[57]=10,
    [61]=11,[63]=12,[65]=40,[66]=10,[67]=10,
    [71]=13,[73]=14,[75]=16,[77]=18,
    [80]=11,[81]=11,[82]=40,[85]=46,[86]=46,
    [95]=4,[96]=4,[99]=4,
}
local function wicon(code)
    local n = WICON[math.floor(code or -1)]
    return n and ("assets/icons/" .. n .. ".png") or nil
end

function conky_rows()
    local rows = {}

    push(cpu_hist, conky.cpu())
    push(mem_hist, conky.mem_pct())
    push(io_raw, conky.disk_io_kb())
    push(down_raw, conky.net_down_kb())
    push(up_raw, conky.net_up_kb())

    io_scale = autoscale(io_scale, peak(io_raw))
    net_scale = autoscale(net_scale, math.max(peak(down_raw), peak(up_raw)))

    -- ============ 天气块 ============
    local w = conky.weather()
    if w then
        -- 城市名 + 大字温度
        rows[#rows + 1] = { label = w.city, text = string.format("%.1f°", w.temp),
                            style = "weather_city", color = C.accent }
        -- 天气描述 + 图标（PNG）
        rows[#rows + 1] = { label = (WMO[math.floor(w.code)] or ""), icon = wicon(w.code),
                            icon_size = 24, style = "weather_desc", color = C.text }
        -- 气压 / 湿度 / 风速
        rows[#rows + 1] = { label = "湿度", text = string.format("%.0f%%", w.humidity),
                            style = "weather_info", color = C.text }
        rows[#rows + 1] = { label = "风速", text = string.format("%.2f km/h", w.wind),
                            style = "weather_info", color = C.text }
        if w.pressure > 0 then
            rows[#rows + 1] = { label = "气压", text = string.format("%.0f hPa", w.pressure),
                                style = "weather_info", color = C.text }
        end
        -- 三天预报：三列并排（星期 / 图标 / 温度）
        local names = { "今天", "明天", "后天" }
        local cells = {}
        for i = 1, math.min(3, #w.daily) do
            local d = w.daily[i]
            cells[i] = {
                label = names[i],
                icon = wicon(d.code),
                sub = string.format("%.0f°/%.0f°", d.min, d.max),
                color = C.text,
            }
        end
        rows[#rows + 1] = { cells = cells, style = "weather_forecast" }
    else
        local err = conky.weather_error()
        rows[#rows + 1] = { label = "天气", text = (err ~= "" and "重试中…" or "加载中…"),
                            style = "weather_error", color = C.label }
    end

    rows[#rows + 1] = { gap = 6 }
    rows[#rows + 1] = { hr = true, color = C.hr }
    rows[#rows + 1] = { gap = 4 }

    -- ============ CPU / 内存 / 磁盘 IO 历史曲线 ============
    rows[#rows + 1] = { label = "Cpu:", text = string.format("%.0f%%", conky.cpu()),
                        style = "graph", color = C.text, graph = cpu_hist,
                        graph_color = C.cpu, graph_height = GRAPH_H }
    rows[#rows + 1] = { label = "Mem:", text = string.format("%.0f%%  %.1fG", conky.mem_pct(), conky.mem_used_gb()),
                        style = "graph", color = C.text, graph = mem_hist,
                        graph_color = C.mem, graph_height = GRAPH_H }
    rows[#rows + 1] = { label = "Disk I/O:", text = fmt_rate(conky.disk_io_kb()) .. "/s",
                        style = "graph", color = C.text, graph = to_pct(io_raw, io_scale),
                        graph_color = C.disk, graph_height = GRAPH_H }

    -- ============ 所有固定磁盘的用量条（>=75% 琥珀、>=90% 红色告警） ============
    for _, d in ipairs(conky.disks()) do
        local col = d.pct >= 90 and C.warn or (d.pct >= 75 and C.hot or C.disk)
        rows[#rows + 1] = { label = d.name,
                            text = string.format("%.0f%%  %.0f/%.0fG", d.pct, d.used_gb, d.total_gb),
                            color = col, style = "disk",
                            bar = d.pct, bar_start = col, bar_end = col }
    end

    rows[#rows + 1] = { gap = 6 }
    rows[#rows + 1] = { hr = true, color = C.hr }
    rows[#rows + 1] = { gap = 4 }

    -- ============ 网络上下行曲线 + 开机以来累计流量 ============
    rows[#rows + 1] = { label = "Down:", text = "↓" .. fmt_rate(conky.net_down_kb()),
                        style = "graph", color = C.text, graph = to_pct(down_raw, net_scale),
                        graph_color = C.net_down, graph_height = GRAPH_H }
    rows[#rows + 1] = { label = "Up:", text = "↑" .. fmt_rate(conky.net_up_kb()),
                        style = "graph", color = C.text, graph = to_pct(up_raw, net_scale),
                        graph_color = C.net_up, graph_height = GRAPH_H }
    rows[#rows + 1] = { label = "累计", text = string.format("收 %s / 发 %s",
                        fmt_gb(conky.net_total_down_gb()), fmt_gb(conky.net_total_up_gb())),
                        style = "total", color = C.label }

    rows[#rows + 1] = { gap = 6 }
    rows[#rows + 1] = { hr = true, color = C.hr }
    rows[#rows + 1] = { gap = 4 }

    -- ============ 进程：总数 + Top 列表（按内存 / 按 CPU） ============
    rows[#rows + 1] = { label = "Proc", text = string.format("%.0f", conky.proc()),
                        style = "proc", color = C.text }
    for i, p in ipairs(conky.top_mem(TOP_N)) do
        rows[#rows + 1] = { label = p.name, text = string.format("%.0fM", p.mem_mb),
                            style = "top", color = C.text }
    end
    rows[#rows + 1] = { gap = 3 }
    for i, p in ipairs(conky.top_cpu(TOP_N)) do
        rows[#rows + 1] = { label = p.name, text = string.format("%.2f%%", p.cpu),
                            style = "top", color = C.text }
    end

    -- 把 C.label 注入每一行：渲染端优先读 label_color，否则才回退到 config.LabelColor。
    -- 然后从 config.json 的 Styles 表按区块名注入字体与字号；行内显式写的字段优先，
    -- 这样自定义脚本仍可直接覆盖，而设置窗口改的是 Styles（不动脚本文件）。
    for _, r in ipairs(rows) do
        if r.label_color == nil then r.label_color = C.label end
        local s = r.style and merge_style(r.style) or nil
        if s then
            r.font_size            = r.font_size            or num(s.Size)
            r.label_font_size      = r.label_font_size      or num(s.LabelSize)
            r.cell_font_size       = r.cell_font_size       or num(s.CellSize)
            r.cell_label_font_size = r.cell_label_font_size or num(s.CellLabelSize)
            r.cell_sub_font_size   = r.cell_sub_font_size   or num(s.CellSubSize)
            r.font_family          = r.font_family          or txt(s.Font)
            r.label_font_family    = r.label_font_family    or txt(s.LabelFont) or txt(s.Font)
            r.cell_font_family     = r.cell_font_family     or txt(s.CellFont) or txt(s.Font)
        end
    end

    return rows
end

--[[
自定义键示例：在 config.json 里加（顶级、任意名字都行），脚本里用 conky.config() 读取：

    {
      "ScaleFloorKb": 32,      ← 上面 cfg.ScaleFloorKb 读到的就是它
      "GraphHeight": 36,
      "TopProcesses": 8,
      "MyNote": "随便加"       ← conky.config().MyNote
    }

字体与字号：设置窗口「字体」页可直接改，背后写的是 Styles 表，不必动脚本：

    "FontFamily": "Consolas",        ← 全局正文字体
    "LabelFontSize": 11,             ← 全局左侧标签字号
    "Styles": {
      "weather_city":     { "Size": 30, "LabelSize": 13, "Font": "KaiTi" },
      "weather_info":     { "Size": 14, "LabelSize": 12 },
      "weather_forecast": { "CellSize": 22, "CellLabelSize": 12, "CellSubSize": 12 },
      "graph":            { "LabelSize": 12 },
      "top":              { "Size": 11 }
    }

    可用键：Size / LabelSize / CellSize / CellLabelSize / CellSubSize /
            Font / LabelFont / CellFont（字尺寸单位磅，0 或留空 = 跟随全局）
    想加新区块：在任意行上写 style = "my_block"，设置窗口就会自动列出它。

改完保存即生效（热重载），完全不用动这个脚本。
--]]
