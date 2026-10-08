local Element = require('elements/Element')

-- MOMOKA[skip-button] — 独占模式的「跳过片头/片尾」按钮。
--
-- 集成模式那颗按钮是 XAML 的 SkipButton（PlayerPage.xaml），由 PlayerViewModel.SkipOffered 驱动；
-- 独占模式画面在 mpv 自己的顶层窗口里，那颗 XAML 按钮不在屏上，于是宿主把这份 offer 经
-- momoka-skip-offer 推过来由本元件画：有文案＝立起一颗按钮，点它回推 momoka-skip-take，
-- 宿主 TakeSkip → AcceptSkip 跳到片段另一端。offer 站多久、什么时候收摊全由宿主的 SkipCoordinator 判
-- （15 秒、离开片段、暂停、换集都归它），本元件不自己计时 —— 宿主发一条空文案即收摊。
--
-- 位置在右下、控制条/时间轴上沿之上；只要 offer 在就常驻可见（min_visibility=1），不靠指针移动唤出
-- —— 与集成模式一致（片头那 15 秒里就算手没动，那颗按钮也得看得见）。curtain（≡ 菜单那种压暗
-- 全屏的模态）升起时，本元件 render_order(6) < curtain(999) 会自动让位。
--
-- ── MOMOKA[skip-keys] 2026-09-30：offer 立着那一段替宿主接住回车与 Esc ─────────────────────
-- 用户报「按回车和 esc 确认跳过不生效」，两种模式都不生效，独占这一半的根因是**键权归属**：独占模式
-- 的键盘归 mpv（起播参数 input-default-bindings=yes、input-vo-keyboard=yes），Esc 是 mpv 内建的
-- 「退全屏/退出」、回车压根没有绑定 —— 两键都不会到 shell 的 Dispatch 那里，提示立着按了也白按。
--
-- 修法是**只在那一段里借键**：offer 立起时（set_offer 非空）用 keybind 把 ENTER/ESC 按到比内建高一级的
-- 优先级上、指向本元件所在脚本的两条无默认键的绑定（main.lua 的 bind_command('momoka-ui-skip-take' /
-- 'momoka-ui-skip-dismiss')），offer 收摊时（空文案，含点按钮跳过、按 Esc 关掉、15 秒到点、换集）
-- 立刻把内建行为还原 —— Esc 的「退全屏/退出」因此在没有提示的时候一字未改，其余任何时候也全归 mpv。
--
-- ── MOMOKA[skip-keys-restore] 2026-10-02：还键不能走 keyunbind ───────────────────────────
-- 用户再报同样的话、并补「点击第二屏的窗口之后就不生效」。随包内核实测（work/probe-keybind-family.py
-- 的 command-list、work/probe-skip-keys.py 的运行实录）：这份 libmpv（v0.41.0-923）**只有 keybind、
-- 没有 keyunbind**，2026-09-30 那版收摊时调 keyunbind 条条报「Command 'keyunbind' not found.」
-- （宿主日志 app-20261002.log 08:48:49 起两对实录），借走的键从此不还 —— 第一次 offer 收摊之后，
-- ESC 的内建「退全屏」被本元件的 dismiss 绑定永远压着，独占模式下 Esc 再也退不出全屏。
-- 还原因此改成**原样按回**：借键前先从 input-bindings 抄下这一颗此刻最前面的「别人的」绑定，收摊时
-- 用 keybind 原样按回去。它落在 keybind 那档优先级（实测 13）而不是内建原来的 0 —— 本项目 config=no
-- 起播、没有用户 input.conf，13 与 0 之间没有别的绑定，行为与还原前一字不差。
--
-- 两条绑定名守 MOMOKA[ui-bind]：绑定一律 momoka-ui-…、回宿主的消息一律 momoka-…，两套不许同名。

---@class SkipButton : Element
local SkipButton = class(Element)

-- 借来的键。值＝要按上去的绑定名（脚本名前缀由 keybind 那行拼）。
-- KP_ENTER 2026-10-02 补：实机日志（app-20261002.log 08:48:47「No key binding found for key
-- 'KP_ENTER'」）说明用户按的是小键盘回车 —— 集成模式里两颗回车都是 VirtualKey.Enter、走同一句
-- Dispatch，独占这里只借 ENTER 就把小键盘那颗漏在了门外。
local BORROWED_KEYS = {
	ENTER = 'momoka-ui-skip-take',
	KP_ENTER = 'momoka-ui-skip-take',
	ESC = 'momoka-ui-skip-dismiss',
}

