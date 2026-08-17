# Emby MPV Client

面向 Emby 4.9.x 与本地 MPV 的现代化 Windows 桌面客户端，基于 .NET 8 WinForms。

## 功能

- 使用 Emby 用户名和密码登录，仅持久化访问令牌，不保存密码
- 以海报墙浏览媒体库、目录、剧集与搜索结果
- 双击视频后调用指定的 `mpv.exe` 直接播放原始视频流
- 图形化编辑 `mpv.conf` 和 `input.conf`
- 自动识别 UTF-8/GBK 编码，保存配置前自动备份
- 现代深色界面、侧边栏导航和独立页面架构

## 架构

- `MainForm.cs`：应用外壳、导航和页面流程编排
- `MediaLibraryView.cs`：媒体库、搜索、海报加载和播放
- `ConfigEditorView.cs`：MPV 配置编辑器
- `ConnectionView.cs`：服务器账户及本地路径设置
- `UiTheme.cs`：统一主题和可复用控件样式
- `EmbyApiClient.cs`：Emby API 访问层

## 构建与运行

```powershell
dotnet run --project .\EmbyMpvClient.csproj
```

发布单文件版本：

```powershell
dotnet publish .\EmbyMpvClient.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

首次启动后进入“连接与路径”，填写 Emby 地址、用户名、密码及 MPV 路径。

设置保存在 `%LOCALAPPDATA%\EmbyMpvClient\settings.json`；密码不会写入磁盘。
