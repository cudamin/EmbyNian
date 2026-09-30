---
name: "embynian-video-output"
description: "EmbyNian 的视频输出设置（基础输出、HDR 与杜比视界、画质与着色器三卡）与 mpv 选项的落地链路：选项书写顺序、内置管线锁定的 vo/gpu-api、画质预设展开、HDR/杜比语义、抖动/去色带/插值的真实开关、运行时着色器切换契约，以及五层验证阶梯（规则测试→裸内核→离线探针→视觉操作→交付门禁）。Use when touching VideoSettings, MpvOutputOptions, HdrOptions, MpvProfiles, ShaderSwitch, LibMpvPipelinePolicy, MpvRenderCheck, the 画面菜单 catalog, or the 设置→视频输出 cards; shader chain/tier content is mpv-shader-quality."
---

# EmbyNian — 视频输出与 HDR/杜比设置

**Policy lives in [CLAUDE.md](../../../CLAUDE.md)**（验证闸门、播放授权、凭据）。着色器链、档位表与每个文件的门控是 [mpv-shader-quality](../mpv-shader-quality/SKILL.md)；planner、进度上报与两条后端是 [embynian-playback](../embynian-playback/SKILL.md)。这里只写**设置页到 mpv 的落地**与它独有的坑；`assets/shaders/README.md` 仍是着色器文件事实的唯一出处。

## 选项的书写顺序 = 优先级（后者覆盖前者）

`PlaybackPlanner.BuildPlayerOptions` 依次叠四层，任何一层写别人拥有的选项就是「界面在骗人」：

1. `MpvBaseline` —— 客户端地板（hr-seek、sub-auto=no、icc-profile-auto=no、截图目录……）。
2. `MpvOutputOptions.Build` —— 画质预设（一句 `profile=`）→ 基础输出 → `HdrOptions.Build` → 音频 → 字幕外观。
3. 着色器链选项（`ShaderGroup.ToMpvOptions`）—— 三缩放器 + 前置条件归链。
4. `LibMpvPipelinePolicy.Build` —— 管线契约，**最后写、后赢、Required 失败即中止**（不回退别的呈现路径）。

设置页每行说明必须与最终发出的选项一致；改一行先想清楚它在哪一层、会被谁覆盖。

## 内置播放器的渲染后端是锁定的

`LibMpvPipelinePolicy` 强制 `vo=gpu-next`、`gpu-api=d3d11`（集成与独占同值；常量 `ForcedRenderer`/`ForcedApi` 是唯一出处，Build 与检查共用）。

- 设置页对内置后端**不提供**视频渲染/图形接口两行——只有一行只读 Fact（`SettingsViewModel.Video.cs` 的 `VideoCard`）；切到外部 mpv 后端才出现下拉（`RefreshVideoBackend` 挂在播放后端那行的 after 上）。
- `MpvRenderCheck.Problems` 按真正会跑的值问：内置传 `pipelineOwned: true`，渲染不匹配的问题不再产出，compute 提醒的建议是「降显卡档/关着色器/改外部 mpv」，不许叫用户去改一个不存在的设置。hwdec 两条后端都从设置来。
- 别试图给内置解锁 vulkan：合成/交换链契约钉在 D3D11，测试钉着「默认 Vulkan 不能覆盖 D3D11」。

## 画质预设是 profile=，不是展开的选项

`profile=fast/high-quality` 只是一句 profile 名。**任何要「还原到预设」的路径必须先 `MpvProfiles.Expand`**——读运行中播放器的 `profile-list` 属性（JSON）把 profile 展开成真实选项；读不到或 profile 缺失/循环就抛异常退回安全路（换片回「停掉重开」）。

- `ShaderSwitch.Options`（切档回落）与 `InlineSwitch.FilmScoped`（换片拨回）都吃 `profilesJson`。实测复现过的错误：不展开时关链会把 fast 的 `scale=bilinear` 还原成出厂 `lanczos`，页面还显示着 fast。
- profile 名不存在 mpv 直接退出什么都不播——预设目录必须穷举内置那三档，测试钉着。
- 别把出厂默认硬抄进代码当「还原值」：问 `option-info/<名>/default-value`（对随包内核实测过，注释里抄文档的默认值错过三处）。

## HDR 与杜比（HdrOptions.Build）

`OwnsColorSpace`（HDR 片源 + tonemap/passthrough）为真时 HDR 模式**接管输出色彩空间**，此时 `icc-profile-auto` 不发（ICC 会覆盖输出目标）。

