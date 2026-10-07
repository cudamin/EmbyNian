---
name: "embynian-audio-output"
description: "EmbyNian 的音频输出设置（设置→音频输出）与 mpv 音频选项的落地链路：设备枚举/核对/回退、声道布局、直通、响度（音量均衡/DRC/下混归一化）、全局延迟、音量记忆，以及随包 libmpv 的无声隔离探针与设置页离线验证。Use when touching AudioSettings, MpvOutputOptions audio section, AudioDeviceCatalogue, PlayerMenuCatalog's 音频 group, the volume/delay paths in PlayerViewModel, or the 设置→音频输出 card; playback terrain is embynian-playback, settings-page traps are embynian-winui-shell."
---

# EmbyNian — 音频输出

**政策在 [CLAUDE.md](../../../CLAUDE.md)**：播放授权三档、闸门与交付。播放地形是 `embynian-playback`，设置页与行模板的坑是 `embynian-winui-shell`，证据判读是 `embynian-verification`。历史音频审查与阶段报告位于本机 `work/`，不是干净检出的前提；当前行为以源码和本轮随包内核读数为准。

## 地形

- **设置模型** `Core/Configuration/AppSettings.cs` 的 `AudioSettings`：Channels、PassthroughCodecs、DynamicRange、ExclusiveMode、Device、DelayMilliseconds、Volume、VolumeNormalize、NormalizeDownmix。`MaxVolume = 130` 与随包 mpv 的 `volume-max` 默认一致，客户端不另发该上限。
- **启动选项** `Core/Mpv/MpvOutputOptions.cs` 的 `Build` 音频段统一下发声道、直通、DRC、设备、独占、滤镜、下混归一化、音量和延迟。`audioDevice` 覆盖位：null＝未核对，照设置原样；空串＝本次名单未找到，省略 `audio-device`，尝试系统默认。名单可能为空或枚举失败，不能把空串解释成确证设备离线。
- **设备目录** `Core/Playback/AudioDeviceCatalogue.cs`：每次 `LoadAsync` 重新枚举，只共享尚在执行的读取；`Known` 是最近完成的一份展示快照（包括空名单），不能用于起播在线判定。一次性原生句柄禁配置/自动脚本/视频，安全选项失败中止；`Selectable` 去掉 mpv 自己的 `auto`。设置下拉空串就是「跟随系统默认设备」。
- **核对接线** `PlaybackService.ResolveAudioDeviceAsync` → `PlaybackPlanner.Plan` → `Build`。只核对内置后端；外部 mpv 有自己的设备表，不能拿随包枚举替它做主。设置行明确这一限制，也不保证播放中拔插或端点被占用时一定有声。
- **播放器菜单** `Core/Mpv/PlayerMenuCatalog.cs` 音频组与设置页共享 `MpvOutputOptions.VolumeNormalizers`，不能复制第二份滤镜串。
- **运行期音量** `VolumeMemory` 管理确认值、UI旧回声窗口、待保存版本与结算时间；`PlayerViewModel` 负责UI线程与持久化。`PlaybackService.SetVolumeAsync` 收命令确认，失败或无控制通道不能把用户要求值当成成功音量。
- **临时音频延迟** `PlaybackService.ChangeAudioDelayAsync` 在同一播放句柄/代次上串行发 `add audio-delay` 或绝对 `set`，读回成功后再让 `PlayerViewModel` 更新数值。字幕延迟不是本轮改动范围，仍需字幕阶段独立审查。

## 支付过学费的规则

