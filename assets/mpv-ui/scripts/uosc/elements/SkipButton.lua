local Element = require('elements/Element')

-- EMBYNIAN[skip-button] — 独占模式的「跳过片头/片尾」按钮。
--
-- 集成模式那颗按钮是 XAML 的 SkipButton（PlayerPage.xaml），由 PlayerViewModel.SkipOffered 驱动；
-- 独占模式画面在 mpv 自己的顶层窗口里，那颗 XAML 按钮不在屏上，于是宿主把这份 offer 经
-- embynian-skip-offer 推过来由本元件画：有文案＝立起一颗按钮，点它回推 embynian-skip-take，
-- 宿主 TakeSkip → AcceptSkip 跳到片段另一端。offer 站多久、什么时候收摊全由宿主的 SkipCoordinator 判
-- （15 秒、离开片段、暂停、换集都归它），本元件不自己计时 —— 宿主发一条空文案即收摊。
--
-- 位置在右下、控制条/时间轴上沿之上；只要 offer 在就常驻可见（min_visibility=1），不靠指针移动唤出
-- —— 与集成模式一致（片头那 15 秒里就算手没动，那颗按钮也得看得见）。curtain（≡ 菜单那种压暗
-- 全屏的模态）升起时，本元件 render_order(6) < curtain(999) 会自动让位。

---@class SkipButton : Element
local SkipButton = class(Element)

function SkipButton:new() return Class.new(self) --[[@as SkipButton]] end
function SkipButton:init()
	Element.init(self, 'skip_button', {render_order = 6})
	self.caption = nil
	-- 别在这里读 state.scale：元件在 set_scale 之前构造，那时它还是 nil。真正的字号在 layout 里算。
	self.font_size = 27
end

-- 宿主推来的一次 offer：非空文案＝立起按钮，空＝收摊。
function SkipButton:set_offer(caption)
	self.caption = (caption and caption ~= '') and caption or nil
	-- offer 在就常驻可见（不靠指针唤出）；不在就交回代理，min_visibility 归零、按钮消失。
	self.min_visibility = self.caption and 1 or 0
	self:layout()
	request_render()
end

function SkipButton:on_display() self:layout() end

-- 几何按当前文案与窗口尺寸算一次：右下角，落在控制条/时间轴上沿之上一点，避开那一排按钮。
function SkipButton:layout()
	if not self.caption then self:reset_proximity() return end

	local scale = state.scale or 1
	self.font_size = round(27 * scale)
	local pad_x, pad_y = round(22 * scale), round(13 * scale)
	local gap = round(11 * scale)
	local icon_size = self.font_size
	local caption_w = text_width(self.caption, {size = self.font_size, bold = true})

	local width = pad_x * 2 + icon_size + gap + caption_w
	local height = pad_y * 2 + icon_size

	-- 落点：控制条与时间轴里位置最高的那条上沿之上；两者都没坐标（禁用/未就绪）就照窗口高度退一档。
	local anchor_top = display.height
	for _, id in ipairs({'controls', 'timeline'}) do
		local el = Elements[id]
		if el and el.ay and el.ay > 0 and el.ay < anchor_top then anchor_top = el.ay end
	end
	if anchor_top >= display.height then anchor_top = display.height - round(96 * scale) end

	local margin = round(16 * scale)
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
		mp.add_timeout(0.01, function() embynian_notify('embynian-skip-take', '') end)
	end)

	local is_hover = self.proximity_raw <= 0
	local ass = assdraw.ass_new()

	-- 底：悬停实一档、平时半透；圆角与描边跟 uosc 一贯（半径 state.radius，描边取前景色）。
	ass:rect(self.ax, self.ay, self.bx, self.by, {
		color = bg,
		radius = state.radius,
		opacity = visibility * (is_hover and 0.95 or 0.75),
		border = round(math.max(1, (state.scale or 1))),
		border_color = fg,
	})

	local cy = round((self.ay + self.by) / 2)
	local icon_x = self.ax + round(22 * (state.scale or 1)) + self.font_size / 2
	ass:icon(icon_x, cy, self.font_size, 'skip_next', {color = fg, opacity = visibility})
	local text_x = icon_x + self.font_size / 2 + round(11 * (state.scale or 1))
	ass:txt(text_x, cy, 4, self.caption, {size = self.font_size, bold = true, color = fg, opacity = visibility})

	return ass
end

return SkipButton
