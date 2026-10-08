# 接口清单

两栏：**客户端在用**（从源码提取，不是从规格抄的）和**规格缺口**。规格随服务器版本变，用前按 [SKILL.md](../SKILL.md) 的"开始之前"重取一次；这份清单是 2026-10-08 对 `4.10.0.40` 校对的结果。

路径写成规格的参数名（`{Id}` / `{UserId}`），客户端源码里是 `{itemId}` / `{Connection.UserId}` —— 同一件事，见 SKILL.md「参数名不一样不代表接口不一样」。

## 浏览与条目

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| GET | `/Users/{UserId}/Views` | 媒体库视图 |
| GET | `/Users/{UserId}/Items` | 主查询：筛选、排序、分页 |
| GET | `/Users/{UserId}/Items/{Id}` | 单条目详情 |
| GET | `/Users/{UserId}/Items/Resume` | 继续观看 |
| GET | `/Users/{UserId}/Items/Latest` | 最新入库 |
| GET | `/Items` | 跨库查询（无用户维度） |
| GET | `/Items/{Id}/Similar` | 相似条目 |
| GET | `/Items/{Id}/CriticReviews` | 影评 |
| GET | `/Shows/NextUp` | 下一集 |
| GET | `/Shows/{Id}/Seasons` · `/Shows/{Id}/Episodes` | 季 / 集列表 |
| POST | `/Items/{Id}` | 更新条目（元数据编辑） |
| POST | `/Items/{Id}/Refresh` | 刷新元数据 / 图片 |
| DELETE | `/Items/{Id}` · POST `/Items/Delete` | 删除条目（两种写法都在） |
| GET | `/Items/{Id}/Download` | 下载到设备 |
| POST / DELETE | `/Collections/{Id}/Items` | 合集增删条目 |

## 图片

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| GET / POST / DELETE | `/Items/{Id}/Images/{Type}` | 条目图片：读 / 传**原始字节** / 删 |
| GET | `/Items/{Id}/Images/{Type}/{Index}/…`（通配） | 章节图，客户端拼 `Items/{itemId}/Images/Chapter/{index}` |
| GET | `/Items/{Id}/RemoteImages` | 远程图片候选 |
| POST | `/Items/{Id}/RemoteImages/Download` | 下载远程图片 |
| GET / POST / DELETE | `/Users/{Id}/Images/{Type}` | 用户头像：传的是 **Base64 文本**，不是原始字节 |

图片 URL 由 `EmbyClient.ImagePath`（`EmbyClient.cs:662`）统一拼，带 `maxWidth` / `quality` / `tag`。**`tag` 是缓存键**，别在没换图时改它。

## 字幕

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| GET | `/Items/{Id}/RemoteSearch/Subtitles/{Language}` | 按三字母语言码搜字幕，带 `MediaSourceId` |
| POST | `/Items/{Id}/RemoteSearch/Subtitles/{SubtitleId}` | 下载并挂上，带 `MediaSourceId` |
| DELETE | `/Items/{Id}/Subtitles/{Index}` | 删外挂字幕轨（内嵌的删不掉，服务器会拒） |
| GET | `/Videos/{Id}/{MediaSourceId}/Subtitles/{Index}/Stream.{Format}` | 取外挂字幕文件 |

**`{SubtitleId}` 必须 `Uri.EscapeDataString`**：那是字幕插件自己造的记号，含斜杠时不转义会把整条路由打断（`EmbyClient.cs:686-688`）。语言码同理。

## 播放

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| GET | `/Videos/{Id}/stream` | 原始流，客户端加 `Static=true` + `MediaSourceId` |
| POST | `/Sessions/Playing` | 开始播放 |
| POST | `/Sessions/Playing/Progress` | 进度 |
| POST | `/Sessions/Playing/Stopped` | 停止 |

正文由 `PlaybackReport` 构造 —— Emby **忽略它不认识的字段**，所以加字段比想象中安全。播放事件经 `Sessions/Playing*` 报给服务器，由服务器上装的通知服务转发出去；客户端不直接对通知服务说话。

## 用户状态

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| POST / DELETE | `/Users/{UserId}/PlayedItems/{Id}` | 标记已看 / 未看 |
| POST / DELETE | `/Users/{UserId}/FavoriteItems/{Id}` | 收藏 / 取消 |
| POST | `/Users/{UserId}/Items/{Id}/HideFromResume` | 从续播里隐藏 |

这三条走 `PostForJsonAsync` / `DeleteForJsonAsync`：响应体是新的 `UserItemDataDto`，读得到就用，读不到不算失败。

## 媒体库管理

