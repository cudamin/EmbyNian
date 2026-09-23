# mpv-ui — 独占模式视频窗的 Lua UI（uosc 嵌入版）

独占模式（内置 libmpv 自建 D3D11 顶层窗口）起播时，`LibMpvBackend` 交给 mpv 的 `scripts` 选项指向
**这里的 `scripts/uosc` 目录**（交目录不交文件：mpv 用目录名给脚本命名，脚本名必须是 `uosc`，控制条上
每个按钮的动作都是 `script-binding uosc/…`），uosc 由此装进视频窗：进度条、控制条、音量条、轨道/章节/
版本菜单和暂停指示都画在 mpv 自己的 OSD 层 —— 只装一份，这条老路不再用于 uosc（`load-script` 会把同一份
uosc 按文件名装成第二份 `main`，2026-09-19 撤）。集成模式的画面在 XAML 视觉树里，控件归外壳，**uosc 一概不装**。

2026-09-22 起这一目录还装着第二个脚本 `scripts/stats.lua`（播放统计，见下节）：它两条管线都装，走的是运行期
`load-script`（按文件名 → 脚本名 `stats`，所以文件名不能改），所以「集成模式不装载任何 Lua」这句话从那天起
只对 uosc 成立。

## 出处与版本

- 上游项目：https://github.com/tomasklaen/uosc ，版本 **5.12.0**（`main.lua` 里的 `uosc_version`）。
- 取自本机 dyphire/mpv-config 2026.08.12 整合包（`portable_config/scripts/uosc`），该目录就是
  上游 uosc 5.12.0 的分发拷贝。
- 许可证：**LGPL 2.1**（`LICENSE.LGPL`，随发布附带）；图标字体 Material Icons Rounded 是
  **Apache 2.0**（`fonts/LICENSE-MaterialIcons.txt`，随发布附带）。
- 装箱范围：`scripts/uosc`（main + lib + elements + intl/zh-hans 等翻译）、两个字体文件。
  其余脚本（thumbfast、quality-menu、chapterskip 等）与 `bin/ziggy` 一律不装箱。

## 播放统计脚本 stats.lua（2026-09-22 加入）

- **它是什么**：mpv 内置脚本 `player/lua/stats.lua` 的**简体中文覆盖版**。统计项（6 个页面 / 107 个字段标签 /
  顺序 / 计算逻辑）与参考项目 <https://github.com/dyphire/mpv-config> 所用的那份上游脚本逐项一致，只有展示
  文案译成中文；两份文件的字符串骨架 diff 只有文件头注释 + 两张显示名映射（轨道类型、轨道标记）。
  逐项对照表见 `work/播放统计对照报告.md`。
- **为什么覆盖而不是自绘**：mpv 的内置统计脚本没有任何 i18n 机制，`script-opts/stats.conf` 只管样式与行为，
  一个字的文案都不承载 —— 想改词只有「放一份同名脚本顶掉它」一条路。原来的客户端自绘面板（`PlaybackStats.cs`）
  因此退场，留档在 `work/removed-2026-09-22/`。
- **怎么装**：Core 的 `MpvStats` 在起播时（`mpv_initialize` 之后）发一条运行期 `load-script <绝对路径>`，
  两条 libmpv 管线都发；外置 `mpv.exe` 后端走命令行 `--script=`（它 `--no-config`，读不到用户目录的 scripts/）。
  同时 `MpvBaseline` 里 `load-stats-overlay=no` 关掉内置英文版 —— 不关会有两份脚本抢 `stats/display-stats`
  这个名字。**文件名必须保持 `stats.lua`**：脚本名取自文件名，`script-binding stats/…` 靠它解析。
- **三态循环（2026-09-22 改）**：那颗「统计」按钮不再是二态开关，而是循环 —— 关闭 → **播放统计**（第 1 页）→
  **着色器统计**（第 2 页 `vo_stats`，即 mpv 的 `vo-passes`：帧计时 Frame Timings、新帧 Fresh、重绘 Redraw，全部汉化）
  → 关闭。逻辑集中在 `EMBYNIAN[cycle]` 那个命名函数 `cycle_stats`，用「当前显示的是第几页」而不是外壳的镜像位当
  状态源，所以键盘、uosc 菜单和集成模式按钮三条入口混用也不会走散。绑定名 `stats/cycle-stats`。着色器统计页
  用 `persistent_overlay` 常驻绘制、不被音量/跳转提示覆盖，随视频输出变化实时重绘；换源（`start-file`）时收面板并
  归位到第 1 页。
