local Element = require('elements/Element')

---@alias TopBarButtonProps {icon: string; hover_fg?: string; hover_bg?: string; command: (fun():string)}

---@class TopBar : Element
local TopBar = class(Element)

function TopBar:new() return Class.new(self) --[[@as TopBar]] end
function TopBar:init()
	Element.init(self, 'top_bar', {render_order = 4})
	self.size = 0
	self.alt_title_size = 0
	self.titles_spacing = 1
	self.icon_size, self.font_size, self.title_by = 1, 1, 1
	self.show_alt_as_main = false
	self.main_title, self.alt_title = nil, nil
	---@type table<string, string|nil>
	self.render_titles = {}

	local function maximized_command()
		mp.command(state.fullormaxed and 'set fullscreen no;set window-maximized no' or 'set window-maximized yes')
	end

	local close = {icon = 'close', hover_bg = '2311e8', hover_fg = 'ffffff', command = function() mp.command('quit') end}
	local max = {icon = 'crop_square', command = maximized_command, is_max = true}
	local min = {icon = 'minimize', command = function() mp.command('cycle window-minimized') end}
	-- MOMOKA[topbar-pin] — 置顶按钮（用户令 2026-09-28 晚「给独占模式右上角也加个置顶图标」）。
	-- 与集成模式右上角那一颗**同位同义**：集成那颗切宿主窗口的 TopMost（PlayerPage.Input.cs 的 SetPinned），
	-- 这颗切 mpv 窗口自己的 ontop —— 独占模式的窗口就是 mpv 那个顶层窗（见文件头 MOMOKA[topbar]：
	-- border=no、系统标题栏不存在），所以「置顶」在这个窗口上就是 mpv 的 ontop 属性，不需要经过宿主。
	-- 排在窗口三颗的**左边**（集成那一排也正是「置顶、最小化、最大化、关闭」），top_bar_controls='left'
	-- 时整排镜子一样翻过去、它落在最右。图标 push_pin —— 装箱的 MaterialIconsRound 里确有此字形（与集成
	-- 那颗 PathIcon 的图钉同义）。已置顶那一档怎么画见 render 里 MOMOKA[topbar-pin-tilt] 那段。
	local pin = {icon = 'push_pin', command = function() mp.command('cycle ontop') end, is_pin = true}
	self.buttons = options.top_bar_controls == 'left' and {close, max, min, pin} or {pin, min, max, close}

	-- MOMOKA[topbar-back] — 左上角返回按钮：独占窗口是独立顶层窗，退出 mpv 即回到外壳（详情页），
	-- 与集成模式左上角的返回同位同义（用户令 2026-09-26「给独占模式左上角加个返回按钮」）。图标用
	-- arrow_back_ios（uosc 上一集按钮同款，装箱的 Material Icons Round 里确有此字形）。
	self.back_button = {icon = 'arrow_back_ios', command = function() mp.command('quit') end}

	self:register_observers()
	self:decide_enabled()
	self:update_dimensions()
end

---@return string|nil
local function expand_template(template)
	-- escape ASS, and strip newlines and trailing slashes and trim whitespace
	local tmp = mp.command_native({'expand-text', template}):gsub('\\n', ' '):gsub('[\\%s]+$', ''):gsub('^%s+', '')
	return tmp and tmp ~= '' and ass_escape(tmp) or nil
end

function TopBar:add_template_listener(template, callback)
	local props = get_expansion_props(template)
	for prop, _ in pairs(props) do
		self:observe_mp_property(prop, 'native', callback)
	end
	if not next(props) then callback() end
end

