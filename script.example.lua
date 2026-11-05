-- ConkyWin Lua 脚本示例
-- 用法：把本文件复制到 exe 同目录并改名为 script.lua，保存后重启 ConkyWin。
-- 删除 script.lua 即恢复内置面板。
--
-- 沙箱环境：无 io/os 等模块；可用 API 见 README「Lua 脚本」一节：
--   conky.cpu() / conky.mem_pct() / conky.mem_used_gb() / conky.mem_total_gb()
--   conky.disk_pct() / conky.disk_used_gb() / conky.disk_total_gb()
--   conky.net_down_kb() / conky.net_up_kb()
--   conky.gpu_util() / conky.gpu_temp() / conky.gpu_mem_used_gb() / conky.gpu_mem_total_gb()
--   conky.gpu_available() / conky.proc() / conky.time(fmt)
--
-- 本示例使用「完整模式」：定义 conky_rows()，整面板由脚本生成。
-- 如果只想微调内置面板的个别行，改用「增强模式」：不定义 conky_rows()，
-- 只定义 conky_cpu_text / conky_cpu_color 等钩子（见文件末尾注释）。

-- 占用率越高颜色越红
local function heat(pct)
    pct = math.min(100, math.max(0, pct))
    -- 0% → 青绿 #4DE3E0，100% → 红 #FF5C5C，中段黄 #FFC85C
    if pct < 50 then
        local t = pct / 50
        return lerp("#4DE3E0", "#FFC85C", t)
    else
        local t = (pct - 50) / 50
        return lerp("#FFC85C", "#FF5C5C", t)
    end
end

-- 十六进制颜色插值
function lerp(c1, c2, t)
    local r1, g1, b1 = tonumber(c1:sub(2, 3), 16), tonumber(c1:sub(4, 5), 16), tonumber(c1:sub(6, 7), 16)
    local r2, g2, b2 = tonumber(c2:sub(2, 3), 16), tonumber(c2:sub(4, 5), 16), tonumber(c2:sub(6, 7), 16)
    local f = function(a, b) return string.format("%02X", math.floor(a + (b - a) * t + 0.5)) end
    return "#" .. f(r1, r2) .. f(g1, g2) .. f(b1, b2)
end

local function rate(kb)
    if kb >= 1024 then return string.format("%.1fM", kb / 1024) end
    return string.format("%dK", kb)
end

function conky_rows()
    local rows = {}

    -- 标题 + 时钟
    rows[#rows + 1] = {
        label = "S Y S T E M",
        text = conky.time("%H:%M:%S"),
        color = "#8FD8FF",
    }

    -- CPU：超 90% 时数值带感叹提示
    local cpu = conky.cpu()
    rows[#rows + 1] = {
        label = "CPU",
        text = string.format("%.0f%%%s", cpu, cpu > 90 and " !" or ""),
        color = heat(cpu),
        bar = cpu,
        bar_start = heat(cpu),
        bar_end = lerp(heat(cpu), "#2F9EFF", 0.4),
    }

    -- 内存
    local mem = conky.mem_pct()
    rows[#rows + 1] = {
        label = "MEM",
        text = string.format("%.1f/%.1fG", conky.mem_used_gb(), conky.mem_total_gb()),
        color = heat(mem),
        bar = mem,
        bar_start = heat(mem),
        bar_end = lerp(heat(mem), "#9B7BFF", 0.4),
    }

    -- 磁盘
    local disk = conky.disk_pct()
    rows[#rows + 1] = {
        label = "DISK",
        text = string.format("%.0f/%.0fG", conky.disk_used_gb(), conky.disk_total_gb()),
        color = heat(disk),
        bar = disk,
        bar_start = heat(disk),
        bar_end = lerp(heat(disk), "#FF8A5C", 0.4),
    }

    -- 网络
    rows[#rows + 1] = {
        label = "NET",
        text = "↓" .. rate(conky.net_down_kb()) .. " ↑" .. rate(conky.net_up_kb()),
        color = conky.net_down_kb() > 1024 and "#4DE3E0" or "#8FD8FF",
    }

    -- GPU（无 NVIDIA 显卡时自动跳过）
    if conky.gpu_available() then
        local gpu = conky.gpu_util()
        rows[#rows + 1] = {
            label = "GPU",
            text = string.format("%.0f%% %.1fG %.0f℃", gpu, conky.gpu_mem_used_gb(), conky.gpu_temp()),
            color = conky.gpu_temp() > 80 and "#FF5C5C" or "#B78CFF",
            bar = math.max(gpu, conky.gpu_mem_used_gb() / conky.gpu_mem_total_gb() * 100),
            bar_start = "#B78CFF",
            bar_end = "#7A5CFF",
        }
    end

    -- 进程数
    rows[#rows + 1] = {
        label = "PROC",
        text = string.format("%.0f", conky.proc()),
        color = "#EAF2FC",
    }

    return rows
end

--[[
「增强模式」示例（不定义 conky_rows，只定义钩子；内置面板保持不变）：

-- CPU 超 90% 数值变红：
function conky_cpu_color(pct)
    if pct > 90 then return "#FF5C5C" end
end

-- 内存显示百分比：
function conky_mem_text(used_gb, total_gb, pct)
    return string.format("%.0f%%", pct)
end

-- CPU 进度条超 80% 后渐变转红：
function conky_bar_color(name, pct)
    if name == "cpu" and pct > 80 then return {"#FFC85C", "#FF5C5C"} end
end

-- 自定义时钟格式：
function conky_time_text()
    return conky.time("%H:%M")
end
]]
