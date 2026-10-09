---
name: "momoka-subtitles"
description: "Audit, fix and verify Momoka subtitle settings and playback behavior. Use for subtitle language selection, forced/foreign-audio modes, ASS/SRT/PGS styling, bilingual or secondary subtitles, color inputs, previews, external subtitle management, settings persistence and same-window episode switching; also use when comparing subtitle features with mpv.conf, dyphire/mpv-config, IINA or mpv.net."
---

# Momoka 字幕审查、修复与验证

先读仓库 [CLAUDE.md](../../../CLAUDE.md)。播放边界、凭据、并行写入和交付要求以它为准；当前命令、开关和报告路径见 [开发与验证](../../../docs/开发与验证.md)。本技能提供字幕专项方法，不另立安全或发布政策。

字幕选择、外观和服务器字幕管理由本技能负责。涉及后端、管线、播放生命周期或 uosc 时联读 [momoka-playback](../momoka-playback/SKILL.md)；Emby 请求和鉴权见 [momoka-emby-api](../momoka-emby-api/SKILL.md)，设置页与控件接线见 [momoka-winui-shell](../momoka-winui-shell/SKILL.md)，验证结果和截图的判读见 [momoka-verification](../momoka-verification/SKILL.md)。

## 先判断任务类型

- 用户只问“审查／缺什么／是否正常”：只给评估，不直接改代码。默认只读，工作要点保留在对话中；只有任务授权了验证或文件报告时才构建、运行探针或写报告，纯审查不发布应用。
- 用户明确要求修复：只修已确认问题，增加双字幕面板、AI、独立下载源等属于扩展，不混进缺陷修复。“补功能”先盘点已有能力，只有明确了具体新增范围、依赖与副作用才实施；不能从参考项目整包推定授权。

## 用一条完整链确认功能

对每个选项记录：**界面含义 → 内存设置 → 保存/归一化 → 起播参数 → 实时应用 → 复位/换片**。

主要定位点（使用前核对仍存在）：

| 范围 | 入口 |
| --- | --- |
| 设置界面 | `src/Momoka.Shell/ViewModels/SettingsViewModel.cs` 的 `SubtitleCard`、`Live`；`Views/SettingsPage.xaml` |
| 外观与默认值 | `Core/Configuration/AppSettings.cs`、`SettingsMigration.cs`；`Core/Mpv/MpvOutputOptions.cs` |
| HDR 字幕亮度 | `Shell/ViewModels/SettingsViewModel.Video.cs`、`Core/Mpv/HdrOptions.cs` |
| 自动选择 | `Core/Playback/TrackSelection.cs`、`TrackLanguagePriority.cs`、`KeywordFilter.cs` |
| 映射与规划 | `Core/Mpv/MpvTrackMap.cs`、`Core/Playback/PlaybackPlanner.cs`、`Core/Emby/ItemDetail.cs` |
| 实时/换片 | `Core/Playback/PlaybackService.cs`、`Core/Mpv/InlineSwitch.cs` |
| 预览和颜色 | `Core/Playback/SubtitlePreviewPlan.cs`、`Core/Infrastructure/HtmlColorSelection.cs`、`Shell/Views/HtmlColorPicker.xaml.cs` |
| 服务器字幕管理 | `Shell/Views/SubtitleDialog.xaml.cs`、`ItemCommands.Server.cs`、`Core/Emby/ServerSubtitles.cs` |
| 独占次字幕 | `assets/mpv-ui/scripts/uosc/main.lua`、`lib/menus.lua` |

表内 Core、Shell 分别代表 `src/Momoka.Core`、`src/Momoka.Shell`。

不要根据陈旧注释判断功能不存在。尤其要区分：

1. 内置 libmpv 与外部 mpv.exe 后端。
2. 集成管线与独占 uosc 管线。
3. 全局持久偏好与本片临时调整。
4. 内封文本、外挂文本、内封图形和服务器无法提供的外挂图形轨。
5. “未选中字幕 sid=no”与“轨道已选但 sub-visibility=no”。