function TopBar:register_observers()
	-- Main title
	if #options.top_bar_title > 0 and options.top_bar_title ~= 'no' then
		if options.top_bar_title == 'yes' then
			local template = nil
			local function update_main_title()
				self.main_title = expand_template(template)
				self:update_render_titles()
			end
			local function remove_template_listener(callback) mp.unobserve_property(callback) end

			self:observe_mp_property('title', 'string', function(_, title)
				remove_template_listener(update_main_title)
				template = title
				if template then
					if template:sub(-6) == ' - mpv' then template = template:sub(1, -7) end
					self:add_template_listener(template, update_main_title)
				end
			end)
		elseif type(options.top_bar_title) == 'string' then
			self:add_template_listener(options.top_bar_title, function()
				self.main_title = expand_template(options.top_bar_title)
				self:update_render_titles()
			end)
		end
	end

	-- Alt title
	if #options.top_bar_alt_title > 0 and options.top_bar_alt_title ~= 'no' then
		self:add_template_listener(options.top_bar_alt_title, function()
			self.alt_title = expand_template(options.top_bar_alt_title)
			self:update_render_titles()
		end)
	end
end

function TopBar:decide_enabled()
	if options.top_bar == 'no-border' then
		self.enabled = not state.border or state.title_bar == false or state.fullscreen
	else
		self.enabled = options.top_bar == 'always'
	end
	self.enabled = self.enabled and (options.top_bar_controls or options.top_bar_title ~= 'no' or state.has_playlist)
end

-- Set titles. Both have to be passed at the same time so that they can be normalized & deduplicated.
function TopBar:update_render_titles()
	local main, alt = self.main_title, self.alt_title

	if main == 'No file' then
		main = t('No file')
	end

	-- Fall back to alt title if main is empty
	if not main or main == '' then
		main, alt = alt, nil
	end

	-- Deduplicate the main and alt titles by checking if one completely
	-- contains the other, and using only the longer one.
	if main and alt and not self.show_alt_as_main then
		local longer_title, shorter_title
		if #main < #alt then
			longer_title, shorter_title = alt, main
		else
			longer_title, shorter_title = main, alt
		end

		local escaped_shorter_title = regexp_escape(shorter_title --[[@as string]])
		if string.match(longer_title --[[@as string]], escaped_shorter_title) then
			main, alt = longer_title, nil
		end
	end

	if self.show_alt_as_main and alt and alt ~= '' then
		main, alt = alt, nil
	end

	self.render_titles.main, self.render_titles.alt = main, alt
	self:update_dimensions()
	request_render()
end

-- MOMOKA[topbar-subline] — 宿主 → uosc 的副标题（第二行）：分辨率 · 视频编码 · 音频格式 · 组名
-- （用户令 2026-09-28「下方那一栏改为分辨率+视频编码+音频格式+组名」，2026-09-27 那批的前置分辨率版）。
-- 独占模式的 top_bar_alt_title 选项留空（register_observers 因此不给它挂模板监听），副标题改由宿主经
-- momoka-subline 直接写进来。位置**不跟着主标题走**：画在左上角返回按钮的正下方（左缘＝窗口左缘，
-- 用户令 2026-09-28「移动到返回按钮的下方」），主标题仍在返回按钮右边一行。空串＝收起副标题；
-- ass_escape 与主标题那条模板路一致（组名取自文件名，可能带需要转义的字符）。
-- 写完催一次 update_render_titles（内部会 update_dimensions＋request_render）。
function TopBar:set_subline(text)
	self.alt_title = (text and text ~= '') and ass_escape(text) or nil
	self:update_render_titles()
end

function TopBar:update_dimensions()
	self.size = round(options.top_bar_size * state.scale)
	self.title_spacing = round(1 * state.scale)
	self.icon_size = round(self.size * 0.5)
	self.font_size = math.floor((self.size - (math.ceil(self.size * 0.25) * 2)) * options.font_scale)
	self.alt_title_size = round(self.font_size * 1.2)
	local window_border_size = Elements:v('window_border', 'size', 0)
	local min_hitbox_height = self.size
	if self.render_titles.alt and options.top_bar_alt_title_place == 'below' then
		min_hitbox_height = min_hitbox_height + self.title_spacing + self.alt_title_size
	end
	self.ax = window_border_size
	self.ay = window_border_size
	self.bx = display.width - window_border_size
	-- MOMOKA[topbar-no-chapter] — 命中区的加高只为副标题那一行（章节那一行已整段撤下，见 render）。
	self.by = math.max(self.size + window_border_size, min_hitbox_height - options.proximity_in)
