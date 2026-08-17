# Emby MPV Client

面向 Emby 4.9.x 的现代化 Windows 桌面客户端，基于 .NET 8 WinForms，用 mpv 播放原始视频流。
整个解决方案不引用任何 NuGet 包，只依赖 .NET 8 共享框架，可离线构建。

## 功能

- 使用 Emby 用户名和密码登录，记住多个服务器与账号，可从服务器的公开用户列表里挑人
- 密码和访问令牌都用 Windows 帐户密钥（DPAPI）加密后保存，只能在当前 Windows 帐户下解开
- 首页分区展示「继续观看」「接下来播放」「媒体库」「最近添加」，海报墙浏览媒体库、目录、剧集与搜索结果
- 详情页提供季/集列表、媒体源与音轨、字幕选择、从上次位置继续播放、标记已观看/未观看、收藏
- 内置 libmpv 播放：`libmpv-2.dll` 直接渲染在客户端窗口里，无需外部窗口；也可以在设置里切回外部 `mpv.exe`（命名管道 IPC）
- 自绘播放控制栏：整宽进度条、章节刻度、拖动条悬停时间气泡与 Emby 章节缩略图，选集/音轨/字幕/着色器浮层，音量、统计信息、全屏
- 着色器配置组：按分辨率和「动画」判定自动挑选配置组，播放过程中也能随时切换
- 播放进度上报、续播位置与「看过」标记同 Emby 双向同步
- 自绘深色界面、侧边栏导航、PerMonitorV2 DPI，画质与快捷键仍由你自己的 `mpv.conf`、`input.conf` 决定
- 诊断模式 `--self-check`（可加 `--dump-ui`）与 `smoke.ps1` 冒烟脚本

## 架构

解决方案是 `EmbyMpvClient.sln`，三个项目：

- `src/EmbyMpvClient.Core`：Emby 访问层、设置与凭据保险箱、mpv 配置读写，以及两个播放后端 —— 内置的 `LibMpvBackend` 和外部进程的 `MpvProcessBackend`。刻意不引用 WinForms 与 System.Drawing，所以能在纯控制台里测。
- `src/EmbyMpvClient.App`：WinForms 外壳与自绘深色界面 —— `Views/` 页面、`Controls/` 自绘控件、`Theme/` 调色板与绘制、`Composition/` 组装根与自检。
- `tests/EmbyMpvClient.Tests`：手写的离线测试宿主，`dotnet run` 本身就是 runner，不用任何 NuGet 包。

第一代单项目源码（`MainForm.cs`、`MediaLibraryView.cs`、`ConfigEditorView.cs`、`UiTheme.cs`、`EmbyApiClient.cs` 等）保留在 `legacy/v1/`，只作参考，不参与构建。

## 构建与运行

```powershell
dotnet build .\EmbyMpvClient.sln
```

```powershell
dotnet run --project .\src\EmbyMpvClient.App\EmbyMpvClient.App.csproj
```

跑测试：

```powershell
dotnet run --project .\tests\EmbyMpvClient.Tests\EmbyMpvClient.Tests.csproj
```

发布单文件版本：

```powershell
dotnet publish .\src\EmbyMpvClient.App\EmbyMpvClient.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\publish
```

发布后会自动把着色器包、mpv 依赖 dll 和 `libmpv-2.dll` 一起复制到输出目录，内置播放器不再依赖外部 mpv 安装。

## 自检与冒烟

`--self-check` 不显示窗口，逐个构建、布局并绘制每个页面和控制栏的每个浮层，同时检查 mpv 路径、配置文件、着色器配置组是否还对得上。报告写在
`%LOCALAPPDATA%\EmbyMpvClient\logs\selfcheck.txt`，全部通过返回 0，有问题返回 1。

```powershell
.\EmbyMpvClient.exe --self-check
```

加上 `--dump-ui` 会把每个页面和浮层的截图写进 `%LOCALAPPDATA%\EmbyMpvClient\logs\ui`：

```powershell
.\EmbyMpvClient.exe --self-check --dump-ui
```

自检刻意跳过真实登录和首屏加载，这部分交给冒烟脚本：启动一段时间后报告进程是否还活着，并打印本次运行的日志。

```powershell
.\smoke.ps1
```

```powershell
.\smoke.ps1 -Release
```

## 数据与路径

数据目录是 `%LOCALAPPDATA%\EmbyMpvClient`：

- `settings.json`（schema v2）与 `settings.backup.json`；文件损坏时会被隔离并回退到默认设置，读取永不抛异常
- `logs\app-yyyyMMdd.log` 运行日志，界面里的「日志」页看的是同一份内容
- `cache\images` 海报与缩略图缓存

首次启动在登录页填写 Emby 地址、用户名和密码；mpv 可执行文件与 `mpv.conf`、`input.conf` 路径在「设置 → mpv 播放器」里改。用内置后端时，程序首次运行会把
`mpv.conf`、`input.conf`、着色器目录和依赖 dll 复制到程序目录各存一份，之后就不再读外部 mpv 的配置目录。
