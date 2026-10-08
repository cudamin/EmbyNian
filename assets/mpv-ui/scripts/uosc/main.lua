--[[ uosc | https://github.com/tomasklaen/uosc ]]
--
-- ┌─ EmbyNian 内置分支 ─────────────────────────────────────────────────────────
-- │ 只随独占模式（libmpv 自建视频窗口）由宿主 C# LibMpvBackend 以 load-script 装载；
-- │ 集成模式不装载任何 Lua。宿主负责 osc=no、osd-fonts-dir 与 load-script，其余照旧。
-- │ 基线：上游 uosc 5.12.0（见下方 uosc_version）。
-- │
-- │ 补丁清单（升级 uosc 时按此逐条重打）。每处改动都用 `EMBYNIAN[槽名]` 标记，
-- │ 全仓一条命令即可枚举全部落点：
-- │     grep -rn "EMBYNIAN\[" assets/mpv-ui/scripts/uosc
-- │
-- │   main.lua（本文件）
-- │     EMBYNIAN[host]          宿主通道：embynian_notify 与 embynian-ready 握手
-- │     EMBYNIAN[pkgpath]       模块搜索路径自举（load-script 不加 package.path）
-- │     EMBYNIAN[controls]      控制条默认值与排布（换集/选集/版本/画面菜单顶替播放列表入口）
-- │     EMBYNIAN[topbar]        无边框顶栏（系统标题栏不存在，顶栏画窗口按钮）
-- │     EMBYNIAN[color]         用户原配色（Fluent 深色一档）
-- │     EMBYNIAN[topbar-pin]    右上角置顶按钮的状态来源：state.ontop 初值＋ontop 属性的观察器
-- │                             （按钮本体与两档画法在 TopBar.lua，同一个槽名 —— 升级 uosc 时两处都要重打）
-- │     EMBYNIAN[timeline-parity] 章节别名与集成 TimelineChapterMap 一致
-- │     EMBYNIAN[drag-cancel]   cursor.lua 保留原生拖窗 canceled 松键，不把取消算成点击
-- │     EMBYNIAN[autoload]      autoload 强制 false（defaults 与读配置后各一处）
-- │     EMBYNIAN[fileend]       handle_file_end / file_end_timer 整块删除
-- │     EMBYNIAN[osddim]        d3d11 起播画布尺寸兜底观察
-- │     EMBYNIAN[ui-bind]       脚本绑定叫 embynian-ui-*，宿主消息叫 embynian-*，两套名字不许同名
-- │     EMBYNIAN[episode]       embynian-ui-* 三个绑定与裁剪说明
-- │     EMBYNIAN[version]       换版本的第四个绑定（embynian-ui-versions）：≡ 菜单里那一行，
-- │                             外加控制条上那颗「版本」按钮（2026-09-23 起按需露面，见下一条）。
-- │     EMBYNIAN[version-count] 宿主 → uosc 的通道：embynian-version-count <几版> 写进
-- │                             state.has_many_versions，控制条上那颗「版本」按钮按它露面 ——
-- │                             只有一版时它不在屏上。uosc 的控件表本来是静态的（原来这里写着
-- │                             「写不出有第二版才露这种条件」），照的正是它自己 has_many_edition
-- │                             那一路的形状：has_ 开头的条件读 state 表，谁改状态谁 trigger
-- │                             dispositions。
-- │     EMBYNIAN[episode-count] 宿主 → uosc 的通道：embynian-episode-count <0/1> 写进
-- │                             state.has_episodes，控制条上那颗「选集」按钮按它露面 ——
-- │                             播放电影（非单集）时它不在屏上（2026-09-26 用户令「播放电影的时候
-- │                             不要显示这个按钮」）。与 version-count 同一条路。
-- │     EMBYNIAN[picture-menu]  第五个绑定（embynian-ui-picture-menu）：独占模式右键/菜单键，
-- │                             以及控制条上那颗「画面菜单」按钮 —— 三者同一份 PlayerMenuCatalog。
-- │     EMBYNIAN[skip-button]   宿主 → uosc 的通道：embynian-skip-offer <文案|空> 驱动一颗右下角的
-- │                             「跳过片头/片尾」按钮（元件在 elements/SkipButton.lua）；点它回推
-- │                             embynian-skip-take（宿主 TakeSkip）。集成模式那颗是 XAML 的，独占
-- │                             模式画面在 mpv 窗口里、那颗不在屏上，故由宿主把 offer 推过来画。
-- │                             start-file 时清一次（换集途中不挂上一集的 offer）。
-- │                             **尺寸与集成那颗同一个观感**（2026-09-30 用户令「缩小图标跳过按钮
-- │                             两倍」＋「参考集成模式的跳过按钮修改独占模式的跳过按钮」）。
-- │     EMBYNIAN[skip-keys]     同上那条 offer 的**第二半**（2026-09-30 用户报「按回车和 esc 确认
-- │                             跳过不生效」）：offer 立着那一段，SkipButton 元件用 keybind 把
-- │                             ENTER/KP_ENTER/ESC 借到本脚本的两条无默认键绑定上（embynian-ui-skip-take /
-- │                             embynian-ui-skip-dismiss，见下面的 bind_command），收摊时把原绑定
-- │                             keybind 按回去（随包内核没有 keyunbind，2026-10-02 实测，还原写法
-- │                             见 SkipButton 的 EMBYNIAN[skip-keys-restore]）。独占模式的键盘归
-- │                             mpv（input-default-bindings=yes），Esc 本来
-- │                             是内建的退全屏/退出、回车没有绑定，不借这两把键，提示立着按它们
-- │                             什么都不会发生。集成模式那半在 shell 侧（PlayerPage 的 Root 兜底
-- │                             + Win32 钩子），不走本通道。
-- │     EMBYNIAN[topbar-subline] 宿主 → uosc 的通道：embynian-subline <分辨率·视频编码·音频格式·组名|空>
-- │                             写进顶栏副标题（第二行），空串＝收起。集成模式那一行是 XAML 的 SubtitleBox，
-- │                             独占模式由 uosc 顶栏画在返回按钮正下方 —— 两模式同源同显。
-- │     EMBYNIAN[click-pause]   轻点空白画面切换暂停的动作（命中区在 lib/utils.lua 的 render 里）；
-- │                             含双击闸：单击押后到 mpv 的双击窗口外才证实，第二拍「按下」即撤
-- │                             （撤在按下不撤在松开——独占全屏切换会把光标挪走、松开过不了位置闸）
-- │     EMBYNIAN[wheel-volume]  空白画面滚轮＝音量（no-osd，只闪右侧音量条，不落 mpv 的 OSD）
-- │   elements/SkipButton.lua
-- │     EMBYNIAN[skip-button]   「跳过片头/片尾」按钮元件本体（右下角常驻可见，offer 在才画）
-- │   elements/Controls.lua
-- │     EMBYNIAN[episode]       控制条快捷项映射到上面的绑定
-- │     EMBYNIAN[controls]      控制条新加的两项快捷项：版本、画面菜单
-- │   elements/Menu.lua
-- │     EMBYNIAN[menu-anchor]   宿主推来的菜单（画面/选集/版本）带 embynian_anchor 时在光标处弹出、
-- │                             不屏幕居中、不压暗幕布 —— 弹出方式与集成模式的右键/按钮浮层一致
-- │     EMBYNIAN[menu-style]    菜单界面整把复刻参考项目右键菜单（用户令 2026-10-07 晚「参考这个项目的
-- │                             lua 脚本右键菜单界面修改本项目独占模式的右键菜单界面（只复刻界面，
-- │                             不抄功能选项）」）：#2C2C2C 不透明底板＋圆角 5＋0.5 白描边、行高＝
-- │                             字号×1.2（fs20）、悬停行 #353535 白字、hint 全亮右对齐、分隔线
-- │                             #3E3E3E 上下各让 4；子面板贴父面板右缘零缝、顶边对齐父项行、父面板
-- │                             不动；无标题/脚注/最小宽。缩放仍走 state.scale（DPI，全屏/最大化
-- │                             ×1.3 —— 同日早些「大小跟集成模式一致」那道令保留的部分）。逐项映射
-- │                             见本文件选项区 EMBYNIAN[menu-style]，几何与上色在 Menu.lua 同名槽
-- │   elements/TopBar.lua
-- │     EMBYNIAN[topbar-back]   左上角返回按钮：退出 mpv＝回到外壳详情页（与集成模式左上角返回同位同义）
-- │     EMBYNIAN[topbar-pin]    右上角置顶按钮（push_pin）：点它 cycle ontop（置顶这块 mpv 窗口）；状态画在
-- │                             图钉的姿势上 —— 未置顶斜 35°、置顶立正（用户令 2026-09-29「置顶不要长亮」，
-- │                             见 render 里 EMBYNIAN[topbar-pin-tilt]）；状态由 main.lua 的 ontop 观察器送进来
-- │     EMBYNIAN[ontop-follows-pause] 置顶跟着播放状态走：在播 ontop yes、暂停 no（用户令 2026-09-29
-- │                             「播放时自动置顶，暂停时自动取消置顶」），换片由 file-loaded 补一拍，
-- │                             空闲不跟；手动 cycle ontop 仍即时生效，只撑到下一条边沿
-- │     EMBYNIAN[topbar-back-glass] 返回键可见底与标题那块玻璃**同形同色**：都高 size-2*margin、同从
-- │                             self.ay+margin 起；返回键那一块四周各让 margin（左缘＝窗口左缘＋margin，
-- │                             用户令 2026-09-28 更晚「左边的空隙要和上面的一样大」）；静止档不透明度取
-- │                             config.opacity.title（用户令 2026-09-28 晚「返回按钮的背景要和标题的
-- │                             背景一致」）。那条「返回键与标题的间隙＝下面那条缝」同批收紧到 title_spacing＝1。
-- │     EMBYNIAN[topbar-subline-italic] 副标题（第二行）走斜体（用户令 2026-09-28 晚「标题下方的视频
-- │                             元数据改为斜体」）；顶栏每一行的玻璃回到 uosc 自家「上下各让 margin」那一条
-- │                             （用户令 2026-09-28 晚「把标题的大小改回跟 mpv_config 项目一样大小」）。
-- │     EMBYNIAN[topbar-subline-dim] 副标题字色从与标题同色（bgt）改成浅灰 c8c8c8（用户令 2026-09-28
-- │                             更晚「元数据的字体加点灰色」）；左缘跟返回键玻璃同一条（窗口左缘＋margin）。
-- │     EMBYNIAN[topbar-subline-branch] 副标题前面缀「└ 」树干（用户令 2026-09-28 更晚「把这个添加到元数据
-- │                             的前面」；照参考项目 uosc 给章节行加的那个前缀）。
-- │     EMBYNIAN[topbar-subline] 顶栏副标题（第二行）由宿主经 embynian-subline 直接写进来（set_subline），
-- │                             挂在返回按钮正下方；顶栏的 top_bar_alt_title 选项留空、不吃模板
-- │                             —— 与集成模式那一行同源同显。
-- │     EMBYNIAN[topbar-no-chapter] 标题下方的「当前章节＋剩余时间」一行整段撤下（用户令 2026-09-28
-- │                             「去掉独占模式下左上角标题下方的章节和时间」）；章节菜单（控制条那颗）照旧。
-- │   elements/Volume.lua
-- │     EMBYNIAN[vol-osd]       音量条自己改音量也走 no-osd（拖条/滚条不再冒 mpv 的 OSD）
-- │   lib/cursor.lua
-- │     EMBYNIAN[cursor-hold]   指针压在控件本体的命中区上（进度条、控制条/顶栏那一排按钮、音量条）时把
-- │                             cursor-autohide 钉成 no，离开时还原 —— 用户令 2026-09-29
-- │                             「只有鼠标停在控件，进度条和上方的按钮还有音量条上的时候才不隐藏鼠标，
-- │                             触发渐变的时候不隐藏控件，但是要隐藏鼠标」
-- │                             （控件的显隐本来就是位置驱动的，不用改；见该处的长注与
-- │                             work/probe-hold-visible-*.txt）
-- │   lib/utils.lua
-- │     EMBYNIAN[nav-removed]   目录/播放列表导航与删文件整块删除
-- │     EMBYNIAN[click-pause]   每帧登记「轻点暂停」的兜底命中区（登记顺序＝最低优先级）
-- │     EMBYNIAN[wheel-volume]  同一处登记「滚轮音量」的兜底命中区（同上）
-- │   lib/menus.lua
-- │     EMBYNIAN[subdl-removed] 在线字幕下载整块删除
-- │
-- │ 宿主消息契约（embynian-* 值域）与验证步骤见 assets/mpv-ui/README.md。
-- └─────────────────────────────────────────────────────────────────────────────
local uosc_version = '5.12.0'

mp.commandv('script-message', 'uosc-version', uosc_version)

mp.set_property('osc', 'no')

--[[ EMBYNIAN[host] — 宿主通道（握手 + 命令转发） ]]

-- 与 C# 宿主（LibMpvBackend/LibMpvHandle）的通道：宿主在 MPV_EVENT_CLIENT_MESSAGE 上
-- 只认第一个参数以 `embynian-` 开头的 script-message，其余一律忽略。uosc 对 mpv 自身的
-- 控制（拖进度条、换轨、全部菜单命令）直接走 mpv 命令，不经过宿主 —— 宿主通过属性观察
-- 收到结果，不需要通知。装载完成先握手：宿主据此在日志里证明 Lua UI 活着。
--
-- ⚠️ 发给宿主的 key 不许与任何脚本绑定同名（见下方 EMBYNIAN[ui-bind]）：mpv 把 script-message
-- 也派给同名绑定，同名＝这条消息把自己再叫醒一次，自激成刷屏。绑定统一叫 embynian-ui-…。
function embynian_notify(key, value) mp.commandv('script-message', key, value) end
embynian_notify('embynian-ready', uosc_version)

-- EMBYNIAN[pkgpath] — 模块搜索路径自举：mpv 只在「配置目录扫描」装载脚本时把脚本目录加进 package.path；
-- 宿主经 load-script/scripts 选项按绝对路径装载时不会加，require('lib/std') 找不到
-- 同目录的模块（实测 2026-09-19：握手能发出来纯因它在第一个 require 之前）。
-- 不能用 mp.get_script_directory()——实测 --script=<文件> 装载下它返回空；直接从
-- 脚本自身的 source 路径取目录，对两种装载方式都成立。
do
	local source = debug and debug.getinfo and debug.getinfo(1, 'S').source or ''
	local here = source:sub(1, 1) == '@' and source:sub(2) or source
	here = here:match('^(.*)[/\\][^/\\]+$') or mp.get_script_directory() or ''
	if here ~= '' then
		package.path = here .. '/?.lua;' .. here .. '/?/init.lua;' .. package.path
	end
	-- 导出给 intl/char_conv/ziggy：它们同样在拼「脚本目录/子目录」，而
	-- mp.get_script_directory() 在 --script 装载下返回 nil。
	script_directory = here ~= '' and here or nil
end

assdraw = require('mp.assdraw')
opt = require('mp.options')
utils = require('mp.utils')
msg = require('mp.msg')
osd = mp.create_osd_overlay('ass-events')
QUARTER_PI_SIN = math.sin(math.pi / 4)

require('lib/std')

--[[ OPTIONS ]]

defaults = {
	timeline_style = 'bar',
	timeline_line_width = 2,
	-- 用户在自有 mpv.conf 时代用 30；嵌入后保留同一档
	timeline_size = 30,
	progress = 'windowed',
	progress_size = 2,
	progress_line_width = 20,
	timeline_persistency = '',
	timeline_border = 1,
	timeline_step = '5',
	timeline_cache = true,
	timeline_heatmap = 'overlay',

	-- EMBYNIAN[controls] — 控制条默认值与排布（2026-09-23 用户令两轮；09-24 再一轮：字幕与音频互换；
	--   09-27 再一轮：撤下左下那颗画面菜单按钮）：
	--   左下：上一集、下一集、统计、章节（有章节时）、**选集、版本**；
	--   中下：上一章节、倍速、下一章节（有章节时）；
	--   右下：字幕、audio、（空一个按钮宽）、全屏。
	-- 与旧版不同处，各有原因：
	--   · **左下角那颗 `embynian-ui-picture-menu` 撤下了**（2026-09-27 用户令「移除集成模式和独占模式左下角的
	--     画面按钮」）。那颗开的是**画面菜单** —— 与集成模式的「更多」按钮、独占模式的右键同一份
	--     PlayerMenuCatalog（09-23 用户令「改成右键画面呼出的那个菜单」）。撤下来不是删功能：右键画面那一棵
	--     照旧在（uosc 自己的右键菜单），绑定 `uosc/embynian-ui-picture-menu` 与宿主通道
	--     `embynian-picture-menu` 都还在、只是控制条上不再有它的入口。uosc 自带的 ≡ 菜单也照旧。
	--   · **选集与版本挪到左下**（用户令「把独占模式里的选集和选版本的按钮移动到左下」，左→右次序
	--     按原话：选集倒数第二个、版本最后一个）；**版本那颗按需露面**（<has_many_versions>，
	--     只有一版时整颗不在屏上）—— 见文件头的 EMBYNIAN[version-count]。**选集那颗也按需露面**
	--     （2026-09-26 用户令「播放电影的时候不要显示这个按钮」：门 <has_episodes>，宿主的
	--     embynian-episode-count 写它，播电影＝0＝整颗不在屏上）—— 见文件头的 EMBYNIAN[episode-count]。
	--   · **音频与字幕往左让，与全屏之间空出一个按钮宽**（用户令「把字幕和音轨按钮往左移动一些，
	--     让音轨按钮和全屏/窗口按钮相隔一个按钮的空位」）：那颗 `gap:1` 就是那个空位 —— uosc 的 gap
	--     是本项宽度的倍数（默认 0.3），写 1 正好一个按钮。**2026-09-24 用户令「把独占模式下字幕和
	--     音频的按钮位置互换」——两颗的先后已调过来**：左→右现在是**字幕、音频**，空位因此落在音频
	--     与全屏之间（上一轮「两颗先后没动、空位落在字幕之后」那句留档随之作废；空位改按字面读，
--     正好挨着原话点的「音轨按钮」）。**集成模式已对齐**（2026-09-29 用户令「集成模式右下角的字幕和音轨键
--     向左移一个键的空位（参考独占模式）」：`PlayerPage.xaml` 那两颗也是字幕在前、音频在后，音频与全屏
--     之间同样空一个按钮格 —— 空位写在 AudioButton 的右边距上）。**音频**照旧不带 <has_many_audio> 条件（只有一条音轨
	--     时那颗按钮也要在；简写自带的 #audio>1 徽章照旧：一轨以上才在角上标数字）。
	--   · 「版本」按钮是 Emby 的媒体源切换（embynian-ui-versions → 宿主把这一条的媒体源推回菜单），
	--     不是上游的 <has_many_edition>editions（mpv 自己的剪辑版本，那颗已撤 —— 两颗都叫「版本」
	--     只会让人点错；要看 mpv 的剪辑版本，≡ 菜单的「工具 → 剪辑版本」还在）。
	-- 播放列表/目录导航、打开文件、单曲循环（宿主裁定连播归宿主）、流画质（外部脚本）不设。
	controls =
	'<video,audio>embynian-ui-prev,<video,audio>embynian-ui-next,command:analytics:script-binding stats/cycle-stats?统计：播放统计 → 着色器统计 → 关闭,<has_chapter>chapters,<has_episodes>embynian-ui-episodes,<has_many_versions>embynian-ui-versions,space,<has_chapter>command:skip_previous:add chapter -1?上一章节,<video,audio>speed,<has_chapter>command:skip_next:add chapter 1?下一章节,space,<video,audio>subtitles,audio,gap:1,fullscreen',
	controls_size = 32,
	controls_margin = 8,
	controls_spacing = 2,
	controls_persistency = '',

	volume = 'right',
	volume_size = 40,
	volume_persistency = '',
	volume_border = 1,
	volume_step = 1,

	speed_persistency = '',
	speed_step = 0.1,
	speed_step_is_factor = false,

	-- EMBYNIAN[menu-style] — 界面整把复刻参考项目（C:\mpv_config-2026.08.12）右键菜单的样子：
	-- 2026-10-07 晚用户令「参考这个项目的 lua 脚本右键菜单界面修改本项目独占模式的右键菜单界面
	-- （只复刻界面，不抄功能选项）」，随图实测定数（截图逐像素量过，见 work/ref-menu/）：
	--   · 底板不透明 #2C2C2C、圆角 5、0.5 白细描边；行高＝字号×(1+gap)=字号×1.2（行与行贴着排，
	--     参考图行距/字号 ≈ 22/20）；分隔符＝上下各让 padding(4) 的 1px #3E3E3E 细线（参考图实测
	--     过分隔符处行距 32＝24＋8）。
	--   · 悬停/键盘所在行＝#353535 浅灰底（左右各让 3、圆角 4），**字色保持白**（参考图悬停行
	--     实测 53,53,53 底＋255 白字，不是 mpv 主线 context_menu 的白底深字）；active 行（当前值）
	--     照旧 fg@0.8 高亮＋深字。hint（快捷键/动态值那列）从 0.5 提到全亮白，右距 8 贴边
	--     （参考图 b/q/PGUP 与 ▸ 同一条右缘）；文字左缩进 36（参考图左侧留白显著大于右侧）。
	--   · 级联＝参考图的骨架：子面板贴着父面板右缘（零缝、共用那根白描边）、顶边对齐父项行、
	--     父面板原地不动；面板不再有最小宽度（参考图面板贴内容）；标题行/脚注/左缘指示条整块
	--     撤下（参考图没有这些）。上一轮（同日早些）的 WinUI 尺寸（fs14/行高42）就此退役，
	--     但**缩放尺子保留**：state.scale（hidpi × 全屏/最大化 1.3）依旧管大小——那道令只废了
	--     「随窗口高」，这里不复活。
	-- 不搬的两件（理由同 2026-09-29 那轮）：子菜单的悬停开合延迟（seconds_to_open/close_submenus=0.2
	-- —— 要动导航状态机，风险大于收益；点开子菜单照旧）；勾选框列（uosc 用 active 高亮示当前值，
	-- 同义不同形）。
	-- 作用于所有 uosc 菜单（画面/选集/版本/音轨/字幕/章节）。
	menu_font_size = 20,
	menu_gap = 0.2,
	menu_min_width = 0,
	menu_padding = 4,
	menu_item_indent = 36,
	menu_item_pad_right = 8,
	menu_hover_inset = 3,
	menu_hover_radius = 4,
	menu_background_color = '2C2C2C',
	menu_corner_radius = 5,
	menu_outline_size = 0.5,
	menu_outline_color = 'FFFFFF',
	menu_focused_color = 'FFFFFF',
	menu_focused_back_color = '353535',
	menu_disabled_color = '555555',
	menu_separator_color = '3E3E3E',
	-- 用户原配置：输入即搜索会锁死「同键关闭菜单」，嵌入后保持 no
	menu_type_to_search = false,

	-- EMBYNIAN[topbar] — mpv 窗口以 border=no 无边框起播（宿主固定），系统标题栏不再存在——uosc 顶栏顶上：
	-- 标题＋最小化/最大化/关闭画进画面（右上），与用户原 mpv 配置同款。关闭=quit，宿主
	-- 把它当停止处理。
	top_bar = 'no-border',
	top_bar_size = 40,
	top_bar_persistency = '',
	top_bar_controls = 'right',
	top_bar_title = 'yes',
	top_bar_alt_title = '',
	top_bar_alt_title_place = 'below',
	top_bar_flash_on = 'video,audio',

	window_border_size = 0,

	-- EMBYNIAN[autoload] — 宿主拥有「播什么、播完去哪」（选集/连播都走 Emby），uosc 的目录续播一律关死，
	-- 也不再让它反过来改 keep-open。
	autoload = false,
	shuffle = false,

	scale = 1,
	scale_fullscreen = 1.3,
	font = '',
	font_scale = 1,
	text_border = 1.2,
	border_radius = 2,
	-- EMBYNIAN[color] — 用户原配色（Fluent 深色一档），与 EmbyNian 外壳的五套深色主题同族
	color = 'foreground=FFFBFE,foreground_text=1C1B1F,background=1C1B1F,background_text=FFFBFE',
	-- EMBYNIAN[menu-style] — menu/submenu 两档自 2026-09-29 起不再被菜单吃（菜单底板改不透明，见上方
	-- 选项区 EMBYNIAN[menu-style]）；值留着不动，免得 uosc 内部默认值在别处兜底出新花样。
	opacity = 'menu=0.9,submenu=0.7,curtain=0.5',
	animation_duration = 100,
	refine = 'sorting',
	flash_duration = 1000,
	proximity_in = 40,
	proximity_out = 120,
	total_time = false, -- deprecated by below
	destination_time = 'playtime-remaining',
	time_precision = 0,
	font_bold = false,
	autohide = false,
	buffered_time_threshold = 60,
	-- 暂停指示只在换集闪烁那一下有对手（宿主控制窗自己画暂停徽章）；
	-- flash 让视频窗也有同样的反馈，static 会常驻压画面。
	pause_indicator = 'flash',
	stream_quality_options = '4320,2160,1440,1080,720,480,360,240,144',
	video_types =
	'3g2,3gp,asf,avi,f4v,flv,h264,h265,m2ts,m4v,mkv,mov,mp4,mp4v,mpeg,mpg,ogm,ogv,rm,rmvb,ts,vob,webm,wmv,y4m',
	audio_types =
	'aac,ac3,aiff,ape,au,cue,dsf,dts,flac,m4a,mid,midi,mka,mp3,mp4a,oga,ogg,opus,spx,tak,tta,wav,weba,wma,wv',
	image_types = 'apng,avif,bmp,gif,j2k,jp2,jfif,jpeg,jpg,jxl,mj2,png,svg,tga,tif,tiff,webp',
	subtitle_types = 'aqt,ass,gsub,idx,jss,lrc,mks,pgs,pjs,psb,rt,sbv,slt,smi,sub,sup,sbv,srt,ssa,ssf,ttxt,txt,usf,vt,vtt',
	playlist_types = 'm3u,m3u8,pls,url,cue',
	load_types = 'video,audio,image',
	default_directory = '~/',
	show_hidden_files = false,
	use_trash = false,
	adjust_osd_margins = true,
	chapter_ranges = 'openings:30abf964,endings:30abf964,ads:c54e4e80',
	-- EMBYNIAN[timeline-parity] 标题先转小写；别名与集成 TimelineChapterMap 保持一致。
	chapter_range_patterns = 'openings:^intro%s*start,^intro$,オープニング$,^片头$,片头开始$;endings:^end$,エンディング$,^片尾$,片尾开始$,^credits$;intros:preview$,预告$,予告$;outros:credits$',
	languages = 'slang,en',
	subtitles_directory = '~~/subtitles',
	disable_elements = 'idle_indicator,audio_indicator',
	ziggy_path = 'default',
}
options = table_copy(defaults)
function handle_options(changed_options)
	if changed_options.time_precision then
		timestamp_zero_rep_clear_cache()
	end
	update_config()
	update_human_times()
	Manager:disable('user', options.disable_elements)
	Elements:trigger('options')
	Elements:update_proximities()
	request_render()
end
opt.read_options(options, 'uosc', handle_options)
-- EMBYNIAN[autoload] — 宿主裁定（见 defaults 注释）：autoload 在嵌入环境里没有任何合法入口，
-- 手改的 script-opts 也不允许把它打开 —— 目录续播会 loadfile 任意本机文件。
options.autoload = false
-- Normalize values
options.proximity_out = math.max(options.proximity_out, options.proximity_in + 1)
if options.chapter_ranges:sub(1, 4) == '^op|' then options.chapter_ranges = defaults.chapter_ranges end
if options.total_time and options.destination_time == 'playtime-remaining' then
	msg.warn('`total_time` is deprecated. Use `destination_time` instead.')
	options.destination_time = 'total'
elseif not itable_index_of({'total', 'playtime-remaining', 'time-remaining'}, options.destination_time) then
	options.destination_time = 'playtime-remaining'
end
if not itable_index_of({'left', 'right'}, options.top_bar_controls) then
	options.top_bar_controls = options.top_bar_controls == 'yes' and 'right' or nil
end

--[[ INTERNATIONALIZATION ]]
local intl = require('lib/intl')
t = intl.t
require('lib/char_conv')
fzy = require('lib/fzy')

--[[ CONFIG ]]
local config_defaults = {
	color = {
		foreground = serialize_rgba('ffffff').color,
		foreground_text = serialize_rgba('000000').color,
		background = serialize_rgba('000000').color,
		background_text = serialize_rgba('ffffff').color,
		window_border = serialize_rgba('000000').color,
		curtain = serialize_rgba('111111').color,
		success = serialize_rgba('a5e075').color,
		error = serialize_rgba('ff616e').color,
		match = serialize_rgba('69c5ff').color,
		heatmap = serialize_rgba('00adee').color,
	},
	opacity = {
		timeline = 0.9,
		position = 1,
		chapters = 0.8,
		slider = 0.9,
		slider_gauge = 1,
		controls = 0,
		speed = 0.6,
		menu = 1,
		submenu = 0.4,
		border = 1,
		title = 1,
		tooltip = 1,
		thumbnail = 1,
		curtain = 0.8,
		idle_indicator = 0.8,
		audio_indicator = 0.5,
		buffering_indicator = 0.3,
		playlist_position = 0.8,
		heatmap = 0.4,
	},
}
config = {
	version = uosc_version,
	-- sets max rendering frequency in case the
	-- native rendering frequency could not be detected
	render_delay = 1 / 60,
	font = options.font ~= '' and options.font or mp.get_property('options/osd-font'),
	osd_margin_x = mp.get_property('osd-margin-x'),
	osd_margin_y = mp.get_property('osd-margin-y'),
	osd_alignment_x = mp.get_property('osd-align-x'),
	osd_alignment_y = mp.get_property('osd-align-y'),
	refine = create_set(comma_split(options.refine)),
	types = {
		video = comma_split(options.video_types),
		audio = comma_split(options.audio_types),
		image = comma_split(options.image_types),
		subtitle = comma_split(options.subtitle_types),
		playlist = comma_split(options.playlist_types),
		media = comma_split(options.video_types
			.. ',' .. options.audio_types
			.. ',' .. options.image_types
			.. ',' .. options.playlist_types),
		load = {}, -- populated by update_load_types() below
	},
	stream_quality_options = comma_split(options.stream_quality_options),
	top_bar_flash_on = comma_split(options.top_bar_flash_on),
	chapter_ranges = (function()
		---@type table<string, string[]> Alternative patterns.
		local alt_patterns = {}
		if options.chapter_range_patterns and options.chapter_range_patterns ~= '' then
			for _, definition in ipairs(split(options.chapter_range_patterns, ';+ *')) do
				local name_patterns = split(definition, ' *:')
				local name, patterns = name_patterns[1], name_patterns[2]
				if name and patterns then alt_patterns[name] = split(patterns, ',') end
			end
		end

		---@type table<string, {color: string; opacity: number; patterns?: string[]}>
		local ranges = {}
		if options.chapter_ranges and options.chapter_ranges ~= '' then
			for _, definition in ipairs(split(options.chapter_ranges, ' *,+ *')) do
				local name_color = split(definition, ' *:+ *')
				local name, color = name_color[1], name_color[2]
				if name and color
					and name:match('^[a-zA-Z0-9_]+$') and color:match('^[a-fA-F0-9]+$')
					and (#color == 6 or #color == 8) then
					local range = serialize_rgba(name_color[2])
					range.patterns = alt_patterns[name]
					ranges[name_color[1]] = range
				end
			end
		end
		return ranges
	end)(),
	color = table_copy(config_defaults.color),
	opacity = table_copy(config_defaults.opacity),
	cursor_leave_fadeout_elements = {'timeline', 'volume', 'top_bar', 'controls'},
	timeline_step = 5,
	timeline_step_flag = '',
}

function update_load_types()
	local extensions = {}
	local types = create_set(comma_split(options.load_types:lower()))

	if types.same then
		types.same = nil
		if state and state.type then types[state.type] = true end
	end

	for _, name in ipairs(table_keys(types)) do
		local type_extensions = config.types[name]
		if type(type_extensions) == 'table' then
			itable_append(extensions, type_extensions)
		else
			msg.warn('Unknown load type: ' .. name)
		end
	end

	config.types.load = extensions
end

-- Updates config with values dependent on options
function update_config()
	-- Required environment config
	if options.autoload then
		mp.commandv('set', 'keep-open', 'yes')
		mp.commandv('set', 'keep-open-pause', 'no')
	end

	-- Adds `{element}_persistency` config properties with forced visibility states (e.g.: `{paused = true}`)
	for _, name in ipairs({'timeline', 'controls', 'volume', 'top_bar', 'speed'}) do
		local option_name = name .. '_persistency'
		local value, flags = options[option_name], {}
		if type(value) == 'string' then
			for _, state in ipairs(comma_split(value)) do flags[state] = true end
		end
		config[option_name] = flags
	end

	-- Opacity
	config.opacity = table_assign({}, config_defaults.opacity, serialize_key_value_list(options.opacity,
		function(value, key)
			return clamp(0, tonumber(value) or config.opacity[key], 1)
		end
	))

	-- Color
	config.color = table_assign({}, config_defaults.color, serialize_key_value_list(options.color, function(value)
		return serialize_rgba(value).color
	end))

	-- Global color shorthands
	fg, bg = config.color.foreground, config.color.background
	fgt, bgt = config.color.foreground_text, config.color.background_text

	-- Timeline step
	do
		local is_exact = options.timeline_step:sub(-1) == '!'
		config.timeline_step = tonumber(is_exact and options.timeline_step:sub(1, -2) or options.timeline_step)
		config.timeline_step_flag = is_exact and 'exact' or ''
	end

	-- Other
	update_load_types()
end
update_config()

-- Default menu items
-- 嵌入版默认菜单：删除了上游的文件删除、文件打开、目录浏览、流画质（外部脚本）、
-- 在线字幕下载与「打开配置目录」——这些入口在 Emby 客户端里要么没有数据源、
-- 要么是宿主明令禁止的能力。选集继续走控制窗；这里只留 mpv 自身能应答的事。
function create_default_menu_items()
	return {
		{title = '字幕', value = 'script-binding uosc/subtitles'},
		{title = '音轨', value = 'script-binding uosc/audio'},
		-- EMBYNIAN[version] — 换版本（同一部片的另一个文件）走宿主的 Emby 导航，同选集：这一项只把请求
		-- 发给宿主（embynian-ui-versions → embynian-versions），菜单由宿主推回。
		-- 标题写死中文而不是 t('Versions')：uosc 的本地化按 slang 找 intl/<lang>.json，而宿主的 slang 是
		-- 「chi,zho,…」这类语言代码，目录里没有对应文件，t() 会原样吐回英文键名。上游自带的几个键有中文
		-- 译文也不会命中，所以这里不跟它走。
		-- 2026-09-22：其余几项一并写死中文（原来是这条注释末尾的「另案」）。理由与上面同一条 —— t() 在这
		-- 台机器上命中不了；而半张中文半张英文的菜单比全英文更糟。
		{title = '版本', value = 'script-binding uosc/embynian-ui-versions'},
		{title = '章节', value = 'script-binding uosc/chapters'},
		{
			title = '工具',
			items = {
				{
					title = '画面比例',
					items = {
						{title = '默认', value = 'set video-aspect-override no'},
						{title = '16:9', value = 'set video-aspect-override "16:9"'},
						{title = '4:3', value = 'set video-aspect-override "4:3"'},
						{title = '2.35:1', value = 'set video-aspect-override "2.35:1"'},
					},
				},
				{title = '音频设备', value = 'script-binding uosc/audio-device'},
				{title = '剪辑版本', value = 'script-binding uosc/editions'},
				{title = '截图', value = 'async screenshot'},
				-- 状态归 stats 脚本，菜单与控制条共用同一个三态入口。
				{title = '统计', value = 'script-binding stats/cycle-stats'},
				{title = '按键绑定', value = 'script-binding uosc/keybinds'},
			},
		},
		{title = '退出', value = 'quit'},
	}
end

--[[ STATE ]]

display = {ax = 0, ay = 0, bx = 1280, by = 720, width = 1280, height = 720, initialized = false}
cursor = require('lib/cursor')
state = {
	platform = (function()
		local platform = mp.get_property_native('platform')
		if platform then
			if itable_index_of({'windows', 'darwin'}, platform) then return platform end
		else
			if os.getenv('windir') ~= nil then return 'windows' end
			local homedir = os.getenv('HOME')
			if homedir ~= nil and string.sub(homedir, 1, 6) == '/Users' then return 'darwin' end
		end
		return 'linux'
	end)(),
	cwd = mp.get_property('working-directory'),
	path = nil, -- current file path or URL
	history = {}, -- history of last played files stored as full paths
	time = nil, -- current media playback time
	speed = 1,
	---@type number|nil
	duration = nil, -- current media duration
	max_seconds = nil, -- max seconds the time in timeline is expected to reach, accounted for speed
	time_human = nil, -- current playback time in human format
	destination_time_human = nil, -- depends on options.destination_time
	pause = mp.get_property_native('pause'),
	ime_active = mp.get_property_native('input-ime'),
	chapters = {},
	chapter_ranges = {},
	current_clipboard_backend = mp.get_property_native('current-clipboard-backend'),
	border = mp.get_property_native('border'),
	title_bar = mp.get_property_native('title-bar'),
	fullscreen = mp.get_property_native('fullscreen'),
	maximized = mp.get_property_native('window-maximized'),
	fullormaxed = mp.get_property_native('fullscreen') or mp.get_property_native('window-maximized'),
	-- EMBYNIAN[topbar-pin] — 顶栏那颗置顶按钮的状态（用户令 2026-09-28 晚「给独占模式右上角也加个置顶图标」）：
	-- mpv 窗口自己的 ontop，与 fullscreen／maximized 同一路从属性里读初值，随后由下面的观察器维护。
	ontop = mp.get_property_native('ontop'),
	render_timer = nil,
	render_last_time = 0,
	volume = mp.get_property_native('volume'),
	volume_max = mp.get_property_native('volume-max'),
	mute = nil,
	type = nil, -- video,image,audio
	is_idle = false,
	is_video = false,
	is_audio = false, -- true if file is audio only (mp3, etc)
	is_image = false,
	is_stream = false,
	has_image = false,
	has_audio = false,
	has_sub = false,
	has_chapter = false,
	-- EMBYNIAN[version-count] — 这个条目挂了几版文件，由宿主的 embynian-version-count 消息写进来
	-- （uosc 自己问不出 Emby 的媒体源表）。默认 false＝那颗「版本」按钮先不画：宿主在装载握手那一刻
	-- 就会把真答案送过来（它能早答，是因为「播哪一条、这条有几版」在 mpv 起来之前就已经在它手上了）。
	has_many_versions = false,
	-- EMBYNIAN[episode-count] — 正在放的是不是单集，由宿主的 embynian-episode-count 消息写进来
	-- （mpv 只看见一条文件，分不出电影与剧）。默认 false＝那颗「选集」按钮先不画：宿主在装载握手那一刻
	-- 就会把真答案送过来。默认不画的另一面是「播电影时它不在屏上」（2026-09-26 用户令）。
	has_episodes = false,
	has_playlist = false,
	shuffle = options.shuffle,
	---@type nil|{pos: number; paths: string[]}
	shuffle_history = nil,
	on_shuffle = function() state.shuffle_history = nil end,
	mouse_bindings_enabled = false,
	uncached_ranges = nil,
	cache = nil,
	cache_buffering = 100,
	cache_underrun = false,
	cache_duration = nil,
	core_idle = false,
	eof_reached = false,
	render_delay = config.render_delay,
	playlist_count = 0,
	playlist_pos = 0,
	margin_top = 0,
	margin_bottom = 0,
	margin_left = 0,
	margin_right = 0,
	hidpi_scale = 1,
	scale = 1,
	radius = 0,
}
buttons = require('lib/buttons')
thumbnail = {width = 0, height = 0, disabled = false}
external = {} -- Properties set by external scripts
key_binding_overwrites = {} -- Table of key_binding:mpv_command
Elements = require('elements/Elements')
Menu = require('elements/Menu')

-- State dependent utilities
require('lib/utils')
require('lib/text')
require('lib/ass')
require('lib/menus')

-- Determine path to ziggy
do
	local bin = 'ziggy-' .. (state.platform == 'windows' and 'windows.exe' or state.platform)
	config.ziggy_path = os.getenv('MPV_UOSC_ZIGGY') or
	options.ziggy_path == 'default' and join_path(script_directory or mp.get_script_directory() or '', join_path('bin', bin)) or
	utils.join_path(mp.command_native({ 'expand-path', options.ziggy_path }), bin)
end

--[[ STATE UPDATERS ]]

function update_display_dimensions()
	state.scale = (state.hidpi_scale or 1) * (state.fullormaxed and options.scale_fullscreen or options.scale)
	state.radius = round(options.border_radius * state.scale)
	local real_width, real_height = mp.get_osd_size()
	if real_width <= 0 then return end
	display.bx, display.width, display.by, display.height = real_width, real_width, real_height, real_height
	display.initialized = true

	-- Tell elements about this
	Elements:trigger('display')

	-- Some elements probably changed their rectangles as a reaction to `display`
	Elements:update_proximities()
	request_render()
end

function update_fullormaxed()
	state.fullormaxed = state.fullscreen or state.maximized
	update_display_dimensions()
	Elements:trigger('prop_fullormaxed', state.fullormaxed)
	cursor:leave()
end

function update_duration()
	local duration = state._duration and ((state.rebase_start_time == false and state.start_time)
		and (state._duration + state.start_time) or state._duration)
	set_state('duration', duration)
	update_human_times()
end

function update_human_times()
	state.speed = state.speed or 1
	if state.time then
		if state.duration then
			if options.destination_time == 'playtime-remaining' then
				state.destination_time_human = format_time((state.time - state.duration) / state.speed, state.duration)
			elseif options.destination_time == 'total' then
				state.destination_time_human = format_time(state.duration, state.duration)
			else
				state.destination_time_human = format_time(state.time - state.duration, state.duration)
			end
		else
			state.destination_time_human = nil
		end
		state.time_human = format_time(state.time, state.duration or state.time)
	else
		state.time_human, state.destination_time_human = nil, nil
	end
end

-- Notifies other scripts such as console about where the unoccupied parts of the screen are.
function update_margins()
	if display.height == 0 then return end

	local function causes_margin(element)
		return element and element.enabled and (element:is_persistent() or element.min_visibility > 0.5)
	end
	local timeline, top_bar, controls, volume = Elements.timeline, Elements.top_bar, Elements.controls, Elements.volume
	-- margins are normalized to window size
	local left, right, top, bottom = 0, 0, 0, 0

	if causes_margin(controls) then
		bottom = (display.height - controls.ay) / display.height
	elseif causes_margin(timeline) then
		bottom = (display.height - timeline.ay) / display.height
	end

	if causes_margin(top_bar) then top = top_bar.title_by / display.height end

	if causes_margin(volume) then
		if options.volume == 'left' then
			left = volume.bx / display.width
		elseif options.volume == 'right' then
			right = volume.ax / display.width
		end
	end

	if top == state.margin_top and bottom == state.margin_bottom and
		left == state.margin_left and right == state.margin_right then
		return
	end

	state.margin_top = top
	state.margin_bottom = bottom
	state.margin_left = left
	state.margin_right = right

	if utils.shared_script_property_set then
		utils.shared_script_property_set('osc-margins', string.format('%f,%f,%f,%f', 0, 0, top, bottom))
	end
	mp.set_property_native('user-data/osc/margins', {l = left, r = right, t = top, b = bottom})

	if not options.adjust_osd_margins then return end
	local osd_margin_y, osd_margin_x, osd_factor_x = 0, 0, display.width / display.height * 720
	if config.osd_alignment_y == 'bottom' then
		osd_margin_y = round(bottom * 720)
	elseif config.osd_alignment_y == 'top' then
		osd_margin_y = round(top * 720)
	end
	if config.osd_alignment_x == 'left' then
		osd_margin_x = round(left * osd_factor_x)
	elseif config.osd_alignment_x == 'right' then
		osd_margin_x = round(right * osd_factor_x)
	end
	mp.set_property_native('osd-margin-y', osd_margin_y + config.osd_margin_y)
	mp.set_property_native('osd-margin-x', osd_margin_x + config.osd_margin_x)
end
function create_state_setter(name, callback)
	return function(_, value)
		set_state(name, value)
		if callback then callback() end
		request_render()
	end
end

function set_state(name, value)
	state[name] = value
	local state_event = state['on_' .. name]
	if state_event then state_event(value) end
	Elements:trigger('prop_' .. name, value)
end

-- EMBYNIAN[fileend] — handle_file_end 与 file_end_timer 整块删除 —— 播放列表/目录续播的入口
-- （autoplay、shuffle、playlist 导航）已全部裁掉，「播完去哪」只归 C# 宿主的连播规则管。

function update_render_delay(name, fps)
	if fps then state.render_delay = 1 / fps end
end

function observe_display_fps(name, fps)
	if fps then
		mp.unobserve_property(update_render_delay)
		mp.unobserve_property(observe_display_fps)
		mp.observe_property('display-fps', 'native', update_render_delay)
	end
end

--[[ STATE HOOKS ]]

mp.register_event('file-loaded', function()
	local path = normalize_path(mp.get_property_native('path'))
	itable_delete_value(state.history, path)
	state.history[#state.history + 1] = path
	set_state('path', path)

	-- Flash top bar on requested file types
	for _, type in ipairs(config.top_bar_flash_on) do
		if state['is_' .. type] then
			Elements:flash({'top_bar'})
			break
		end
	end
end)
mp.register_event('end-file', function(event)
	set_state('path', nil)
end)
mp.observe_property('playback-time', 'number', create_state_setter('time', function()
	update_human_times()
end))
mp.observe_property('rebase-start-time', 'bool', create_state_setter('rebase_start_time', update_duration))
mp.observe_property('demuxer-start-time', 'number', create_state_setter('start_time', update_duration))
mp.observe_property('duration', 'number', create_state_setter('_duration', update_duration))
mp.observe_property('speed', 'number', create_state_setter('speed', update_human_times))
mp.observe_property('track-list', 'native', function(name, value)
	-- checks the file dispositions
	local types = {sub = 0, image = 0, audio = 0, video = 0}
	for _, track in ipairs(value) do
		if track.type == 'video' then
			if track.image or track.albumart then
				types.image = types.image + 1
			else
				types.video = types.video + 1
			end
		elseif types[track.type] then
			types[track.type] = types[track.type] + 1
		end
	end
	set_state('is_audio', types.video == 0 and types.audio > 0)
	set_state('is_image', types.image > 0 and types.video == 0 and types.audio == 0)
	set_state('has_image', types.image > 0)
	set_state('has_audio', types.audio > 0)
	set_state('has_many_audio', types.audio > 1)
	set_state('has_sub', types.sub > 0)
	set_state('has_many_sub', types.sub > 1)
	set_state('is_video', types.video > 0)
	set_state('has_many_video', types.video > 1)
	set_state('type', state.is_video and 'video' or state.is_audio and 'audio' or state.is_image and 'image' or nil)
	update_load_types()
	Elements:trigger('dispositions')
end)
mp.observe_property('editions', 'number', function(_, editions)
	if editions then set_state('has_many_edition', editions > 1) end
	Elements:trigger('dispositions')
end)
mp.observe_property('chapter-list', 'native', function(_, chapters)
	local chapters, chapter_ranges = serialize_chapters(chapters), {}
	if chapters then chapters, chapter_ranges = serialize_chapter_ranges(chapters) end
	set_state('chapters', chapters)
	set_state('chapter_ranges', chapter_ranges)
	set_state('has_chapter', #chapters > 0)
	Elements:trigger('dispositions')
end)
mp.observe_property('border', 'bool', create_state_setter('border'))
mp.observe_property('title-bar', 'bool', create_state_setter('title_bar'))
mp.observe_property('loop-file', 'native', create_state_setter('loop_file'))
mp.observe_property('ab-loop-a', 'number', create_state_setter('ab_loop_a'))
mp.observe_property('ab-loop-b', 'number', create_state_setter('ab_loop_b'))
mp.observe_property('playlist-pos-1', 'number', create_state_setter('playlist_pos'))
mp.observe_property('playlist-count', 'number', function(_, value)
	set_state('playlist_count', value)
	set_state('has_playlist', value > 1)
	Elements:trigger('dispositions')
end)
mp.observe_property('fullscreen', 'bool', create_state_setter('fullscreen', update_fullormaxed))
mp.observe_property('window-maximized', 'bool', create_state_setter('maximized', update_fullormaxed))
-- EMBYNIAN[topbar-pin] — 顶栏那颗置顶按钮（TopBar.lua 的 push_pin）要的第二个数：mpv 窗口此刻是不是置顶。
-- 形状与上面两条一模一样（同一个 create_state_setter：写 state + request_render 各一次），因为「谁改的它」
-- 不重要 —— 按钮自己（cycle ontop）、mpv 自己的快捷键或将来别处改了，都从这里回到顶栏重画一遍。
-- **不经过宿主**：置顶在这里就是一扇 mpv 窗口的属性，宿主那边的 TopMost 管的是它自己的主窗口（集成模式）。
mp.observe_property('ontop', 'bool', create_state_setter('ontop'))
mp.observe_property('idle-active', 'bool', function(_, idle)
	set_state('is_idle', idle)
	Elements:trigger('dispositions')
	mp.commandv('script-message-to', 'thumbfast', 'clear')
end)
mp.observe_property('pause', 'bool', create_state_setter('pause'))
-- EMBYNIAN[ontop-follows-pause] — 独占那颗图钉跟着播放状态走（用户令 2026-09-29「播放时自动置顶，暂停时
-- 自动取消置顶」）：在播＝ontop yes、暂停＝ontop no，顶栏图钉立没立正由上面那条 ontop 观察器跟着画。
-- 手动那颗（cycle ontop）仍即时生效，只是只撑到下一条边沿 —— 与集成那头 SetPinned 的规矩同一句
-- （PlayerPage.OnStatusApplied）。两处来源：pause 的每一条边沿（uosc 按钮、快捷键、宿主经 IPC 暂停，
-- 从哪来都一样）；以及 file-loaded —— 片与片之间 pause 常常不变（换片不换窗，同窗换片那条路），
-- 光盯它会漏掉「新片开播」这一下。空闲不跟（idle-active）：没有片子在放，置不置顶归手动，
-- 别把空闲那扇窗也按到顶上。
local function follow_pause_ontop(paused)
	if mp.get_property_bool('idle-active') then return end
	mp.set_property_bool('ontop', not paused)
end
mp.observe_property('pause', 'bool', function(_, paused) follow_pause_ontop(paused) end)
mp.register_event('file-loaded', function() follow_pause_ontop(mp.get_property_native('pause')) end)
mp.observe_property('volume', 'number', create_state_setter('volume'))
mp.observe_property('volume-max', 'number', create_state_setter('volume_max'))
mp.observe_property('mute', 'bool', create_state_setter('mute'))
mp.observe_property('osd-dimensions', 'native', function(name, val)
	update_display_dimensions()
	request_render()
end)
-- EMBYNIAN[osddim] — 画布尺寸兜底（实测 2026-09-19）：d3d11 窗口管线下 osd-dimensions 的 native
-- 观察在起播初段会漏掉「画布=视频尺寸 → 画布=窗口尺寸」那一次变化，uosc 拿着旧画布布局，
-- 控件画得出来而点击热区全部错位（菜单/字幕「点了没反应」）。number 观察两个尺寸属性，
-- 每次变化都强制重测——重复调 update_display_dimensions 是幂等的。
mp.observe_property('osd-width', 'number', function() update_display_dimensions() end)
mp.observe_property('osd-height', 'number', function() update_display_dimensions() end)
mp.observe_property('display-hidpi-scale', 'native', create_state_setter('hidpi_scale', update_display_dimensions))
mp.observe_property('cache', 'string', create_state_setter('cache'))
mp.observe_property('cache-buffering-state', 'number', create_state_setter('cache_buffering'))
mp.observe_property('demuxer-via-network', 'native', create_state_setter('is_stream', function()
	Elements:trigger('dispositions')
end))
mp.observe_property('demuxer-cache-state', 'native', function(prop, cache_state)
	local cached_ranges, bof, eof, uncached_ranges = nil, nil, nil, nil
	if cache_state then
		cached_ranges, bof, eof = cache_state['seekable-ranges'], cache_state['bof-cached'], cache_state['eof-cached']
		set_state('cache_underrun', cache_state['underrun'])
		set_state('cache_duration', not cache_state.eof and cache_state['cache-duration'] or nil)
	else
		cached_ranges = {}
		set_state('cache_underrun', false)
	end

	if not (state.duration and (#cached_ranges > 0 or state.cache == 'yes' or
			(state.cache == 'auto' and state.is_stream))) then
		if state.uncached_ranges then set_state('uncached_ranges', nil) end
		set_state('cache_duration', nil)
		return
	end

	-- Normalize
	local ranges = {}
	for _, range in ipairs(cached_ranges) do
		ranges[#ranges + 1] = {
			math.max(range['start'] or 0, 0),
			math.min(range['end'] or state.duration --[[@as number]], state.duration),
		}
	end
	table.sort(ranges, function(a, b) return a[1] < b[1] end)
	if bof then ranges[1][1] = 0 end
	if eof then ranges[#ranges][2] = state.duration end
	-- Invert cached ranges into uncached ranges, as that's what we're rendering
	local inverted_ranges = {{0, state.duration}}
	for _, cached in pairs(ranges) do
		inverted_ranges[#inverted_ranges][2] = cached[1]
		inverted_ranges[#inverted_ranges + 1] = {cached[2], state.duration}
	end
	uncached_ranges = {}
	local last_range = nil
	for _, range in ipairs(inverted_ranges) do
		if last_range and last_range[2] + 0.5 > range[1] then -- fuse ranges
			last_range[2] = range[2]
		else
			if range[2] - range[1] > 0.5 then -- skip short ranges
				uncached_ranges[#uncached_ranges + 1] = range
				last_range = range
			end
		end
	end

	set_state('uncached_ranges', uncached_ranges)
end)
mp.observe_property('display-fps', 'native', observe_display_fps)
mp.observe_property('estimated-display-fps', 'native', update_render_delay)
mp.observe_property('eof-reached', 'native', create_state_setter('eof_reached'))
mp.observe_property('core-idle', 'native', create_state_setter('core_idle'))

--[[ KEY BINDS ]]

-- Adds a key binding that respects rerouting set by `key_binding_overwrites` table.
---@param name string
---@param callback fun(event: table)
---@param flags nil|string
function bind_command(name, callback, flags)
	mp.add_key_binding(nil, name, function(...)
		if key_binding_overwrites[name] then
			mp.command(key_binding_overwrites[name])
		else
			callback(...)
		end
	end, flags)
end

bind_command('toggle-ui', function() Elements:toggle({'timeline', 'controls', 'volume', 'top_bar'}) end)
bind_command('flash-ui', function() Elements:flash({'timeline', 'controls', 'volume', 'top_bar'}) end)
bind_command('flash-timeline', function() Elements:flash({'timeline'}) end)
bind_command('flash-top-bar', function() Elements:flash({'top_bar'}) end)
bind_command('flash-volume', function() Elements:flash({'volume'}) end)
bind_command('flash-speed', function() Elements:flash({'speed'}) end)
bind_command('flash-pause-indicator', function() Elements:flash({'pause_indicator'}) end)
bind_command('flash-progress', function() Elements:flash({'progress'}) end)
bind_command('toggle-progress', function() Elements:maybe('timeline', 'toggle_progress') end)
bind_command('toggle-title', function() Elements:maybe('top_bar', 'toggle_title') end)
bind_command('decide-pause-indicator', function() Elements:maybe('pause_indicator', 'decide') end)
bind_command('menu', function() toggle_menu_with_items() end)
bind_command('menu-blurred', function() toggle_menu_with_items({mouse_nav = true}) end)
bind_command('keybinds', function()
	if Menu:is_open('keybinds') then
		Menu:close()
	else
		open_command_menu({type = 'keybinds', items = get_keybinds_items(), search_style = 'palette'})
	end
end)
bind_command('load-subtitles', create_track_loader_menu_opener({
	prop = 'sub',
	title = t('Load subtitles'),
	loaded_message = t('Loaded subtitles'),
	allowed_types = itable_join(config.types.video, config.types.subtitle),
}))
bind_command('load-audio', create_track_loader_menu_opener({
	prop = 'audio',
	title = t('Load audio'),
	loaded_message = t('Loaded audio'),
	allowed_types = itable_join(config.types.video, config.types.audio),
}))
bind_command('load-video', create_track_loader_menu_opener({
	prop = 'video',
	title = t('Load video'),
	loaded_message = t('Loaded video'),
	allowed_types = config.types.video,
}))
bind_command('subtitles', create_select_tracklist_type_menu_opener({
	title = t('Subtitles'),
	type = 'sub',
	prop = 'sid',
	enable_prop = 'sub-visibility',
	secondary = {prop = 'secondary-sid', icon = 'vertical_align_top', enable_prop = 'secondary-sub-visibility'},
	load_command = 'script-binding uosc/load-subtitles',
}))
bind_command('audio', create_select_tracklist_type_menu_opener({
	title = t('Audio'), type = 'audio', prop = 'aid', load_command = 'script-binding uosc/load-audio',
}))
bind_command('video', create_select_tracklist_type_menu_opener({
	title = t('Video'), type = 'video', prop = 'vid', load_command = 'script-binding uosc/load-video',
}))
bind_command('playlist', create_self_updating_menu_opener({
	title = t('Playlist'),
	type = 'playlist',
	list_prop = 'playlist',
	footnote = t('Paste path or url to add.') .. ' ' .. t('%s to reorder.', 'ctrl+up/down/pgup/pgdn/home/end'),
	serializer = function(playlist)
		local items = {}
		local playlist_titles = mp.get_property_native('user-data/playlistmanager/titles') or {}
		for index, item in ipairs(playlist) do
			local is_url = is_protocol(item.filename)
			local title = type(item.title) == 'string' and #item.title > 0 and item.title or false
			items[index] = {
				title = is_url and (title or playlist_titles[item.filename] or url_decode(item.filename)) or
				serialize_path(item.filename).basename,
				hint = tostring(index),
				active = item.current,
				value = index,
			}
		end
		return items
	end,
	on_activate = function(event) mp.commandv('set', 'playlist-pos-1', tostring(event.value)) end,
	on_paste = function(event) mp.commandv('loadfile', tostring(event.value), 'append') end,
	on_key = function(event)
		if event.id == 'ctrl+c' and event.selected_item then
			local payload = mp.get_property_native('playlist/' .. (event.selected_item.value - 1) .. '/filename')
			set_clipboard(payload)
		end
	end,
	on_move = function(event)
		local from, to = event.from_index, event.to_index
		mp.commandv('playlist-move', tostring(from - 1), tostring(to - (to > from and 0 or 1)))
	end,
	on_remove = function(event) mp.commandv('playlist-remove', tostring(event.value - 1)) end,
}))
bind_command('chapters', create_self_updating_menu_opener({
	title = t('Chapters'),
	type = 'chapters',
	list_prop = 'chapter-list',
	active_prop = 'chapter',
	serializer = function(chapters, current_chapter)
		local items = {}
		chapters = normalize_chapters(chapters)
		for index, chapter in ipairs(chapters) do
			items[index] = {
				title = chapter.title or '',
				hint = format_time(chapter.time, state.duration),
				value = index,
				active = index - 1 == current_chapter,
			}
		end
		return items
	end,
	on_activate = function(event) mp.commandv('set', 'chapter', tostring(event.value - 1)) end,
}))
bind_command('editions', create_self_updating_menu_opener({
	title = t('Editions'),
	type = 'editions',
	list_prop = 'edition-list',
	active_prop = 'current-edition',
	serializer = function(editions, current_id)
		local items = {}
		for _, edition in ipairs(editions or {}) do
			local edition_id_1 = tostring(edition.id + 1)
			items[#items + 1] = {
				title = edition.title or t('Edition %s', edition_id_1),
				hint = edition_id_1,
				value = edition.id,
				active = edition.id == current_id,
			}
		end
		return items
	end,
	on_activate = function(event) mp.commandv('set', 'edition', event.value) end,
}))
-- EMBYNIAN[episode] — 绑定裁剪：stream-quality（外部 quality-menu 脚本不在宿主里）、open-file/items/
-- first/last 系（文件与播放列表导航，宿主禁止）、shuffle、paste 系（绕过宿主 loadfile）、
-- delete-file 系（删除用户文件）、show-in-directory / open-config-directory（拉起外部进程）
-- 全部删除；换集经 embynian-ui-* 交给宿主的 Emby 导航（见 Controls 快捷项），
-- 选集菜单向宿主要数据（embynian-episodes → 宿主 open-menu 推回）。
--
-- EMBYNIAN[ui-bind] — 绑定名（embynian-ui-…）与宿主消息名（embynian-…）必须分家，这不是口味问题：
-- mpv 把一条 `script-message <名字>` 同时派给**同名**的脚本绑定。旧版这里叫 embynian-episodes，
-- 于是按钮发的消息又把按钮自己叫醒，一条消息变 1.3 万条/秒的刷屏（2026-09-19 实测：单发一条
-- script-message 得 120657 条回声），视频卡顿、菜单被宿主逐条 open-menu 重建到点不动 —— 用户
-- 报的「点选集卡顿／点一集不换／点击变暂停／退不出去」全出自这一条。上一集/下一集没这个毛病，
-- 正是因为它们的绑定叫 embynian-episode-prev/next、消息叫 embynian-episode，本来就不同名。
bind_command('embynian-ui-prev', function() embynian_notify('embynian-episode', '-1') end)
bind_command('embynian-ui-next', function() embynian_notify('embynian-episode', '1') end)
bind_command('embynian-ui-episodes', function() embynian_notify('embynian-episodes', '') end)
-- EMBYNIAN[version] — 版本菜单向宿主要数据（embynian-versions → 宿主 open-menu 推回这一条目的媒体源）。
-- 与上面三条同一个形状：绑定叫 embynian-ui-…，消息叫 embynian-…，两套名字不许同名。
bind_command('embynian-ui-versions', function() embynian_notify('embynian-versions', '') end)
-- EMBYNIAN[picture-menu] — 右键点画面呼出的「画面菜单」向宿主要数据（embynian-picture-menu → 宿主把
-- PlayerMenuCatalog 那张树 open-menu 推回，与集成模式右键同一份）。右键/菜单键的绑定由宿主起播后经
-- keybind 补上（config=no 之下 input.conf 不读、uosc 默认不绑键，见 MpvUi.MenuKeys）。同一个形状：绑定叫
-- embynian-ui-picture-menu、消息叫 embynian-picture-menu，两套名字不许同名（理由见上面 EMBYNIAN[ui-bind]）。
bind_command('embynian-ui-picture-menu', function() embynian_notify('embynian-picture-menu', '') end)

-- EMBYNIAN[version-count] — 宿主 → uosc 的通道：这个条目挂了几版文件。
-- 上面那一排都是 uosc 向宿主要东西，这一条反过来 —— 宿主主动把答案送来，控制条上那颗「版本」按钮按它
-- 露面（只有一版时整颗不在屏上；用户令 2026-09-23「只有一个版本的情况下不显示…」）。
--
-- 门写在 controls 串里（<has_many_versions>embynian-ui-versions），形状照 mpv 自己的 <has_many_edition>——
-- uosc 的 has_ 开头的显示条件读的是 state 表，所以这里只需写格 + 催一次 dispositions（等于
-- chapter-list / editions 那两个观察器做的事）。**不改 controls 串**：uosc 的 options 只在装载时读一遍，
-- 运行期换整张控件表反而要重造全部按钮。
--
-- 消息名照旧不与任何脚本绑定同名（EMBYNIAN[ui-bind]）：绑定一律 embynian-ui-…，这条是 embynian-…，
-- 而且 uosc 这边只有 register_script_message，没有同名的 bind_command。
mp.register_script_message('embynian-version-count', function(value)
	set_state('has_many_versions', (tonumber(value) or 0) > 1)
	Elements:trigger('dispositions')
end)

-- EMBYNIAN[episode-count] — 宿主 → uosc 的通道：正在放的是不是单集（0＝电影，1＝单集）。
-- 与 version-count 同一条路、同一个形状：门写在 controls 串里（<has_episodes>embynian-ui-episodes），
-- 播电影时那颗「选集」按钮整颗不在屏上（2026-09-26 用户令「播放电影的时候不要显示这个按钮」）。
-- 消息名照旧不与任何脚本绑定同名（EMBYNIAN[ui-bind]）：uosc 这边只有 register_script_message。
mp.register_script_message('embynian-episode-count', function(value)
	set_state('has_episodes', (tonumber(value) or 0) > 0)
	Elements:trigger('dispositions')
end)

-- EMBYNIAN[topbar-subline] — 宿主 → uosc 的通道：左上角第二行那句文件信息（分辨率 · 视频编码 · 音频格式 ·
-- 组名，空串＝收起）。集成模式那一行是 XAML 的 SubtitleBox（PlayerViewModel.Subtitle 驱动）；独占模式画面在
-- mpv 窗口里，那一行由 uosc 顶栏画，于是宿主把同一句话推过来（两模式同源、显示一致，用户令 2026-09-27、
-- 内容 2026-09-28 加分辨率）。主标题走 force-media-title，副标题走这条 —— TopBar 把它画在返回按钮正下方。
-- 消息名照旧不与任何脚本绑定同名（EMBYNIAN[ui-bind]）：uosc 这边只有 register_script_message。
mp.register_script_message('embynian-subline', function(text)
	if Elements.top_bar then Elements.top_bar:set_subline(text or '') end
end)

-- EMBYNIAN[skip-button] — 宿主 → uosc 的通道：跳过片头/片尾的 offer 文案（空串＝收摊）。
-- 集成模式那颗按钮是 XAML 的（PlayerPage.xaml，SkipOffered 驱动）；独占模式画面在 mpv 窗口里、
-- 那颗不在屏上，于是宿主把这份 offer 推过来由 skip_button 元件画。offer 站多久、何时收摊全归宿主的
-- SkipCoordinator 判，本元件不自己计时；点它由元件回推 embynian-skip-take（宿主 TakeSkip）。
-- 消息名照旧不与任何脚本绑定同名（EMBYNIAN[ui-bind]）：uosc 这边只有 register_script_message，
-- 那颗按钮点击直接 embynian_notify('embynian-skip-take')，不是 bind_command。
mp.register_script_message('embynian-skip-offer', function(caption)
	if Elements.skip_button then Elements.skip_button:set_offer(caption) end
end)

bind_command('embynian-ui-skip-take', function() embynian_notify('embynian-skip-take', '') end)
bind_command('embynian-ui-skip-dismiss', function() embynian_notify('embynian-skip-dismiss', '') end)

require('lib/embynian_shortcuts')

bind_command('menu-prev', function() Elements:maybe('menu', 'navigate_by_items', -1) end)
bind_command('menu-next', function() Elements:maybe('menu', 'navigate_by_items', 1) end)
bind_command('menu-prev-page', function() Elements:maybe('menu', 'navigate_by_page', -1) end)
bind_command('menu-next-page', function() Elements:maybe('menu', 'navigate_by_page', 1) end)
bind_command('menu-start', function() Elements:maybe('menu', 'navigate_by_items', -math.huge) end)
bind_command('menu-end', function() Elements:maybe('menu', 'navigate_by_items', math.huge) end)
bind_command('menu-activate', function() Elements:maybe('menu', 'activate_selected_item') end)
bind_command('menu-back', function() Elements:maybe('menu', 'back') end)
bind_command('audio-device', create_self_updating_menu_opener({
	title = t('Audio devices'),
	type = 'audio-device-list',
	list_prop = 'audio-device-list',
	active_prop = 'audio-device',
	serializer = function(audio_device_list, current_device)
		current_device = current_device or 'auto'
		local ao = mp.get_property('current-ao') or ''
		local items = {}
		for _, device in ipairs(audio_device_list) do
			if device.name == 'auto' or string.match(device.name, '^' .. ao) then
				local hint = string.match(device.name, ao .. '/(.+)')
				if not hint then hint = device.name end
				items[#items + 1] = {
					title = device.description:sub(1, 7) == 'Default'
						and t('Default %s', device.description:sub(9))
						or device.description,
					hint = hint,
					active = device.name == current_device,
					value = device.name,
				}
			end
		end
		return items
	end,
	on_activate = function(event) mp.commandv('set', 'audio-device', event.value) end,
}))
bind_command('copy-to-clipboard', function()
	if state.path then
		set_clipboard(state.path)
	else
		mp.commandv('show-text', t('Nothing to copy'), 3000)
	end
end)

-- EMBYNIAN[click-pause] — 轻点空白画面切换暂停/播放（动作在这里，命中区每帧登记在 lib/utils.lua）。
--
-- 为什么不再用 mp.add_key_binding('MBTN_LEFT', …)（旧版即此，2026-09-19 撤）：那是拿第二条路去和
-- uosc 的 force 绑定抢同一个键，而「这一下点击归谁」取决于每帧 decide_keybinds 是否及时 —— 脚本
-- 一旦被什么拖住（比如 EMBYNIAN[ui-bind] 那场自激刷屏），命中区状态就是陈的，点菜单、点控件会穿
-- 透到这条备用绑定上，于是「点选集里的一集」变成「暂停」——用户报的「点击操作会触发自动暂停」。
-- 改成 uosc 自己的兜底命中区之后每一下点击只有一个答主：find_zone 从后往前找，元素与菜单先登记，
-- 轮不到它就说明指针在空白画面上。
--   · 登记顺序＝最低优先级（lib/utils.lua 的 render 里紧接 clear_zones，早于所有元素）。
--   · window_drag = true：留住「按住画面拖动窗口」（window-dragging=yes，见 Core/Mpv/MpvUi.cs 的
--     装配）；也让 decide_keybinds 把等级留在 1 而不是 2（等级 2 顺带禁掉光标自动隐藏，等级 1 的
--     allow-vo-dragging+allow-hide-cursor 正是要的）。
--   · 菜单开着时它天然让位：菜单的 primary_down/up 是 primary_click 的传播阻断者（lib/cursor.lua）。
--   · 原生拖窗的 canceled 松键由 cursor.lua 排除；6px 与 0.5s 只过滤普通轻点。
-- 双击闸（2026-09-19，用户令「双击画面 全屏/还原时不要触发开始和暂停」；修法对齐集成模式的
-- TapPicture/SecondTapOnPicture：单击押后到双击窗口之外才证实，第二拍「按下」即撤）。
-- 为什么撤在第二拍的**按下**而不是松开 —— 真窗口实测（work/probe-doubleclick-real-trace.txt，
-- exclusive-fs=yes）：双击的四条光标事件 uosc 全都收到了（down/up/down/up），押后那一拍也照发
-- （提交=1、撤销=0、pause 照翻）—— 因为第二拍按下当场触发 mpv 内建 MBTN_LEFT_DBL＝全屏，独占
-- 全屏切换里 uosc 的 update_fullormaxed 会 cursor:leave() 把光标挪到无穷远，第二拍的松开于是
-- 过不了 ≤6px 位置闸（或 find_zone 直接扑空），撤销永远轮不到跑。而按下事件先于全屏切换送达，
-- 闸只能挂在那里。mpv 自己那层无嫌疑：内建 MBTN_LEFT 是 ignore（input-bindings 实录），不发暂停。
--   · 双击判定与 mpv 同源：同一把尺 input-doubleclick-time，从第一次按下起算；mpv 的 DBL 本来就只看
--     间隔不看位置，这里一致。**这个值由宿主写进装配**（MpvUi.Build，值取 PictureTap.ClickDelayMilliseconds
--     = 300 毫秒，与集成模式那份押后是同一个常量，用户令 2026-09-23「两种模式的延迟统一，参考
--     C:\mpv_config-2026.08.12」——那份配置的 inputevent.lua 也是拿 mpv 这条属性的默认值做 debounce 的）。
--     脚本只读它，不自己定数。
--   · 第二拍按下若落在控件命中区上（find_zone('primary_down') 有主），不算双击 —— 那是「点完画面
--     马上去点按钮」，押后的暂停照给；控件上 mpv 的内建 DBL 本来就被 uosc 的 ignore 闸住，不全屏。
--   · 叫醒窗口的那一下不作数：见下面 EMBYNIAN[click-pause-wake]。
--   · 代价与集成模式同款：轻点暂停比手慢一个押后窗口（300 毫秒）。
-- 判据与读数：work/probe-click-pause-wheel.py（命令账）＋ work/probe-doubleclick-real.py（真窗口）。
-- ⚠️ embynian_fallback = true：这条命中区罩着整个画布，算「画面」不算「控件」—— cursor.lua 的
-- cursor:on_control()（光标保活判据）靠这个标记跳过它，否则指针停在空白画面上也会被当成「压在控件上」，
-- cursor-autohide 被钉成 no、光标永不藏（2026-09-29 用户报「独占模式下鼠标不会自动隐藏」的根因）。
embynian_click_pause_hitbox = {ax = 0, ay = 0, bx = 1280, by = 720, window_drag = true, embynian_fallback = true}
embynian_click_pause_pending = nil  -- 还没到期的那一拍（在飞＝这一下还没被证明是单击）
embynian_click_pause_press_last = nil -- 上一次左键按下的时刻（任意位置，判第二拍用）
embynian_click_pause_second_half = false -- 最近一次按下是不是「画布上的双击第二拍」

function embynian_click_pause_window()
	local option = mp.get_property_native('input-doubleclick-time')
	return type(option) == 'number' and option > 0 and option / 1000 or 0.3
end

-- EMBYNIAN[click-pause-wake] — **叫醒窗口的那一下不作数**（用户令 2026-09-23：「先点一下让窗口置顶，
-- 然后再点一下触发暂停/播放」）。这是 Windows 上内容区的通用规矩：激活点击只激活、不落在内容上；
-- 不加这一条，从别的窗口回来点画面的第一下会当场切掉暂停/播放（实测 work/probe-activation-click-*.txt）。
-- 判据是 mpv 的 focused 属性：w32 的 VO 在 WM_SETFOCUS/WM_KILLFOCUS 上更新它（w32_common.c 的
-- VOCTRL_GET_FOCUSED），于是「按下的时刻 − 窗口变成前台的时刻 ≤ 0.4 秒」就是这一下。
-- Alt+Tab 唤回不算 —— 那种第一次点击本来就该照常暂停，而 mpv 分不出两者，所以取的是「刚变前台」这个
-- 更宽的口子：代价是唤回之后 0.4 秒内的第一下点击会被吃掉一次（与 Windows 自己那一套同款）。
embynian_click_pause_focus_at = nil
mp.observe_property('focused', 'bool', function(_, value)
	if value then embynian_click_pause_focus_at = mp.get_time() end
end)

function embynian_click_pause_waking(press_time)
	return embynian_click_pause_focus_at ~= nil
		and press_time - embynian_click_pause_focus_at <= 0.4
end

-- 押后到期：窗口里没有第二拍按下 ⇒ 单击证实，发那一条暂停。
function embynian_click_pause_commit()
	embynian_click_pause_pending = nil
	mp.commandv('cycle', 'pause')
	Elements:flash({'pause_indicator'})
end

-- 双击的第二拍（或换源/收摊）：作废已押后的那一拍。
function embynian_click_pause_cancel()
	if embynian_click_pause_pending then
		embynian_click_pause_pending:kill()
		embynian_click_pause_pending = nil
	end
end

-- 每一次左键按下（任意位置）都过这里：记时刻；画布上且与上一拍间隔小于双击窗口＝双击的第二拍，
-- 当场撤掉押后的那一拍 —— 必须赶在全屏切换把光标挪走之前（见顶部说明）。
cursor:on('primary_down', function()
	local now = mp.get_time()
	local zone = cursor:find_zone('primary_click')
	local on_canvas = zone and zone.hitbox == embynian_click_pause_hitbox
	embynian_click_pause_second_half = on_canvas
		and embynian_click_pause_press_last ~= nil
		and now - embynian_click_pause_press_last < embynian_click_pause_window()
	embynian_click_pause_press_last = on_canvas and now or nil
	if embynian_click_pause_second_half then embynian_click_pause_cancel() end
end)

cursor:on('primary_up', function(shortcut)
	if shortcut and shortcut.canceled then
		embynian_click_pause_press_last = nil
		embynian_click_pause_second_half = false
	end
end)

-- 押后那一拍要是撞上换源/收摊（片尾自动连播、用户换集）就作废 —— 与 ChromeReveal 的「新一播放＝Reset」
-- 同一条道理：那一拍不该打在新一集身上。
function embynian_click_pause_reset()
	embynian_click_pause_cancel()
	embynian_click_pause_press_last = nil
	embynian_click_pause_second_half = false
	cursor.last_events.primary_down = nil
	cursor.last_events.secondary_down = nil
	if Menu:is_open() then Menu:close(true) end
end

for _, embynian_event in ipairs({'start-file', 'end-file'}) do
	mp.register_event(embynian_event, embynian_click_pause_reset)
end

-- EMBYNIAN[skip-button] — 换源即收起上一片的「跳过」offer：宿主下一拍也会按新片重推，但换集加载途中
-- 不该还挂着上一集的「跳过片尾」。宿主发空文案是常规收摊路径，这一条是保险。
mp.register_event('start-file', function()
	if Elements.skip_button then Elements.skip_button:set_offer('') end
end)

function embynian_click_pause_zone()
	local hitbox = embynian_click_pause_hitbox
	hitbox.bx, hitbox.by = display.width, display.height
	cursor:zone('primary_click', hitbox, function()
		local down = cursor.last_events.primary_down
		-- 三道具闸，缺一条都会误伤：① 这一下按下的「起点」必须没被别的区接管（zone_handled）——
		-- 点菜单外那一下正是靠它躲开的：菜单的兜底区接下了按下（顺手关菜单），松开时菜单已经拆完，
		-- 光看命中区会以为这是一次空白点击，于是「关菜单」顺带把片子暂停（2026-09-19 探针实测）；
		-- ② 时窗 0.5s、③ 位移 ≤6px；原生拖窗必须靠 canceled 排除，窗口跟手时局部坐标可能不变。
		if down and not down.zone_handled and mp.get_time() - down.time < 0.5
			and math.abs(cursor.x - down.x) + math.abs(cursor.y - down.y) <= 6 then
			if embynian_click_pause_second_half then return end -- 双击的第二拍：全屏/还原归 mpv，这里不发
			if embynian_click_pause_waking(down.time) then return end -- 叫醒窗口的那一下不作数（见上面那条）
			embynian_click_pause_pending = mp.add_timeout(
				math.max(0, down.time + embynian_click_pause_window() - mp.get_time()),
				embynian_click_pause_commit)
		end
	end)
end

-- EMBYNIAN[wheel-volume] — 空白画面上的滚轮＝音量，反馈只落 uosc 自己那根（右侧音量条）。
--
-- 为什么要有这一条：mpv 内建的 `WHEEL_UP add volume 2` 会带出**左上角**那行 OSD「Volume」
-- （uosc 关掉的只是 mpv 自带的 OSC，不是这层 OSD；宿主已设的 osd-bar=no 也拦不住它的文字），
-- 而 uosc 右侧那根音量条的位置正好没人用 —— 用户令「滚轮调整音量时不要在左上角显示 Volume，
-- 直接显示右侧的音量条」。接管之后音量走 `no-osd`（命令级前缀，mpv 对这条命令不更新 OSD），
-- 反馈改由 flash 音量元素承担。
-- 命中区登记位置与规矩同上面那条：render 里最前面＝最低优先级，指针落在时间轴/速度条/音量条上时
-- 它们的命中区先命中，滚轮仍归它们（跳转 / 倍速 / volume_step）；只有空白画面才落到这里。
-- 步进 2 与 mpv 内建同速（实测 work/probe-input-before.txt 的 `add volume  2`），换了实现不许改手感。
-- embynian_fallback = true 的用意见上面 click_pause_hitbox 那条注释：整画布兜底区不算控件，
-- cursor:on_control() 光标保活判据靠这个标记跳过它。
embynian_wheel_volume_hitbox = {ax = 0, ay = 0, bx = 1280, by = 720, embynian_fallback = true}
embynian_wheel_volume_step = 2

function embynian_set_volume(delta)
	mp.commandv('no-osd', 'add', 'volume', tostring(delta))
	Elements:flash({'volume'})
end

function embynian_wheel_volume_zone()
	local hitbox = embynian_wheel_volume_hitbox
	hitbox.bx, hitbox.by = display.width, display.height
	cursor:zone('wheel_up', hitbox, function() embynian_set_volume(embynian_wheel_volume_step) end)
	cursor:zone('wheel_down', hitbox, function() embynian_set_volume(-embynian_wheel_volume_step) end)
end

--[[ MESSAGE HANDLERS ]]

mp.register_script_message('show-submenu', function(id) toggle_menu_with_items({submenu = id}) end)
mp.register_script_message('show-submenu-blurred', function(id)
	toggle_menu_with_items({submenu = id, mouse_nav = true})
end)
mp.register_script_message('open-menu', function(json, submenu_id)
	local data = utils.parse_json(json)
	if type(data) ~= 'table' or type(data.items) ~= 'table' then
		msg.error('open-menu: received json didn\'t produce a table with menu configuration')
	else
		open_command_menu(data, {submenu = submenu_id, on_close = data.on_close})
	end
end)
mp.register_script_message('update-menu', function(json)
	local data = utils.parse_json(json)
	if type(data) ~= 'table' or type(data.items) ~= 'table' then
		msg.error('update-menu: received json didn\'t produce a table with menu configuration')
	else
		local menu = data.type and Menu:is_open(data.type)
		if menu then menu:update(data) end
	end
end)
mp.register_script_message('select-menu-item', function(type, item_index, menu_id)
	local menu = Menu:is_open(type)
	local index = tonumber(item_index)
	if menu and index and not menu.mouse_nav then
		index = round(index)
		if index > 0 and index <= #menu.current.items then
			menu:select_index(index, menu_id)
			menu:scroll_to_index(index, menu_id, true)
		end
	end
end)
mp.register_script_message('close-menu', function(type)
	if Menu:is_open(type) then Menu:close() end
end)
mp.register_script_message('menu-action', function(name, ...)
	local menu = Menu:is_open()
	if menu then
		local method = ({
			['search-cancel'] = 'search_cancel',
			['search-query-update'] = 'search_query_update',
		})[name]
		if method then menu[method](menu, ...) end
	end
end)
mp.register_script_message('thumbfast-info', function(json)
	local data = utils.parse_json(json)
	if type(data) ~= 'table' or not data.width or not data.height then
		thumbnail.disabled = true
		msg.error('thumbfast-info: received json didn\'t produce a table with thumbnail information')
	else
		thumbnail = data
		request_render()
	end
end)
mp.register_script_message('set', function(name, value)
	external[name] = value
	Elements:trigger('external_prop_' .. name, value)
end)
mp.register_script_message('toggle-elements', function(elements) Elements:toggle(comma_split(elements)) end)
mp.register_script_message('set-min-visibility', function(visibility, elements)
	local fraction = tonumber(visibility)
	local ids = comma_split(elements and elements ~= '' and elements or 'timeline,controls,volume,top_bar')
	if fraction then Elements:set_min_visibility(clamp(0, fraction, 1), ids) end
end)
mp.register_script_message('flash-elements', function(elements) Elements:flash(comma_split(elements)) end)
mp.register_script_message('overwrite-binding', function(name, command) key_binding_overwrites[name] = command end)
mp.register_script_message('disable-elements', function(id, elements) Manager:disable(id, elements) end)

--[[ ELEMENTS ]]

-- Dynamic elements
local constructors = {
	window_border = require('elements/WindowBorder'),
	buffering_indicator = require('elements/BufferingIndicator'),
	pause_indicator = require('elements/PauseIndicator'),
	top_bar = require('elements/TopBar'),
	timeline = require('elements/Timeline'),
	controls = options.controls and options.controls ~= 'never' and require('elements/Controls'),
	volume = itable_index_of({'left', 'right'}, options.volume) and require('elements/Volume'),
}

-- Required elements
require('elements/Curtain'):new()
-- EMBYNIAN[skip-button] — 独占模式「跳过片头/片尾」按钮（宿主经 embynian-skip-offer 驱动，见上面的
-- 消息处理器与 elements/SkipButton.lua）。与 Curtain 一样直接实例化：它不进 Manager 的可禁用清单。
require('elements/SkipButton'):new()

-- Element manager
-- Handles creating and destroying elements based on disabled_elements user+script config.
Manager = {
	-- Managed disable-able element IDs
	_ids = itable_join(table_keys(constructors), {'idle_indicator', 'audio_indicator'}),
	---@type table<string, string[]> A map of clients and a list of element ids they disable
	_disabled_by = {},
	---@type table<string, boolean>
	disabled = {},
}

-- Set client and which elements it wishes disabled. To undo just pass an empty `element_ids` for the same `client`.
---@param client string
---@param element_ids string|string[]|nil `foo,bar` or `{'foo', 'bar'}`.
function Manager:disable(client, element_ids)
	self._disabled_by[client] = comma_split(element_ids)
	---@diagnostic disable-next-line: deprecated
	self.disabled = create_set(itable_join(unpack(table_values(self._disabled_by))))
	self:_commit()
end

function Manager:_commit()
	-- Create and destroy elements as needed
	for _, id in ipairs(self._ids) do
		local constructor = constructors[id]
		if not self.disabled[id] then
			if not Elements:has(id) and constructor then constructor:new() end
		else
			Elements:maybe(id, 'destroy')
		end
	end

	-- We use `on_display` event to tell elements to update their dimensions
	Elements:trigger('display')
end

-- Initial commit
Manager:disable('user', options.disable_elements)
