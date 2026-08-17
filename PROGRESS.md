# 开发进度

最后更新：2026-08-17

## 已完成（第一阶段 · v1 单项目）

- [x] 创建 .NET 8 Windows 桌面项目
- [x] EMBY 4.9.x 登录与令牌持久化
- [x] 媒体库、目录、搜索与列表浏览
- [x] 调用指定 mpv 播放 EMBY 原始视频流
- [x] mpv.conf 图形化全量编辑
- [x] input.conf 图形化全量编辑
- [x] UTF-8/GBK 编码识别
- [x] 配置保存前按时间自动备份
- [x] README 使用说明

v1 源码已移到 `legacy/v1/`。其中的配置编辑页面没有搬进 v2：解析、UTF-8/GBK 识别和带时间戳的备份仍在 `Core/Mpv`（`MpvConfigDocument`、`MpvConfigFile`），也有测试覆盖，但目前没有任何界面调用它们。

## 已完成（第二阶段 · v2 重写）

- [x] 拆成 Core / App / Tests 三个项目，Core 不引用 WinForms，整个解决方案零 NuGet 依赖
- [x] 全自绘深色界面：调色板、绘制工具、自定义控件、PerMonitorV2 DPI
- [x] 获取海报并改成海报墙布局
- [x] 首页分区：继续观看、接下来播放、媒体库、最近添加
- [x] 内置 libmpv 后端，视频直接渲染在客户端窗口内；外部 `mpv.exe` 后端保留，可在设置里切换
- [x] 自绘播放控制栏：整宽进度条、章节刻度、悬停时间气泡与 Emby 章节缩略图、选集/音轨/字幕/着色器浮层、音量、统计信息、全屏
- [x] 播放进度上报、继续观看、已看状态
- [x] 剧集季/集详情与字幕、音轨选择
- [x] 着色器配置组：自动生成配置包、按分辨率与动画类型挑选、播放过程中切换
- [x] 多服务器与多账号记忆，登录页可从服务器公开用户列表里选人
- [x] 密码与访问令牌用 DPAPI 加密保存
- [x] 设置页改动后自动保存，`settings.json` schema v2 带备份与坏文件隔离
- [x] `--self-check` / `--self-check --dump-ui` 自检，配合 `smoke.ps1` 冒烟脚本
- [x] 单实例运行，第二次启动激活已有窗口
- [x] 手写离线测试宿主，`dotnet run` 即可跑
- [x] 应用图标 `app.ico`

## 未完成

- [ ] 常用 mpv 参数改为下拉框、开关、滑块等专用控件 —— 设置页已经用下拉框、开关和数字输入框承载客户端自己的设置，但没有滑块，v2 也还没有 mpv.conf/input.conf 的编辑页面，按原意这项仍未完成
- [ ] 服务器发现 —— 多用户选择已完成，局域网广播搜索还没有写
- [ ] 生成安装包 —— 应用图标已完成，安装包脚本（Inno Setup / WiX / NSIS 之类）还没有

## 继续开发入口

项目目录：`C:\Users\89400\Desktop\EmbyMpvClient`

先执行 `dotnet build .\EmbyMpvClient.sln` 和 `dotnet run --project .\tests\EmbyMpvClient.Tests\EmbyMpvClient.Tests.csproj` 验证；改过界面再跑一遍 `--self-check --dump-ui` 看页面截图，然后根据本文件未完成项继续。