## 自动选择的关键不变量

- 先解析用户最终选定的音轨，再判断 `ForeignAudioOnly`。默认中文、手选日语和反方向都要测试。
- `其他字幕` 是字幕候选通配项，不代表用户能听懂任何语言；不能参与“是否为外语”的判断。
- 自动候选必须可加载。`MpvTrackMap` 无编号的轨道不能成为看似成功的自动结果；下拉应禁用并解释，而非只写日志。
- `SubtitleChoice.None` 与 `Off` 不可混用。未知/无候选与明确关闭有不同含义。
- 语言、标题评分、forced/default 标记是不同维度。当前字幕语言仅看语言字段；标题“候补”是软降权，不擅自恢复旧的标题猜语言或硬排除。
- 明确的默认轨回退和任意语言兜底不是一回事。检查无默认、空语言表、回退关闭、强制模式及不可用默认轨的组合。
- 更新实际选择时也更新“自动”行的说明；不能实际播日语、下拉却还说按中文音轨不显示字幕。

## 外观：测内核，别照抄注释

查询随包内核的 `mpv-version`、`option-info/<name>/default-value`、`choices` 与范围；版本不同，默认值和别名也可能不同。

- `sub-ass-override=scale` 保留 ASS 自带样式，但允许缩放。字体、颜色等“没变”可能是适用范围，不是参数没发送。
- PGS/VobSub 是图片，字体/文字颜色不能像 SRT 一样修改。强制 ASS 样式可能破坏特效或定位，应明确说明。
- `opaque-box` 是每行描边盒＋阴影盒：分别使用 outline 色和 back 色。名字中的 opaque 不能推导出所有颜色必须 100% 不透明。
- `background-box` 包住所有文字行，使用 back 色，shadow offset 在此控制背景留白。使用两行不同长度字幕验证几何。
- 背景颜色与透明度是独立用户输入。清空颜色后仍须明确透明度是继续生效还是被禁用，不能保留一个可拖但无效的滑块。
- HEX/RGB 精确输入保持原值；整数 HSV 只能作为显示值或用户主动编辑 HSV 时的输入。典型回归值：`#123456`、`#AC5D5D`、`#FE0102`。内核颜色用 `#AARRGGBB` 字节下发，三位小数 RGB 会产生一阶色差；透明度量化到最近的 8 位 alpha。
- WinUI `TextChanged` 延后触发，不能仅靠赋值期间的 `_quiet` 抑制回填；同步 `TextChanging` 记录是否用户输入，异步阶段再刷新预览和保存，避免在布局事件里改视觉树。捕获丢失要取消未提交的拖色。
- 预览的相对缩放要跟随设置。预览不是 libass 真帧，不把字形/实际屏幕大小近似伪装成精确效果。
- 未指定值的实时路径要显式恢复默认。仅停止发送一个选项不会清除播放中已设置的旧值。

## 生命周期与危险操作

