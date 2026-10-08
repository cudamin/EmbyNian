---
name: "embynian-emby-api"
description: "EmbyNian 与 Emby Server 之间的 REST 协议层：请求基址与鉴权头、重定向与凭据边界、错误到异常的映射、超时与下载约定，以及客户端实际调用的接口清单、规格缺口和容易猜错的路径。Use when touching Core/Emby/EmbyHttp.cs, EmbyUrl.cs, EmbyApiException.cs, EmbyServerAddress.cs, EmbyClient*.cs, or anything that builds an Emby URL; feature behavior belongs to the domain skills and docs/媒体库管理.md, docs/用户管理.md."
---

# EmbyNian — Emby Server REST 协议层

规则归 [CLAUDE.md](../../../CLAUDE.md)：授权、凭据、闸门与交付以它为准。本技能只记协议层本身容易误判的地方，不另立安全或发布政策。

`src/EmbyNian.Core/Emby/` 是与服务器对话的全部落点。**功能语义**在领域技能和 `docs/媒体库管理.md`、`docs/用户管理.md`；这里只讲"请求怎么发出去、失败怎么读"。这两份文档声明的对照基准是同一台服务器的同一份规格 —— 本技能是它们背后的协议面。

## 开始之前：先拿规格，别猜

服务器自己在基址下暴露完整 OpenAPI：

```
GET {ApiBase}openapi          # 例如 http://host:8896/emby/openapi
```

- **不需要任何凭据**：实测不带 `api_key`、不带 `X-Emby-Token` 也返回 200。
- 实测（2026-10-08，本机那台）：3.5 MB、OpenAPI `3.0.1`、`info.version` = `4.10.0.40`、434 条路径 / 500 个操作 / 340 个 schema。**这是观测值不是常量** —— 服务器升级就变，用之前重取一次。
- 官方 UI 壳：`https://swagger.emby.media/?url=<urlencoded 规格地址>`，仅用于匿名查看规格。不要把令牌填入这个第三方站点或 URL；需要鉴权的核对，通过进程内请求头直接访问受信任的服务器，并遵守 CLAUDE.md 的操作授权范围。
- 落盘快照在 `work/emby-openapi.json`（`work/` 已被 gitignore），只作离线对照，不作事实来源。

## 地形

| 文件 | 管什么 |
| --- | --- |
| `EmbyHttp.cs` | 唯一发 HTTP 的地方：逐请求头、超时、重定向、错误翻译、下载 |
| `EmbyHttpRedirect.cs` | 跳转策略与凭据边界 |
| `EmbyApiException.cs` | 异常层次：`EmbyApiException` / `EmbyAuthenticationException` / `EmbyTokenExpiredException` / `EmbyUnreachableException` |
| `EmbyServerAddress.cs` | 用户输入 → `ApiBase`（**恒以 `/emby/` 结尾**） |
| `EmbyUrl.cs` | 拼 URL 的纯函数：流、字幕、图片、章节图 |
| `DeviceIdentity.cs` | 设备身份与 `X-Emby-Authorization` 那行的拼法 |
| `EmbyClient.cs` + `.Libraries` / `.Users` / `.Dashboard` / `.MoviePilot` | 分部类，按领域分组的方法 |

## 三件反直觉的事

### 1. 客户端不调用 `/Items/{Id}/PlaybackInfo`

规格里它存在（GET + POST），但全仓没有一处调用。播放地址是 `EmbyUrl.Stream` 直接拼出来的：

```
Videos/{itemId}/stream{ext}?Static=true&MediaSourceId=…
```

`Static=true` 是让服务器原样吐文件而不是转码。**将来要接转码决策、直连判断或媒体源切换时，`PlaybackInfo` 才是入口 —— 但现在没有**，别照着规格假设已经有了。

### 2. 规格有缺口，客户端在用规格里没有的接口

