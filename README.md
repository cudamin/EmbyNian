# EmbyNian

这是一个个人自用的客户端，在天才程序员Claude、GPT、DeepSeek、GLM的协助下完成开发。

telegram 发布频道：https://t.me/EmbyNian

界面是原生 WinUI 3，播放内核是 libmpv。解压即用，

## 设计参考

- UI 界面参考 **Emby 小秘** 设计。
- 播放器架构参考 [**mpv-winui-player**](https://github.com/ikas-mc/mpv-winui-player) 和 **小幻影视** 设计。
- 播放器控件、着色器与 mpv 相关设置参考 [**mpv-config**](https://github.com/dyphire/mpv-config) 进行设置
- 内置 [**mpv-winbuild**](https://github.com/dyphire/mpv-winbuild/releases/tag/mpv_own-2026-08-31)

## 主要功能

**画质** — 着色器随程序打包（ArtCNN、ravu、CfL、SSimDownscaler 等），按放大倍数分四档 × 真人/动画 × 显卡档，每一档都做色度重建。

**字幕** — 字幕优先级筛选，排除繁体中文，优先选择双语字幕、特效字幕。

**浏览** — 主页轮播大图，电影页面、剧集页面融合徽标、背景图、艺术图、横幅。

**界面** — 五套深色配色（墨绿、纯黑、午夜、石墨、紫夜），点一下立即生效。

## 自定义快捷键

| 按键 | 作用 |
| --- | --- |
| 空格 / `←` `→` / `↑` `↓` | 播放暂停 / 快退快进 / 音量 ±5 |
| `F` / `Esc` / `M` / `T` | 全屏 / 退出 / 静音 / 窗口置顶 |
| `P` / `N` | 上一集 / 下一集（跨季） |
| `PageUp` / `PageDown` | 上一章节 / 下一章节 |
| `[` `]` / `Backspace` | 倍速 ±0.1 / 回到 1.0 |
| `Z` / `X`（加 `Shift` 反向） | 字幕 / 音频延迟 ±0.1 秒 |
| `Y` / `N` | 出现跳过按钮时：确认跳过 / 关闭该按钮 |

以上都能在 **设置 → 快捷键** 里改绑（支持 `Ctrl` / `Alt` / `Shift` 组合键，只在播放窗口内生效）；`Esc` 和 `Y` 固定不可改。跳过按钮出现时 `N` 临时用来关掉它（不跳转），其余时候 `N` 仍是「下一集」。


### 系统要求

- Windows 10 1809 及以上，仅 x64
- 服务器：Emby 4.10.x
- 可选：「服务器控制台」页需要 WebView2 Runtime，没有则提示改用浏览器打开

### 下载

到 [Releases](https://github.com/cudamin/EmbyNian/releases) 下载最新的 `EmbyNian_windows-x64_x.x.x.exe` 安装包。

