do
	local native_mp = {}
	for key, value in pairs(mp) do native_mp[key] = value end
	local results = {}
	local clock, timers, commands, properties, keys
	local original_request_render, original_tween = request_render, tween
	local function require_equal(actual, expected, message)
		if actual ~= expected then error((message or 'mismatch') .. ': ' .. tostring(actual) .. ' ~= ' .. tostring(expected)) end
	end
	local function emit(name)
		for _, callback in ipairs(fixture_events[name] or {}) do callback({event = name}) end
	end
	local function advance(seconds)
		clock = clock + seconds
		for _ = 1, 10 do
			local due = false
			for _, timer in ipairs(timers) do
				if timer.enabled and timer.at <= clock then
					timer.enabled, due = false, true
					timer.callback()
				end
			end
			if not due then return end
		end
	end
	local function count_command(first, second)
		local count = 0
		for _, command in ipairs(commands) do
			if command[1] == first and (second == nil or command[2] == second) then count = count + 1 end
		end
		return count
	end
	local function canvas()
		cursor:clear_zones()
		embynian_click_pause_zone()
		cursor.x, cursor.y, cursor.hidden, cursor.disabled = 400, 350, false, false
	end
	local function down() cursor:trigger('primary_down', create_shortcut('primary_down')) end
	local function up() cursor:trigger('primary_up', create_shortcut('primary_up')) end
	local function menu(data)
		local value
		local item = Menu:open(data or {type = 'fixture', items = {{title = 'one', value = 1}, {title = 'two', value = 2}, {title = 'three', value = 3}}},
			function(event) if event.type == 'activate' then value = event.value end end)
		return item, function() return value end
	end
	local function reset()
		if Menu:is_open() then Menu:close(true) end
		clock, timers, commands, properties, keys = 100, {}, {}, {}, {}
		display.width, display.height, display.bx, display.by, display.initialized = 1280, 720, 1280, 720, true
		state.scale, state.fullormaxed, state.pause, state.duration, state.time, state.is_video = 1, false, false, 1000, 100, true
		state.ime_active = false
		cursor.last_events = {}
		cursor.handlers.primary_up = {}
		cursor.history:clear()
		cursor.first_real_mouse_move_received = true
		cursor.x, cursor.y, cursor.hidden, cursor.disabled = 400, 350, false, false
		Elements.curtain.dependents, Elements.curtain.opacity = {}, 0
		embynian_click_pause_pending, embynian_click_pause_press_last = nil, nil
		embynian_click_pause_second_half, embynian_click_pause_focus_at = false, nil
		if embynian_click_pause_reset then embynian_click_pause_reset() end
		canvas()
	end
	local function test(name, run)
		local ok, detail = pcall(function() reset(); run() end)
		results[#results + 1] = {name = name, passed = ok, detail = ok and '' or tostring(detail)}
	end
	native_mp.add_timeout(0.2, function()
		local ok, fatal = pcall(function()
			mp.get_time = function() return clock end
			mp.add_timeout = function(delay, callback)
				local timer = {timeout = delay, at = clock + delay, callback = callback, enabled = true}
				function timer:kill() self.enabled = false end
				function timer:resume() self.at, self.enabled = clock + self.timeout, true end
				function timer:is_enabled() return self.enabled end
				timers[#timers + 1] = timer
				return timer
			end
			mp.commandv = function(...) commands[#commands + 1] = {...}; return true end
			mp.command = function(value) commands[#commands + 1] = {'command', value}; return true end
			mp.set_property_native = function(name, value) properties[name] = value; return true end
			mp.set_property_bool = mp.set_property_native
			mp.set_property = mp.set_property_native
			mp.get_property_native = function(name, fallback)
				if properties[name] ~= nil then return properties[name] end
				if name == 'input-doubleclick-time' then return 300 end
				if name == 'input-bindings' then return {} end
				return native_mp.get_property_native(name, fallback)
			end
			mp.get_property_bool = function(name, fallback) return properties[name] or fallback or false end
			mp.add_forced_key_binding = function(key, name, callback) keys[key] = {name = name, callback = callback} end
			mp.remove_key_binding = function(name)
				for key, binding in pairs(keys) do if binding.name == name then keys[key] = nil end end
			end
			mp.observe_property = function(name, _, callback) callback(name, mp.get_property_native(name)) end
			mp.unobserve_property = function() end
			request_render = function() end
			tween = function(_, to, setter, duration_or_callback, callback)
				setter(type(to) == 'function' and to() or to)
				local done = type(duration_or_callback) == 'function' and duration_or_callback or callback
				if done then done() end
				return nil
			end
			clock, timers, commands, properties, keys = 100, {}, {}, {}, {}
			test('menu close does not click through to underlying button', function()
				local button = {ax = 350, ay = 300, bx = 450, by = 400}
				local clicked = 0
				cursor:zone('primary_click', button, function() clicked = clicked + 1 end)
				cursor:zone('primary_down', display, function() end)
				down()
				cursor:clear_zones()
				cursor:zone('primary_click', button, function() clicked = clicked + 1 end)
				advance(0.2); up()
				require_equal(clicked, 0)
			end)
			test('a release cannot activate a newly revealed button', function()
				local clicked = 0
				down()
				cursor:zone('primary_click', {ax = 350, ay = 300, bx = 450, by = 400}, function() clicked = clicked + 1 end)
				advance(0.05); up()
				require_equal(clicked, 0)
			end)
			test('secondary compound clicks keep the secondary event identity', function()
				local event
				cursor:zone('secondary_click', {ax = 350, ay = 300, bx = 450, by = 400}, function(shortcut) event = shortcut.key end)
				cursor:trigger('secondary_down', create_shortcut('secondary_down'))
				cursor:trigger('secondary_up', create_shortcut('secondary_up'))
				require_equal(event, 'secondary_click')
			end)
			test('button capture survives a render with a new rectangle', function()
				local owner, clicked = {}, 0
				cursor:zone('primary_click', {ax = 350, ay = 300, bx = 450, by = 400, input_owner = owner}, function() clicked = clicked + 1 end)
				down(); cursor:clear_zones()
				cursor:zone('primary_click', {ax = 350, ay = 300, bx = 450, by = 400, input_owner = owner}, function() clicked = clicked + 1 end)
				up(); require_equal(clicked, 1)
			end)
			test('normal compound button click fires exactly once', function()
				local clicked = 0
				cursor:zone('primary_click', {ax = 350, ay = 300, bx = 450, by = 400}, function() clicked = clicked + 1 end)
				down(); advance(0.05); up()
				require_equal(clicked, 1)
			end)
			test('centered to anchored menu replacement releases curtain', function()
				menu()
				require_equal(#Elements.curtain.dependents, 1)
				local replacement = menu({type = 'picture', embynian_anchor = true, items = {{title = 'one', value = 1}}})
				replacement:close(true)
				require_equal(#Elements.curtain.dependents, 0)
				require_equal(Elements.curtain.opacity, 0)
			end)
			test('centered menu replacement owns one curtain registration', function()
				menu(); menu(); menu()
				require_equal(#Elements.curtain.dependents, 1)
				Menu:close(true)
				require_equal(#Elements.curtain.dependents, 0)
			end)
			test('menu keypad enter activates selected item', function()
				local item, picked = menu()
				item:select_index(2)
				item:handle_shortcut(create_shortcut('kp_enter'), {event = 'press', is_mouse = false})
				require_equal(picked(), 2)
			end)
			test('menu size matches the reference context menu metrics', function()
				local item = menu()
				-- 参考项目（C:\mpv_config-2026.08.12）右键菜单的实测值，见 main.lua EMBYNIAN[menu-style]：
				-- 字号 20、行高＝字号×1.2＝24、hint 字号−1、面板无最小宽、面板留空 4
				require_equal(item.font_size, 20)
				require_equal(item.item_height, 24)
				require_equal(item.font_size_hint, 19)
				require_equal(item.min_width, 0)
				require_equal(item.padding, 4)
				item:close(true)
			end)
			test('menu scales 1.3x live while fullscreen or maximized', function()
				local item = menu()
				state.fullscreen = true
				update_fullormaxed()
				require_equal(state.scale, 1.3)
				require_equal(item.font_size, 26) -- round(20 * 1.3)
				require_equal(item.item_height, 31) -- round(26 * 1.2)
				state.fullscreen, state.maximized = false, true
				update_fullormaxed()
				require_equal(state.scale, 1.3)
				require_equal(item.font_size, 26)
				state.fullscreen, state.maximized = false, false
				update_fullormaxed()
				require_equal(state.scale, 1)
				require_equal(item.font_size, 20)
				item:close(true)
			end)
			test('menu font follows the hidpi scale like the integrated flyout', function()
				local item = menu()
				state.hidpi_scale = 1.5
				update_display_dimensions() -- state.scale 重算不依赖 osd 画布，harness 里 vo=null 也一样
				item:on_display()
				require_equal(item.font_size, 30)
				state.hidpi_scale = 1
				update_display_dimensions()
				item:on_display()
				require_equal(item.font_size, 20)
				item:close(true)
			end)
			test('menu pointer click supersedes keyboard selected row', function()
				local item, picked = menu()
				cursor.x, cursor.y = item.ax + item.padding + 10, item.current.top + item.scroll_step * 1.5
				item:update_proximity()
				item.mouse_nav = false
				item:select_index(3)
				item:handle_cursor_down()
				item:handle_cursor_up(create_shortcut('primary_up'))
				require_equal(picked(), 2)
			end)
			test('menu drag cancels on pointer leaving without infinite scroll', function()
				local rows = {}; for i = 1, 100 do rows[i] = {title = tostring(i), value = i} end
				local item = menu({type = 'fixture', items = rows})
				item:set_scroll_to(500)
				cursor.x, cursor.y = item.ax + 40, item.current.top + 40
				item:update_proximity(); item:handle_cursor_down()
				cursor:leave()
				require_equal(item.drag_last_y, nil)
				require_equal(item.is_dragging, false)
				require_equal(item.current.scroll_y, 500)
			end)
			test('timeline cancellation restores the original pause state', function()
				local item = Elements.timeline
				item:update_dimensions(); item:handle_cursor_down()
				require_equal(properties.pause, true)
				item:on_global_mouse_leave(); item:handle_cursor_up()
				require_equal(properties.pause, false)
			end)
			test('timeline release finishes fast dragging with an exact seek', function()
				local item = Elements.timeline
				item:update_dimensions(); item:handle_cursor_down(); item:set_from_cursor(true)
				cursor.x = 700
				item:handle_cursor_up()
				local last
				for _, command in ipairs(commands) do if command[1] == 'seek' then last = command end end
				require_equal(last[3], 'absolute+exact')
				require_equal(properties.pause, false)
			end)
			test('timeline paused drag stays paused after cancellation', function()
				state.pause = true
				local item = Elements.timeline
				item:update_dimensions(); item:handle_cursor_down(); item:on_global_mouse_leave()
				require_equal(properties.pause, true)
			end)
			test('single picture click followed by a button retains its pause', function()
				down(); advance(0.05); up(); advance(0.1)
				cursor:zone('primary_click', {ax = 350, ay = 300, bx = 450, by = 400}, function() end)
				down(); advance(0.25)
				require_equal(count_command('cycle', 'pause'), 1)
			end)
			test('double picture click never toggles pause', function()
				down(); advance(0.05); up(); advance(0.1); down(); advance(0.05); up(); advance(0.5)
				require_equal(count_command('cycle', 'pause'), 0)
			end)
			test('picture press cannot survive across a file boundary', function()
				down(); emit('start-file'); advance(0.05); canvas(); up(); advance(0.5)
				require_equal(count_command('cycle', 'pause'), 0)
			end)
			test('right edge submenu stays within the display', function()
				cursor.x, cursor.y = 1270, 300
				local item = menu({type = 'picture', embynian_anchor = true, items = {
					{title = 'Parent', items = {{title = 'Child', value = 1}}}
				}})
				item:select_index(1)
				local boxes = {}
				local original = assdraw.ass_new
				assdraw.ass_new = function()
					return setmetatable({}, {__index = function(_, name)
						if name == 'rect' then return function(_, ax, ay, bx, by, opts)
							if opts and opts.color == options.menu_background_color then boxes[#boxes + 1] = {ax = ax, bx = bx} end
						end end
						return function() end
					end})
				end
				local ok, err = pcall(function() item:render() end)
				assdraw.ass_new = original
				assert(ok, err)
				require_equal(#boxes, 2)
				for _, box in ipairs(boxes) do assert(box.ax >= 0 and box.bx <= display.width, 'submenu outside display') end
				item:close(true)
			end)
			test('submenu preview guard survives mouse navigation toward it', function()
				-- 2026-10-07 复刻参考菜单实拍抓的崩：预览面板表是 x/y/w/h，去程守卫把它喂给
				-- direction_to_rectangle_distance（要 ax/ay/bx/by）＝算 nil，uosc 脚本整只死掉。
				-- 钉住：mouse_nav 下悬停子菜单父行再渲染，必须活着（选中行被悬停改写是另一回事）。
				cursor.x, cursor.y = 640, 360
				cursor.history:clear()
				local item = menu({type = 'fixture', items = {
					{title = 'Parent', items = {{title = 'Child', value = 1}}},
					{title = 'Leaf', value = 2},
				}})
				item.mouse_nav = true
				item:select_index(1)
				local ok, err = pcall(function() item:render() end)
				assert(ok, 'render crashed with a hovered submenu row: ' .. tostring(err))
				item:close(true)
			end)
			test('file boundary closes stale menus and releases the curtain', function()
				menu(); emit('start-file')
				require_equal(Menu:is_open(), nil)
				require_equal(#Elements.curtain.dependents, 0)
			end)
			test('external track without title uses a fallback label', function()
				properties['track-list'] = {{type = 'sub', id = 1, external = true}}
				local open = create_select_tracklist_type_menu_opener({type = 'sub', prop = 'sid', title = 'Subtitles'})
				open()
				require_equal(Menu:is_open().current.items[1].value, 1)
			end)
			test('host shortcuts rebind and stay suspended while a menu owns input', function()
				fixture_messages['embynian-shortcuts'](utils.format_json({['Ctrl+F9'] = 'toggle-pause', SPACE = ''}))
				assert(keys['Ctrl+F9'], 'new key not installed')
				keys.SPACE.callback()
				require_equal(count_command('script-message', 'embynian-shortcut'), 0)
				keys['Ctrl+F9'].callback()
				require_equal(count_command('script-message', 'embynian-shortcut'), 1)
				menu()
				require_equal(keys['Ctrl+F9'], nil)
				fixture_messages['embynian-shortcuts'](utils.format_json({['Ctrl+F8'] = 'toggle-pause', SPACE = ''}))
				require_equal(keys['Ctrl+F8'], nil)
				Menu:close(true)
				assert(keys['Ctrl+F8'], 'menu close did not restore latest bindings')
				require_equal(keys['Ctrl+F9'], nil)
				fixture_messages['embynian-shortcuts']('{}')
			end)
			test('closing a menu re-enables unchanged host bindings', function()
				fixture_messages['embynian-shortcuts'](utils.format_json({p = 'previous-episode'}))
				menu(); Menu:close(true)
				assert(keys.p, 'unchanged binding not restored')
				keys.p.callback()
				require_equal(count_command('script-message', 'embynian-shortcut'), 1)
				fixture_messages['embynian-shortcuts']('{}')
			end)
			test('external track reload advertises a registered F5 key', function()
				properties['track-list'] = {{type = 'sub', id = 1, external = true, title = 'fixture.srt'}}
				local open = create_select_tracklist_type_menu_opener({type = 'sub', prop = 'sid', title = 'Subtitles'})
				open()
				assert(keys.f5, 'F5 has no input binding')
				keys.f5.callback({event = 'press', is_mouse = false})
				require_equal(count_command('sub-reload'), 1)
			end)
		end)
		if not ok then results[#results + 1] = {name = 'fixture setup', passed = false, detail = tostring(fatal)} end
		for key, value in pairs(native_mp) do mp[key] = value end
		request_render, tween = original_request_render, original_tween
		native_mp.commandv('script-message', 'fixture-result', utils.format_json(results))
	end)
end
