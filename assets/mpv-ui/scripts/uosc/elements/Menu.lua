local Element = require('elements/Element')

---@alias MenuAction {name: string; icon: string; label?: string; filter_hidden?: boolean;}

-- Menu data structure accepted by `Menu:open(menu)`.
---@alias MenuData {id?: string; type?: string; title?: string; hint?: string; footnote: string; search_style?: 'on_demand' | 'palette' | 'disabled';  item_actions?: MenuAction[]; item_actions_place?: 'inside' | 'outside'; callback?: string[]; keep_open?: boolean; bold?: boolean; italic?: boolean; muted?: boolean; separator?: boolean; align?: 'left'|'center'|'right'; items?: MenuDataChild[]; selected_index?: integer; on_search?: string|string[]; on_paste?: string|string[]; on_move?: string|string[]; on_close?: string|string[]; search_debounce?: number|string; search_submenus?: boolean; search_suggestion?: string; search_submit?: boolean; bind_keys?: string[]}
---@alias MenuDataChild MenuDataItem|MenuData
---@alias MenuDataItem {title?: string; hint?: string; icon?: string; value: any; actions?: MenuAction[]; actions_place?: 'inside' | 'outside'; active?: boolean; keep_open?: boolean; selectable?: boolean; bold?: boolean; italic?: boolean; muted?: boolean; separator?: boolean; align?: 'left'|'center'|'right'}
---@alias MenuOptions {mouse_nav?: boolean;}

-- Internal data structure created from `MenuData`.
---@alias MenuStack {id?: string; type?: string; title?: string; hint?: string; footnote: string; search_style?: 'on_demand' | 'palette' | 'disabled';  item_actions?: MenuAction[]; item_actions_place?: 'inside' | 'outside'; callback?: string[]; selected_index?: number; action_index?: number; keep_open?: boolean; bold?: boolean; italic?: boolean; muted?: boolean; separator?: boolean; align?: 'left'|'center'|'right'; items: MenuStackChild[]; on_search?: string|string[]; on_paste?: string|string[]; on_move?: string|string[]; on_close?: string|string[]; search_debounce?: number|string; search_submenus?: boolean; search_suggestion?: string; search_submit?: boolean; bind_keys?: string[]; parent_menu?: MenuStack; submenu_path: integer[]; active?: boolean; width: number; height: number; top: number; scroll_y: number; scroll_height: number; title_width: number; hint_width: number; max_width: number; is_root?: boolean; fling?: Fling, search?: Search, ass_safe_title?: string}
---@alias MenuStackChild MenuStackItem|MenuStack
---@alias MenuStackItem {title?: string; hint?: string; icon?: string; value: any; actions?: MenuAction[]; actions_place?: 'inside' | 'outside'; active?: boolean; keep_open?: boolean; selectable?: boolean; bold?: boolean; italic?: boolean; muted?: boolean; separator?: boolean; align?: 'left'|'center'|'right'; title_width: number; hint_width: number; ass_safe_hint?: string}
---@alias Fling {y: number, distance: number, time: number, easing: fun(x: number), duration: number, update_cursor?: boolean}
---@alias Search {query: string; cursor: number; timeout: unknown; min_top: number; max_width: number; source: {width: number; top: number; scroll_y: number; selected_index?: integer; items?: MenuStackChild[]}}

---@alias MenuEventActivate {type: 'activate'; index: number; value: any; action?: string; modifiers?: string; alt: boolean; ctrl: boolean; shift: boolean; is_pointer: boolean; keep_open?: boolean; menu_id: string;}
---@alias MenuEventMove {type: 'move'; from_index: number; to_index: number; menu_id: string;}
---@alias MenuEventSearch {type: 'search'; query: string; menu_id: string;}
---@alias MenuEventKey {type: 'key'; id: string; key: string; modifiers?: string; alt: boolean; ctrl: boolean; shift: boolean; menu_id: string; selected_item?: {index: number; value: any; action?: string;}}
---@alias MenuEventPaste {type: 'paste'; value: string; menu_id: string; selected_item?: {index: number; value: any; action?: string;}}
---@alias MenuEventBack {type: 'back';}
---@alias MenuEventClose {type: 'close';}
---@alias MenuEvent MenuEventActivate | MenuEventMove | MenuEventSearch | MenuEventKey | MenuEventPaste | MenuEventBack | MenuEventClose
---@alias MenuCallback fun(data: MenuEvent)

---@class Menu : Element
local Menu = class(Element)

---@param data MenuData
---@param callback MenuCallback
---@param opts? MenuOptions
function Menu:open(data, callback, opts)
	local open_menu = Menu:is_open()
	if open_menu then
		open_menu.is_being_replaced = true
		open_menu:close(true)
	end
	return Menu:new(data, callback, opts)
end

---@param menu_type? string
---@return Menu|nil
function Menu:is_open(menu_type)
	return Elements.menu and (not menu_type or Elements.menu.type == menu_type) and Elements.menu or nil
end

---@param immediate? boolean Close immediately without fadeout animation.
---@param callback? fun() Called after the animation (if any) ends and element is removed and destroyed.
---@overload fun(callback: fun())
function Menu:close(immediate, callback)
	if type(immediate) ~= 'boolean' then callback = immediate end

	local menu = self == Menu and Elements.menu or self

	if state.ime_active == false and mp.get_property_bool('input-ime') then
		mp.set_property_bool('input-ime', false)
	end

	if menu and not menu.destroyed then
		if menu.is_closing then
			menu:tween_stop()
			return
		end

		local function close()
			local on_close = menu.root.on_close -- removed in menu:destroy()
			Elements:remove('menu') -- calls menu:destroy() under the hood
			Elements:update_proximities()
			cursor:queue_autohide()

			-- Call :close() callback
			if callback then callback() end

			-- Call callbacks/events defined on menu config
			local close_event = {type = 'close'}
			if not on_close or menu:command_or_event(on_close, {}, close_event) ~= 'event' then
				menu.callback(close_event)
			end

			request_render()
		end

		menu.is_closing = true

		if immediate then
			close()
		else
			menu:fadeout(close)
		end
	end
end

---@param data MenuData
---@param callback MenuCallback
---@param opts? MenuOptions
---@return Menu
function Menu:new(data, callback, opts) return Class.new(self, data, callback, opts) --[[@as Menu]] end
---@param data MenuData
---@param callback MenuCallback
---@param opts? MenuOptions
function Menu:init(data, callback, opts)
	Element.init(self, 'menu', {render_order = 1001})

	-----@type fun()
	self.callback = callback
	self.opts = opts or {}
	self.anchor = nil -- MOMOKA[menu-anchor]: {x,y} 光标锚点；set 时菜单在光标处弹出、不居中、不压暗幕布。
	self.mouse_nav = self.opts.mouse_nav -- Stops pre-selecting items
	self.item_height = nil
	self.min_width = nil
	self.menu_scale = state.scale -- MOMOKA[menu-style]：菜单自己的缩放（DPI×全屏/最大化 1.3），update_content_dimensions 重算
	self.radius = state.radius -- MOMOKA[menu-style]：菜单面板的圆角基准（同上）
	self.item_spacing = 0 -- MOMOKA[menu-style]：行距在 update_content_dimensions 里按字号重算
	self.item_indent = nil -- MOMOKA[menu-style]：文字左缩进（参考菜单左侧留白显著大于右侧）
	self.item_pad_right = nil -- MOMOKA[menu-style]：右列（hint/▸）距面板右缘的贴边距
	self.hover_inset = nil -- MOMOKA[menu-style]：悬停行四边让空
	self.hover_radius = nil -- MOMOKA[menu-style]：悬停行圆角
	self.item_padding = nil
	self.separator_size = nil
	self.padding = nil
	self.gap = nil
	self.font_size = nil
	self.font_size_hint = nil
	self.scroll_step = nil -- Item height (separators take their own, smaller slot — see update_content_dimensions).
	self.scroll_height = nil -- Items + spacings - container height.
	self.layout = nil -- MOMOKA[menu-style]：可见面板矩形表 {menu, x, y, w, h}（bg 坐标），render/compute_layout 维护
	self.drag_panel_id = nil -- MOMOKA[menu-style]：拖拽起始面板（拖哪条滚哪条）
	self.opacity = 0 -- Used to fade in/out.
	self.type = data.type
	---@type MenuStack Root MenuStack.
	self.root = nil
	---@type MenuStack Current MenuStack.
	self.current = nil
	---@type MenuStack[] All menus in a flat array.
	self.all = nil
	---@type table<string, MenuStack> Map of submenus by their ids, such as `'Tools > Aspect ratio'`.
	self.by_id = {}
	self.type_to_search = options.menu_type_to_search
	self.is_being_replaced = false
	self.is_closing = false
	self.drag_last_y = nil
	self.is_dragging = false

	if utils.shared_script_property_set then
		utils.shared_script_property_set('uosc-menu-type', self.type or 'undefined')
	end
	mp.set_property_native('user-data/uosc/menu/type', self.type or 'undefined')
	-- MOMOKA[menu-anchor] — 宿主推来的画面/选集/版本菜单要像集成模式那样在光标处弹出（右键点哪弹哪），
	-- 不走 uosc 默认的屏幕居中大模态：**在 update(data) 之前**记下打开时的光标位置当锚点 —— 首次量算
	-- （update_dimensions/update_coordinates）就得用它，晚一步菜单会先居中再跳。锚点在时还跳过压暗整屏
	-- 的幕布（见下）。锚点缺席（普通键盘菜单、或光标不可用）时一切照旧居中，无回归。
	if data.momoka_anchor and type(cursor.x) == 'number' and cursor.x ~= math.huge
		and type(cursor.y) == 'number' and cursor.y ~= math.huge then
		self.anchor = {x = cursor.x, y = cursor.y}
	end

	self:update(data)

	for _, menu in ipairs(self.all) do self:scroll_to_index(menu.selected_index, menu.id) end
	if self.mouse_nav then self.current.selected_index = nil end

	self:tween_property('opacity', 0, 1)
	if momoka_clear_shortcuts then momoka_clear_shortcuts() end
	self:enable_key_bindings()
	if not self.anchor then Elements:maybe('curtain', 'register', self.id) end

	if data.search_submit then
		-- We have to defer this so that menu callbacks don't fire before the menu
		-- instance we're constructing here is returned, as they might depend on it.
		mp.add_timeout(0.01, function()
			self:search_submit()
		end)
	end
