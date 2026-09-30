---
name: "embynian-audio-output"
description: "EmbyNian 的音频输出设置（设置→音频输出）与 mpv 音频选项的落地链路：设备枚举/核对/回退、声道布局、直通、响度（音量均衡/DRC/下混归一化）、全局延迟、音量记忆，以及随包 libmpv 的无声隔离探针与设置页离线验证。Use when touching AudioSettings, MpvOutputOptions audio section, AudioDeviceCatalogue, PlayerMenuCatalog's 音频 group, the volume/delay paths in PlayerViewModel, or the 设置→音频输出 card; playback terrain is embynian-playback, settings-page traps are embynian-winui-shell."
---

# EmbyNian — 音频输出

**政策在 [CLAUDE.md](../../../CLAUDE.md)**：播放授权三档、闸门与交付。播放地形是 `embynian-playback`，设置页与行模板的坑是 `embynian-winui-shell`，证据判读是 `embynian-verification`。本文件只讲音频这一域自己的事实，全部有出处（2026-09-30 审查与修复，证据在 `work/audio-output-audit-20260930.md` 与 `work/audio-audit-20260930-native.json`；work/ 不入库，结论以此为准）。

## 地形

- **设置模型** `Core/Configuration/AppSettings.cs` 的 `AudioSettings`：Channels、PassthroughCodecs、DynamicRange、ExclusiveMode、Device、DelayMilliseconds、Volume、VolumeNormalize、NormalizeDownmix。`MaxVolume = 130` 与 mpv 自己的 `volume-max` 默认同源——客户端因此从不下发 `volume-max`，别引入第二个上限来源。
- **mpv 落地** `Core/Mpv/MpvOutputOptions.cs` 的 `Build`，音频段是唯一写手：`audio-channels`、`audio-spdif`（按目录序拼，手改的编码名过不了）、`ad-lavc-ac3drc`、`audio-device`、`audio-exclusive`、`af`、`audio-normalize-downmix`、`volume`（100 不发）、`audio-delay`（毫秒÷1000）。`audioDevice` 覆盖位是服务层核对结果：null＝不核对照设置原样（外部后端/测试），空串＝核对过、确实不在线＝什么都不发。
- **设备目录** `Core/Playback/AudioDeviceCatalogue.cs`：一次性 libmpv 句柄读 `audio-device-list`（`vo=null` 不开窗）；`Selectable` 剔除 mpv 自己的 `auto`（设置行首项「跟随系统默认设备」存空串就是它）；**非空才缓存**，空结果不缓存（枚举失败是暂态）；`UsableDevice(stored, devices)` 是回退判断本体，大小写不敏感、不在名单交回空串。
- **核对接线** `PlaybackService.ResolveAudioDeviceAsync` → `PlaybackPlanner.Plan(ticket, connection, audioDevice)` → `Build`。**只对内置后端核对**：外部 mpv.exe 的设备表是它自己那一份，拿随包 libmpv 的枚举替它做主没有依据。
- **播放器菜单** `Core/Mpv/PlayerMenuCatalog.cs` 音频组：声道、下混滤镜（`cycle-values af`）、下混归一化、独占。滤镜串与设置页同源于 `MpvOutputOptions.VolumeNormalizers`，抄第二份就是「菜单与设置页说的不是一回事」。
- **运行期音量/延迟** Shell `PlayerViewModel`（Events/Transport/Mpv 分部），Shell 程序集单测进不去——行为靠自检与离线探针，覆盖缺口要如实报。

## 支付过学费的规则