- **失效端点不是 mpv 自己回退。** 随包 v0.41.0-923 对强制指定的不存在设备报「no sound」。应用层起播前重新枚举，不在本次表里就省略设备选项；不能把上次非空表缓存一辈子，也不改用户保存的选择。
- **请求值不是真实端点。** `audio-device=auto` 只表示请求自动；`current-ao=wasapi` 只表示驱动。诊断保留「请求值与驱动」措辞，不改称实际耳机/音箱。
- **音量必须是真读数。** `PlayerStatus.VolumeKnown` 由内外后端在收到有限音量值后置真；默认100、未装载和无控制状态不能持久化。首次实际100也需要发状态通知。UI输入须确认，迟到确认不得覆盖已收到的更新原生值。
- **保存时钟不属于页面。** 独占/uosc播放没有挂载的WinUI播放页。音量变化后由VM自己的可取消延时结算；相同回声不推迟时钟，原生连调保留最新值。写盘失败保留同一待保存版本、可重试并限频，不能先清pending再假装成功。换片/停止/关闭及时结算；新片观察重置不丢失败待存值。
- **延迟有双单位。** 全局设置毫秒，mpv临时值秒。起播UI种到全局值；微调交给内核相对相加，不能从UI缓存加完再绝对写回。清零才是绝对set 0。拒绝或读不回时不显示虚假的成功值；新片不接旧片的读回和排队命令。
- **NumberBox清空不归零。** NaN提交忽略，灰字保留当前设置；真实失焦/回车后的配置文件才是保存证据，UIA setValue返回成功不是提交成功。
- **直通绕过处理链。** 音量均衡、下混归一化和软件音量不作用于直通轨；DTS和DTS-HD同开按DTS-HD处理，不是叠两层效果。
- **下混归一化是防削波，不是对白增强。** 它缩小混音矩阵，不单独抬高中置。`loudnorm`动态模式会重采样到192kHz，`dynaudnorm`与`loudnorm`也都不保证每集绝对同响度。
- **音频设置页不实时推送。** 改动从下次播放生效；临时滤镜、声道、延迟不保存，音量记忆是明确例外。同窗换片按 `InlineSwitch.FilmScoped` 恢复该场选项，不是假定每场都重建mpv进程。
- **备份/恢复保留本机音量。** `SettingsPreferences` 复制音频偏好后恢复目的地 `Volume`；新增字段先确认它是偏好还是运行记忆，不照旧注释猜名单。

## 五层验证

1. **规则与假后端**：`AudioOutputTests` 覆盖枚举新鲜度/并发/失败恢复、VolumeMemory时序与护栏、音量命令确认、延迟相对/清零/失败/换片；`PlaybackTests` 保留Build/Plan设备覆盖与音频选项；`SettingsTests`、`InlineSwitchTests` 覆盖清洗/备份与同窗复位。先编译当前测试，不运行旧程序集。
2. **随包内核无声采集**：`py .claude/skills/embynian-audio-output/scripts/probe-audio-native.py --out <新报告路径>`。记录DLL哈希、初始化返回码、默认值/候选、滤镜循环、延迟连调与四个零信号用例。固定禁配置/脚本/视频/独占；选项或日志订阅失败中止并留不完整报告，不覆盖旧报告。`test-probe-audio-native.py` 是不加载DLL的安全/失败夹具。采集器退出0仅代表采集完整，必须逐项判读；失效端点预期失败与其他失败分开。
3. **隔离Shell接线**：当前正式测试项目仍只引用Core；需要真实VM或控件时可用独立WinUI宿主、独立AppPaths、假HTTP/后端。`--probe-subtitles inspect`也能承载真实设置页，但不证明音频服务接线。阶段6辅助宿主在本机 `work/stage6-shell-probe`，不是干净检出的正式入口，复用前先读并核对资源与当前源代码。
4. **视觉与操作**：检查音频页受影响宽度、下拉与NumberBox输入/失焦/清空/保存失败提示；用实际隔离设置文件核对落盘。无声探针不验证听感、真实功放、独占音频、设备热拔插或所有DPI。
5. **项目门禁**：按CLAUDE.md执行适用构建、格式、测试、发布与自检。独立树发布不更新桌面快捷方式。普通闸门4可能使用真实账号，不是离线；旧版同红仅说明症状之前存在，不证明环境归因或未执行路径没回归，不能改基线洗绿。

## 边界

真实服务器播放、独占听测、热拔插和功放直通验证须按主规程授权。两个后端都不读用户mpv配置；参考配置的注释不是已启用选项，`auto-safe`默认也不是因为他人选了多声道列表就变成缺陷。