- **映射到 SDR**：锁 `target-trc=bt.1886`、`target-prim=bt.709`；**不发**用户的 `target-peak`——那是 HDR 输出的上限，不是 SDR 映射的上限。
- **HDR 输出**：`target-colorspace-hint=auto` + `mode=target`（显示器能力优先，目标不行仍有色调映射兜底）；**不强制 `tone-mapping=clip`**——旧实现把截高光当「直通」卖，已改。`hdr-compute-peak` 保持可用。
- `target-peak`、`hdr-reference-white`、`sub-hdr-peak`、`image-subs-hdr-peak` 是四个独立事实：输出峰值、SDR 白、文字字幕/OSD 白、图形字幕白（内核默认 1000），不许合并成一个「HDR 亮度」。
- 杜比/HDR10+ 元数据开关走 `vf=@embynian-hdr:format=dolbyvision=no[:enhancement-layer=no][:hdr10plus=no]`。**关元数据不是 HDR10 回退**（Profile 5 关掉会偏色），说明里要这么说；增强层开关不代表所有 Profile 7 FEL 已完整支持。
- 数值行的行型是 `SettingOptionalNumberRow`（自动勾选＋NumberBox，范围夹紧，Probe 自检）；nits 范围 10–10000 是内核的，不是拍脑袋。

## 容易写错的那几行

- **抖动**：`dither-depth` 才是开关；`DitherDepth` 8/10 覆盖 auto；「继承画质预设」≠关闭（预设可能带 dither=no）。
- **去色带强度**三档对应 low 1/48/16、medium 2/64/24、high 3/64/24（数值来自参考配置 DeBand-low/medium/high）。hdeband 只跟**老片源轴**（高度≤576）走、与放大倍数无关；链里有 hdeband 时必须 `deband=no`（`ShaderChainRules` 盯着）。
- **插值**必须配 `display-*` 同步。`ResolveSync` 是唯一决策点：设置页说明读它、planner 发它；`VideoSync=audio` + 插值 → 显式发 no 并带原因句。菜单里的插值行是「开启（同时使用显示同步）」radio＋「关闭」，不是单个 cycle。
- **反交错**是 `DeinterlaceMode` no/auto/yes；`yes` 强制处理逐行片源（旧 bool 已迁移）。
- 视频输出三卡的说明统一说「下次播放生效」——设置页本来就不动正在播的片子，「此刻生效」是谎话（自检钉着这句话）。

## 运行时切着色器（两条管线同一套）

`PlaybackService.SetShaderGroupAsync`：读 `profile-list` + 各 `option-info/default-value` 做还原基线 → `ShaderSwitch.Options` → 经 `IPlayerControl.CommandAsync` 逐条 `set` 并**核对返回值**；某条被拒或读不到基线就返回 false。UI（`ChangeShaderGroupAsync`）成功才钉住档位并提示，失败提示「未能完整应用」——不许没核对就报成功。

独占模式走 uosc 菜单：契约键是 `VideoWindowContract.Shader`（`embynian-shader`），值域收窄为 `off`/`auto`/八档 id；`Parse` 拒收其余值。菜单行按 DFS 序号对 `PlayerMenuCatalog.Commands`，推送菜单带 active 高亮。

## 验证阶梯

从下往上走：上一层没过不碰下一层，哪一层能回答问题就停在哪一层。

1. **纯规则测试。** 先编译当前 Core＋测试项目，再跑 runner（`--no-build` 只认刚编译出来的产物）。相关套件：`PlaybackTests`（输出／计划／运行条件／管线契约）、`SettingsTests`（迁移与 Normalize、恢复默认）、`InlineSwitchTests`（换片签名、FilmScoped、预设展开回落）、`MpvUiTests`（契约键、绑定名与消息名不同名）。

2. **裸内核。** 无媒体上下文问选项：`py` ctypes 隔离句柄（`config=no, vo=null, ao=null, video=no, audio=no, load-scripts=no, force-window=no, terminal=no`）读 `option-info/<名>/default-value`、`/choices`、`/min`、`/max`；试设值，0=接受、负数=拒绝（`screenshot` 是命令不是选项，-5 属预期）；`profile-list` 读回 JSON 数组 `[{name, options:[{key,value}]}]`，即 `MpvProfiles.Expand` 的输入形状。需要媒体上下文的（换片、预设展开的运行期行为）只用本地临时合成素材，禁用户配置、脚本和网络。**裸内核不等于完整 uosc／独占窗口验收。**

