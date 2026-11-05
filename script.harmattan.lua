-- Harmattan 风格布局（复刻 Linux conky 主题，配合 config.json: PanelWidth=268, Position.RightGap=0）
--
-- 主题切换：把下面的 THEME 改成 "color"（彩色）或 "mono"（白色单色，原版风格），保存即生效（热重载）。
-- 想微调颜色直接改 C 表；也可以把自定义键写进 config.json，用 conky.config() 读取（见文末示例）。
local THEME = "color"

local C
if THEME == "mono" then
    C = {
        text = "#FFFFFF", label = "#AAAAAA", hr = "#55FFFFFF",
        cpu = "#FFFFFF", mem = "#FFFFFF", disk = "#FFFFFF",
        net_down = "#FFFFFF", net_up = "#FFFFFF", gpu = "#FFFFFF",
        accent = "#FFFFFF",
    }
else
    C = {
        text = "#EAF2FC", label = "#7A90AC", hr = "#30FFFFFF",
        cpu = "#2F9EFF", mem = "#9B7BFF", disk = "#FFC85C",
        net_down = "#4DE3E0", net_up = "#8FD8FF", gpu = "#B78CFF",
        accent = "#4DE3E0",
    }
end

-- config.json 透传示例：任意自定义键都能在脚本里读（保存 config 即生效，不用改脚本）
local cfg = conky.config()
local SCALE_FLOOR_KB = cfg.ScaleFloorKb or 16   -- 速率曲线最低满格量（KB/s）
local GRAPH_H        = cfg.GraphHeight or 30    -- 曲线高度
local TOP_N          = cfg.TopProcesses or 5    -- 进程列表条数

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
    [51]="毛毛雨",[53]="毛毛雨",[55]="毛毛雨",[61]="小雨",[63]="中雨",[65]="大雨",
    [66]="冻雨",[67]="冻雨",[71]="小雪",[73]="中雪",[75]="大雪",[77]="雪粒",
    [80]="阵雨",[81]="阵雨",[82]="强阵雨",[85]="阵雪",[86]="阵雪",
    [95]="雷雨",[96]="雷雨冰雹",[99]="雷雨冰雹",
}

-- WMO 天气代码 → 图标（文本符号，单色主题下也协调）
local ICON = {
    [0]="☀︎",[1]="🌤︎",[2]="⛅︎",[3]="☁︎",[45]="🌫︎",[48]="🌫︎",
    [51]="🌧︎",[53]="🌧︎",[55]="🌧︎",[61]="🌧︎",[63]="🌧︎",[65]="🌧︎",
    [66]="🌧︎",[67]="🌧︎",[71]="🌨︎",[73]="🌨︎",[75]="🌨︎",[77]="🌨︎",
    [80]="🌧︎",[81]="🌧︎",[82]="🌧︎",[85]="🌨︎",[86]="🌨︎",
    [95]="⛈︎",[96]="⛈︎",[99]="⛈︎",
}

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
                            font_size = 26, color = C.accent }
        -- 天气描述 + 图标
        rows[#rows + 1] = { label = (WMO[math.floor(w.code)] or ""), text = (ICON[math.floor(w.code)] or ""),
                            font_size = 15, color = C.text }
        -- 气压 / 湿度 / 风速
        rows[#rows + 1] = { label = "湿度", text = string.format("%.0f%%", w.humidity), color = C.text }
        rows[#rows + 1] = { label = "风速", text = string.format("%.2f km/h", w.wind), color = C.text }
        if w.pressure > 0 then
            rows[#rows + 1] = { label = "气压", text = string.format("%.0f hPa", w.pressure), color = C.text }
        end
        -- 三天预报：三列并排（星期 / 图标 / 温度）
        local names = { "今天", "明天", "后天" }
        local cells = {}
        for i = 1, math.min(3, #w.daily) do
            local d = w.daily[i]
            cells[i] = {
                label = names[i],
                text = ICON[math.floor(d.code)] or "",
                sub = string.format("%.0f°/%.0f°", d.min, d.max),
                color = C.text,
            }
        end
        rows[#rows + 1] = { cells = cells }
    else
        local err = conky.weather_error()
        rows[#rows + 1] = { label = "天气", text = (err ~= "" and "重试中…" or "加载中…"), color = C.label }
    end

    rows[#rows + 1] = { gap = 6 }
    rows[#rows + 1] = { hr = true, color = C.hr }
    rows[#rows + 1] = { gap = 4 }

    -- ============ CPU / 内存 / 磁盘 IO 历史曲线 ============
    rows[#rows + 1] = { label = "Cpu:", text = string.format("%.0f%%", conky.cpu()),
                        color = C.text, graph = cpu_hist, graph_color = C.cpu, graph_height = GRAPH_H }
    rows[#rows + 1] = { label = "Mem:", text = string.format("%.0f%%  %.1fG", conky.mem_pct(), conky.mem_used_gb()),
                        color = C.text, graph = mem_hist, graph_color = C.mem, graph_height = GRAPH_H }
    rows[#rows + 1] = { label = "Disk I/O:", text = fmt_rate(conky.disk_io_kb()) .. "/s",
                        color = C.text, graph = to_pct(io_raw, io_scale), graph_color = C.disk, graph_height = GRAPH_H }

    rows[#rows + 1] = { gap = 6 }
    rows[#rows + 1] = { hr = true, color = C.hr }
    rows[#rows + 1] = { gap = 4 }

    -- ============ 网络上下行曲线 + 开机以来累计流量 ============
    rows[#rows + 1] = { label = "Down:", text = "↓" .. fmt_rate(conky.net_down_kb()),
                        color = C.text, graph = to_pct(down_raw, net_scale), graph_color = C.net_down, graph_height = GRAPH_H }
    rows[#rows + 1] = { label = "Up:", text = "↑" .. fmt_rate(conky.net_up_kb()),
                        color = C.text, graph = to_pct(up_raw, net_scale), graph_color = C.net_up, graph_height = GRAPH_H }
    rows[#rows + 1] = { label = "累计", text = string.format("收 %s / 发 %s",
                        fmt_gb(conky.net_total_down_gb()), fmt_gb(conky.net_total_up_gb())), color = C.label }

    rows[#rows + 1] = { gap = 6 }
    rows[#rows + 1] = { hr = true, color = C.hr }
    rows[#rows + 1] = { gap = 4 }

    -- ============ 进程：总数 + Top 列表（按内存 / 按 CPU） ============
    rows[#rows + 1] = { label = "Proc", text = string.format("%.0f", conky.proc()), color = C.text }
    for i, p in ipairs(conky.top_mem(TOP_N)) do
        rows[#rows + 1] = { label = p.name, text = string.format("%.0fM", p.mem_mb), color = C.text, font_size = 12 }
    end
    rows[#rows + 1] = { gap = 3 }
    for i, p in ipairs(conky.top_cpu(TOP_N)) do
        rows[#rows + 1] = { label = p.name, text = string.format("%.2f%%", p.cpu), color = C.text, font_size = 12 }
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

改完保存即生效（热重载），完全不用动这个脚本。
--]]