- **怎么叫出来**：独占模式归 mpv 的输入层，Core 另绑 `i`（一次性，`display-stats`）/`I`（三态循环，`cycle-stats`），
  uosc 控制条上有「统计」按钮、菜单「工具 → 统计」也走同一条 `cycle-stats`；集成模式的键盘归外壳，播放页那颗
  「统计」按钮发 `script-binding stats/cycle-stats`（`PlayerViewModel.CycleStatsCommand`）。老的 `display-stats-toggle`
  绑定保留未删，作向后兼容入口。
- **画在哪**：mpv 的 OSD 层。独占模式那是它自己的窗口；集成模式那是合成进 XAML 的那一帧，所以两边都看得见。
- **许可证**：与 mpv 项目一致（GPL-2.0-or-later）。它替换掉的那份内置脚本本来就在随发布一起走的 `libmpv-2.dll`
  里，本文件只是把它换成中文版放在盘上 —— 出处已在脚本头部写明。**如果发行时要附许可证文本，这一条要一并考虑。**

## 与上游的差异（嵌入版裁剪）

**代码里的权威清单**：每处改动都用 `EMBYNIAN[槽名]` 标记，`main.lua` 顶部有逐条补丁清单，
升级 uosc 时一条命令即可枚举全部落点，不必靠本文档记忆：

```
grep -rn "EMBYNIAN\[" assets/mpv-ui/scripts/uosc
```

下面是同一批改动的散文说明，全部以「EmbyNian 宿主独占播什么、播完去哪」为判据：