本地 `4.10.0.40` 规格未登记客户端使用的 `GET Notifications/Services`、`GET / POST / DELETE Notifications/Services/Configured`，也未登记通知配置选项使用的 `GET Users` 和 `GET Library/VirtualFolders`。后两条分别由 `GetNotificationUsersAsync`、`GetNotificationLibrariesAsync` 调用，返回列表，不能直接替换成返回分页结果的查询接口。

**规格缺项不证明接口不存在，源码调用也不证明当前服务器支持它。** 分别记录源码请求、规格覆盖和实测证据；没有实测时保留待核实状态。发现新的缺口就登记到 [接口清单](references/endpoints.md)。

### 3. 参数名不一样不代表接口不一样

客户端用 `{itemId}` / `{Connection.UserId}`，规格用 `{Id}` / `{UserId}`：

| 客户端 | 规格 |
| --- | --- |
| `Users/{Connection.UserId}/Items/{itemId}` | `/Users/{UserId}/Items/{Id}` |
| `Users/{Connection.UserId}/PlayedItems/{itemId}` | `/Users/{UserId}/PlayedItems/{Id}` |
| `Items/{itemId}/Images/{imageType}` | `/Items/{Id}/Images/{Type}` |
| `Users/{sourceId}/CopyData` | `/Users/{UserId}/CopyData` |

是**同一条路**。用规格校对时按结构对，别按字面量 grep —— 字面量对不上是常态。章节图使用规格已登记的 `/Items/{Id}/Images/{Type}/{Index}`，其中 `Type=Chapter`；不要把它写成带任意尾部的通配路由。

## 别自己发明的地方

- **基址只在一处归一。** `EmbyServerAddress.Normalize` 把任何输入统一成恒以 `/emby/` 结尾的 `ApiBase`（用户粘进来的 `/emby` 先被剥掉再拼回）。所以 `EmbyUrl.Combine` 里的相对路径**不带 `/emby`**。
- **凭据通过头传递，传递通道同样要核对。** `EmbyHttp.SendAsync` 为每次请求设置 `X-Emby-Authorization` 和 `X-Emby-Token`，`EmbyUrl.Stream` 不带 `api_key`。内置后端由 `LibMpvBackend` 通过进程内 API 设置 `http-header-fields`；外部后端先由 `MpvProcessBackend` 校验 IPC 服务端属于刚启动的进程，再发送 `MpvArgumentBuilder.LoadCommands` 构造的请求头和加载命令。不要将令牌放入 URL、启动参数或日志；具体链路见 [播放技能](../embynian-playback/SKILL.md)。`EmbyHttp.Redact` 去掉日志 URL 的 query，不能代替这些传递边界。
- **逐请求头，别动 `DefaultRequestHeaders`。** v1 在每次调用前改它，一并发取海报就自己跟自己抢（`EmbyHttp.cs:13-16`）。
- **路径段要过守卫。** `LibraryId`（`EmbyClient.Libraries.cs:120`）和 `UserSegment`（`EmbyClient.Users.cs:130`）拒绝 `.`、`..` 和含 `/ \ ? #` 的 id，再 `Uri.EscapeDataString`。新写把 id 拼进路径的方法时照抄这两个，别直接插值。

细节：[传输层与鉴权](references/http-and-auth.md)、[接口清单](references/endpoints.md)。

## 验证

- 新增或修改请求路径时，先 `GET {ApiBase}openapi` 校对；规格里没有的，确认是缺口并登记进本技能。
- 传输层行为（重定向、401 分类、`.part` 清理、日志脱敏）有单测覆盖，改 `EmbyHttp` / `EmbyHttpRedirect` / `EmbyUrl` 后跑测试项目，而不是只跑界面检查。
- 活服务器核对走只读；写操作按 CLAUDE.md 的授权档位来。
- 报告实测、未覆盖与失败项；不把历史失败当成通过，不用改基线消除未经解决的红项。

## 交付

改到协议层时同步本技能与 [docs/开发与验证.md](../../../docs/开发与验证.md) 的相关命令。`docs/媒体库管理.md`、`docs/用户管理.md` 声明了对照的服务器版本，**换基准时一起更新**。
