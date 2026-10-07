-- EMBYNIAN[shortcuts]: the host owns action identities; modal menus temporarily own their keys.
local bindings = {}
local active = {}

function embynian_clear_shortcuts()
	for _, name in ipairs(active) do mp.remove_key_binding(name) end
	active = {}
end

function embynian_refresh_shortcuts()
	embynian_clear_shortcuts()
	if Menu:is_open() then return end
	local index = 0
	for key, action in pairs(bindings) do
		index = index + 1
		local name = 'embynian-action-' .. index
		active[#active + 1] = name
		mp.add_forced_key_binding(key, name, function()
			if action ~= '' then embynian_notify('embynian-shortcut', action) end
		end, {repeatable = action:find('seek-', 1, true) == 1 or action:find('volume-', 1, true) == 1})
	end
end

mp.register_script_message('embynian-shortcuts', function(json)
	local values = utils.parse_json(json)
	if type(values) ~= 'table' then return end
	local next_bindings = {}
	for key, action in pairs(values) do
		if type(key) == 'string' and type(action) == 'string'
			and #key < 48 and key:match('^[%w%+%[%]]+$')
			and (action == '' or action:match('^[a-z%-]+$')) then
			next_bindings[key] = action
		end
	end
	bindings = next_bindings
	embynian_refresh_shortcuts()
end)
