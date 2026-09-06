# EmbyNian

telegram发布频道：https://t.me/EmbyNian

Emby 4.9.x 的 Windows 桌面客户端，纯AI开发。



当前版本 0.0.1。

## 系统要求

- Windows 10 1809 及以上，仅 x64（只在 Windows 11 上验证过）
- 发行版自包含运行时，解压即用；没有安装程序，也没有自动更新
- 服务器：Emby 4.9.x
- 可选：「服务器控制台」页需要 WebView2 Runtime，没有则提示改用浏览器打开


## 功能


- **画质**：着色器随程序打包（ArtCNN、ravu、CfL、SSimDownscaler 等 15 个文件），按放大倍数分四档 × 真人/动画 × 显卡档，每一档都做色度重建；DVD 代片源自动加去色带和降噪；超过 30 fps 自动让出 CNN 放大器；mpv 自带的 `fast` / `high-quality` 预设不受开关影响

## 快捷键

播放时：

| 按键 | 作用 |
| --- | --- |
| 空格 / `←` `→` / `↑` `↓` | 播放暂停 / 快退快进 / 音量 ±5 |
| `F` / `Esc` / `M` / `T` | 全屏 / 退出 / 静音 / 窗口置顶 |
| `P` / `N` | 上一集 / 下一集（跨季） |
| `PageUp` / `PageDown` | 上一章节 / 下一章节 |
| `[` `]` / `Backspace` | 倍速 ±0.1 / 回到 1.0 |
| `Z` / `X`（加 `Shift` 反向） | 字幕 / 音频延迟 ±0.1 秒 |
| `Y` | 确认跳过片头 / 片尾 |

浏览时 `Alt+←` / `Alt+→` 前进后退，卡片上 `Shift+F10` 出右键菜单。鼠标：滚轮调音量，单击暂停，双击全屏，拖浮层顶栏移动窗口。触摸：点一下暂停，双击全屏，长按出菜单。


## 许可与开发

打包的着色器和 `vulkan-1.dll` 各有上游许可，逐文件列在 `assets/shaders/README.md` 和 `assets/mpv-runtime/README.md`；播放内核是 mpv 的 libmpv。本仓库自身尚未声明许可。

架构、构建、发布与 `--self-check` 自检见 [`docs/开发与验证.md`](docs/开发与验证.md)，规矩在 [`CLAUDE.md`](CLAUDE.md)，在途工作在 [`PROGRESS.md`](PROGRESS.md)。