3. **真实控件离线探针。** 现有三条播放探针（`--probe-composition`、`--probe-cursor`、`--probe-player-motion`）固定走集成管线，不覆盖独占／uosc／外部 mpv——相关改动如实报覆盖缺口。字幕批次已立了先例 `--probe-subtitles [inspect]`：启动前分流隔离数据、临时默认设置、假操作委托、禁止 HTTP 的处理器，`inspect` 留屏供手工／UIA 检查，与自检共用桌面互斥须串行——给视频输出新增探针照这个边界做；在此之前，设置层验证走自检的假行探针（`SettingOptionalNumberRow.Probe`、`SettingChoiceRow.Probe`、精简模式探针），不碰屏上那一页。留屏检查也可用 `--show-settings 视频输出`（配 `tools/shot.ps1` 或 `--dump-ui`），配 `--screen`、`--theme` 指定屏与主题；这些开关不与播放／导航／自检混用。

4. **视觉和 UI 操作。** 用可用的官方 Computer Use 技能，先语义定位（UIA／AutomationId；本项目 x:Name 即句柄，新控件跑 `tools/scan-automation-ids.js`）。视频输出上值得亲眼看的：三卡同分类的渲染与行模板、自动／手动数值行的切换与夹紧、切后端后两行下拉的出现／消失、着色器 A/B（统计页读数＋截图，规矩见 mpv-shader-quality）、独占着色器子菜单观感。行没挂树就量会读到空（模板／折叠的既有陷阱）：先让真实控件 Loaded（走查选中那一分类）再检查结果，别直接放宽断言。

5. **项目交付门禁。** 按当前 CLAUDE.md 的四道闸门执行；测试与自检／桌面探针**串行**（共用一个互斥体）。已有失败照实报红，不因为归因旧版就说全绿；比较旧发布件时说明它是否已包含部分在途修改。发布（闸门 3）守多窗口规则：当天已有别的会话发布过就不重发，说明桌面快捷方式仍是旧版。

## 参考来源与依据

对照上游、核对默认值语义或新增选项时用这些（每条「能回答什么、有什么坑」详见 [references/sources.md](references/sources.md)）：

- dyphire/mpv-config — <https://github.com/dyphire/mpv-config>（[mpv.conf](https://github.com/dyphire/mpv-config/blob/master/mpv.conf)）
- 用户本地 mpv.conf — `C:/mpv_config-2026.08.12/portable_config/mpv.conf`（机器上的文件，不入库）
- mpv.net 设置目录 — <https://github.com/mpvnet-player/mpv.net/blob/master/src/MpvNet.Windows/Resources/editor_conf.txt>
- IINA 的 HDR 实现 — <https://github.com/iina/iina/blob/develop/iina/VideoView.swift>
- mpv 官方选项说明 — <https://mpv.io/manual/master/>；随包内核（v0.41.0-923-g7b8915bc1）钉版：亮度/输出参数 [options.rst @ 7b8915bc1](https://github.com/mpv-player/mpv/blob/7b8915bc1/DOCS/man/options.rst)，杜比视界控制 [vf.rst @ 7b8915bc1](https://github.com/mpv-player/mpv/blob/7b8915bc1/DOCS/man/vf.rst)
- ArtCNN — <https://github.com/Artoriuz/ArtCNN>

仓库内依据：[HdrOptions.cs](../../../src/EmbyNian.Core/Mpv/HdrOptions.cs)（HDR 参数生成）、[PlayerMenuCatalog.cs](../../../src/EmbyNian.Core/Mpv/PlayerMenuCatalog.cs)（HDR 临时菜单）、[MpvOutputOptions.cs](../../../src/EmbyNian.Core/Mpv/MpvOutputOptions.cs)（选项落地）。规矩两条：**内核查询先于任何文档**；本地配置 ≠ dyphire master，上游行为不能当本地行为。

## 改这些设置时的连带

- 三卡同挂「视频输出」一个分类：`Cards` 按 Category 分组，自检走查按组走；新增分区不用改名单，新增行要过 `SettingRowTemplates` 的模板选择器（没有 case 的行静默不画）。
- 值要进 `SettingsMigration.Normalize`（Choice 目录 + Clamp），手改的 settings.json 才拦得住；`SettingsReset`/备份恢复走反射逐属性拷贝，新字段自动跟上。
- 硬解目录、渲染器目录的每一项都会逐字发给 mpv——mpv 不认识的选项直接退出，加一项前先用上面的探针验一次。
