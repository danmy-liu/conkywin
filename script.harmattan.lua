-- Harmattan 风格布局（复刻 Linux conky 主题，配合 config.json: PanelWidth=268, Position.RightGap=0）
--
-- 主题切换接口：把下面的 THEME 改成 "color"（彩色）或 "mono"（白色单色，原版风格），
-- 保存即生效（ConkyWin 会热重载本脚本，不用重启）。想微调颜色直接改下面的 C 表。
local THEME = "color"

local C
if THEME == "mono" then
    C = {
        text = "#FFFFFF", label = "#AAAAAA", hr = "#66FFFFFF",
        cpu = "#FFFFFF", mem = "#FFFFFF", disk = "#FFFFFF",
        net_down = "#FFFFFF", net_up = "#FFFFFF", gpu = "#FFFFFF",
        accent = "#FFFFFF",
    }
else
    C = {
        text = "#EAF2FC", label = "#7A90AC", hr = "#40FFFFFF",
        cpu = "#2F9EFF", mem = "#9B7BFF", disk = "#FFC85C",
        net_down = "#4DE3E0", net_up = "#8FD8FF", gpu = "#B78CFF",
        accent = "#4DE3E0",
    }
end

-- 曲线历史序列（最多 60 个点，约 1 分钟）
local function push(t, v)
    t[#t + 1] = v
    if #t > 60 then table.remove(t, 1) end
    return t
end

-- ============ 速率曲线自适应满格量 ============
-- 固定满格量（比如 2MB/s）在平时几 K 的流量下会画成一条直线，看不出起伏。
-- 改成自适应：峰值走高立刻放大满格量，峰值回落到一半以下再缓慢收缩，
-- 这样无论是 3K 还是 5M/s，曲线都能占满整个高度。
local SCALE_FLOOR_KB = 16   -- 最低满格量（KB/s），越小越灵敏，也越容易看到"噪声"

-- 取整到 1/2/5 × 10^n，让刻度值好看、不跳变
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

-- 满格量更新：涨得快、落得慢（滞后 2 倍，避免刻度来回抖）
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

local cpu_hist, mem_hist = {}, {}
local io_raw, down_raw, up_raw = {}, {}, {}
local io_scale, net_scale = SCALE_FLOOR_KB, SCALE_FLOOR_KB

local function fmt_rate(kb)
    if kb >= 1024 then return string.format("%.1fM", kb / 1024) end
    return string.format("%.0fK", kb)
end

-- WMO 天气代码 → 中文
local WMO = {
    [0]="晴",[1]="少云",[2]="多云",[3]="阴",[45]="雾",[48]="雾凇",
    [51]="毛毛雨",[53]="毛毛雨",[55]="毛毛雨",[61]="小雨",[63]="中雨",[65]="大雨",
    [66]="冻雨",[67]="冻雨",[71]="小雪",[73]="中雪",[75]="大雪",[77]="雪粒",
    [80]="阵雨",[81]="阵雨",[82]="强阵雨",[85]="阵雪",[86]="阵雪",
    [95]="雷雨",[96]="雷雨冰雹",[99]="雷雨冰雹",
}

function conky_rows()
    local rows = {}

    push(cpu_hist, conky.cpu())
    push(mem_hist, conky.mem_pct())
    push(io_raw, conky.disk_io_kb())
    push(down_raw, conky.net_down_kb())
    push(up_raw, conky.net_up_kb())

    -- 磁盘 IO 单独一个刻度；上下行共用一个刻度（两条曲线高度才可比）
    io_scale = autoscale(io_scale, peak(io_raw))
    net_scale = autoscale(net_scale, math.max(peak(down_raw), peak(up_raw)))

    -- 天气（Open-Meteo，首次加载约 1-3 秒；成功后会本地缓存，重启不需要重新等）
    local w = conky.weather()
    if w then
        rows[#rows + 1] = { label = w.city, text = string.format("%.1f°", w.temp),
                            font_size = 30, color = C.accent }
        rows[#rows + 1] = { label = "湿度", text = string.format("%.0f%%", w.humidity), color = C.text }
        rows[#rows + 1] = { label = "风速", text = string.format("%.0f km/h", w.wind), color = C.text }
        local names = { "今天", "明天", "后天" }
        for i = 1, math.min(3, #w.daily) do
            local d = w.daily[i]
            rows[#rows + 1] = {
                label = names[i] .. " " .. (WMO[math.floor(d.code)] or ""),
                text = string.format("%.0f°/%.0f°", d.min, d.max),
                color = C.text,
            }
        end
    else
        local err = conky.weather_error()
        rows[#rows + 1] = { label = "天气", text = (err ~= "" and "重试中…" or "加载中…"),
                            color = C.label }
    end

    rows[#rows + 1] = { hr = true, color = C.hr }

    -- CPU / 内存 / 磁盘 IO 历史曲线
    rows[#rows + 1] = { label = "CPU", text = string.format("%.0f%%", conky.cpu()),
                        color = C.cpu, graph = cpu_hist, graph_color = C.cpu }
    rows[#rows + 1] = { label = "MEM", text = string.format("%.0f%% %.1fG", conky.mem_pct(), conky.mem_used_gb()),
                        color = C.mem, graph = mem_hist, graph_color = C.mem }
    rows[#rows + 1] = { label = "DISK I/O", text = fmt_rate(conky.disk_io_kb()) .. "/s",
                        color = C.disk, graph = to_pct(io_raw, io_scale), graph_color = C.disk }

    rows[#rows + 1] = { hr = true, color = C.hr }

    -- 网络上下行曲线（满格量自适应，几 K 的流量也有起伏）
    rows[#rows + 1] = { label = "DOWN", text = "↓" .. fmt_rate(conky.net_down_kb()),
                        color = C.net_down, graph = to_pct(down_raw, net_scale), graph_color = C.net_down }
    rows[#rows + 1] = { label = "UP", text = "↑" .. fmt_rate(conky.net_up_kb()),
                        color = C.net_up, graph = to_pct(up_raw, net_scale), graph_color = C.net_up }

    rows[#rows + 1] = { hr = true, color = C.hr }

    -- GPU（无 NVIDIA 显卡时自动隐藏）
    if conky.gpu_available() then
        local gpu = conky.gpu_util()
        rows[#rows + 1] = {
            label = "GPU",
            text = string.format("%.0f%% %.1fG %.0f℃", gpu, conky.gpu_mem_used_gb(), conky.gpu_temp()),
            color = conky.gpu_temp() > 80 and "#FF5C5C" or C.gpu,
            bar = math.max(gpu, conky.gpu_mem_used_gb() / conky.gpu_mem_total_gb() * 100),
            bar_start = C.gpu, bar_end = C.gpu,
        }
    end

    rows[#rows + 1] = { label = "PROC", text = string.format("%.0f", conky.proc()), color = C.text }

    return rows
end
