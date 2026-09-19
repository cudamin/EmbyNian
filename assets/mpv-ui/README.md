# mpv-ui — 独占模式视频窗的 Lua UI（uosc 嵌入版）

独占模式（内置 libmpv 自建 D3D11 顶层窗口）起播时，`LibMpvBackend` 用 `load-script` 把这里的
uosc 装进视频窗：进度条、控制条、音量条、轨道/章节/版本菜单和暂停指示都画在 mpv 自己的 OSD 层。
集成模式的画面在 XAML 视觉树里，控件归外壳，不装载任何 Lua。

## 出处与版本

- 上游项目：https://github.com/tomasklaen/uosc ，版本 **5.12.0**（`main.lua` 里的 `uosc_version`）。
- 取自本机 dyphire/mpv-config 2026.08.12 整合包（`portable_config/scripts/uosc`），该目录就是
  上游 uosc 5.12.0 的分发拷贝。
- 许可证：**LGPL 2.1**（`LICENSE.LGPL`，随发布附带）；图标字体 Material Icons Rounded 是
  **Apache 2.0**（`fonts/LICENSE-MaterialIcons.txt`，随发布附带）。
- 装箱范围：`scripts/uosc`（main + lib + elements + intl/zh-hans 等翻译）、两个字体文件。
  其余脚本（thumbfast、quality-menu、chapterskip 等）与 `bin/ziggy` 一律不装箱。

## 与上游的差异（嵌入版裁剪）

差异集中在四处，全部以「EmbyNian 宿主独占播什么、播完去哪」为判据：

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
   - 新增绑定 `embynian-episode-prev/next`（换集）、`embynian-episodes`（要选集菜单）——
     控制条行首是上一集/下一集，随后是菜单与选集；上游的「视频轨」按钮由选集菜单顶替，
     单曲循环按钮不设（连播归宿主）。
   - `top_bar_controls='right'`：mpv 以 border=no 无边框起播（宿主固定），系统标题栏不存在，
     顶栏画标题与最小化/最大化/关闭（关闭=quit，宿主当停止处理）。
   - `osd-width`/`osd-height` number 观察兜底：d3d11 窗口管线下 osd-dimensions 的 native 观察
     会漏掉起播初段「画布=视频尺寸→画布=窗口尺寸」的变化，导致控件可见而点击热区全错位。
2. **lib/utils.lua**：目录/播放列表导航（`get_adjacent_files`、`decide_navigation_in_list`、
   `navigate_*`）与删文件（`delete_file`、`delete_file_navigate`）整块删除。
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
| `embynian-seek` | 0–1 比例 | 预留扩展；当前 uosc 时间轴直接对 mpv seek，不经宿主 |

不带 `embynian-` 前缀的 script-message 一律被宿主忽略。宿主 → uosc 一条：`open-menu`（选集
菜单的 JSON，shape 与 uosc MenuData 对齐）。uosc 靠 mpv 属性观察自取其余全部状态（音量、
轨道、章节的变化会自动反映到控制窗的选择器——它们读的是同一份 mpv 状态）。

## 改动时的验证

1. 改完 Lua 先过编译检查：`luajit.exe -e "assert(loadfile('<文件>'))"`（本机 luajit 在
   mpv 整合包目录）。**注意这只能抓语法**——运行期 nil（如观察器回调里的残留引用）
   只在 mpv 里才炸，而 uosc 的握手在第一个 require 之前发出，「握手成功」不等于
   「脚本活着」。
2. `work/probe-uosc-window.py` 是带本地测试视频的实拍探针（PrintWindow 截窗口自身表面，
   不被遮挡/虚拟化骗）；`work/bisect6-uosc.py` 走 IPC 读 `user-data/uosc-diag` 心跳——
   若 UI 空白，先开 `--log-file` 查 Lua error，再怀疑渲染。
3. 重建发布后跑 `tools/verify-publish.ps1`——它守装箱完整性（入口脚本、工具库、时间轴、
   两个字体、LGPL 文本）。