function SkipButton:new() return Class.new(self) --[[@as SkipButton]] end
function SkipButton:init()
	Element.init(self, 'skip_button', {render_order = 6})
	self.caption = nil
	self.keys_borrowed = false
	-- 还键的凭据：借键前抄下的每颗键原来的绑定命令（见文件头 MOMOKA[skip-keys-restore]）。
	self.originals = {}
	-- 别在这里读 state.scale：元件在 set_scale 之前构造，那时它还是 nil。真正的字号在 layout 里算。
	self.font_size = 16
end

-- 宿主推来的一次 offer：非空文案＝立起按钮，空＝收摊。
function SkipButton:set_offer(caption)
	self.caption = (caption and caption ~= '') and caption or nil
	-- offer 在就常驻可见（不靠指针唤出）；不在就交回代理，min_visibility 归零、按钮消失。
	self.min_visibility = self.caption and 1 or 0
	-- 借键／还键跟着 offer 走 —— 这是本元件唯一改动全局键盘的地方（见文件头 MOMOKA[skip-keys]）。
	self:borrow_keys(self.caption ~= nil)
	self:layout()
	request_render()
end

---offer 立着时把 ENTER/KP_ENTER/ESC 借过来，收摊时原样还回去。幂等：同一状态重复调用不发第二条命令。
---@param borrow boolean
function SkipButton:borrow_keys(borrow)
	if borrow == self.keys_borrowed then return end
	self.keys_borrowed = borrow

	for key, binding in pairs(BORROWED_KEYS) do
		if borrow then
			-- 抄原绑定必须在 keybind 之前：之后 input-bindings 里最前面的就是自己那条了。只抄第一次
			-- （originals 里已记下的键不再抄）—— 收摊的还原本身也是一条 keybind，二次借键若再抄就会
			-- 把还原那条当成「原绑定」，一借一还之间账本越滚越厚。
			if self.originals[key] == nil then
				self.originals[key] = self:topmost_foreign_binding(key)
			end
			-- 不传 flags：mpv 的 keybind 默认就落在比内建高一级的优先级上（与宿主给 MBTN_RIGHT/MENU
			-- 那两把同一个写法，见 MpvUi.MenuKeys），内建的 ESC 因此在这一段里不执行。
			mp.commandv('keybind', key, 'script-binding uosc/' .. binding)
		else
			-- 还原＝原样按回（keyunbind 在随包内核里不存在，见文件头 MOMOKA[skip-keys-restore]）。
			-- 原来就没绑定的键（小键盘回车在这套内建里一颗绑定都没有）按一条 ignore 顶住 —— 行为与
			-- 「没绑定」等价：键不干活，也不再刷「No key binding found」。
			mp.commandv('keybind', key, self.originals[key] or 'ignore')
		end
	end
end

---这一颗键此刻真正接活儿的那条绑定命令（优先级最高的非本脚本条目），没有则 nil。
---@param key string
---@return string|nil
function SkipButton:topmost_foreign_binding(key)
	local best, best_priority = nil, nil
	for _, entry in ipairs(mp.get_property_native('input-bindings') or {}) do
		if entry.key == key and not tostring(entry.cmd or ''):find('momoka-ui-skip-', 1, true) then
			local priority = tonumber(entry.priority) or -math.huge
			if best_priority == nil or priority > best_priority then
				best, best_priority = entry.cmd, priority
			end
		end
	end
	return best
end

function SkipButton:on_display() self:layout() end