end

function TopBar:toggle_title()
	if options.top_bar_alt_title_place ~= 'toggle' then return end
	self.show_alt_as_main = not self.show_alt_as_main
	self:update_render_titles()
end

function TopBar:on_prop_border()
	self:decide_enabled()
	self:update_dimensions()
end

function TopBar:on_prop_title_bar()
	self:decide_enabled()
	self:update_dimensions()
end

function TopBar:on_prop_fullscreen()
	self:decide_enabled()
	self:update_dimensions()
end

function TopBar:on_prop_maximized()
	self:decide_enabled()
	self:update_dimensions()
end

function TopBar:on_prop_has_playlist()
	self:decide_enabled()
	self:update_dimensions()
end

function TopBar:on_display() self:update_dimensions() end

function TopBar:on_options()
	self:decide_enabled()
	self:update_dimensions()
end

function TopBar:render()
	local visibility = self:get_visibility()
	if visibility <= 0 then return end
	local ass = assdraw.ass_new()
	-- `by` might be artificially extended (see update_dimensions) to keep the subline row clickable
	-- under low proximity options, so we can't use it for rendering.
	local ax, ay, bx, by = self.ax, self.ay, self.bx, self.ay + self.size
	local margin = math.floor((self.size - self.font_size) / 4)

	-- Window controls
	if options.top_bar_controls then
		local is_left, button_ax = options.top_bar_controls == 'left', 0
		if is_left then
			button_ax = ax
			ax = self.size * #self.buttons
		else
			button_ax = bx - self.size * #self.buttons
			bx = button_ax
		end

		for _, button in ipairs(self.buttons) do
			if button.is_max then
				button.icon = state.fullscreen and 'close_fullscreen' or
				(state.maximized and 'filter_none' or 'crop_square')
			end

			local rect = {ax = button_ax, ay = ay, bx = button_ax + self.size, by = by, input_owner = button}
			local is_hover = get_point_to_rectangle_proximity(cursor, rect) <= 0
			-- MOMOKA[topbar-pin-tilt] — 状态画在图钉的姿势上（用户令 2026-09-29「置顶不要长亮，改为非置顶
			-- 的时候图标是斜的，置顶的时候恢复原样」）：未置顶斜 35°、置顶立正。角度取自
			-- Momoka.Shell 那颗的 PinTiltDegrees（PlayerPage.Input.cs）—— 两头同一个姿势、同一份来历。
			-- libass 的 \frz 正角是**逆时针**，集成那头 WinUI RotateTransform 正角是顺时针，所以这里取
			-- 负号：-35 画出来与集成 +35 同一个方向（钉头向右倒）。ass:txt 的 opts.rotate 原生接这个标记
			-- （lib/ass.lua），icon 把 opts 整包递下去，不用另开一路。旋转绕锚点（\an5 的字面中心）转，
			-- 图标不会甩出那格底。上一版「已置顶整颗常亮」那一档（lit 认 state.ontop）同日撤下 —— lit 回到
			-- 与其余几颗同一句 is_hover，亮与不亮只剩悬停那一层；is_pin 仍在，是姿势那一半的路标。
			-- 状态来源没变：main.lua 观察 mpv 的 ontop 属性写 state.ontop，图钉立没立正跟着它走。
			local lit = is_hover
			local icon_rotate = button.is_pin and state.ontop ~= true and -35 or nil
			local opacity = lit and 1 or config.opacity.controls
			local button_fg = lit and (button.hover_fg or bg) or fg
			local button_bg = lit and (button.hover_bg or fg) or bg

			cursor:zone('primary_click', rect, button.command)

			local bg_size = self.size - margin
			local bg_ax, bg_ay = rect.ax + (is_left and margin or 0), rect.ay + margin
			local bg_bx, bg_by = bg_ax + bg_size, bg_ay + bg_size

			ass:rect(bg_ax, bg_ay, bg_bx, bg_by, {
				color = button_bg, opacity = visibility * opacity, radius = state.radius,
			})

			ass:icon(bg_ax + bg_size / 2, bg_ay + bg_size / 2, bg_size * 0.5, button.icon, {
				color = button_fg,
				border_color = button_bg,
				opacity = visibility,
				border = options.text_border * state.scale,
				rotate = icon_rotate,
			})

			button_ax = button_ax + self.size
		end
	end

	-- MOMOKA[topbar-back] — 返回按钮画在窗口标题左侧（点它退出 mpv＝回到外壳详情页）。放在窗口控制块之后、
	-- 标题之前：控制块在右侧（top_bar_controls='right'，独占默认）时不动 ax，返回按钮就落在最左。
	-- MOMOKA[topbar-back-glass] — 可见底与标题那块玻璃**同形同色**（用户令 2026-09-28 晚「返回按钮的背景要和
	-- 标题的背景一致」，问实了＝连大小一起跟标题一致）：高 size-2*margin、四周各让 margin（左缘＝窗口左缘＋
	-- margin，与上沿同一个数）、贴到窗口左缘内侧（与它正下方的副标题同一左缘）。2026-09-28 那版「整格 size
	-- 见方、贴角」按这条令撤回；**同日更晚又按「左边的空隙要和上面的一样大」把左缘也让进 margin** —— 于是
	-- 玻璃在整格 size 里四边各留 margin（uosc 自家窗口按钮那套「外边让 margin」的画法，只是不缩小而已）。
	-- 命中区跟着可见底走（老写法是整格 size 见方，指针压在标题左端也会点亮返回键）；图标仍按可见底的一半画。
	do
		local glass = self.size - margin * 2
		local rect = {
			ax = ax + margin,
			ay = ay + margin,
			bx = ax + margin + glass,
			by = ay + margin + glass,
			input_owner = self.back_button,
		}
		local is_hover = get_point_to_rectangle_proximity(cursor, rect) <= 0
		-- MOMOKA[topbar-back] — 返回键始终带一块可见背景：uosc 窗口按钮默认 opacity.controls=0，静止时只有
		-- 图标、没有底（压在亮画面上看不清），用户要「给返回键加背景」。静止＝深底＋亮箭头，悬停＝翻成亮底暗箭头。
		-- MOMOKA[topbar-back-glass] — 静止档的不透明度取 config.opacity.title（与标题那块玻璃同一个数，用户令
		-- 2026-09-28 晚「返回按钮的背景要和标题的背景一致」）：原来是写死的 0.55，比标题淡一层、压在画面上发灰。
		local bg_opacity = is_hover and 1 or config.opacity.title
		local button_fg = is_hover and bg or fg
		local button_bg = is_hover and fg or bg

		cursor:zone('primary_click', rect, self.back_button.command)

		local bg_size = glass
		local bg_ax, bg_ay = rect.ax, rect.ay
		local bg_bx, bg_by = bg_ax + bg_size, bg_ay + bg_size

		ass:rect(bg_ax, bg_ay, bg_bx, bg_by, {
			color = button_bg, opacity = visibility * bg_opacity, radius = state.radius,
		})
		ass:icon(bg_ax + bg_size / 2, bg_ay + bg_size / 2, bg_size * 0.5, self.back_button.icon, {
			color = button_fg,
			border_color = button_bg,
			opacity = visibility,
			border = options.text_border * state.scale,
		})

		-- MOMOKA[topbar-back-glass] — 标题那一块从这里起：缝＝title_spacing（用户令 2026-09-28 晚「返回键跟
		-- 标题的间隙右边要跟下面一致」—— 两行之间本来就是 title_spacing，右边那条缝照样收成它）。标题左缘
		-- 不再另加 margin：uosc 原版那个 margin 是「窗口左缘到标题」的量，这里已经由返回键玻璃左边那条
		-- 让出去了（见上，返回键玻璃左缘＝窗口左缘＋margin，与标题上沿同一个数）。
		ax = rect.bx + self.title_spacing
	end

	-- Window title
	local main_title, alt_title = self.render_titles.main, self.render_titles.alt
	if main_title or state.has_playlist then
		local padding = round(self.font_size / 2)
		local left_aligned = options.top_bar_controls == 'left'
		-- MOMOKA[topbar-back-glass] — 标题玻璃回到 uosc 自家那一条（用户令 2026-09-28 晚「把标题的大小改回跟
		-- C:\mpv_config-2026.08.12 这个项目一样大小」）：高 size-2*margin、从 self.ay+margin 起，上下各让
		-- margin —— 2026-09-28 那版「画满整格 size 高」按这条令撤回。左缘不再另加 margin（缝已由返回键推进 ax）。
		local title_ax, title_bx, title_ay = ax, bx - margin, self.ay + margin

		-- Playlist position
		if state.has_playlist then
			local text = state.playlist_pos .. '' .. state.playlist_count
			local formatted_text = '{\\b1}' .. state.playlist_pos .. '{\\b0\\fs' .. self.font_size * 0.9 .. '}/'
				.. state.playlist_count
			local opts = {size = self.font_size, wrap = 2, color = fgt, opacity = visibility}
			local rect_width = round(text_width(text, opts) + padding * 2)
			local ax = left_aligned and title_bx - rect_width or title_ax
			local rect = {
				ax = ax,
				ay = title_ay,
				bx = ax + rect_width,
				by = by - margin,
				input_owner = self.id .. '/playlist',
			}
			local opacity = get_point_to_rectangle_proximity(cursor, rect) <= 0
				and 1 or config.opacity.playlist_position
			if opacity > 0 then
				ass:rect(rect.ax, rect.ay, rect.bx, rect.by, {
					color = fg, opacity = visibility * opacity, radius = state.radius,
				})
			end
			ass:txt(rect.ax + (rect.bx - rect.ax) / 2, rect.ay + (rect.by - rect.ay) / 2, 5, formatted_text, opts)
			if left_aligned then title_bx = rect.ax - margin else title_ax = rect.bx + margin end

			-- Click action
			cursor:zone('primary_click', rect, function() mp.command('script-binding uosc/playlist') end)
		end

		-- Skip rendering titles if there's not enough horizontal space
		if title_bx - title_ax > self.font_size * 3 and options.top_bar_title ~= 'no' then
			-- Main title
			if main_title then
				local opts = {
					size = self.font_size,
					wrap = 2,
					color = bgt,
					opacity = visibility,
					border = options.text_border * state.scale,
					border_color = bg,
					clip = string.format('\\clip(%d, %d, %d, %d)', self.ax, ay, title_bx, by),
				}
				local rect_ideal_width = round(text_width(main_title, opts) + padding * 2)
				local rect_width = math.min(rect_ideal_width, title_bx - title_ax)
				local ax = left_aligned and title_bx - rect_width or title_ax
				-- MOMOKA[topbar-back-glass] — 标题玻璃下沿同样让进 margin（与参考项目「上下各让 margin」一条）。
				local by = by - margin
				local title_rect = {ax = ax, ay = title_ay, bx = ax + rect_width, by = by, input_owner = self}

				if options.top_bar_alt_title_place == 'toggle' then
					cursor:zone('primary_click', title_rect, function() self:toggle_title() end)
				end

				ass:rect(title_rect.ax, title_rect.ay, title_rect.bx, title_rect.by, {
					color = bg, opacity = visibility * config.opacity.title, radius = state.radius,
				})
				local align = left_aligned and rect_ideal_width == rect_width and 6 or 4
				local x = align == 6 and title_rect.bx - padding or ax + padding
				ass:txt(x, ay + (self.size / 2), align, main_title, opts)
				title_ay = by + self.title_spacing
			end

			-- Alt title
			-- MOMOKA[topbar-subline] — 副标题（宿主经 momoka-subline 写进来的文件信息行）挂在**返回按钮的
			-- 正下方**：左缘＝返回键玻璃的左缘（＝窗口左缘＋margin，用户令 2026-09-28 晚「左边的空隙要和上面
			-- 的一样大」把整簇按 margin 内缩之后，返回键玻璃与自己正下方这一行仍共用同一条左缘），不再跟着
			-- 主标题的左缘走（用户令 2026-09-28「移动到返回按钮的下方」）。top_bar_controls='left' 的老摆法照旧。
			if alt_title and options.top_bar_alt_title_place == 'below' then
				local by = title_ay + self.alt_title_size
				-- MOMOKA[topbar-subline-branch] — 副标题前面缀一个「└ 」（用户令 2026-09-28 更晚「把这个添加到
				-- 元数据的前面」）：参考项目 uosc 原版给**章节那一行**加的就是这个树干，这里照它画在副标题上 ——
				-- 上面主标题那一行是树干、副标题挂在它底下。量字宽与画字都用带前缀的那一串（框宽跟着一起宽），
				-- 前缀画在玻璃里面（与参考项目同一个位置）。top_bar_controls='left' 的老摆法不加前缀（同参考条件）。
				local subline_text = left_aligned and alt_title or '└ ' .. alt_title
				-- MOMOKA[topbar-subline-size] — 字号是 alt_title_size 的一档缩小。原版 0.77（窗口档 \fs18、
				-- 全屏档 \fs24），用户令 2026-09-29「元数据缩小一点点，集成模式和独占模式大小要一致」收到
				-- 0.71（两档正好落到 \fs17 / \fs22）。集成那头的 Shell 字号 = 这里的 \fs × 0.75（libass \fs
				-- 是 72 DPI pt、WinUI FontSize 是 96 DPI px，见 Styles.xaml 注），两头必须一起改：
				-- Styles.xaml 的 EgSublineFontSize（12.75）＋ PlayerPage.Chrome.cs 的 FullscreenSubtitleFont（16.5）
				-- ＋ PlayerPage.SelfCheck.Chrome.cs 的 SubtitleFontSize 断言。
				local opts = {
					size = round(self.alt_title_size * 0.71),
					-- MOMOKA[topbar-subline-italic] — 副标题（分辨率 · 视频编码 · 音频格式 · 组名）走斜体，
					-- 用户令 2026-09-28 晚「标题下方的视频元数据改为斜体」；字宽算量同样认这个标记
					-- （lib/text.lua 的 whole_text_width 会把斜体那点倾斜算进去），框宽跟着对得上。
					italic = true,
					wrap = 2,
					-- MOMOKA[topbar-subline-dim] — 字色比标题淡一档的浅灰（用户令 2026-09-28 晚「元数据的
					-- 字体加点灰色」）：原来是 bgt（＝background_text FFFBFE，与标题同色）。**ass.txt 把 opts.color
					-- 原样接在 `\1c&H` 后面**（lib/ass.lua），也就是这里要写 ASS 的 BBGGRR 顺序 —— 本值是中性灰、
					-- 两个顺序同一个串，不踩那个坑。想再深/再浅改这一个数即可。
					color = 'c8c8c8',
					border = options.text_border * state.scale,
					border_color = bg,
					opacity = visibility,
				}
				local subline_ax = left_aligned and title_bx or self.ax + margin
				local rect_ideal_width = round(text_width(subline_text, opts) + padding * 2)
				local rect_width = math.min(rect_ideal_width, title_bx - subline_ax)
				local ax = left_aligned and subline_ax - rect_width or subline_ax
				local bx = ax + rect_width
				opts.clip = string.format('\\clip(%d, %d, %d, %d)', subline_ax, title_ay, bx, by)
				ass:rect(ax, title_ay, bx, by, {
					color = bg, opacity = visibility * config.opacity.title, radius = state.radius,
				})
				local align = left_aligned and rect_ideal_width == rect_width and 6 or 4
				local x = align == 6 and bx - padding or ax + padding
				ass:txt(x, title_ay + self.alt_title_size / 2, align, subline_text, opts)
				title_ay = by + self.title_spacing
			end
		end
		self.title_by = title_ay - 1
	else
		self.title_by = ay
	end

	return ass
end

return TopBar
