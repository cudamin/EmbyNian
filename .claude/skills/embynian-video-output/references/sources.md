# 参考来源与依据

改视频输出/HDR/杜比选项、怀疑某个默认值或语义、或要新增选项时按需读这里。**先用正文里那条 ctypes 探针问随包内核，再用这些来源交叉核对**——所有文档都可能过时，内核查询是唯一现场事实。

## 外部参考（各能回答什么、各有什么坑）

- [dyphire/mpv-config](https://github.com/dyphire/mpv-config)（[mpv.conf](https://github.com/dyphire/mpv-config/blob/master/mpv.conf)）— Windows 下 mpv 配置的成熟参考。值得借鉴：条件配置（`profile-cond`）、HDR 目标判断、去色带低/中/高三档（本机 `DebandStrength` 三档的数值 1/48/16、2/64/24、3/64/24 就来自它的 DeBand-low/medium/high）。**坑：本项目用户的本地配置与它的 master 并不相同**（本地 `hwdec=no` + `profile=SSIM`，上游 `auto-copy-safe` + NNEDI3，fps-fix 条件也不同）——不能把上游当前行为当成用户本地播放器的行为，更不能照抄整份。
- [本地 mpv.conf](C:/mpv_config-2026.08.12/portable_config/mpv.conf) — 用户自己的配置（程序目录外、不入库、随机器存在）。它是「装机默认从哪来」的历史出处之一（如插值关闭阈值 120、去色带三档数值）。读它回答「当初为什么是这个数」，改行为仍以现行代码与内核为准。
- [mpv.net 设置目录](https://github.com/mpvnet-player/mpv.net/blob/master/src/MpvNet.Windows/Resources/editor_conf.txt)（仓库镜像 `work/mpvnet-ref/`）— 成熟播放器怎么给 mpv 选项分类、怎么写说明（缩放器与位深独立、官方文档入口）。**坑：它的一些默认值描述有历史痕迹**（如 dither-depth 的 auto 语义），与随包内核 `option-info` 冲突时以内核查询为准。
- [IINA 的 HDR 实现](https://github.com/iina/iina/blob/develop/iina/VideoView.swift) — 学思路不搬代码：HDR 能力检测（gamma/primaries 判定）、映射算法与目标峰值的用户设置、ICC 与 HDR 的互斥协调、「检测失败退回自动」。它是 macOS EDR/CGColorSpace，Windows/D3D11 没有对应物。
- [mpv 官方选项说明（master）](https://mpv.io/manual/master/) — 选项语义的权威出处。**核对随包内核时用钉版本的快照**：亮度/输出参数见 [options.rst @ 7b8915bc1](https://github.com/mpv-player/mpv/blob/7b8915bc1/DOCS/man/options.rst)（`target-peak`、`hdr-reference-white`、`sub-hdr-peak`、`image-subs-hdr-peak`、`target-colorspace-hint(-mode)`），即随包 libmpv v0.41.0-923-g7b8915bc1 的源码树。**升级 libmpv-2.dll 后这份钉版要重钉，选项先用探针重验。**
- [ArtCNN](https://github.com/Artoriuz/ArtCNN) — 动画放大链的上游。C4F16/C4F32 是实时档；Chroma 系列模型只有 ONNX，GLSL 色度重建因此走 CfL。链与档位的取舍归 `mpv-shader-quality`，这里不重复。

## 仓库内的依据（改 HDR/杜比相关代码先看这些落点）

- [HdrOptions.cs](../../../src/EmbyNian.Core/Mpv/HdrOptions.cs) — HDR 参数生成：色彩空间接管判定（`OwnsColorSpace`）、映射到 SDR 与 HDR 输出两套、四个亮度事实、杜比/HDR10+ 元数据 vf。
- [PlayerMenuCatalog.cs](../../../src/EmbyNian.Core/Mpv/PlayerMenuCatalog.cs) — 播放中临时菜单（「HDR 相关」组：映射曲线、动态映射、输出模式、传输特性、参考白；以及插值/截图行的现行写法）。
- [MpvOutputOptions.cs](../../../src/EmbyNian.Core/Mpv/MpvOutputOptions.cs) — 选项目录与 `Build`：每个下拉的值域、每个说明对应发出的选项名。
- [MpvProfiles.cs](../../../src/EmbyNian.Core/Mpv/MpvProfiles.cs) / [ShaderSwitch.cs](../../../src/EmbyNian.Core/Mpv/ShaderSwitch.cs) — 画质预设展开与运行时还原的契约。
- [LibMpvPipelinePolicy.cs](../../../src/EmbyNian.Core/Playback/LibMpvPipelinePolicy.cs) / [MpvRenderCheck.cs](../../../src/EmbyNian.Core/Mpv/MpvRenderCheck.cs) — 管线锁定的 vo/gpu-api 与按事实问的兼容性检查。

**杜比视界控制说明**：内核侧入口是 [vf.rst @ 7b8915bc1](https://github.com/mpv-player/mpv/blob/7b8915bc1/DOCS/man/vf.rst) 的 `format` 滤镜（`dolbyvision`、`hdr10plus`、`enhancement-layer`、`min-luma`/`max-luma`）；元数据随 `--target-colorspace-hint-mode` 的说明（options.rst 同上，`source-dynamic` 不发送完整杜比元数据）。
