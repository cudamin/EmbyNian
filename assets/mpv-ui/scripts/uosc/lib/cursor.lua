---@alias CursorEventHandler fun(shortcut: Shortcut)

local cursor = {
	x = math.huge,
	y = math.huge,
	hidden = true,
	disabled = false,
	disablers = {}, -- List of ids. When not empty, the cursor handling is disabled.
	distance = 0, -- Distance traveled during current move. Reset by `cursor.distance_reset_timer`.
	last_hover = false, -- Stores `mouse.hover` boolean of the last mouse event for enter/leave detection.
	-- Event handlers that are only fired on zones defined during render loop.
	---@type {event: string, hitbox: Hitbox; handler: CursorEventHandler}[]
	zones = {},
	handlers = {
		primary_down = {},
		primary_up = {},
		secondary_down = {},
		secondary_up = {},
		wheel_down = {},
		wheel_up = {},
		move = {},
	},
	first_real_mouse_move_received = false,
	history = CircularBuffer:new(10),
	autohide_fs_only = nil,
	-- Tracks current key binding levels for each event. 0: disabled, 1: enabled, 2: enabled + window dragging prevented
	binding_levels = {
		mbtn_left = 0,
		mbtn_left_dbl = 0,
		mbtn_right = 0,
		wheel = 0,
	},
	is_dragging_prevented = false,
	event_forward_map = {
		primary_down = 'MBTN_LEFT',
		primary_up = 'MBTN_LEFT',
		secondary_down = 'MBTN_RIGHT',
		secondary_up = 'MBTN_RIGHT',
		wheel_down = 'WHEEL_DOWN',
		wheel_up = 'WHEEL_UP',
	},
	event_binding_map = {
		primary_down = 'mbtn_left',
		primary_up = 'mbtn_left',
		primary_click = 'mbtn_left',
		secondary_down = 'mbtn_right',
		secondary_up = 'mbtn_right',
		secondary_click = 'mbtn_right',
		wheel_down = 'wheel',
		wheel_up = 'wheel',
	},
	window_dragging_blockers = create_set({'primary_click', 'primary_down'}),
	event_propagation_blockers = {
		primary_down = 'primary_click',
		primary_click = 'primary_down',
		secondary_down = 'secondary_click',
		secondary_click = 'secondary_down',
	},
	event_meta = {
		primary_down = {is_start = true, trigger_event = 'primary_click'},
		primary_up = {is_end = true, start_event = 'primary_down', trigger_event = 'primary_click'},
		secondary_down = {is_start = true, trigger_event = 'secondary_click'},
		secondary_up = {is_end = true, start_event = 'secondary_down', trigger_event = 'secondary_click'},
	},
	-- Holds positions and times of starting events (events that start compound events like click).
	---@type {[string]: {x: number, y: number, time: number, zone_handled: boolean}}
	last_events = {},
}

cursor.autohide_timer = mp.add_timeout(1, function() cursor:autohide() end)
cursor.autohide_timer:kill()
mp.observe_property('cursor-autohide', 'number', function(_, val)
	cursor.autohide_timer.timeout = (val or 1000) / 1000
end)

cursor.distance_reset_timer = mp.add_timeout(0.2, function()
	cursor.distance = 0
	request_render()
end)
cursor.distance_reset_timer:kill()

-- Called at the beginning of each render
function cursor:clear_zones()
	itable_clear(self.zones)
end

---@param hitbox Hitbox
function cursor:collides_with(hitbox)
	return point_collides_with(self, hitbox)
end

-- Returns zone for event at current cursor position.
---@param event string
function cursor:find_zone(event)
	-- Premature optimization to ignore a high frequency event that is not needed as a zone atm.
	if event == 'move' then return end

	for i = #self.zones, 1, -1 do
		local zone = self.zones[i]
		local is_blocking_only = zone.event == self.event_propagation_blockers[event]
		if (zone.event == event or is_blocking_only) and self:collides_with(zone.hitbox) then
			return not is_blocking_only and zone or nil
		end
	end
