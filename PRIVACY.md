# EmbyNian 隐私政策

最后更新：2026-10-08

## 一句话

EmbyNian 是一个本地播放客户端。**开发者不收集、不上传、不分享你的任何数据**；应用只连接你自己配置的服务器，信息都留在你自己的电脑上。

## 1. 适用范围

本政策适用于 Windows 桌面应用 EmbyNian（Microsoft Store 包身份 `MOMOKA.EmbyNian`）。应用运行在你自己的设备上，配合你自己搭建或你已获授权使用的 Emby 媒体服务器工作。

## 2. 开发者收集的数据

**没有。**

应用不包含遥测、使用统计、广告、崩溃上报或任何回传开发者服务器的代码，也不访问开发者运营的任何服务。

## 3. 应用在你设备上保存的数据

全部保存在本机，默认位置是 `%LOCALAPPDATA%\EmbyNian`：

| 内容 | 位置 | 说明 |
| --- | --- | --- |
| 服务器地址、账号名、界面与播放设置 | `settings.json` | 明文保存地址、账号名和各项设置 |
| 密码、访问令牌 | `settings.json` | **用 Windows 数据保护接口（DPAPI）加密**，只有本机当前 Windows 账户能解密；不保存明文 |
| 设置备份 | `settings.backup.json` | 不含密码与令牌 |
| 日志 | `logs\` | 请求地址只记到「协议 + 主机 + 路径」，查询参数被去掉；密码与令牌不写日志 |
| 海报与背景图缓存 | `cache\images\` | 从你自己的服务器下载的图片 |
| 着色器编译缓存 | `cache\shaders\` | 本机生成 |
| 截图 | `screenshots\`（可在设置里改到别处） | 你在播放时主动截的图 |
| 下载的媒体文件 | 你指定的目录（默认「视频」文件夹） | 你主动发起的下载 |
| 服务器控制台页的浏览数据 | `webview2\` | 内嵌 WebView2 打开你服务器自己的网页控制台时产生 |

应用还会读取本机已安装字体列表（用于字幕字体选择），仅在本机显示，不外传。

## 4. 网络连接

应用只与以下地址通信，全部由你配置，或由你自己的服务器在数据里给出：

1. **你自己的 Emby 服务器** —— 你在应用里填的地址。用于登录、读取媒体库、上报播放进度、同步续播位置与「看过」标记。
2. **你自己配置的第三方片库管理服务**（可选功能） —— 你在应用里填的地址。用于搜索、订阅与整理。
3. **你的服务器在元数据里给出的图片地址** —— 海报与背景图可能由你的服务器指向第三方图片服务（例如 `image.tmdb.org`）。应用只是按服务器给出的地址取图，不自行决定去哪个第三方。

发往 Emby 服务器的请求带有这些标识：客户端名 `EmbyNian`、**本机计算机名**、随机生成并保存在本机的设备 ID、应用版本号，以及你的访问令牌。这些只发给**你自己的服务器**，用途是让服务器认出「这是哪台设备」。

**应用不会把上述任何内容发送给开发者或任何第三方。**

## 5. 我们不做什么

- 不收集个人信息、使用统计或诊断数据
- 不含广告、内购或用户追踪
- 不向开发者回传任何数据
- 不读取与功能无关的文件

## 6. 儿童

应用不面向儿童，也不收集任何年龄相关信息。

## 7. 数据删除

- 卸载应用**不会**删除第 3 节列出的数据。
- 要彻底清除：删除 `%LOCALAPPDATA%\EmbyNian` 整个目录，以及你自己设置的截图目录与下载目录。
- 要撤销服务器上的登录：在你的 Emby 服务器上删除对应的设备或访问令牌。

## 8. 第三方组件

应用内包含以下第三方组件，各自遵循其许可，随包附带许可与归属文本（`shaders\` 与 `mpv-ui\` 目录下）：mpv / libmpv（GPL-3.0 / LGPL）、uosc 与 Material Icons（MIT / Apache-2.0）、ArtCNN / ravu / CfL / SSimDownscaler 等着色器（LGPL），以及 Microsoft Windows App SDK 与 WebView2 Runtime。

这些组件同样不收集数据，不改变本政策。

## 9. 政策变更

本政策如有修改，会更新本文件顶部的日期，并在项目仓库中留下记录。

## 10. 联系方式

问题或请求请通过项目仓库的议题页提出：<https://github.com/cudamin/EmbyNian/issues>

---

# EmbyNian Privacy Policy

Last updated: 2026-10-08

EmbyNian is a Windows desktop client for an Emby media server that you own or are authorized to use.

**The developer collects, uploads, and shares no data.** The app contains no telemetry, analytics, advertising, or crash reporting, and does not contact any service operated by the developer.

Everything the app stores stays on your machine, under `%LOCALAPPDATA%\EmbyNian`: your server address, account name and preferences (`settings.json`); your password and access token, encrypted with the Windows Data Protection API (DPAPI) so that only the current Windows account on this machine can decrypt them; logs, in which request URLs are recorded without query parameters and credentials are never written; a poster and backdrop image cache; a shader compilation cache; screenshots you take; and WebView2 browsing data created by the embedded server console page. The app also reads the local font list for subtitle font selection; that information is only displayed locally.

The app connects only to: (1) your own Emby server, at the address you enter, to sign in, browse your library, report playback progress, and sync resume positions; (2) a third-party library-management service that you configure yourself, if you enable that feature, at the address you enter; and (3) image URLs that your own server supplies in its metadata (for example `image.tmdb.org`). Requests to your Emby server carry the client name `EmbyNian`, this computer's machine name, a randomly generated device ID stored locally, the app version, and your access token — sent only to your own server, so it can identify this device.

Uninstalling the app does not delete the data listed above; delete the `%LOCALAPPDATA%\EmbyNian` folder (and any screenshot or download folders you configured) to remove it. To revoke access, remove the corresponding device or token on your Emby server.

Bundled third-party components — mpv / libmpv, uosc and Material Icons, the ArtCNN / ravu / CfL / SSimDownscaler shaders, and the Microsoft Windows App SDK and WebView2 Runtime — follow their own licenses, with license and attribution texts shipped alongside the app. They do not collect data.

Questions: <https://github.com/cudamin/EmbyNian/issues>