1. **main.lua**
   - 顶部加了宿主通道（`embynian_notify` / `embynian-ready` 握手）；
   - **模块搜索路径自举**：mpv 只在配置目录扫描装载时把脚本目录加进 `package.path`，
     宿主经 `load-script`/`scripts` 按绝对路径装载时不会加，且此时 `mp.get_script_directory()`
     返回 nil——所以从 `debug.getinfo(1).source` 取自身目录自举，并导出全局 `script_directory`
     供 intl/char_conv/ziggy 兜底（没有这个，uosc 在第一个 require 处就静默死亡）；
   - 默认值改写为嵌入场景专用（无窗口按钮、控制条只留 mpv 自身能应答的项）；
   - 默认菜单删掉删除文件、文件打开、流画质、在线字幕下载等入口；
   - 绑定删掉 open-file / items / first / last / paste 系 / delete-file 系 /
     show-in-directory / open-config-directory / stream-quality / shuffle；
   - `handle_file_end` 与 `file_end_timer` 整块删除（连播只归宿主的 Emby 导航管，
     `pause` 观察器里的 `file_end_timer:kill()` 残留引用一并清掉——nil 索引会让 mpv
     整个销毁脚本，UI 一片空白，实测 2026-09-19）；
   - `options.autoload` 在读入配置后强制为 false。
   - 新增绑定 `embynian-ui-prev/next`（换集）、`embynian-ui-episodes`（要选集菜单）、
     `embynian-ui-versions`（要版本菜单）、`embynian-ui-picture-menu`（要画面菜单）。
     控制条 2026-09-23 按用户令重排为三组：**左下**上一集/下一集/统计/章节（有章节时）/画面菜单，
     **中下**上一章节/倍速/下一章节，**右下**选集/版本/音频/字幕，全屏在最右。四处与旧版不同：
     那颗菜单按钮开的是**画面菜单**（与集成模式的「更多」按钮、独占模式右键同一份
     `PlayerMenuCatalog`；uosc 自带的 ≡ 菜单保留，只是控制条上不再有它的入口）；音频按钮不再带
     「多音轨才显示」的条件（只有一条音轨时也在，简写自带的 `#audio>1` 徽章仍只在一轨以上标数字）；
     「版本」是 Emby 的媒体源切换，上游那颗 mpv 剪辑版本按钮（`<has_many_edition>editions`）已撤，
     免得两颗同名。上游的「视频轨」按钮由选集菜单顶替，单曲循环按钮不设（连播归宿主）。
     **绑定名带 `-ui-`、宿主消息名不带，是硬规矩**：mpv 把一条
     `script-message` 也派给同名的脚本绑定，同名就等于「这条消息把自己再叫醒一次」——
     2026-09-19 的实测事故即此（详见下节）。
   - 「轻点空白画面切换暂停」不再用 `mp.add_key_binding('MBTN_LEFT', …)`（旧版即此，已撤）：
     那是一把和 uosc 的 force 绑定抢同一个键的第二条路，命中区状态一陈旧就会把「点菜单」变成
     「暂停」。现在它是 uosc 自己的兜底命中区（动作在 main.lua 的 `embynian_click_pause_zone`，
     每帧在 `lib/utils.lua` 的 `render()` 里、早于所有元素登记一次），于是每一下点击只有一个答主。
     **含双击闸（2026-09-19，修法对齐集成模式的 TapPicture/SecondTapOnPicture）**：单击押后到 mpv
     的双击窗口之外才证实；**第二拍的「按下」当场撤掉押后那一拍**（`cursor:on('primary_down')`）。
     押后窗口＝`input-doubleclick-time`，**值由宿主按 `PictureTap.ClickDelayMilliseconds`（300 毫秒）
     写进装配**（`MpvUi.Build`）—— 集成模式那份押后是同一个常量，两种模式与用户的参考 mpv 配置
     （`C:\mpv_config-2026.08.12` 的 `inputevent.lua` 也拿这个属性做 debounce）于是同一条延迟
     （用户令 2026-09-23）。为什么撤在按下不撤在松开——真窗口实测：双击的第二拍按下当场触发 mpv 内建
     `MBTN_LEFT_DBL`＝全屏，独占全屏切换里 uosc 的 `update_fullormaxed` 会 `cursor:leave()` 把光标挪到
     无穷远，第二拍的松开过不了 ≤6px 位置闸，撤销永远轮不到跑（四条光标事件全到、提交=1、撤销=0、pause
     照翻）。mpv 自己那层无嫌疑：内建 `MBTN_LEFT` 是 `ignore`。第二拍落在控件命中区上不算双击（「点完画面
     马上去点按钮」，押后的暂停照给；控件上 mpv 的 DBL 本就被 uosc 的 ignore 闸住）。**叫醒窗口的那一下
     不作数**（`EMBYNIAN[click-pause-wake]`，用户令 2026-09-23「先点一下让窗口置顶，然后再点一下触发
     播放」）：判据是 mpv 的 `focused`（w32 的 VO 在 WM_SETFOCUS/WM_KILLFOCUS 上更新），按下时刻距
     窗口变前台 ≤0.4 秒的那一下什么都不做。代价与集成模式同款：轻点暂停比手慢一个押后窗口（300 毫秒）。
     换源/收摊（`start-file`/`end-file`）时作废押后那一拍，不打在新一集身上。
   - 「空白画面滚轮＝音量」（2026-09-19，`embynian_wheel_volume_zone`）：mpv 内建的
     `WHEEL_UP add volume 2` 会带出**左上角**那行「Volume: N%」OSD（`osd-bar=no` 拦不住它的文字，
     uosc 关掉的只是自带 OSC），接管之后音量走 `no-osd add volume 2`（步进与内建同速），反馈改由
     flash 右侧音量条承担；指针落在时间轴/速度条/音量条上时滚轮仍归它们（跳转/倍速/volume_step）。
     音量条自己改音量（拖、滚）也一并 `no-osd`（`elements/Volume.lua` 的 `EMBYNIAN[vol-osd]`）。
   - `top_bar_controls='right'`：mpv 以 border=no 无边框起播（宿主固定），系统标题栏不存在，
     顶栏画标题与最小化/最大化/关闭（关闭=quit，宿主当停止处理）。
   - `osd-width`/`osd-height` number 观察兜底：d3d11 窗口管线下 osd-dimensions 的 native 观察
     会漏掉起播初段「画布=视频尺寸→画布=窗口尺寸」的变化，导致控件可见而点击热区全错位。
