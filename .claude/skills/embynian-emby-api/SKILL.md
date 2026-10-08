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
- 官方 UI 壳：`https://swagger.emby.media/?api_key=<key>&url=<urlencoded 规格地址>`。那个 `api_key` 只给 UI 试调用用，不是取规格的前提。
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

`Notifications/Services`（GET，`EmbyClient.cs:460`）和 `Notifications/Services/Configured`（GET / POST / DELETE，`:470/482/486`）**在规格里都搜不到**；规格只登记了 `/Notifications/Types`、`/Notifications/Admin`、`/Notifications/Services/Test`、`/Notifications/Services/Defaults`。

**结论：规格是"服务器实现"的近似，不是全集。** 客户端在用的路径如果规格里没有，先确认是缺口，而不是自己写错。发现新的缺口就登记到 [接口清单](references/endpoints.md)。

### 3. 参数名不一样不代表接口不一样

客户端用 `{itemId}` / `{Connection.UserId}`，规格用 `{Id}` / `{UserId}`：

| 客户端 | 规格 |
| --- | --- |
| `Users/{Connection.UserId}/Items/{itemId}` | `/Users/{UserId}/Items/{Id}` |
| `Users/{Connection.UserId}/PlayedItems/{itemId}` | `/Users/{UserId}/PlayedItems/{Id}` |
| `Items/{itemId}/Images/{imageType}` | `/Items/{Id}/Images/{Type}` |
| `Users/{sourceId}/CopyData` | `/Users/{UserId}/CopyData` |

是**同一条路**。用规格校对时按结构对，别按字面量 grep —— 字面量对不上是常态。反过来，`Items/{Id}/Images/Chapter/{Index}` 没有独立条目，靠通配形式 `/Items/{Id}/Images/{Type}/{Index}/…` 覆盖。

## 别自己发明的地方

- **基址只在一处归一。** `EmbyServerAddress.Normalize` 把任何输入统一成恒以 `/emby/` 结尾的 `ApiBase`（用户粘进来的 `/emby` 先被剥掉再拼回）。所以 `EmbyUrl.Combine` 里的相对路径**不带 `/emby`**。
- **凭据进头不进 URL。** `EmbyHttp.cs:310-312` 发 `X-Emby-Authorization` + `X-Emby-Token`。`EmbyUrl.Stream` 的注释写死了"流地址刻意不带 `api_key`"，token 由 mpv 用 `--http-header-fields` 送 —— 这样它不会进代理和服务器访问日志。**不要把 `api_key` 塞回 query**，`Redact`（`EmbyHttp.cs:419`）剥 query 正是为此。
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