-- 几何按当前文案与窗口尺寸算一次：右下角，落在控制条/时间轴上沿之上一点，避开那一排按钮。
--
-- 尺寸（2026-09-30 用户令「缩小图标跳过按钮两倍」＋「参考集成模式的跳过按钮修改独占模式的跳过按钮」；
-- 2026-10-01 用户令「跳过按钮太小，调大 1.5 倍」，两轮都跟着集成走）：与集成的窗口档**同一个观感**，
-- 全屏/最大化那一档靠 state.scale（scale_fullscreen = 1.3）自动跟上，与集成那颗 ×1.3 是同一把尺。
-- 集成的窗口档（PlayerPage.Chrome.cs 的 WindowSkip* 一族）是内边距 18×21、行距 7.5、右侧让位 21、
-- 图标与标题 12；这里：
--   · 几何量（内边距、行距、让位、图标格）与集成 1:1 —— uosc 的坐标就是显示像素，scale 里已经含
--     了 hidpi 那一层，与 WinUI 的有效像素同一条尺；
--   · 字号（\fs）要先除 0.75：ASS 的 \fs 按 72 DPI 的 pt 渲染，WinUI 的 FontSize 是 96 DPI 的 px，
--     乘 0.75 才等大（这条换算在 PlayerPage.xaml 的 OsdGlyphStyle/WindowGlyphStyle 那一段量过、
--     集成与外挂顶栏都照它对齐），所以「集成 12」＝ ass 16。
-- 修订前是 font_size 27、内边距 22×13、行距 11、让位 16 —— 那时独占这颗比集成那颗大出约四分之一，
-- 09-30 那一批抹平的就是这个差异。
function SkipButton:layout()
	if not self.caption then self:reset_proximity() return end

	local scale = state.scale or 1
	-- 图标与标题：ass 的 \fs（＝集成那 12 px ÷ 0.75）。
	self.font_size = round(16 * scale)
	-- 图标占的格子与其余几何量：与集成同一个数（px）。
	local glyph = round(12 * scale)
	local pad_x, pad_y = round(18 * scale), round(21 * scale)
	local gap = round(7.5 * scale)
	local caption_w = text_width(self.caption, {size = self.font_size, bold = true})

	local width = pad_x * 2 + glyph + gap + caption_w
	local height = pad_y * 2 + glyph

	-- 落点：控制条与时间轴里位置最高的那条上沿之上；两者都没坐标（禁用/未就绪）就照窗口高度退一档。
	local anchor_top = display.height
	for _, id in ipairs({'controls', 'timeline'}) do
		local el = Elements[id]
		if el and el.ay and el.ay > 0 and el.ay < anchor_top then anchor_top = el.ay end
	end
	if anchor_top >= display.height then anchor_top = display.height - round(96 * scale) end

	-- 右侧让位与集成那颗同一个数（WindowSkipPadY＝21，集成写在那颗按钮的右边距上）。
	local margin = round(21 * scale)
	local bx = display.width - margin
	local by = anchor_top - margin
	self:set_coordinates(round(bx - width), round(by - height), round(bx), round(by))
end

function SkipButton:render()
	if not self.caption or self.bx <= self.ax then return end
	local visibility = self:get_visibility()
	if visibility <= 0 then return end

	-- 命中区照 Button 那一套：点击押到下一拍发，免得在事件派发中途改动元件栈引出竞态。
	cursor:zone('primary_click', self, function()
		mp.add_timeout(0.01, function() momoka_notify('momoka-skip-take', '') end)
	end)

	local scale = state.scale or 1
	local is_hover = self.proximity_raw <= 0
	local ass = assdraw.ass_new()

	-- 底：悬停实一档、平时半透；圆角与描边跟 uosc 一贯（半径 state.radius，描边取前景色）。
	ass:rect(self.ax, self.ay, self.bx, self.by, {
		color = bg,
		radius = state.radius,
		opacity = visibility * (is_hover and 0.95 or 0.75),
		border = round(math.max(1, scale)),
		border_color = fg,
	})

	-- 图标与标题的横向落点：与 layout 同一组数（内边距 18、图标格 12、行距 7.5），纵向居中。
	local glyph = round(12 * scale)
	local pad_x, gap = round(18 * scale), round(7.5 * scale)
	local cy = round((self.ay + self.by) / 2)
	local icon_x = self.ax + pad_x + glyph / 2
	ass:icon(icon_x, cy, self.font_size, 'skip_next', {color = fg, opacity = visibility})
	local text_x = icon_x + glyph / 2 + gap
	ass:txt(text_x, cy, 4, self.caption, {size = self.font_size, bold = true, color = fg, opacity = visibility})

	return ass
end

return SkipButton