- **mpv 对指定设备失败不回退。** 随包内核（v0.41.0-923 实测）对失效端点一路报错到「no sound」，日志自己说「forced … Try unsetting it」。回退因此只能应用层做：起播前 `UsableDevice` 核对，不在线就不发。改这条链时保持测试钉住的语义：核对空串 ⇒ 起播选项无 `audio-device`。
- **`audio-device` 属性读数不是实际端点。** 自动模式下它答 `auto`；`current-ao` 只是驱动名（wasapi≠耳机/显示器/音箱）。`NoteAudioDeviceAsync` 等 `current-ao` 出现才记录，诊断页标「请求值与驱动」——别改回「实际设备」。
- **延迟双单位。** 设置存毫秒，mpv 与界面微调用秒；`Transport` 起播把 `AudioDelay` 播到全局值（同窗换片 `FilmScoped` 本来就把内核拨回新票里的全局延迟），微调从该基准相加后**绝对 set**。清零基准就是「全局 +500ms 首次微调变 +100ms」那类事故。
- **音量记忆两条护栏，缺一就是回归。** ① `_playback.CanControl` 为假的快照是缺省 100，记下它会把真设置覆盖成 100（外部无 IPC 后端的外部性）；② 只在读数与 `_kernelVolume` 不同时记——每拍都记会永远刷新结算时间戳，存盘等不到，且结算前轮询不许写回，音量条从此冻住。
- **NumberBox 清空＝NaN＝提交忽略。** 「清空不归零、设置原样」是对的，但必须可见：`SettingNumberRow.Placeholder` 跟读回的存储值走，灰字显示仍在生效的值。删掉灰字，空框就再次冒充「已归零」。
- **直通绕过处理链。** `audio-spdif` 生效时音量均衡、下混归一化、软件音量都不再作用于该轨——每个直通开关的说明都写这条代价。`dts` 与 `dts-hd` 同时勾选按 `dts-hd`（mpv 语义，非两层效果）。mpv 手册原话「There is not much reason to use this」——直通是兼容手段，不是默认建议。
- **`audio-normalize-downmix` 是防削波，不是对白增强。** 落地是 swresample 的 `rematrix_maxval`（1 或 1000），FFmpeg 把超载矩阵整体缩小；不单独抬高中置。文案已按此改口，别改回去。
- **响度滤镜两条的事实边界。** `loudnorm` 动态模式把输入重采样到 192kHz（FFmpeg `af_loudnorm` 事实）；`dynaudnorm` 跟窗、`loudnorm` 定点（−16 LUFS），都不是逐集绝对一致。设置页行与右键菜单共享同两串，加档先加目录。
- **设置页音频行没有实时推送**（字幕外观才有）：行只写设置并落盘，下一场播放生效；播放中的临时调整在画面菜单、不持久（每次播放都是 `--no-config` 的新 mpv）。页头那句生效时机是照此写的，删了它「改了却听不出变化」会被当成坏了。
- **音频组在 `SettingsPreferences` 里是「列要留的」**：`Volume`（播放器上次被留在哪儿）豁免于恢复默认/备份。往 `AudioSettings` 加偏好字段时先读那段的注释——加进「要留的」名单就是新偏好悄悄不跟随备份。

## 验证

- **随包内核无声探针** `scripts/probe-audio-native.py`：读 `option-info` 默认值与 choices（问内核，别信手册或注释）、核对候选值接受性、`cycle-values af` 循环、四个无声用例（共享 auto、失效端点、null AO 挂两条滤镜）。约束：`config=no`/`vo=null`/`video=no`，夹具是 `anullsrc` 零信号——**不发可闻信号、不申请独占、不碰用户配置**。用例可照它的形状增删；结果写 JSON，判读规则见 `embynian-verification`。
- **设置页离线验证**：`--probe-subtitles inspect`（隔离数据、无账号、无播放服务）承载**真实的**设置页，用 UIA 断言行说明、下拉档位、占位与无障碍名。注意 UIA `setValue` 对 NumberBox 不提交——聚焦后写值再失焦（或回车）才落盘，用隔离目录的 `settings.json` 判读，别信框里显示的。
- **单测位置**：`tests/EmbyNian.Tests/PlaybackTests.cs`（`UsableDevice`、Build 的 audioDevice 覆盖、Plan 贯穿；紧挨既有的设备两条）。Shell 侧行为（延迟基准、音量护栏、占位）没有单测——改动时在交付说明里点名这个缺口。
- **闸门 4 环境红的鉴别**：自检卡登录（检查数骤减、设置相关全「消失」）先别怀疑自己——按 `embynian-verification` 用改动前提交建临时工作树发布旧版 `-Exe` 对照；旧版同红＝环境（本域实例：服务器连不上），照实报红、基线不动。

## 边界

- 真实播放、独占听测、热拔插、功放直通验证走 CLAUDE.md 第三档授权；探针证明的是内核语义，不是听感。
- 参考配置（dyphire/mpv-config、本地 mpv.conf）只作对照：**注释行不是已启用**；`auto-safe` 仍是官方默认，不因别人写了 `7.1,5.1,stereo` 就当它是缺陷。本客户端两个后端都不读用户 mpv 配置。