2. **lib/utils.lua**：目录/播放列表导航（`get_adjacent_files`、`decide_navigation_in_list`、
   `navigate_*`）与删文件（`delete_file`、`delete_file_navigate`）整块删除；`render()` 里
   `cursor:clear_zones()` 之后多登记一个「轻点空白画面切换暂停」的兜底命中区（动作在 main.lua）。
3. **lib/menus.lua**：`open_subtitle_downloader`（OpenSubtitles 下载，curl 子进程）整块删除，
   外部 API key 不随库发布。
4. **script-opts**：不装箱。播放以 `config=no` 起播，mpv 根本不会读 script-opts；
   全部嵌入值直接写在 main.lua 的 `defaults` 表里，注释即文档。

## 宿主通道（embynian-* script-message）

uosc → 宿主（`MPV_EVENT_CLIENT_MESSAGE`，契约与解析在 `src/EmbyNian.Core/Mpv/MpvUi.cs` 的
`VideoWindowContract`）：

| 消息 | 值 | 含义 |
| --- | --- | --- |
| `embynian-ready` | uosc 版本号 | 装载握手；`LibMpvHandle` 记 `VideoWindowUiReady` 并进日志 |
| `embynian-episode` | `-1` / `1` | 换集请求 → `PlayerViewModel.StepEpisodeAsync`（Emby 单集导航） |
| `embynian-episodes` | （保留） | 要选集菜单；宿主把本季单集经 `open-menu` 推回 uosc 画 |
| `embynian-episode-index` | 1 起算序号 | 选集菜单点中的一项 → `PlayerViewModel.SwitchEpisode` |
| `embynian-versions` | （保留） | 要版本菜单；宿主把这一条目的媒体源经 `open-menu` 推回（只有一版时回一行「没有可切换的版本」） |
| `embynian-version-index` | 1 起算序号 | 版本菜单点中的一项 → `PlayerViewModel.SwitchVersion` |
| `embynian-picture-menu` | （保留） | 要画面菜单；右键、键盘菜单键与控制条那颗按钮走的是同一条绑定 |
| `embynian-menu-index` | 1 起算序号 | 画面菜单点中的一行 → `RunMenuNodeAsync`（与集成模式右键点同一行是同一句执行） |
| `embynian-seek` | 0–1 比例 | 预留扩展；当前 uosc 时间轴直接对 mpv seek，不经宿主 |

不带 `embynian-` 前缀的 script-message 一律被宿主忽略。宿主 → uosc 一条：`open-menu`（选集
菜单的 JSON，shape 与 uosc MenuData 对齐）。uosc 靠 mpv 属性观察自取其余全部状态（音量、
轨道、章节的变化会自动反映到控制窗的选择器——它们读的是同一份 mpv 状态）。

### 两套名字不许同名（2026-09-19 事故）

`script-message <名字>` 在 mpv 里**同时**喂给同名的脚本绑定。旧版那个按钮叫 `embynian-episodes`、
它发的消息也叫 `embynian-episodes`，于是：按钮 → 消息 → 按钮 → 消息……实测**一秒 1300 条**，
一晚上写了 7.5MB 日志；宿主逐条回推 `open-menu`，菜单被拆了重建一千多次/秒。用户看到的就是
「点选集卡顿／点一集不换／点击变暂停／退不出去」，字幕菜单（uosc 自家菜单）也被同一场风暴打死：
输入状态机根本没机会跑完一帧。上一集/下一集从来没事，正因为它们的绑定叫 `embynian-ui-prev/next`
而消息叫 `embynian-episode` —— 本来就不同名。

规矩：**脚本绑定 ⇒ `embynian-ui-…`，宿主消息 ⇒ `embynian-…`，两边永不同名**。仓库里有两条守卫：
`tests/EmbyNian.Tests/MpvUiTests.cs` 的「uosc 绑定名与宿主消息名不许同名」（扫源码比对契约键）
与宿主侧的 `EpisodeMenuRequestGate`（幂等请求一秒只放行几条，挡下的条数进日志）。