- 恢复默认、导入备份与逐项编辑应共用外观应用逻辑；检查画面、预览和持久值是否一致。同片外观批次按编辑版本串行，先收集已知默认值再写；明确不支持的选项跳过，读数未知或写入拒绝在应用其余已知选项后返回可见的部分失败，不猜默认值、不无声废弃整批。同窗换片必须并入完整 `SubtitleStyleOptions`，不能只复位菜单碰到的两项。
- 备用源重试可以清除源相关的流索引，不可撤销用户明确关闭字幕。缺字幕元数据不等于缺音轨信息，已知母语仍先执行 `ForeignAudioOnly` 的关闭规则。
- `track-list/selected` 同时包含主次字幕；上报使用 `main-selection`（0 主、1 次）、容器 `ff-index` 或实际 `external-filename`，未知/本地外挂不猜服务器索引。`PlaybackTrackState` 保存本场读数，关闭为 -1，未知为 null。
- 保存失败须留下可见的未保存状态和重试入口。重试保存当前内存值，不重新导入、不重复删除，也不静默回滚整份真实配置。
- 同窗换片按完整可写属性清单复位，包含 uosc 控制的主次可见性、次字幕索引/延迟/位置/样式。裸 mpv 默认不会替客户端复位全部属性。
- 区分 `sub-remove` 的“本次卸载”与 Emby DELETE 的“服务器文件删除”。服务器删除显示媒体文件、字幕名、影响和不可撤销性，确认前请求数必须为零。`ServerSubtitles` 固定登录 scope 和文件；确认后重读完整字幕快照，变化或无法区分的同标签文件拒绝删除。GET/DELETE 不是原子操作，不宣称防住服务器并发替换。
- 字幕写入和后续刷新期间不允许关闭、重复下载或并发删除。响应不明先刷新，刷新成功后通知父页重读，不通过重复写请求“修复”；`Touched` 与 `NeedsRefresh` 分别表达写入完成和需要更新父页。
- WinUI 同一 UI 线程不能叠两个 ContentDialog。已有字幕管理对话框内使用明确的内联确认或关闭后串联确认；取消优先，不让 Enter 默认执行删除。

## 参考开源项目的方式

只读用户指定的配置，不导入或覆盖它。引用固定版本/提交，区分已发布版本与开发分支。

- [mpv 文档](https://mpv.io/manual/stable/)：用与实际内核匹配的提交解释语义。
- [dyphire/mpv-config](https://github.com/dyphire/mpv-config)：参考操作分类、主次字幕和兼容项；下载、同步、AI 需要脚本，不是原生参数。
- [IINA](https://github.com/iina/iina)：参考分组、主次控制、精确同步、编码后重载。
- [mpv.net](https://github.com/mpvnet-player/mpv.net)：参考参数目录和提示，不信任未核对的旧默认值。
- [mpv-sub-select](https://github.com/CogentRedTester/mpv-sub-select)：参考音轨/字幕规则配对和可选重选逻辑。

不要把本地 `sub-auto=fuzzy`／字幕目录扫描直接套到 Emby HTTP 流；不要默认固定 GB18030 处理所有非 UTF-8 字幕。

## 验证阶梯

1. **纯规则测试。** 先编译当前 Core＋测试，再跑 runner。相关套件位于 `tests/Momoka.Tests/`：`SubtitleBehaviorTests`、[SubtitleManagementTests](../../../tests/Momoka.Tests/SubtitleManagementTests.cs)（切服、注销与删除前快照核对）、`SubtitlePreviewTests`、`HtmlColorTests`、`ServiceTests`、`PlaybackTests`、`InlineSwitchTests`。
2. **裸内核。** 无媒体上下文读取参数；需要测换片时只用本地临时 SRT＋合成画面，禁用户配置、脚本和网络。明确裸内核不等于完整 uosc 验收。
3. **真实控件离线探针。** `--probe-subtitles` 在真实设置加载前分流，使用临时默认设置、假操作委托和禁止 HTTP 的处理器。`inspect` 留屏，供原生桌面工具检查；用 `--screen`、`--theme`，不要与播放/导航/自检开关混用。
4. **视觉和 UI 操作。** 使用可用的官方 Computer Use 技能，先语义定位。验证取消仍留字幕、未保存/重试、双行背景盒、窄窗口与缩放。没有挂树的 TextBox 可能不发 TextChanged；先让真实控件 Loaded，再检查结果，别直接放宽断言。
5. **项目交付门禁。** 按当前 CLAUDE.md 执行；测试与桌面探针串行。已有失败照实报，不因为归因旧版就称全绿。比较旧发布件时说明它是否已经包含部分在途修改。

详细组合见 [验证矩阵](references/test-matrix.md)。

## 汇报格式

先说结果，再列：修复范围、实际通过/失败/跳过数、发布是否更新、未覆盖部分、可复现证据路径。审查时分开“已确认缺陷”“已有但入口分散”“新功能建议”。不以测试数量代替用户可见行为，也不把预览断言当内核渲染真值。
