# EmbyNian

自用的Emby客户端，由天才程序员Claude、GPT、DeepSeek、GLM完成开发。

telegram 发布频道：https://t.me/EmbyNian

界面是原生 WinUI 3，播放器内核是 libmpv。
## 设计参考

- UI 界面参考 **Emby 小秘** 设计。
- 播放器架构参考 [**mpv-winui-player**](https://github.com/ikas-mc/mpv-winui-player) 和 **小幻影视** 设计。
- 播放器控件、着色器与 mpv 相关设置参考 [**mpv-config**](https://github.com/dyphire/mpv-config) 进行设置
- 内置 [**mpv-winbuild**](https://github.com/dyphire/mpv-winbuild/releases/tag/mpv_own-2026-08-31)

## 主要功能

**着色器** — 着色器随程序打包（ArtCNN、ravu、CfL、SSimDownscaler 等），按放大倍数分四档 × 真人/动画 × 显卡档，每一档都做色度重建。

**字幕筛选** — 字幕优先级筛选，排除繁体中文，优先选择双语字幕、特效字幕。

**MoviePilot** — 通过调用MoviePilot API内置多个功能的快捷键，提高片库管理效率。

**界面** — 五套深色配色墨绿、纯黑、午夜、石墨、紫夜，首页轮播图。

### 系统要求

- Windows 10 1809 及以上，仅 x64
- Emby 4.10.x
- MoviePilot 3.x.x
- 可选：「服务器控制台」页需要 WebView2 Runtime，没有则提示改用浏览器打开

### 下载

到 [Releases](https://github.com/cudamin/EmbyNian/releases) 下载最新的 `EmbyNian_windows-x64_x.x.x.exe` 安装包。