| 方法 | 路径 | 备注 |
| --- | --- | --- |
| GET | `/Library/VirtualFolders/Query` | 分页读库列表，`StartIndex` / `Limit` / `TotalRecordCount` |
| POST | `/Library/VirtualFolders` | 新建；`name` / `collectionType` / `refreshLibrary` 走 query，选项走 `LibraryOptions` 正文 |
| POST | `/Library/VirtualFolders/LibraryOptions` | 存库选项；正文 `Id` + `LibraryOptions` |
| POST | `/Library/VirtualFolders/Name` | 重命名；正文 `Id` + `NewName` |
| POST | `/Library/VirtualFolders/Delete` | 移除库 |
| POST | `/Library/VirtualFolders/Paths` | 加目录（`refreshLibrary=true`） |
| POST | `/Library/VirtualFolders/Paths/Update` | 改目录映射与凭据 |
| POST | `/Library/VirtualFolders/Paths/Delete` | 删目录 |
| GET | `/Libraries/AvailableOptions` | 新建时的默认选项，按 `LibraryContentType` / `IsNewLibrary` |
| GET | `/Library/MediaFolders` · `/Library/SelectableMediaFolders` | 库目录 / 用户可选库 |
| POST | `/Library/Refresh` | 扫描全部媒体库 |
| POST | `/Items/{libraryId}/Refresh` | 单库扫描；`MetadataRefreshMode` / `ImageRefreshMode` / `ReplaceAll*` |
| GET | `/ScheduledTasks` | 任务列表 |
| POST | `/ScheduledTasks/Running/{Id}` | 启动任务 |
| POST | `/ScheduledTasks/Running/{Id}/Delete` | **停止**任务 —— 停止用 `Delete`，不是 DELETE 方法 |
| GET / POST | `/System/Configuration/{key}` | 命名配置，如 `metadata` |

## 环境浏览（新建库选目录）

| 方法 | 路径 |
| --- | --- |
| GET | `/Environment/DefaultDirectoryBrowser` |
| GET | `/Environment/Drives` |
| GET | `/Environment/NetworkDevices` |
| POST | `/Environment/DirectoryContents`（带 `Username` / `Password`） |
| POST | `/Environment/ValidatePath`（带 `ValidateWriteable`） |
| GET | `/Environment/ParentPath`（**返回纯文本字节**，不是 JSON） |

## 用户管理

| 方法 | 路径 | 备注 |
| --- | --- | --- |
| POST | `/Users/AuthenticateByName` | 登录，拿 token |
| GET | `/Users/Query` | 分页读用户 |
| GET | `/Users/Public` | 公开用户列表 |
| POST | `/Users/New` | 新建；复制数据用 `CopyFromUserId` + `UserCopyOptions` |
| GET / POST / DELETE | `/Users/{Id}` | 读 / 改 / 删 |
| POST | `/Users/{Id}/Policy` · `/Password` · `/Delete` · `/Configuration/Partial` | 权限 / 密码 / 删除 / 个人 PIN |
| POST | `/Users/{sourceId}/CopyData` | 复制到已有用户；正文 `ToUserIds` + `CopyOptions` |
| GET | `/Users/CopyDataOptions` | 可复制项清单 |
| POST / DELETE | `/Users/{Id}/Connect/Link` · POST `/Users/{Id}/Connect/Link/Delete` | Emby Connect 关联 / 解绑 |
| GET | `/Auth/Providers` · `/Features?FeatureType=User` · `/Localization/ParentalRatings` · `/Channels` · `/Devices` | 编辑页的选项来源 |

**两套复制参数不能混**：新建时 `CopyFromUserId` + `UserCopyOptions`，复制到已有用户时 `ToUserIds` + `CopyOptions`。混用会静默失败或复制错东西。

## 系统与通知

| 方法 | 路径 |
| --- | --- |
| GET | `/System/Info` · `/System/Info/Public` |
| GET / POST | `/System/Configuration` |
| POST | `/System/Restart` · `/System/Shutdown` |
| GET | `/System/ActivityLog/Entries` |
| GET | `/Plugins` |
| GET | `/Localization/Cultures` · `/Localization/Countries` |
| GET | `/Notifications/Types` · `/Notifications/Services/Defaults` |
| POST | `/Notifications/Services/Test` |

## 规格缺口

**客户端在用，规格里没有**（`4.10.0.40`）：

| 方法 | 路径 | 客户端位置 |
| --- | --- | --- |
| GET | `/Notifications/Services` | `EmbyClient.cs:460`，带 `UserId` query |
| GET / POST / DELETE | `/Notifications/Services/Configured` | `EmbyClient.cs:470/482/486` |

规格里 `/Notifications/*` 只登记了 `Types`、`Admin`、`Services/Test`、`Services/Defaults`。**规格是"服务器实现"的近似，不是全集** —— 客户端的通知渠道配置这条路只能靠实测，不能靠规格。发现新缺口就补进这张表。

## 容易猜错的

| 你可能会猜 | 实际 |
| --- | --- |
| `GET /Users` 列用户 | 没有这条路；用 `/Users/Query`（分页）或 `/Users/Public` |
| `/Items/{ItemId}/PlaybackInfo` | 路径是 `/Items/{Id}/PlaybackInfo`；而且**客户端根本没用它** |
| `/Items/{ItemId}/Images/...` | 参数名是 `{Id}`；章节图走通配 `{Type}/{Index}` |
| `DELETE /ScheduledTasks/Running/{Id}` 停任务 | 停任务是 **POST** `.../{Id}/Delete` |
| `/Users/{Id}/CopyData` 给新用户复制 | 那是复制到**已有**用户；新建走 `/Users/New` 的 `CopyFromUserId` |
| 用户头像按原始字节上传 | 那是**条目图片**的约定；头像传 Base64 文本 |

## 校对方法

```bash
# 取规格（不需要凭据）
curl -s "$API_BASE/openapi" -o work/emby-openapi.json
```

然后按**结构**对路径，不要按字面量 grep：参数名在两边不一样（见上）。规格里找不到时先确认是不是缺口，是就登记到本文档，而不是改成"规格里有的写法"。