## 改动时的验证

1. 改完 Lua 先过编译检查：`luajit.exe -e "assert(loadfile('<文件>'))"`（本机 luajit 在
   mpv 整合包目录）。**注意这只能抓语法**——运行期 nil（如观察器回调里的残留引用）
   只在 mpv 里才炸，而 uosc 的握手在第一个 require 之前发出，「握手成功」不等于
   「脚本活着」。
2. `work/probe-uosc-window.py` 是带本地测试视频的实拍探针（PrintWindow 截窗口自身表面，
   不被遮挡/虚拟化骗）；`work/bisect6-uosc.py` 走 IPC 读 `user-data/uosc-diag` 心跳——
   若 UI 空白，先开 `--log-file` 查 Lua error，再怀疑渲染。
3. `work/probe-uosc-menu-flow.py <uosc 目录> <tag>` 是**点击流程**的无人探针（vo=null，不需要窗口）：
   它把源目录拷成 `work/probe-<tag>/uosc`（目录名必须是 uosc）、往 `main.lua` 尾部追加只存在于拷贝里
   的探针代码，然后自己扮演宿主跑一遍「点选集 → 回推 open-menu → 点一集 → 点菜单外 → 点字幕按钮」，
   报告每条消息的条数、菜单开关状态、pause 与命中等级。判据：一次点击＝**1** 条 `embynian-episodes`；
   点一集＝**1** 条 `embynian-episode-index`；点菜单外＝menu closed 且 pause 不变；空白处单击＝pause 翻转。
   报告落在 `work/probe-uosc-flow-<tag>.txt`（2026-09-19 修那五条 bug 的前后对照就是它量的）。
3. `work/probe-click-pause-wheel.py <uosc 目录> <tag>` 是**轻点/双击/滚轮**的无人探针（vo=null）：
   用 uosc 自己的入口合成事件，对着命令账数 `cycle pause` 与音量命令。判据：双击（相隔 <双击窗口）
   ＝**0** 条 `cycle pause`；单击＝**1** 条（松手后约 200ms，押后窗口的代价）；两次慢点击＝**2** 条；
   空白处滚轮＝`no-osd add volume 2` 且音量条被 flash；时间轴/速度条/音量条上的滚轮仍归元素自己。
   报告落在 `work/probe-input-<tag>.txt`。
4. `work/probe-input-osd-shot.py <uosc 目录> <tag>` 是**屏上实拍**（真窗口＋合成鼠标）：
   黑底 lavfi 源（`color=c=black` ＋ `audio-file=av://lavfi:sine`，实测唯一能 vid/aid 双全的写法），
   `PrintWindow(PW_RENDERFULLCONTENT)` 抓窗口自身表面（mpv 的 `screenshot-to-file` 不含 OSD 层，
   抓回来全黑）。判据：滚轮那一张的左上角（0,0-420,120）亮像素必须为 0（补丁前是「Volume: N%」
   白字，928 亮像素），右侧音量条区必须亮起来。报告落在 `work/probe-input-osd-<tag>.txt`，
   PNG 在 `work/shots/`。
5. `work/probe-doubleclick-real.py <uosc 目录> <tag>` 是**真窗口双击取证**（真窗口＋合成鼠标，
   含 `d3d11-exclusive-fs=yes` 的独占全屏切换）：uosc 拷贝里注入命令账/提交撤销计数/光标事件
   追踪/`input-bindings` 左键全量 dump。判据：双击后 fullscreen 翻转、pause 不变、命令账无
   `cycle pause`、撤销 ≥1（撤在第二拍按下）。**注意 pump 必须处理 MPV_EVENT_CLIENT_MESSAGE(16)**，
   只收日志会把回包全丢（第一次跑就这么哑的）。报告落在 `work/probe-doubleclick-real-<tag>.txt`。
6. 重建发布后跑 `tools/verify-publish.ps1`——它守装箱完整性（入口脚本、工具库、时间轴、
   两个字体、LGPL 文本）。