end

function Menu:destroy()
	Element.destroy(self)
	self.is_closing = false
	if not self.anchor then Elements:maybe('curtain', 'unregister', self.id) end
	if utils.shared_script_property_set then
		utils.shared_script_property_set('uosc-menu-type', nil)
	end
	mp.set_property_native('user-data/uosc/menu/type', nil)
	if not self.is_being_replaced and momoka_refresh_shortcuts then momoka_refresh_shortcuts() end
end

---@param data MenuData
function Menu:update(data)
	local new_root = {is_root = true, submenu_path = {}}
	local new_all = {}
	local new_menus = {} -- menus that didn't exist before this `update()`
	local new_by_id = {}
	local menus_to_serialize = {{new_root, data}}
	local old_current_id = self.current and self.current.id
	local menu_state_props = {'selected_index', 'action_index', 'scroll_y', 'fling', 'search'}
	local internal_props_set = create_set(itable_append({'is_root', 'submenu_path', 'id', 'items'}, menu_state_props))

	table_assign_exclude(new_root, data, internal_props_set)

	local i = 0
	while i < #menus_to_serialize do
		i = i + 1
		local menu, menu_data = menus_to_serialize[i][1], menus_to_serialize[i][2]
		local parent_id = menu.parent_menu and not menu.parent_menu.is_root and menu.parent_menu.id
		if menu_data.id then
			menu.id = menu_data.id
		elseif not menu.is_root then
			menu.id = (parent_id and parent_id .. ' > ' or '') .. (menu_data.title or i)
		else
			menu.id = '{root}'
		end
		menu.icon = 'chevron_right'

		-- Normalize `search_debounce`
		if type(menu_data.search_debounce) == 'number' then
			menu.search_debounce = math.max(0, menu_data.search_debounce --[[@as number]])
		elseif menu_data.search_debounce == 'submit' then
			menu.search_debounce = 'submit'
		else
			menu.search_debounce = menu.on_search and 300 or 0
		end

		-- Update items
		local first_active_index = nil
		menu.items = {
			{title = t('Empty'), value = 'ignore', italic = 'true', muted = 'true', selectable = false, align = 'center'},
		}

		for i, item_data in ipairs(menu_data.items or {}) do
			if item_data.active and not first_active_index then first_active_index = i end

			local item = {}
			table_assign_exclude(item, item_data, internal_props_set)
			if item.keep_open == nil then item.keep_open = menu.keep_open end

			-- Submenu
			if item_data.items then
				item.parent_menu = menu
				item.submenu_path = itable_join(menu.submenu_path, {i})
				menus_to_serialize[#menus_to_serialize + 1] = {item, item_data}
			end

			menu.items[i] = item
		end

		if menu.is_root then menu.selected_index = menu_data.selected_index or first_active_index end

		-- Retain old state
		local old_menu = self.by_id[menu.id]
		if old_menu then
			table_assign_props(menu, old_menu, menu_state_props)
		else
			new_menus[#new_menus + 1] = menu
		end

		new_all[#new_all + 1] = menu
		new_by_id[menu.id] = menu
	end

	self.root, self.all, self.by_id = new_root, new_all, new_by_id
	self.current = self.by_id[old_current_id] or self.root

	self:update_content_dimensions()
	self:reset_navigation()

	-- Ensure palette menus have active searches, and clean empty searches from menus that lost the `palette` flag
	local update_dimensions_again = false
	for _, menu in ipairs(self.all) do
		local is_palette = menu.search_style == 'palette'
		if not menu.search and (is_palette or (menu.search_suggestion and itable_index_of(new_menus, menu))) then
			update_dimensions_again = true
			self:search_init(menu.id)
		elseif not is_palette and menu.search and menu.search.query == '' then
			update_dimensions_again = true
			menu.search = nil
		end
	end
	-- We update before _and_ after because search_inits need the initial un-searched
	-- menu's position and scroll state to save on the `search.source` table.
	if update_dimensions_again then
		self:update_content_dimensions()
		self:reset_navigation()
	end
	-- Apply search suggestions
	for _, menu in ipairs(new_menus) do
		if menu.search_suggestion then
			menu.search.query = menu.search_suggestion
			menu.search.cursor = #menu.search_suggestion
		end
	end
	for _, menu in ipairs(self.all) do
		if menu.search then
			-- the menu items are new objects and the search needs to contain those
			menu.search.source.items = not menu.on_search and menu.items or nil
			-- Only internal searches are immediately submitted
			if not menu.on_search then self:search_internal(menu.id, true) end
		end

		if menu.selected_index then self:select_by_offset(0, menu) end
	end

	self:search_ensure_key_bindings()
end

---@param items MenuDataChild[]
function Menu:update_items(items)
	local data = table_assign({}, self.root)
	data.items = items
	self:update(data)
end

function Menu:update_content_dimensions()
	-- MOMOKA[menu-style] — 尺子整把换成参考项目（C:\mpv_config-2026.08.12）右键菜单的实测值：
	-- 2026-10-07 晚用户令「只复刻界面，不抄功能选项」。字号 20、行高＝字号×(1+gap)＝字号×1.2（行与行
	-- 贴着排，参考图行距/字号≈22/20）；文字左缩进 36、右列贴边 8（参考图左松右紧）；面板不再有最小
	-- 宽度（贴内容）。分隔符占自己的小槽位（上下各让 padding 的 1px 细线，参考图过分隔符行距 32＝
	-- 24＋8），行高因此按前缀和（item_tops/content_height）算，不再假设每行等高。缩放照旧＝
	-- state.scale（hidpi × 全屏/最大化 1.3）——同日早些那道令保留的部分，2026-09-29 那把随窗口高的
	-- 尺子（osd_height/720）不复活。
	local menu_scale = state.scale
	self.menu_scale = menu_scale
	self.font_size = round(options.menu_font_size * menu_scale * options.font_scale)
	self.item_height = round(self.font_size * (1 + options.menu_gap))
	self.item_spacing = 0
	self.min_width = round(options.menu_min_width * menu_scale)
	self.separator_size = round(1 * menu_scale)
	self.scrollbar_size = round(2 * menu_scale)
	self.padding = round(options.menu_padding * menu_scale)
	self.gap = round(2 * menu_scale)
	self.radius = round(options.border_radius * menu_scale)
	self.font_size_hint = self.font_size - 1
	self.item_indent = round(options.menu_item_indent * menu_scale)
	self.item_pad_right = round(options.menu_item_pad_right * menu_scale)
	self.hover_inset = round(options.menu_hover_inset * menu_scale)
	self.hover_radius = round(options.menu_hover_radius * menu_scale)
	self.item_padding = round(4 * menu_scale)
	self.scroll_step = self.item_height + self.item_spacing

	local hint_opts = {size = self.font_size_hint}

	for _, menu in ipairs(self.all) do
		-- Estimate width of a widest item：内容左缘从 item_indent 起算，右缘到 item_pad_right 止
		local max_width = 0
		for _, item in ipairs(menu.items) do
			local icon_width = item.icon and self.font_size or 0
			item.title_width = text_width(item.title, {
				size = self.font_size, bold = item.bold or options.font_bold, italic = item.italic,
			})
			item.hint_width = text_width(item.hint, hint_opts)
			local spacings_in_item = (item.title_width > 0 and 1 or 0)
				+ (item.hint_width > 0 and 1 or 0) + (icon_width > 0 and 1 or 0)
			local estimated_width = self.item_indent + item.title_width + item.hint_width + icon_width
				+ (self.item_padding * spacings_in_item) + self.item_pad_right
			if estimated_width > max_width then max_width = estimated_width end
		end

		menu.max_width = max_width

		-- MOMOKA[menu-style] — 行位置前缀和：普通行高 item_height，分隔符槽位＝1px 线＋上下各让
		-- padding（参考图分隔符上下各 4px 留空）。所有滚动/命中/绘制都改读这张表。
		local item_tops = {}
		local y = 0
		for i, item in ipairs(menu.items) do
			item_tops[i] = y
			y = y + (item.separator and (self.separator_size + self.padding * 2) or self.item_height)
		end
		menu.item_tops = item_tops
		menu.content_height = y
	end

	self:update_dimensions()
end

-- Height of one row (separators take a compact slot; everything else a full item row).
function Menu:row_height(menu, index)
	return menu.items[index].separator and (self.separator_size + self.padding * 2) or self.item_height
end

-- Content-space y of the row containing `y` (nil if below the last row).
function Menu:index_at_y(menu, y)
	local items = menu.items
	for i = 1, #items do
		local top = menu.item_tops[i]
		if y >= top and y < top + self:row_height(menu, i) then return i end
	end
end

function Menu:update_dimensions()
	-- Coordinates and sizes are of the scrollable area. Search input is rendered
	-- above it, so we need to account for that in max_height and ay position.
	-- This is a debt from an era where we had different cursor event handling,
	-- and dumb titles with no search inputs. It could use a refactor.
	local margin = round(self.item_height / 2)
	local external_buttons_reserve = display.width / self.item_height > 14 and self.scroll_step * 6 - margin * 2 or 0
	local width_available = display.width - margin * 2 - self.padding * 2 - external_buttons_reserve
	local height_available = display.height - margin * 2 - self.padding * 2
	local min_width = math.min(self.min_width, width_available)

	for _, menu in ipairs(self.all) do
		local width = math.max(menu.search and menu.search.max_width or 0, menu.max_width)
		menu.width = round(clamp(min_width, width, width_available))
		-- MOMOKA[menu-style] — 标题行整块撤下（参考菜单没有标题），只有搜索输入还在上方占一格；
		-- 脚注随之整块撤下，不再预留高度。
		local title_height = menu.search and self.scroll_step + self.separator_size + 1 or 0
		local max_height = height_available - title_height
		local content_height = menu.content_height or self.scroll_step * #menu.items
		menu.height = math.min(content_height, max_height)
		local min_top = title_height + margin + self.padding
		if menu.is_root and self.anchor then
			-- MOMOKA[menu-anchor] — 上缘贴着光标（搜索框在其上方，留一格 padding）；顶到下边就上移，整menu保持在屏内。
			local max_top = display.height - margin - self.padding - menu.height
			menu.top = clamp(min_top, self.anchor.y + title_height + self.padding, math.max(min_top, max_top))
		else
			menu.top = clamp(
				min_top,
				menu.search and math.min(menu.search.min_top, menu.search.source.top) or height_available,
				round((height_available - menu.height + title_height) / 2)
			)
		end
		if menu.search then
			menu.search.min_top = math.min(menu.search.min_top, menu.top)
			menu.search.max_width = math.max(menu.search.max_width, menu.width)
		end
		menu.scroll_height = math.max(content_height - menu.height, 0)
		self:set_scroll_to(menu.scroll_y, menu.id) -- clamps scroll_y to scroll limits
	end

	self:update_coordinates()
end

-- MOMOKA[menu-style] — 参考菜单的级联布局：根面板在锚点/居中的位置上，子面板贴着父面板右缘
-- （零缝、共用那根白描边）、顶边对齐父项里通往它的那一行，父面板原地不动（用户令 2026-10-07 晚
-- 「只复刻界面」；上游那套「子菜单坐屏幕中、父面板往左排队」的几何退役）。返回可见面板表
-- （{menu, x, y, w, h}，面板 bg 坐标，根在前），最后一级是 current 选中项的子菜单预览。
function Menu:compute_layout()
	local panels = {}
	local root = self.root
	local margin = round(self.item_height / 2)
	local root_title_h = root.search and self.scroll_step + self.separator_size + 1 or 0
	local root_w = root.width + self.padding * 2
	local root_x
	if self.anchor then
		-- MOMOKA[menu-anchor] — 锚点在时左缘贴光标（横向夹住不越屏）；否则照旧屏幕居中。
		root_x = round(clamp(0, self.anchor.x - self.padding, math.max(0, display.width - root_w - self.padding)))
	else
		root_x = round((display.width - root_w) / 2)
	end
	local root_y = (root.top or margin + self.padding) - root_title_h - self.padding
	panels[#panels + 1] = {
		menu = root, x = root_x, y = root_y, w = root_w,
		h = root.height + root_title_h + self.padding * 2,
	}

	-- current 的祖先链（根→current），逐级落位
	local path = {}
	local menu = self.current
	while menu and not menu.is_root do
		path[#path + 1] = menu
		menu = menu.parent_menu
	end
	for i = #path, 1, -1 do
		panels[#panels + 1] = self:place_panel(panels[#panels], path[i].parent_menu, path[i], margin)
	end

	-- current 里悬停/键盘所在行若本身是子菜单 → 预览面板贴着 current 右缘（悬停即预览）
	local selected = self.current.selected_index and self.current.items[self.current.selected_index]
	if selected and selected.items then
		panels[#panels + 1] = self:place_panel(panels[#panels], self.current, selected --[[@as MenuStack]], margin)
	end

	return panels
end

-- 把 child 面板贴着 parent_panel 右缘放下（零缝），顶边对齐 parent 里通往 child 的那一行；
-- 底边越界上收、右侧越界翻到父面板左边（参考实现同一条规则）。
function Menu:place_panel(parent_panel, parent_menu, child, margin)
	local w = child.width + self.padding * 2
	local h = child.height + self.padding * 2
	local x = parent_panel.x + parent_panel.w
	local y = parent_panel.y + self.padding
	if parent_menu then
		local row = itable_index_of(parent_menu.items, child)
		if row and parent_menu.item_tops then
			y = parent_panel.y + self.padding + parent_menu.item_tops[row] - parent_menu.scroll_y
		end
	end
	y = clamp(margin, y, math.max(margin, display.height - margin - self.padding - h))
	if x + w > display.width - margin then
		x = parent_panel.x - w
		if x < 0 then
			x = math.max(0, math.min(parent_panel.x + parent_panel.w, display.width - margin - w))
		end
	end
	return {menu = child, x = x, y = y, w = w, h = h}
end

-- 光标下的可见面板（self.layout 由 compute_layout 维护；从后往前＝后画者在上）。
function Menu:panel_at(x, y)
	local layout = self.layout
	if not layout then return end
	for i = #layout, 1, -1 do
		local panel = layout[i]
		if x >= panel.x and x <= panel.x + panel.w and y >= panel.y and y <= panel.y + panel.h then
			return panel
		end
	end
end

-- Updates element coordinates to the union of the visible panels.
function Menu:update_coordinates()
	local panels = self:compute_layout()
	self.layout = panels
	local ax, ay = panels[1].x, panels[1].y
	local bx, by = panels[1].x + panels[1].w, panels[1].y + panels[1].h
	for i = 2, #panels do
		local panel = panels[i]
		ax, ay = math.min(ax, panel.x), math.min(ay, panel.y)
		bx, by = math.max(bx, panel.x + panel.w), math.max(by, panel.y + panel.h)
	end
	self:set_coordinates(ax, ay, bx, by)
end

function Menu:reset_navigation()
	local menu = self.current

	-- Reset indexes and scroll
	self:set_scroll_to(menu.scroll_y) -- clamps scroll_y to scroll limits
	if menu.items and #menu.items > 0 then
		-- Normalize existing selected_index always, and force it only in keyboard navigation
		if not self.mouse_nav then
			self:select_by_offset(0)
		end
	else
		self:select_index(nil)
	end

	-- Walk up the parent menu chain and activate items that lead to current menu
	local parent = menu.parent_menu
	while parent do
		parent.selected_index = itable_index_of(parent.items, menu)
		menu, parent = parent, parent.parent_menu
	end

	request_render()
end

function Menu:back()
	if not self:is_alive() then return end

	local parent = self.current.parent_menu

	-- MOMOKA[menu-style] — 级联布局里面板位置由父项行钉死，进出子菜单不再有横移动画
	-- （上游的 slide_in_menu/set_offset_x 随旧几何一起退役）。
	if parent then
		self:activate_menu(parent.id)
	else
		self.callback({type = 'back'})
	end
end

function Menu:fadeout(callback) self:tween_property('opacity', 1, 0, callback) end

-- If `menu_id` is provided, will return menu with that id or `nil`. If `menu_id` is `nil`, will return current menu.
---@param menu_id? string
---@return MenuStack | nil
function Menu:get_menu(menu_id) return menu_id == nil and self.current or self.by_id[menu_id] end

function Menu:get_first_active_index(menu_id)
	local menu = self:get_menu(menu_id)
	if not menu then return end
	for index, item in ipairs(menu.items) do
		if item.active then return index end
	end
end

---@param pos? number
---@param menu_id? string
function Menu:set_scroll_to(pos, menu_id)
	local menu = self:get_menu(menu_id)
	if not menu then return end
	menu.scroll_y = clamp(0, pos or 0, menu.scroll_height)
	request_render()
end

---@param delta? number
---@param menu_id? string
function Menu:set_scroll_by(delta, menu_id)
	local menu = self:get_menu(menu_id)
	if not menu then return end
	self:set_scroll_to(menu.scroll_y + delta, menu_id)
end

---@param pos? number
---@param menu_id? string
---@param fling_options? table
function Menu:scroll_to(pos, menu_id, fling_options)
	local menu = self:get_menu(menu_id)
	if not menu then return end
	menu.fling = {
		y = menu.scroll_y,
		distance = clamp(-menu.scroll_y, pos - menu.scroll_y, menu.scroll_height - menu.scroll_y),
		time = mp.get_time(),
		duration = 0.1,
		easing = ease_out_sext,
	}
	if fling_options then table_assign(menu.fling, fling_options) end
	request_render()
end

---@param delta? number
---@param menu_id? string
---@param fling_options? Fling
function Menu:scroll_by(delta, menu_id, fling_options)
	local menu = self:get_menu(menu_id)
	if not menu then return end
	self:scroll_to((menu.fling and (menu.fling.y + menu.fling.distance) or menu.scroll_y) + delta, menu_id, fling_options)
end

---@param index? integer
---@param menu_id? string
---@param immediate? boolean
function Menu:scroll_to_index(index, menu_id, immediate)
	local menu = self:get_menu(menu_id)
	if not menu then return end
	if (index and index >= 1 and index <= #menu.items) then
		-- MOMOKA[menu-style] — 行位置查前缀和表（分隔符占小槽位，行不再等高）
		local position = round((menu.item_tops and menu.item_tops[index] or self.scroll_step * (index - 1))
			- ((menu.height - self.scroll_step) / 2))
		if immediate then
			self:set_scroll_to(position, menu_id)
		else
			self:scroll_to(position, menu_id)
		end
	end
end

---@param index? integer
---@param menu_id? string
function Menu:select_index(index, menu_id)
	local menu = self:get_menu(menu_id)
	if not menu then return end
	menu.selected_index = (index and index >= 1 and index <= #menu.items) and index or nil
	self:select_action(menu.action_index, menu_id) -- normalize selected action index
	request_render()
end

---@param index? integer
---@param menu_id? string
function Menu:select_action(index, menu_id)
	local menu = self:get_menu(menu_id)
	if not menu then return end
	local actions = menu.items[menu.selected_index] and menu.items[menu.selected_index].actions or menu.item_actions
	if not index or not actions or type(actions) ~= 'table' or index < 1 or index > #actions then
		menu.action_index = nil
		return
	end
	menu.action_index = index
	request_render()
end

---@param delta? integer
---@param menu_id? string
function Menu:navigate_action(delta, menu_id)
	local menu = self:get_menu(menu_id)
	if not menu then return end
	local actions = menu.items[menu.selected_index] and menu.items[menu.selected_index].actions or menu.item_actions
	if actions and delta ~= 0 then
		-- Circular navigation where zero gets converted to nil
		local index = (menu.action_index or (delta > 0 and 0 or #actions + 1)) + delta
		self:select_action(index <= #actions and index > 0 and (index - 1) % #actions + 1 or nil, menu_id)
	else
		self:select_action(nil, menu_id)
	end
	request_render()
end

function Menu:next_action() self:navigate_action(1) end
function Menu:prev_action() self:navigate_action(-1) end

---@param value? any
---@param menu_id? string
function Menu:select_value(value, menu_id)
	local menu = self:get_menu(menu_id)
	if not menu then return end
	local index = itable_find(menu.items, function(item) return item.value == value end)
	self:select_index(index)
end

---@param menu_id? string
function Menu:deactivate_items(menu_id)
	local menu = self:get_menu(menu_id)
	if not menu then return end
	for _, item in ipairs(menu.items) do item.active = false end
	request_render()
end

---@param index? integer
---@param menu_id? string
function Menu:activate_index(index, menu_id)
	local menu = self:get_menu(menu_id)
	if not menu then return end
	if index and index >= 1 and index <= #menu.items then menu.items[index].active = true end
	request_render()
end

---@param value? any
---@param menu_id? string
function Menu:activate_value(value, menu_id)
	local menu = self:get_menu(menu_id)
	if not menu then return end
	local index = itable_find(menu.items, function(item) return item.value == value end)
	self:activate_index(index, menu_id)
end

---@param value? any
---@param menu_id? string
function Menu:activate_one_value(value, menu_id)
	local menu = self:get_menu(menu_id)
	if not menu then return end
	local index = itable_find(menu.items, function(item) return item.value == value end)
	self:activate_index(index, menu_id)
end

---@param id string One of menus in `self.all`.
function Menu:activate_menu(id)
	local menu = self:get_menu(id)
	if menu then
		self.current = menu
		self:update_coordinates()
		self:reset_navigation()
		self:search_ensure_key_bindings()
		local parent = menu.parent_menu
		while parent do
			parent.selected_index = itable_index_of(parent.items, menu)
			self:scroll_to_index(parent.selected_index, parent)
			menu, parent = parent, parent.parent_menu
		end
		request_render()
	end
end

---@param index? integer
---@param menu_id? string
function Menu:delete_index(index, menu_id)
	local menu = self:get_menu(menu_id)
	if not menu then return end
	if (index and index >= 1 and index <= #menu.items) then
		table.remove(menu.items, index)
		self:update_content_dimensions()
		self:scroll_to_index(menu.selected_index, menu_id)
	end
end

---@param value? any
---@param menu_id? string
function Menu:delete_value(value, menu_id)
	local menu = self:get_menu(menu_id)
	if not menu then return end
	local index = itable_find(menu.items, function(item) return item.value == value end)
	self:delete_index(index)
end

---@param shortcut? Shortcut
---@param is_pointer? boolean Whether this was called by a pointer.
function Menu:activate_selected_item(shortcut, is_pointer)
	local menu = self.current
	local item = menu.items[menu.selected_index]
	if item then
		-- Is submenu
		if item.items then
			self:activate_menu(item.id)
			-- MOMOKA[menu-style] — 开子菜单时首行即选中（参考截图里子面板第一行就是高亮那格）；
			-- 键盘路径 reset_navigation 已做过同一件事，这里统一再归一一次（幂等）。
			self:select_by_offset(0, item)
			self.opacity = 1
		else
			local actions = item.actions or menu.item_actions
			local action = actions and actions[menu.action_index]
			self.callback({
				type = 'activate',
				index = menu.selected_index,
				value = item.value,
				is_pointer = is_pointer == true,
				action = action and action.name,
				keep_open = item.keep_open or menu.keep_open,
				modifiers = shortcut and shortcut.modifiers or nil,
				alt = shortcut and shortcut.alt or false,
				ctrl = shortcut and shortcut.ctrl or false,
				shift = shortcut and shortcut.shift or false,
				menu_id = menu.id,
			})
		end
	end
end

---@param index integer
function Menu:move_selected_item_to(index)
	if self.current.search then return end -- Moving filtered items is an undefined behavior
	local callback = self.current.on_move
	local from, items_count = self.current.selected_index, self.current.items and #self.current.items or 0
	if callback and from and from ~= index and index >= 1 and index <= items_count then
		local event = {type = 'move', from_index = from, to_index = index, menu_id = self.current.id}
		self:command_or_event(callback, {from, index, self.current.id}, event)
		self:select_index(index, self.current.id)
		self:scroll_to_index(index, self.current.id, true)
	end
end

---@param delta number
function Menu:move_selected_item_by(delta)
	local current_index, items_count = self.current.selected_index, self.current.items and #self.current.items or 0
	if current_index and items_count > 1 then
		local new_index = clamp(1, current_index + delta, items_count)
		if current_index ~= new_index then
			self:move_selected_item_to(new_index)
		end
	end
end

function Menu:on_display() self:update_content_dimensions() end
function Menu:on_prop_fullormaxed() self:update_content_dimensions() end
function Menu:on_options() self:update_content_dimensions() end

-- MOMOKA[menu-style] — 面板感知版选行：光标在哪块可见面板上，就在哪块菜单里选行
-- （父面板/预览子面板都是活面板，参考菜单的用法）。
---@param panel? {menu: MenuStack, x: number, y: number, w: number, h: number}
function Menu:select_from_cursor(panel)
	panel = panel or self:panel_at(cursor.x, cursor.y)
	if not panel then return end
	local menu = panel.menu
	local x, y = cursor.x - panel.x, cursor.y - panel.y - self.padding
	local index = self:index_at_y(menu, y + menu.scroll_y)
	local item = index and menu.items[index]
	self.mouse_nav = true
	if x >= 0 and x <= panel.w and y >= 0 and item and item.selectable ~= false then
		self:select_index(index, menu.id)
	else
		self:select_index(nil, menu.id)
	end
	menu.action_index = nil
end

function Menu:handle_cursor_down()
	if self.proximity_raw <= 0 then
		local panel = self:panel_at(cursor.x, cursor.y)
		if panel then
			-- 点在非当前面板（父面板/预览子面板）上时先把它扶正成当前面板，再选中点的那一行；
			-- 松手时 activate_selected_item 照常走（叶子执行、子菜单开下一层）。
			if panel.menu ~= self.current then self:activate_menu(panel.menu.id) end
			self:select_from_cursor(panel)
		end
		self.drag_last_y = cursor.y
		self.drag_panel_id = panel and panel.menu.id or nil
		self.current.fling = nil
	else
		self:close()
	end
end

---@param shortcut? Shortcut
function Menu:handle_cursor_up(shortcut)
	if self.proximity_raw <= -self.padding and self.drag_last_y and not self.is_dragging then
		-- 只在确实点中某块面板时才激活（联合边界里面板之外的缝隙不算）
		if self.drag_panel_id and self:panel_at(cursor.x, cursor.y) then
			self:select_from_cursor()
			self:activate_selected_item(shortcut, true)
		end
	end
	if self.is_dragging then
		local distance = cursor:get_velocity().y / -3
		if math.abs(distance) > 50 then
			local menu = self:get_menu(self.drag_panel_id)
			if menu then
				menu.fling = {
					y = menu.scroll_y,
					distance = distance,
					time = cursor.history:head().time,
					easing = ease_out_quart,
					duration = 0.5,
					update_cursor = true,
				}
				request_render()
			end
		end
	end
	self.is_dragging = false
	self.drag_last_y = nil
	self.drag_panel_id = nil
end

function Menu:on_global_mouse_leave()
	self.is_dragging = false
	self.drag_last_y = nil
	self.drag_panel_id = nil
	self.current.fling = nil
end

function Menu:on_global_mouse_move()
	if cursor.hidden or cursor.x == math.huge or cursor.y == math.huge then
		self:on_global_mouse_leave()
		return
	end
	self.mouse_nav = true
	if self.drag_last_y then
		self.is_dragging = self.is_dragging or math.abs(cursor.y - self.drag_last_y) >= 10
		if self.is_dragging then
			local distance = self.drag_last_y - cursor.y
			if distance ~= 0 then self:set_scroll_by(distance, self.drag_panel_id) end
			self.drag_last_y = cursor.y
		end
	end
	request_render()
end

-- MOMOKA[menu-style] — 滚轮滚光标底下那块面板（参考菜单每一层都是活面板）；不在面板上时落回当前面板。
function Menu:handle_wheel_up() self:scroll_by(self.scroll_step * -3, self:wheel_panel_id(), {update_cursor = true}) end
function Menu:handle_wheel_down() self:scroll_by(self.scroll_step * 3, self:wheel_panel_id(), {update_cursor = true}) end

function Menu:wheel_panel_id()
	local panel = self:panel_at(cursor.x, cursor.y)
	return panel and panel.menu.id or nil
end

---@param offset integer
---@param menu? MenuStack
function Menu:select_by_offset(offset, menu)
	menu = menu or self.current

	-- Blur selected_index when navigating off bounds and submittable search is active.
	-- Blurred selected_index is an implied focused input, so enter can submit it.
	if menu.search and menu.search_debounce == 'submit' and (
			(menu.selected_index == 1 and offset < 0) or (menu.selected_index == #menu.items and offset > 0)
		) then
		self:select_index(nil, menu.id)
	else
		local index = clamp(1, (menu.selected_index or offset >= 0 and 0 or #menu.items + 1) + offset, #menu.items)
		local prev_index = itable_find(menu.items, function(item) return item.selectable ~= false end, index, 1)
		local next_index = itable_find(menu.items, function(item) return item.selectable ~= false end, index)
		if prev_index and next_index then
			if offset == 0 then
				self:select_index(index - prev_index <= next_index - index and prev_index or next_index, menu.id)
			elseif offset > 0 then
				self:select_index(next_index, menu.id)
			else
				self:select_index(prev_index, menu.id)
			end
		else
			self:select_index(prev_index or next_index or nil, menu.id)
		end
	end

	request_render()
end

---@param offset integer
---@param immediate? boolean
function Menu:navigate_by_items(offset, immediate)
	self:select_by_offset(offset)
	if self.current.selected_index then
		self:scroll_to_index(self.current.selected_index, self.current.id, immediate)
	end
end

---@param offset integer
---@param immediate? boolean
function Menu:navigate_by_page(offset, immediate)
	local items_per_page = round((self.current.height / self.scroll_step) * 0.4)
	self:navigate_by_items(items_per_page * offset, immediate)
end

function Menu:paste()
	local menu = self.current
	local payload = get_clipboard()
	if not payload then return end
	if menu.search then
		self:search_query_insert(payload)
	elseif menu.on_paste then
		local selected_item = menu.items and menu.selected_index and menu.items[menu.selected_index]
		local actions = selected_item and selected_item.actions or menu.item_actions
		local selected_action = actions and menu.action_index and actions[menu.action_index]
		self:command_or_event(menu.on_paste, {payload, menu.id}, {
			type = 'paste',
			value = payload,
			menu_id = menu.id,
			selected_item = selected_item and {
				index = menu.selected_index, value = selected_item.value, action = selected_action,
			},
		})
	elseif menu.search_style ~= 'disabled' then
		self:search_start(menu.id)
		self:search_query_replace(payload, menu.id)
	end
end

---@param menu_id string
---@param no_select_first? boolean
function Menu:search_internal(menu_id, no_select_first)
	local menu = self:get_menu(menu_id)
	if not menu then return end
	local query = menu.search.query:lower()
	if query == '' then
		-- Reset menu state to what it was before search
		for key, value in pairs(menu.search.source) do menu[key] = value end
	else
		-- Inherit `search_submenus` from parent menus
		local search_submenus, parent_menu = menu.search_submenus, menu.parent_menu
		while not search_submenus and parent_menu do
			search_submenus, parent_menu = parent_menu.search_submenus, parent_menu.parent_menu
		end
		menu.items = search_items(menu.search.source.items, query, search_submenus)
		-- Select 1st item in search results
		if not no_select_first then
			menu.scroll_y = 0
			self:select_index(1, menu_id)
		end
	end
	self:update_content_dimensions()
end

---@param items MenuStackChild[]
---@param query string
---@param recursive? boolean
---@param prefix? string
---@return MenuStackChild[]
function search_items(items, query, recursive, prefix)
	local result = {}
	local haystacks = {}
	local flat_items = {}
	local concat = table.concat
	local romanization = need_romanization()

	for _, item in ipairs(items) do
		if item.selectable ~= false then
			local prefixed_title = prefix and prefix .. ' / ' .. (item.title or '') or item.title
			haystacks[#haystacks + 1] = item.title
			flat_items[#flat_items + 1] = item

			if item.items and recursive then
				itable_append(result, search_items(item.items, query, recursive, prefixed_title))
			end
		end
	end

	local seen = {}

	local fuzzy = fzy.filter(query, haystacks, false)
	for _, match in ipairs(fuzzy) do
		local idx, positions, score = match[1], match[2], match[3]
		local matched_title = haystacks[idx]
		local item = flat_items[idx]
		local prefixed_title = prefix and prefix .. ' / ' .. (item.title or '') or item.title

		if item.selectable ~= false and not (item.items and recursive) and not seen[item] then
			local bold = item.bold or options.font_bold
			local font_color = item.active and fgt or bgt
			local ass_safe_title = highlight_match(matched_title, positions, font_color, bold) or nil
			local new_item = table_assign({}, item)
			new_item.title = prefixed_title
			new_item.ass_safe_title = prefix and prefix .. ' / ' .. (ass_safe_title or '') or ass_safe_title
			new_item.score = score
			result[#result + 1] = new_item
			seen[item] = true
		end
	end

	for _, item in ipairs(items) do
		local title = item.title and item.title:lower()
		local hint = item.hint and item.hint:lower()
		local bold = item.bold or options.font_bold
		local font_color = item.active and fgt or bgt
		local ass_safe_title = nil
		local prefixed_title = prefix and prefix .. ' / ' .. (item.title or '') or item.title
		if item.selectable ~= false and not (item.items and recursive) and not seen[item] then
			local score = 0
			local match = false

			if title and romanization then
				local ligature_conv_title, ligature_roman = char_conv(title, true)
				local initials_arr_conv, initials_roman = char_conv(title, false)
				local initials_conv_title = concat(initials(initials_arr_conv))
				if ligature_conv_title:find(query, 1, true) then
					match = true
					score = 1000
					local pos = get_roman_match_positions(title, query, 'ligature', ligature_roman)
					if pos then
						ass_safe_title = highlight_match(item.title, pos, font_color, bold)
					end
				elseif initials_conv_title:find(query, 1, true) then
					match = true
					score = 900
					local pos = get_roman_match_positions(title, query, 'initial', initials_roman)
					if pos then
						ass_safe_title = highlight_match(item.title, pos, font_color, bold)
					end
				end
			end

			if hint and not match then
				if hint:find(query, 1, true) then
					match = true
					score = 100
				elseif concat(initials(hint)):find(query, 1, true) then
					match = true
					score = 90
				end
			end

			if match then
				local new_item = table_assign({}, item)
				new_item.title = prefixed_title
				new_item.ass_safe_title = prefix and prefix .. ' / ' .. (ass_safe_title or '') or ass_safe_title
				new_item.score = score
				result[#result + 1] = new_item
			end
		end
	end

	table.sort(result, function(a, b) return a.score > b.score end)

	return result
end

---@param menu_id? string
function Menu:search_submit(menu_id)
	local menu = self:get_menu(menu_id)
	if not menu or not menu.search then return end
	local callback, query = menu.on_search, menu.search.query
	if callback then
		self:command_or_event(callback, {query, menu.id}, {type = 'search', query = query, menu_id = menu.id})
	else
		self:search_internal(menu.id)
	end
end

-- Move search query cursor by an amount.
---@param amount number `<0` for left, `>0` for right.
---@param word_mode? boolean Move by words/segments. Overwrites amount, but respects its direction.
function Menu:search_cursor_move(amount, word_mode)
	local menu = self:get_menu()
	if not menu or not menu.search then return end
	local query, cursor = menu.search.query, menu.search.cursor
	if word_mode then
		menu.search.cursor = find_string_segment_bound(query, cursor, amount) + (amount < 0 and -1 or 0)
	else
		local move = amount > 0 and utf8_next or utf8_prev
		local step_count = 0
		local limit = math.abs(amount)

		while step_count < limit do
			local next_cursor = move(query, cursor)
			if next_cursor == cursor then break end
			cursor = next_cursor
			step_count = step_count + 1
		end

		menu.search.cursor = clamp(0, cursor, #query)
	end
	request_render()
end

---@param query string
---@param menu_id? string
---@param immediate? boolean
function Menu:search_query_replace(query, menu_id, immediate)
	local menu = self:get_menu(menu_id)
	if not menu or not menu.search then return end
	menu.search.query = query
	menu.search.cursor = #query
	self:search_trigger(menu_id, immediate)
end

-- Insert string into search query at cursor.
---@param str string
---@param menu_id? string
function Menu:search_query_insert(str, menu_id)
	local menu = self:get_menu(menu_id)
	if not menu or not menu.search then return end
	local query, cursor = menu.search.query, menu.search.cursor
	local head, tail = string.sub(query, 1, cursor), string.sub(query, cursor + 1)
	menu.search.query = head .. str .. tail
	menu.search.cursor = cursor + #str
	self:search_trigger(menu_id)
end

-- Trigger menu search callbacks, should be called after any query changes.
function Menu:search_trigger(menu_id, immediate)
	local menu = self:get_menu(menu_id)
	if not menu or not menu.search then return end
	if menu.search_debounce ~= 'submit' then
		if menu.search.timeout then menu.search.timeout:kill() end
		if menu.search.timeout and not immediate then
			menu.search.timeout:resume()
		else
			self:search_submit(menu_id)
		end
	else
		-- `search_debounce='submit'` behavior: We blur selected item when query
		-- changes to let [enter] key submit searches instead of activating items.
		self:select_index(nil, menu.id)
	end
	request_render()
end

---@param event? string
---@param word_mode? boolean Delete by words.
function Menu:search_query_backspace(event, word_mode)
	local search = self.current.search
	if not search then return end

	local cursor, old_query = search.cursor, search.query
	local head, tail = string.sub(old_query, 1, cursor), string.sub(old_query, cursor + 1)

	if word_mode then
		cursor = find_string_segment_bound(head, cursor, -1) - 1
	elseif cursor > 0 then
		-- The while loop is for skipping utf8 continuation bytes
		while cursor > 1 and old_query:byte(cursor) >= 0x80 and old_query:byte(cursor) <= 0xbf do
			cursor = cursor - 1
		end
		cursor = cursor - 1
	end

	local new_query = head:sub(1, cursor) .. tail
	if new_query ~= old_query then
		search.query = new_query
		search.cursor = math.max(0, cursor)
		self:search_trigger()
	end

	if #new_query == 0 then
		local is_palette = self.current.search_style == 'palette'
		if not is_palette and self.type_to_search then
			self:search_cancel()
		elseif is_palette and event ~= 'repeat' then
			self:back()
		end
	end
end

---@param event? string
---@param word_mode? boolean Delete by words.
function Menu:search_query_delete(event, word_mode)
	local search = self.current.search
	if not search then return end

	local cursor, old_query = search.cursor, search.query
	local head, tail = string.sub(old_query, 1, cursor), string.sub(old_query, cursor + 1)
	local tail_cursor = 1

	if word_mode then
		tail_cursor = find_string_segment_bound(tail, 0, 1) + 1
	else
		-- The while loop is for skipping utf8 continuation bytes
		while tail_cursor < #tail and tail:byte(tail_cursor) >= 0x80 and tail:byte(tail_cursor) <= 0xbf do
			tail_cursor = tail_cursor + 1
		end
		tail_cursor = tail_cursor + 1
	end

	local new_query = head .. tail:sub(tail_cursor)
	if new_query ~= old_query then
		search.query = new_query
		search.cursor = #head
		self:search_trigger()
	end

	if #new_query == 0 then
		local is_palette = self.current.search_style == 'palette'
		if not is_palette and self.type_to_search then
			self:search_cancel()
		elseif is_palette and event ~= 'repeat' then
			self:back()
		end
	end
end

function Menu:search_text_input(info)
	local menu = self.current
	if not menu.search and menu.search_style == 'disabled' then return end
	if info.event ~= 'up' then
		local key_text = info.key_text
		if not key_text then
			-- might be KP0 to KP9 or KP_DEC
			key_text = info.key_name:match('KP_?(.+)')
			if not key_text then return end
			if key_text == 'DEC' then key_text = '.' end
		end
		if not menu.search then self:search_start() end
		self:search_query_insert(key_text)
	end
end

---@param menu_id? string
function Menu:search_cancel(menu_id)
	local menu = self:get_menu(menu_id)
	if not menu or not menu.search or menu.search_style == 'palette' then
		self:search_query_replace('', menu_id)
		return
	end
	if state.ime_active == false then
		mp.set_property_bool('input-ime', false)
	end
	self:search_query_replace('', menu_id, true)
	menu.search = nil
	self:search_ensure_key_bindings()
	self:update_dimensions()
	self:reset_navigation()
end

---@param menu_id? string
function Menu:search_init(menu_id)
	local menu = self:get_menu(menu_id)
	if not menu then return end
	if menu.search then return end
	if state.ime_active == false then
		mp.set_property_bool('input-ime', true)
	end
	local timeout
	if menu.search_debounce ~= 'submit' and menu.search_debounce > 0 then
		timeout = mp.add_timeout(menu.search_debounce / 1000, self:create_action(function()
			self:search_submit(menu.id)
		end))
		timeout:kill()
	end
	menu.search = {
		query = '',
		cursor = 0,
		timeout = timeout,
		min_top = menu.top,
		max_width = menu.width,
		source = {
			width = menu.width,
			top = menu.top,
			scroll_y = menu.scroll_y,
			selected_index = menu.selected_index,
			items = not menu.on_search and menu.items or nil,
		},
	}
end

---@param menu_id? string
function Menu:search_start(menu_id)
	local menu = self:get_menu(menu_id)
	if not menu or menu.search_style == 'disabled' then return end
	self:search_init(menu_id)
	self:search_ensure_key_bindings()
	self:update_dimensions()
end

---@param menu_id? string
function Menu:search_clear_query(menu_id)
	local menu = self:get_menu(menu_id)
	if not menu then return end
	if not self.current.search_style == 'palette' and self.type_to_search then
		self:search_cancel(menu_id)
	else
		self:search_query_replace('', menu_id)
	end
end

function Menu:search_enable_key_bindings()
	if self:has_keybindings('search') then return end
	local flags = {repeatable = true, complex = true}
	self:add_key_binding('any_unicode', {self:create_key_handler('search_text_input'), flags}, 'search')
	-- KP0 to KP9 and KP_DEC are not included in any_unicode
	-- despite typically producing characters, they don't have a info.key_text
	self:add_key_binding('kp_dec', {self:create_key_handler('search_text_input'), flags}, 'search')
	for i = 0, 9 do
		self:add_key_binding('kp' .. i, {self:create_key_handler('search_text_input'), flags}, 'search')
	end
end

function Menu:search_ensure_key_bindings()
	if self.current.search or (self.type_to_search and self.current.search_style ~= 'disabled') then
		self:search_enable_key_bindings()
	else
		self:remove_key_bindings('search')
	end
end

function Menu:enable_key_bindings()
	local standalone_keys = {'/', 'kp_divide', 'mbtn_back', 'ctrl+f', 'ctrl+v', 'ctrl+c'}
	if type(self.root.bind_keys) == 'table' then itable_append(standalone_keys, self.root.bind_keys) end
	-- `+` at the end enables `repeatable` flag
	local modifiable_keys = {'up+', 'down+', 'left', 'right', 'enter', 'kp_enter', 'bs', 'tab', 'esc', 'pgup+',
		'pgdwn+', 'home', 'end', 'del'}
	local modifiers = {nil, 'alt', 'alt+ctrl', 'alt+shift', 'alt+ctrl+shift', 'ctrl', 'ctrl+shift', 'shift'}

	---@param shortcut Shortcut
	---@param flags table<string, boolean>
	local function bind(shortcut, flags)
		local handler = self:create_action(function(info) self:handle_shortcut(shortcut, info) end)
		self:add_key_binding(shortcut.id, {handler, flags})
	end

	for i, key in ipairs(standalone_keys) do
		bind(create_shortcut(key), {repeatable = false, complex = true})
	end

	for i, key in ipairs(modifiable_keys) do
		local flags = {repeatable = false, complex = true}

		if key:sub(-1) == '+' then
			key = key:sub(1, -2)
			flags.repeatable = true
		end

		for j = 1, #modifiers do
			bind(create_shortcut(key, modifiers[j]), flags)
		end
	end

	self:search_ensure_key_bindings()
end

-- Handles all key and mouse button shortcuts, except unicode inputs.
---@param shortcut Shortcut
---@param info ComplexBindingInfo
function Menu:handle_shortcut(shortcut, info)
	if not self:is_alive() then return end

	self.mouse_nav = info.is_mouse
	local menu, id, key, modifiers = self.current, shortcut.id, shortcut.key, shortcut.modifiers
	local selected_index = menu.selected_index
	local selected_item = menu and selected_index and menu.items[selected_index]
	local is_submenu = selected_item and selected_item.items ~= nil
	local actions = selected_item and selected_item.actions or menu.item_actions
	local selected_action = actions and menu.action_index and actions[menu.action_index]

	if info.event == 'up' then return end

	function trigger_shortcut(shortcut)
		self.callback(table_assign({}, shortcut, {
			type = 'key',
			menu_id = menu.id,
			selected_item = selected_item and {
				index = selected_index, value = selected_item.value, action = selected_action,
			},
		}))
	end

	if ((key == 'enter' or key == 'kp_enter') and selected_item) or (id == 'right' and is_submenu and not menu.search) then
		self:activate_selected_item(shortcut)
	elseif (id == 'enter' or id == 'kp_enter') and menu.search and menu.search_debounce == 'submit' then
		self:search_submit()
	elseif id == 'up' or id == 'down' then
		self:navigate_by_items(id == 'up' and -1 or 1, true)
	elseif id == 'pgup' or id == 'pgdwn' then
		self:navigate_by_page(id == 'pgup' and -1 or 1)
	elseif menu.search and (id == 'left' or id == 'ctrl+left') then
		self:search_cursor_move(-1, modifiers == 'ctrl')
	elseif menu.search and (id == 'right' or id == 'ctrl+right') then
		self:search_cursor_move(1, modifiers == 'ctrl')
	elseif menu.search and id == 'home' then
		self:search_cursor_move(-math.huge)
	elseif menu.search and id == 'end' then
		self:search_cursor_move(math.huge)
	elseif id == 'home' or id == 'end' then
		self:navigate_by_items(id == 'home' and -math.huge or math.huge)
	elseif id == 'shift+tab' then
		self:prev_action()
	elseif id == 'tab' then
		self:next_action()
	elseif id == 'ctrl+up' then
		self:move_selected_item_by(-1)
	elseif id == 'ctrl+down' then
		self:move_selected_item_by(1)
	elseif id == 'ctrl+pgup' then
		self:move_selected_item_by(-round((menu.height / self.scroll_step) * 0.4))
	elseif id == 'ctrl+pgdwn' then
		self:move_selected_item_by(round((menu.height / self.scroll_step) * 0.4))
	elseif id == 'ctrl+home' then
		self:move_selected_item_by(-math.huge)
	elseif id == 'ctrl+end' then
		self:move_selected_item_by(math.huge)
	elseif id == '/' or id == 'kp_divide' or id == 'ctrl+f' then
		self:search_start()
	elseif key == 'esc' then
		if menu.search and menu.search_style ~= 'palette' then
			self:search_cancel()
		else
			self:close()
		end
	elseif id == 'left' and menu.parent_menu then
		self:back()
	elseif key == 'bs' then
		if menu.search then
			if modifiers == 'shift' then
				self:search_clear_query()
			elseif not modifiers or modifiers == 'ctrl' then
				self:search_query_backspace(info.event, modifiers == 'ctrl')
			end
		elseif not modifiers and info.event ~= 'repeat' then
			self:back()
		end
	elseif menu.search and (id == 'del' or id == 'ctrl+del' or id == 'shift+del') then
		if id == 'shift+del' then
			-- During search `del` edits the string. We convert `shift+del` to
			-- `del` to have a way to trigger menu callbacks bound to `del`.
			trigger_shortcut(create_shortcut('del'))
		else
			self:search_query_delete(info.event, modifiers == 'ctrl')
		end
	elseif key == 'mbtn_back' then
		self:back()
	elseif id == 'ctrl+v' then
		self:paste()
	else
		trigger_shortcut(shortcut)
	end
end

-- Check if menu is not closed or closing.
function Menu:is_alive() return not self.is_closing and not self.destroyed end

---@param name string
function Menu:create_key_handler(name)
	return self:create_action(function(...)
		self.mouse_nav = false
		self:maybe(name, ...)
	end)
end

-- Sends command with params, or triggers a callback event if `command == 'callback'`.
-- Intended to handle `on_{event}: 'callback' | string | string[]` events.
-- Returns what happened.
---@param command string|number|string[]|number[]
---@param params string[]|number[]
---@param event MenuEvent
---@return 'event' | 'command' | nil
function Menu:command_or_event(command, params, event)
	if command == 'callback' then
		self.callback(event)
		return 'event'
	elseif type(command) == 'table' then
		---@diagnostic disable-next-line: deprecated
		mp.command_native(itable_join(command, params))
		return 'command'
	elseif type(command) == 'string' then
		mp.command(command .. ' ' .. table.concat(params, ' '))
		return 'command'
	end
	return nil
end

-- MOMOKA[menu-style] — 参考菜单的渲染：每帧按 compute_layout 画全部可见面板（根、祖先链、
-- current 选中项的子菜单预览），元素边界＝面板并集；无标题/脚注/左缘指示条；悬停行 #353535
-- 白字（左右各让 hover_inset、圆角 hover_radius）；分隔符＝通宽 1px 细线（menu_separator_color）
-- 坐在自己的小槽位正中；hint 列全亮白、右缘贴 item_pad_right；文字左缘从 item_indent 起算
-- （参考图左松右紧）。用户令 2026-10-07 晚「只复刻界面，不抄功能选项」。
function Menu:render()
	for _, menu in ipairs(self.all) do
		if menu.fling then
			local time_delta = state.render_last_time - menu.fling.time
			local progress = menu.fling.easing(math.min(time_delta / menu.fling.duration, 1))
			self:set_scroll_to(round(menu.fling.y + menu.fling.distance * progress), menu.id)
			if progress < 1 then request_render() else menu.fling = nil end
		end
	end

	cursor:zone('primary_down', display, self:create_action(function() self:handle_cursor_down() end))
	cursor:zone('primary_up', display, self:create_action(function(shortcut) self:handle_cursor_up(shortcut) end))
	cursor:zone('wheel_down', self, function() self:handle_wheel_down() end)
	cursor:zone('wheel_up', self, function() self:handle_wheel_up() end)

	-- 布局每帧重算（悬停/滚动会挪预览子面板），元素边界随之＝可见面板的并集
	self.layout = self:compute_layout()
	local uax, uay, ubx, uby = math.huge, math.huge, -math.huge, -math.huge
	for _, panel in ipairs(self.layout) do
		uax, uay = math.min(uax, panel.x), math.min(uay, panel.y)
		ubx, uby = math.max(ubx, panel.x + panel.w), math.max(uby, panel.y + panel.h)
	end
	self:set_coordinates(uax, uay, ubx, uby)

	local ass = assdraw.ass_new()

	for _, panel in ipairs(self.layout) do
		self:draw_panel(ass, panel)
	end

	return ass
end

---@param panel {menu: MenuStack, x: number, y: number, w: number, h: number}
function Menu:draw_panel(ass, panel)
	local menu = panel.menu
	local is_current = menu == self.current
	local menu_opacity = self.opacity
	local icon_size = self.font_size
	local title_h = menu.search and self.scroll_step + self.separator_size + 1 or 0
	-- Scrollable content area coordinates（搜索框在其上方，占一格 title_h）
	local content_rect = {
		ax = panel.x + self.padding,
		ay = panel.y + self.padding + title_h,
		bx = panel.x + panel.w - self.padding,
		by = panel.y + panel.h - self.padding,
	}
	local scroll_clip = '\\clip(0,' .. content_rect.ay .. ',' .. display.width .. ',' .. content_rect.by .. ')'

	-- Background
	ass:rect(panel.x, panel.y, panel.x + panel.w, panel.y + panel.h, {
		color = options.menu_background_color,
		opacity = menu_opacity,
		radius = options.menu_corner_radius > 0 and options.menu_corner_radius * self.menu_scale or 0,
		border = options.menu_outline_size > 0 and options.menu_outline_size * self.menu_scale or nil,
		border_color = options.menu_outline_color,
	})

	-- Scrollbar
	if menu.scroll_height > 0 then
		local groove_height = menu.height - 2
		local thumb_height = math.max((menu.height / (menu.scroll_height + menu.height)) * groove_height, 40)
		local thumb_y = content_rect.ay + 1 + ((menu.scroll_y / menu.scroll_height) * (groove_height - thumb_height))
		local sax = content_rect.bx - round(self.scrollbar_size / 2)
		local sbx = sax + self.scrollbar_size
		ass:rect(sax, thumb_y, sbx, thumb_y + thumb_height, {color = fg, opacity = menu_opacity * 0.8})
	end

	-- Visible rows（前缀和找可见窗口；分隔符占小槽位，行不再等高）
	local items = menu.items
	local start_index, end_index = 1, 0
	while start_index <= #items
		and menu.item_tops[start_index] + self:row_height(menu, start_index) <= menu.scroll_y do
		start_index = start_index + 1
	end
	end_index = start_index
	while end_index <= #items and menu.item_tops[end_index] < menu.scroll_y + menu.height do
		end_index = end_index + 1
	end

	local blur_selected_index = self.mouse_nav and is_current
	local blur_action_index = self.mouse_nav and menu.action_index ~= nil
	---@type MenuAction|nil
	local selected_action
	-- 悬停子菜单的预览面板（layout 末位且父级是本面板）：光标朝它去/停在它上面时，本面板的
	-- 悬停行不被吹掉、也不被路过的行抢走（上游同一条守卫）。面板表是 x/y/w/h，几何函数要 ax/ay/bx/by。
	local preview_rect
	if is_current and self.layout then
		local last = self.layout[#self.layout]
		if last and last.menu ~= menu and last.menu.parent_menu == menu then
			preview_rect = {ax = last.x, ay = last.y, bx = last.x + last.w, by = last.y + last.h}
		end
	end

	for index = start_index, end_index, 1 do
		local item = items[index]
		if not item then break end -- end_index deliberately overshoots by one
		local item_ay = content_rect.ay + menu.item_tops[index] - menu.scroll_y
		local item_height = self:row_height(menu, index)
		local item_by = item_ay + item_height

		-- Separator
		-- MOMOKA[menu-style] — 通宽 1px 细线坐在槽位正中（参考图实测线色 ≈ #3E3E3E、上下各让 4px）
		if item.separator then
			if item_by <= content_rect.by then
				local line_ay = round(item_ay + item_height / 2 - self.separator_size / 2)
				ass:rect(panel.x, line_ay, panel.x + panel.w, line_ay + self.separator_size, {
					color = options.menu_separator_color, opacity = menu_opacity, clip = scroll_clip,
				})
			end
		else
			local item_center_y = item_ay + item_height / 2
			local item_clip = (item_ay < content_rect.ay or item_by > content_rect.by) and scroll_clip or nil
			local is_selected = menu.selected_index == index
			-- MOMOKA[menu-style] — 文字左缘从 item_indent 起算，右列（hint/▸）贴 item_pad_right
			local text_ax, text_bx = panel.x + self.item_indent, panel.x + panel.w - self.item_pad_right
			local font_color = is_selected and options.menu_focused_color or item.active and fgt or bgt
			local actions = is_selected and (item.actions or menu.item_actions) -- not nil = actions are visible
			local action = actions and actions[menu.action_index] -- not nil = action is selected

			if action then selected_action = action end

			-- Hover/active row highlight
			-- MOMOKA[menu-style] — 悬停/键盘所在行＝#353535 浅灰底、**字色保持白**（参考图悬停行
			-- 实测 53 底 255 字）；active 行（当前值）照旧 fg@0.8＋深字，叠加时取悬停底色。
			local highlight_opacity = (item.active and 0.8 or 0) + (is_selected and 1 or 0)
			if highlight_opacity > 0 then
				ass:rect(panel.x + self.hover_inset, item_ay, panel.x + panel.w - self.hover_inset, item_by, {
					radius = self.hover_radius,
					color = is_selected and options.menu_focused_back_color or fg,
					opacity = math.min(highlight_opacity, 1) * menu_opacity,
					clip = item_clip,
				})
			end

			local title_clip_bx = text_bx

			-- Actions（宿主菜单不用；uosc 内部流程保留）
			local actions_rect
			if is_selected and actions and #actions > 0 and not item.items then
				local place = item.actions_place or menu.item_actions_place
				local margin = self.gap * 2
				local size = item_by - item_ay - margin * 2
				local rect_width = size * #actions + margin * (#actions - 1)

				-- Place actions outside of menu when requested and there's enough space for it
				actions_rect = {
					ay = item_ay + margin,
					by = item_by - margin,
					is_outside = place == 'outside' and display.width - panel.x - panel.w + margin * 2 > rect_width,
				}
				actions_rect.bx = actions_rect.is_outside and panel.x + panel.w + margin + rect_width or
					text_bx - margin
				actions_rect.ax = actions_rect.bx

				for i = 1, #actions, 1 do
					local action_index = #actions - (i - 1)
					local action = actions[action_index]

					-- Hide when the action shouldn't be displayed when the item is a result of a search/filter
					if not (action.filter_hidden and menu.search) then
						local is_active = action_index == menu.action_index
						local bx = actions_rect.ax - (i == 1 and 0 or margin)
						item.action_inputs = item.action_inputs or {}
						item.action_inputs[action_index] = item.action_inputs[action_index] or {}
						local rect = {
							ay = actions_rect.ay,
							by = actions_rect.by,
							ax = bx - size,
							bx = bx,
							input_owner = item.action_inputs[action_index],
						}
						actions_rect.ax = rect.ax

						ass:rect(rect.ax, rect.ay, rect.bx, rect.by, {
							radius = self.radius > 2 and self.radius - 1 or self.radius,
							color = is_active and fg or bg,
							border = is_active and self.gap or nil,
							border_color = bg,
							opacity = menu_opacity,
							clip = item_clip,
						})
						ass:icon(rect.ax + size / 2, rect.ay + size / 2, size * 0.66, action.icon, {
							color = is_active and bg or fg, opacity = menu_opacity, clip = item_clip,
						})

						-- Re-use rect as a hitbox by growing it so it bridges gaps to prevent flickering
						rect.ay, rect.by, rect.bx = item_ay, item_ay + self.scroll_step, rect.bx + margin

						cursor:zone('primary_click', rect, self:create_action(function(shortcut)
							self.mouse_nav = true
							menu.selected_index = index
							menu.action_index = action_index
							self:activate_selected_item(shortcut, true)
						end))
						-- Select action on cursor hover
						if self.mouse_nav and get_point_to_rectangle_proximity(cursor, rect) <= 0 then
							blur_action_index = false
							if not is_active then
								menu.action_index = action_index
								selected_action = actions[action_index]
								request_render()
							end
						end
					end
				end

				title_clip_bx = actions_rect.ax - self.gap * 2
			end

			-- Icon（子菜单的 ▸、错误 spinner 等）
			if item.icon then
				if not actions_rect or actions_rect.is_outside then
					local x = (not item.title and not item.hint and item.align == 'center')
						and panel.x + panel.w / 2
						or text_bx - (icon_size / 2)
					if item.icon == 'spinner' then
						ass:spinner(x, item_center_y, icon_size * 1.5, {color = font_color, opacity = menu_opacity * 0.8})
					else
						ass:icon(x, item_center_y, icon_size * 1.5, item.icon, {
							color = font_color, opacity = menu_opacity, clip = item_clip,
						})
					end
				end
				text_bx = text_bx - icon_size - self.item_padding
				title_clip_bx = math.min(text_bx, title_clip_bx)
			end

			local hint_clip_bx = title_clip_bx
			if item.hint_width > 0 then
				-- controls title & hint clipping proportional to the ratio of their widths
				-- both title and hint get at least 50% of the width, unless they are smaller then that
				local width = text_bx - text_ax - self.item_padding
				local title_min = math.min(item.title_width, width * 0.5)
				local hint_min = math.min(item.hint_width, width * 0.5)
				local title_ratio = item.title_width / (item.title_width + item.hint_width)
				title_clip_bx = math.min(
					title_clip_bx,
					round(text_ax + clamp(title_min, width * title_ratio, width - hint_min))
				)
			end

			-- Hint
			-- MOMOKA[menu-style] — 快捷键/动态值那列全亮白（参考图 b/q/PGUP 与正文同色），右缘贴边
			if item.hint then
				item.ass_safe_hint = item.ass_safe_hint or ass_escape(item.hint)
				local clip = '\\clip(' .. title_clip_bx + self.item_padding .. ','
					.. math.max(item_ay, content_rect.ay) .. ',' .. hint_clip_bx .. ','
					.. math.min(item_by, content_rect.by) .. ')'
				ass:txt(text_bx, item_center_y, 6, item.ass_safe_hint, {
					size = self.font_size_hint,
					color = font_color,
					wrap = 2,
					opacity = menu_opacity,
					clip = clip,
				})
			end

			-- Title
			if item.title then
				item.ass_safe_title = item.ass_safe_title or ass_escape(item.title)
				local clip = '\\clip(' .. text_ax .. ',' .. math.max(item_ay, content_rect.ay) .. ','
					.. title_clip_bx .. ',' .. math.min(item_by, content_rect.by) .. ')'
				local title_x, align = text_ax, 4
				if item.align == 'right' then
					title_x, align = title_clip_bx, 6
				elseif item.align == 'center' then
					title_x, align = text_ax + (title_clip_bx - text_ax) / 2, 5
				end
				ass:txt(title_x, item_center_y, align, item.ass_safe_title, {
					size = self.font_size,
					color = font_color,
					italic = item.italic,
					bold = item.bold,
					wrap = 2,
					opacity = menu_opacity * (item.muted and 0.5 or 1),
					clip = clip,
				})
			end

			-- Select hovered item
			-- MOMOKA[menu-style] — 每块可见面板都是活面板：光标停在哪块上，哪块的行跟着亮
			-- （父面板高亮不吹掉，参考图「导航＋子面板首行」双高亮）；当前面板保留上游的
			-- 吹掉（悬空白处取消选中）与去程守卫（朝预览子面板走时不换行）。
			local item_rect_hitbox = {
				ax = panel.x,
				ay = math.max(item_ay, panel.y),
				bx = panel.x + panel.w - (item.items and 0 or self.hover_inset),
				by = math.min(item_ay + item_height, panel.y + panel.h),
			}
			if self.mouse_nav and item.selectable ~= false then
				if is_current then
					if preview_rect and cursor:direction_to_rectangle_distance(preview_rect)
						or actions_rect and actions_rect.is_outside and cursor:direction_to_rectangle_distance(actions_rect) then
						blur_selected_index = false
					else
						if get_point_to_rectangle_proximity(cursor, item_rect_hitbox) <= 0 then
							blur_selected_index = false
							if menu.selected_index ~= index then
								menu.selected_index = index
								request_render()
							end
						end
					end
				elseif get_point_to_rectangle_proximity(cursor, item_rect_hitbox) <= 0 then
					if menu.selected_index ~= index then
						menu.selected_index = index
						request_render()
					end
				end
			end
		end
	end

	-- We are in mouse nav and cursor isn't hovering any row of the current panel
	-- MOMOKA[menu-style] — 只在光标确实待在本面板里扫空时才吹掉选中；光标在别的面板上（比如
	-- 子菜单开着、光标停在父面板那行）时保留 —— 参考图「父行＋子面板首行」两行同灰就是这条。
	if blur_selected_index and self:panel_at(cursor.x, cursor.y) == panel then
		menu.selected_index = nil
	end
	if blur_action_index then
		menu.action_index = nil
		request_render()
	end

	-- Search input（标题行整块撤下后的唯一上方部件；Momoka 菜单 type_to_search=no，仅 '/' 呼出）
	if menu.search then
		local title_height = self.item_height + self.padding - 3
		local requires_submit = menu.search_debounce == 'submit'
		local rect = {
			ax = content_rect.ax,
			ay = content_rect.ay - self.scroll_step - self.separator_size - 1,
			bx = content_rect.bx,
			by = content_rect.ay - self.separator_size - 1,
		}
		-- Centers
		rect.cx, rect.cy = round(rect.ax + (rect.bx - rect.ax) / 2), round(rect.ay + (rect.by - rect.ay) / 2)

		if menu.title and not menu.ass_safe_title then
			menu.ass_safe_title = ass_escape(menu.title)
		end

		-- Background
		ass:rect(content_rect.ax + 3, rect.ay + 3, content_rect.bx - 3, rect.ay + title_height - 1, {
			color = fg .. '\\1a&HFF', opacity = menu_opacity * 0.1,
			radius = self.radius > 0 and self.radius + self.padding or 0,
			border = 1, border_color = fg, border_opacity = menu_opacity * 0.8
		})
		ass:texture(content_rect.ax + 3, rect.ay + 3, content_rect.bx - 3, rect.ay + title_height - 1, 'n', {
			size = 80, color = bg, opacity = menu_opacity * 0.1, anchor_x = content_rect.ax + 2, anchor_y = rect.ay + 2,
		})

		-- Separator
		ass:rect(
			rect.ax, rect.by, rect.bx, rect.by + self.separator_size, {color = fg, opacity = menu_opacity * 0.2}
		)

		-- Blur selection (also activates search input) when user clicks title
		if is_current then
			cursor:zone('primary_down', rect, function()
				self:select_index(nil)
			end)
		end

		do
			-- Icon
			local icon_size, icon_opacity = self.font_size * 1.3, menu_opacity * (requires_submit and 0.5 or 1)
			local icon_rect = {
				ax = rect.ax,
				ay = rect.ay,
				bx = content_rect.ax + icon_size + self.item_padding * 1.5,
				by = rect.by,
			}

			if is_current and requires_submit then
				cursor:zone('primary_down', icon_rect, function() self:search_submit() end)
				if get_point_to_rectangle_proximity(cursor, icon_rect) <= 0 then
					icon_opacity = menu_opacity
				end
			end

			ass:icon(rect.ax + icon_size / 2, rect.cy, icon_size, 'search', {
				color = fg,
				opacity = icon_opacity,
				clip = '\\clip(' ..
					icon_rect.ax .. ',' .. icon_rect.ay .. ',' .. icon_rect.bx .. ',' .. icon_rect.by .. ')',
			})

			-- Query/Placeholder
			local cursor_height_half, cursor_thickness = round(self.font_size * 0.6), round(self.font_size / 12)
			local cursor_ax = rect.bx + 1
			if menu.search.query ~= '' then
				local opts = {
					size = self.font_size,
					color = bgt,
					wrap = 2,
					opacity = menu_opacity,
					clip = '\\clip(' .. icon_rect.bx .. ',' .. rect.ay .. ',' .. rect.bx .. ',' .. rect.by .. ')',
				}
				local query, cursor = menu.search.query, menu.search.cursor
				-- Add a ZWNBSP suffix to prevent libass from trimming trailing spaces
				local head = ass_escape(string.sub(query, 1, cursor)) .. '\239\187\191'
				local tail_no_escape = string.sub(query, cursor + 1)
				local tail = ass_escape(tail_no_escape) .. '\239\187\191'
				cursor_ax = math.max(round(cursor_ax - text_width(tail_no_escape, opts)), rect.cx)
				ass:txt(cursor_ax, rect.cy, 6, head, opts)
				ass:txt(cursor_ax, rect.cy, 4, tail, opts)
			else
				local placeholder = (menu.search_style == 'palette' and menu.ass_safe_title)
					and menu.ass_safe_title
					or (requires_submit and t('type & ctrl+enter to search') or t('type to search'))
				ass:txt(rect.bx, rect.cy, 6, placeholder, {
					size = self.font_size,
					italic = true,
					color = bgt,
					wrap = 2,
					opacity = menu_opacity * 0.4,
					clip = '\\clip(' .. rect.ax .. ',' .. rect.ay .. ',' .. rect.bx .. ',' .. rect.by .. ')',
				})
			end

			-- Selected input indicator for submittable searches.
			-- (input is selected when `selected_index` is `nil`)
			if menu.search_debounce == 'submit' and not menu.selected_index then
				local size_half = round(1 * self.menu_scale)
				ass:rect(
					content_rect.ax, rect.by - size_half, content_rect.bx, rect.by + size_half,
					{color = fg, opacity = menu_opacity}
				)
			end
			local input_is_blurred = menu.search_debounce == 'submit' and menu.selected_index

			-- Cursor
			local cursor_bx = cursor_ax + cursor_thickness
			ass:rect(cursor_ax, rect.cy - cursor_height_half, cursor_bx, rect.cy + cursor_height_half, {
				color = fg,
				opacity = menu_opacity * (input_is_blurred and 0.5 or 1),
				clip = '\\clip(' .. cursor_ax .. ',' .. rect.ay .. ',' .. cursor_bx .. ',' .. rect.by .. ')',
			})
		end
	end
end

return Menu