end

-- Defines an event zone for a hitbox on currently rendered screen. Available events:
-- - primary_down, primary_up, primary_click, secondary_down, secondary_up, secondary_click, wheel_down, wheel_up
--
-- Notes:
-- - Zones are cleared on beginning of every `render()`, and need to be rebound.
-- - One event type per zone: only the last bound zone per event gets triggered.
-- - In current implementation, you have to choose between `_click` or `_down`. Binding both makes only the last bound fire.
-- - Primary `_down` and `_click` disable dragging. Define `window_drag = true` on hitbox to re-enable.
-- - Anything that disables dragging also implicitly disables cursor autohide.
-- - `move` event zones are ignored due to it being a high frequency event that is currently not needed as a zone.
---@param event string
---@param hitbox Hitbox
---@param callback CursorEventHandler
function cursor:zone(event, hitbox, callback)
	self.zones[#self.zones + 1] = {event = event, hitbox = hitbox, handler = callback}
end

-- Binds a permanent cursor event handler active until manually unbound using `cursor:off()`.
-- `_click` events are not available as permanent global events, only as zones.
---@param event string
---@param callback CursorEventHandler
---@return fun() disposer Unbinds the event.
function cursor:on(event, callback)
	if self.handlers[event] and not itable_index_of(self.handlers[event], callback) then
		self.handlers[event][#self.handlers[event] + 1] = callback
		self:decide_keybinds()
	end
	return function() self:off(event, callback) end
end

-- Unbinds a cursor event handler.
---@param event string
function cursor:off(event, callback)
	if self.handlers[event] then
		local index = itable_index_of(self.handlers[event], callback)
		if index then
			table.remove(self.handlers[event], index)
			self:decide_keybinds()
		end
	end
end

-- Binds a cursor event handler to be called once.
---@param event string
function cursor:once(event, callback)
	local function callback_wrap()
		callback()
		self:off(event, callback_wrap)
	end
	return self:on(event, callback_wrap)
end

-- Trigger the event.
---@param event string
---@param shortcut? Shortcut
function cursor:trigger(event, shortcut)
	local forward, zone_handled = true, false

	-- Call raw event handlers.
	local zone = self:find_zone(event)
	local callbacks = self.handlers[event]
	if zone or #callbacks > 0 then
		forward = false
		if zone and shortcut then
			zone.handler(shortcut)
			zone_handled = true
		end
		for _, callback in ipairs(callbacks) do callback(shortcut) end
	end

	if event ~= 'move' then
		-- Call compound/parent (click) event handlers if both start and end events are within `parent_zone.hitbox`.
		local meta = self.event_meta[event]
		if meta then
			-- Trigger compound event
			local parent_zone = self:find_zone(meta.trigger_event)
			if parent_zone then
				forward = false -- Canceled here so we don't forward down events if they can lead to a click.
				if meta.is_end then
					local start_event = self.last_events[meta.start_event]
					if start_event and point_collides_with(start_event, parent_zone.hitbox) and shortcut then
						parent_zone.handler(create_shortcut('primary_click', shortcut.modifiers))
					end
				end
			end
		end

		-- Forward unhandled events.
		if forward then
			local forward_name = self.event_forward_map[event]
			local last_down = meta and meta.is_end and self.last_events[meta.start_event]
			local down_zone_handled = last_down and last_down.zone_handled
			if forward_name and not down_zone_handled then
				-- Forward events if there was no handler.
				local active = find_active_keybindings(forward_name)
				if active and active.cmd then
					local is_wheel = event:find('wheel', 1, true)
					local is_up = event:sub(-3) == '_up'
					if active.owner then
						-- Binding belongs to other script, so make it look like regular key event.
						-- Mouse bindings are simple, other keys would require repeat and pressed handling,
						-- which can't be done with mp.set_key_bindings(), but is possible with mp.add_key_binding().
						local state = is_wheel and 'pm' or is_up and 'um' or 'dm'
						local name = active.cmd:sub(active.cmd:find('/') + 1, -1)
						mp.commandv('script-message-to', active.owner, 'key-binding', name, state, forward_name)
					elseif is_wheel or is_up then
						-- input.conf binding, react to button release for mouse buttons
						mp.command(active.cmd)
					end
				end
			end
		end
	end

	-- Track last events
	local last = self.last_events[event] or {}
	last.x, last.y, last.time, last.zone_handled = self.x, self.y, mp.get_time(), zone_handled
	self.last_events[event] = last

	-- Refresh cursor autohide timer.
	self:queue_autohide()
end

-- Enables or disables keybinding groups based on what event listeners are bound.
function cursor:decide_keybinds()
	local new_levels = {mbtn_left = 0, mbtn_right = 0, wheel = 0}
	self.is_dragging_prevented = false

	-- Check global events.
	for name, handlers in ipairs(self.handlers) do
		local binding = self.event_binding_map[name]
		if binding then
			new_levels[binding] = math.max(new_levels[binding], #handlers > 0 and 1 or 0)
		end
	end

	-- Check zones.
	for _, zone in ipairs(self.zones) do
		local binding = self.event_binding_map[zone.event]
		if binding and cursor:collides_with(zone.hitbox) then
			local new_level = (self.window_dragging_blockers[zone.event] and zone.hitbox.window_drag ~= true) and 2
				or math.max(new_levels[binding], zone.hitbox.window_drag == false and 2 or 1)

			-- We only allow dragging preventing levels when cursor is on top of the draggable element,
			-- otherwise it breaks window dragging. This means touch devices need to tap the draggable
			-- element before they can start dragging it. Can't think of a way around this atm.
			if new_level > 1 and not cursor:collides_with(zone.hitbox) then
				new_level = 1
			end

			new_levels[binding] = math.max(new_levels[binding], new_level)
			if new_level > 1 then
				self.is_dragging_prevented = true
			end
		end
	end

	-- Window dragging only gets prevented when on top of an element, which is when double clicks should be ignored.
	new_levels.mbtn_left_dbl = new_levels.mbtn_left == 2 and 2 or 0

	for name, level in pairs(new_levels) do
		if level ~= self.binding_levels[name] then
			local flags = level == 1 and 'allow-vo-dragging+allow-hide-cursor' or ''
			mp[(level == 0 and 'disable' or 'enable') .. '_key_bindings'](name, flags)
			self.binding_levels[name] = level
			self:queue_autohide()
		end
	end
end

function cursor:_find_history_sample()
	local time = mp.get_time()
	for _, e in self.history:iter_rev() do
		if time - e.time > 0.1 then
			return e
		end
	end
	return self.history:tail()
end

-- Returns the current velocity vector in pixels per second.
---@return Point
function cursor:get_velocity()
	local snap = self:_find_history_sample()
	if snap then
		local x, y, time = self.x - snap.x, self.y - snap.y, mp.get_time()
		local time_diff = time - snap.time
		if time_diff > 0.001 then
			return {x = x / time_diff, y = y / time_diff}
		end
	end
	return {x = 0, y = 0}
end

---@param x integer
---@param y integer
function cursor:move(x, y)
	local old_x, old_y = self.x, self.y

	-- mpv reports initial mouse position on linux as (0, 0), which always
	-- displays the top bar, so we hardcode cursor position as infinity until
	-- we receive a first real mouse move event with coordinates other than 0,0.
	if not self.first_real_mouse_move_received then
		if x > 0 and y > 0 and x < 99999999 and y < 99999999 then
			self.first_real_mouse_move_received = true
		else
			x, y = math.huge, math.huge
		end
	end

	-- Add 0.5 to be in the middle of the pixel
	self.x, self.y = x + 0.5, y + 0.5

	if old_x ~= self.x or old_y ~= self.y then
		if self.x == math.huge or self.y == math.huge then
			self.hidden = true
			self.history:clear()

			-- Slowly fadeout elements that are currently visible
			for _, id in ipairs(config.cursor_leave_fadeout_elements) do
				local element = Elements[id]
				if element then
					local visibility = element:get_visibility()
					if visibility > 0 then
						element:tween_property('forced_visibility', visibility, 0, function()
							element.forced_visibility = nil
						end)
					end
				end
			end

			Elements:update_proximities()
			Elements:trigger('global_mouse_leave')
		else
			if self.hidden then
				-- Cancel potential fadeouts
				for _, id in ipairs(config.cursor_leave_fadeout_elements) do
					if Elements[id] then Elements[id]:tween_stop() end
				end

				self.hidden = false
				Elements:trigger('global_mouse_enter')
			end

			-- Update current move travel distance
			-- `mp.get_time() - last.time < 0.5` check is there to ignore first event after long inactivity to
			-- filter out big jumps due to window being repositioned/rescaled (e.g. opening a different file).
			local last = self.last_events.move
			if last and last.x < math.huge and last.y < math.huge and mp.get_time() - last.time < 0.5 then
				self.distance = self.distance + get_point_to_point_proximity(cursor, last)
				cursor.distance_reset_timer:kill()
				cursor.distance_reset_timer:resume()
			end

			Elements:update_proximities()
			-- Update history
			self.history:insert({x = self.x, y = self.y, time = mp.get_time()})
		end

		Elements:proximity_trigger('mouse_move')
		self:queue_autohide()
	end

	self:trigger('move')

	request_render()
end

function cursor:leave() self:move(math.huge, math.huge) end

--[[ EMBYNIAN[cursor-hold] — 指针压在控件的**本体**上（进度条、控制条/顶栏那一排按钮、音量条）时，不让
-- mpv 收走光标。只是停在唤出带里（触发渐变的位置）不算。

用户令 2026-09-29：「只有鼠标停在控件，进度条和上方的按钮还有音量条上的时候才不隐藏鼠标，触发渐变的时候
不隐藏控件，但是要隐藏鼠标。」这条把两半拆成了各认各的判据：

  · **控件那一半这里本来就已经成立**：uosc 的元件显隐只看 proximity，而 proximity 只在 `cursor:leave()`
    时归零，`leave` 只挂 hover=false / 全屏切换 / 菜单禁用器 —— mpv 自己收光标不会把 hover 翻假
    （实测 work/probe-hold-visible-before.txt：压在顶栏带上四秒，`showing=false` 而 `top_bar=1.00`
    一直没动）。所以「停在唤出带里控件不自动隐藏」不用改，本次一个字没动。
  · **光标那一半要改**：独占模式的宿主不碰 `cursor-autohide`（见 PlayerViewModel.ShowMpvCursor 的
    `PictureInHostWindow` 门），于是这里是 mpv 自己的默认 1000ms 在收 —— 压在音量条上也照收
    （同一份取证：右缘那一段 1.16s 后 `showing` 翻假）。

**2026-09-28 那版判据是错的**（`in_control_reach`：四块元件的 `proximity > 0` 就算数）。proximity 是
唤出带 —— 指针落进带子（离元件矩形 120px 以内，还没碰到它）就把光标钉住了，而用户要的恰恰是「带子里
要隐藏鼠标」。改判 `<`：`Element:update_proximity()` 里只有进入矩形内（`proximity_raw == 0`）才是
`proximity == 1`，所以 `< 1` 就是「正压在本体上」—— 与集成模式 Core 那条 `PointerHolds`（只认
`ChromePart` 那四处命中）是同一件事，这也正是用户令里「两种模式交互逻辑一致」的那一半。

做法：指针压在**注册过命中区的元素本体**上（`primary_down` / `primary_click` /
`wheel_up` / `wheel_down` 四条任一命中）就把 `cursor-autohide` 改成 `no`，离开时把装配时的原值还回去。
问命中区而不是自己拿矩形算，是因为**按钮级几何只有元件自己知道**：控制条那一排按钮（Button.lua）、
顶栏那四颗窗口按钮与返回键、音量条的静音键、时间轴上的章节圆点，各自 `cursor:zone` 注册的都是**它们自己的
小矩形**，而元件本体（如 `controls` 那条整幅宽的控制条）比它们大得多。**这一问因此比按元件矩形算更窄也更
准**，正好落在用户点名的「按钮」上。

⚠️ **问命中区时必须跳过 EMBYNIAN 的两条兜底区**（点画面暂停＝`primary_click`、滚轮音量＝`wheel_up`/`wheel_down`，
见 main.lua）：它们的 hitbox 罩着**整个画布**，是「画面」的交互区、不是「控件」——不跳过的话，指针停在
空白画面上也命中，hold 恒真、`cursor-autohide` 恒为 `no`，光标永远不藏（2026-09-29 用户报
「独占模式下鼠标不会自动隐藏」的根因；work/probe-hold-visible-repro.txt 实锤：激活那一拍 autohide 就翻 no，
死区停四秒光标不藏）。两条兜底区的 hitbox 都带着 `embynian_fallback` 标记，判据里一律跳过。

`no` 是 mpv 认的取值：playloop 每拍重算 `mouse_cursor_visible`，`cursor_autohide_delay == -1` 那一支
直接置真并推 VOCTRL_SET_CURSOR_VISIBILITY，于是**已经藏着的光标也会当场放回来**（player/playloop.c
的 handle_cursor_autohide，v0.40.0 第 840 行）。还原之后「鼠标静止一秒就藏」在画面中间照旧成立。
]]
do
	local original = mp.get_property('cursor-autohide')
	cursor.autohide_base = (original == nil or original == '') and '1000' or original
	cursor.autohide_hold = false

	-- 指针此刻正压在控件本体上吗。判据是「有没有一条非兜底的命中区罩着它」，见上面那段注释。
	-- 自己遍历（从后往前＝先查后登记的高优先级区）而不是 find_zone：find_zone 只回第一个命中的区，
	-- 而兜底区每帧登记在最前（render 最先＝优先级最低），空白画面上它就是唯一命中，拿回来还得自己丢，
	-- 等于没问；跳过判据见 hitbox 上的 embynian_fallback 标记（main.lua）。
	function cursor:on_control()
		if self.hidden or self.disabled then return false end
		for i = #self.zones, 1, -1 do
			local zone = self.zones[i]
			if not zone.hitbox.embynian_fallback
				and (zone.event == 'primary_down' or zone.event == 'primary_click'
					or zone.event == 'wheel_up' or zone.event == 'wheel_down')
				and self:collides_with(zone.hitbox) then
				return true
			end
		end
		return false
	end

	-- 只在结论变了的时候动 mpv 的属性（它每次改动都会唤一遍观察者与 playloop）。
	function cursor:refresh_hold()
		local hold = self:on_control()
		if hold == self.autohide_hold then return end
		self.autohide_hold = hold
		mp.set_property('cursor-autohide', hold and 'no' or self.autohide_base)
	end
end

function cursor:is_autohide_allowed()
	return options.autohide and (not self.autohide_fs_only or state.fullscreen)
		and not self.is_dragging_prevented
		and not Menu:is_open()
end
mp.observe_property('cursor-autohide-fs-only', 'bool', function(_, val) cursor.autohide_fs_only = val end)

-- Cursor auto-hiding after period of inactivity.
function cursor:autohide()
	if self:is_autohide_allowed() then
		self:leave()
		self.autohide_timer:kill()
	end
end

function cursor:queue_autohide()
	-- EMBYNIAN[cursor-hold]：先问一次「指针压在本体上了吗」。放在那道 `options.autohide` 闸前面 ——
	-- 装箱的默认是 autohide=false（收光标的事整个交给 mpv 的 cursor-autohide），这一句要是排在闸后就永远
	-- 跑不到。落点挑这里是因为每一次鼠标移动（cursor:move）与每一次鼠标事件（cursor:trigger）都会经过它。
	self:refresh_hold()
	if self:is_autohide_allowed() then
		self.autohide_timer:kill()
		self.autohide_timer:resume()
	end
end

-- Calculates distance in which cursor reaches rectangle if it continues moving on the same path.
-- Returns `nil` if cursor is not moving towards the rectangle.
---@param rect Rect
function cursor:direction_to_rectangle_distance(rect)
	local prev = self:_find_history_sample()
	if not prev then return false end
	local end_x, end_y = self.x + (self.x - prev.x) * 1e10, self.y + (self.y - prev.y) * 1e10
	return get_ray_to_rectangle_distance(self.x, self.y, end_x, end_y, rect)
end

---@param id string
function cursor:register_disabler(id)
	self.disablers[#self.disablers + 1] = id
	if #self.disablers > 0 then
		cursor.disabled = true
		self:leave()
	end
end

---@param id string
function cursor:unregister_disabler(id)
	self.disablers = itable_filter(self.disablers, function(item) return item ~= id end)
	if #self.disablers == 0 then cursor.disabled = false end
end

---@param event string
---@param shortcut Shortcut
---@param cb? fun(shortcut: Shortcut)
function cursor:create_handler(event, shortcut, cb)
	return function()
		if self.disabled then return end
		if cb then cb(shortcut) end
		self:trigger(event, shortcut)
	end
end

-- Movement
local function handle_mouse_pos(_, mouse)
	if not mouse or cursor.disabled then return end
	if cursor.last_hover and not mouse.hover then
		cursor:leave()
	elseif not (cursor.last_hover == false and mouse.hover == false) then -- filters out duplicate mouse out events
		cursor:move(mouse.x, mouse.y)
	end
	cursor.last_hover = mouse.hover
end

local function handle_touch_pos(_, touches)
	if not touches or cursor.disabled then return end
	local touch = touches[1]
	if touch then
		cursor:move(touch.x, touch.y)
	end
end

mp.observe_property('mouse-pos', 'native', handle_mouse_pos)
mp.observe_property('touch-pos', 'native', handle_touch_pos)

-- Key binding groups
local modifiers = {nil, 'alt', 'alt+ctrl', 'alt+shift', 'alt+ctrl+shift', 'ctrl', 'ctrl+shift', 'shift'}
local primary_bindings = {}
for i = 1, #modifiers do
	local mods = modifiers[i]
	local mp_name = (mods and mods .. '+' or '') .. 'mbtn_left'
	primary_bindings[#primary_bindings + 1] = {
		mp_name,
		cursor:create_handler('primary_up', create_shortcut('primary_up', mods)),
		cursor:create_handler('primary_down', create_shortcut('primary_down', mods), function(...)
			handle_mouse_pos(nil, mp.get_property_native('mouse-pos'))
		end),
	}
end
mp.set_key_bindings(primary_bindings, 'mbtn_left', 'force')
mp.set_key_bindings({
	{'mbtn_left_dbl', 'ignore'},
}, 'mbtn_left_dbl', 'force')
mp.set_key_bindings({
	{
		'mbtn_right',
		cursor:create_handler('secondary_up', create_shortcut('secondary_up')),
		cursor:create_handler('secondary_down', create_shortcut('secondary_down')),
	},
}, 'mbtn_right', 'force')
mp.set_key_bindings({
	{'wheel_up', cursor:create_handler('wheel_up', create_shortcut('wheel_up'))},
	{'wheel_down', cursor:create_handler('wheel_down', create_shortcut('wheel_down'))},
}, 'wheel', 'force')

-- Monitor mpv UI's and disable uosc's cursor handling when any are open
mp.observe_property('user-data/mpv/context-menu/open', 'bool', function(_, value)
	if value == true then cursor:register_disabler('context-menu')
	else cursor:unregister_disabler('context-menu') end
end)
mp.observe_property('user-data/mpv/console/open', 'bool', function(_, value)
	if value == true then cursor:register_disabler('console')
	else cursor:unregister_disabler('console') end
end)

return cursor
