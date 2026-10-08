# 接口清单

下表记录**当前客户端调用**；规格中的其他方法放在单独的备选表，未登记的调用放在规格缺口表。源码入口为 `EmbyClient*.cs`、`EmbyServerGateway` 和 `EmbyUrl`，以本轮源码为准。规格对照使用 2026-10-08 的 `4.10.0.40` 快照；这是静态核对，不证明每条请求已在当前服务器实测。重新取得规格的方法见 [SKILL.md](../SKILL.md)；离线时明确报告快照版本与覆盖限制。

路径写成规格的参数名（`{Id}` / `{UserId}`），客户端源码里是 `{itemId}` / `{Connection.UserId}` —— 同一件事，见 SKILL.md「参数名不一样不代表接口不一样」。

## 浏览与条目

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| GET | `/Users/{UserId}/Views` | 媒体库视图 |
| GET | `/Users/{UserId}/Items` | 主查询：筛选、排序、分页 |
| GET | `/Users/{UserId}/Items/{Id}` | 单条目详情 |
| GET | `/Users/{UserId}/Items/Resume` | 继续观看 |
| GET | `/Users/{UserId}/Items/Latest` | 最新入库 |
| GET | `/Items` | `OrderVersionsByAddedAsync` 按 `Ids` 取得备选版本的 `DateCreated`，用于版本按入库时间排序 |
| GET | `/Items/{Id}/Similar` | 相似条目 |
| GET | `/Genres` · `/Tags` · `/Years` | 筛选可选值；`GetFilterValuesAsync`，key 来自 `EmbyFilterBy.Lists` |
| GET | `/Shows/NextUp` | 下一集 |
| GET | `/Shows/{Id}/Seasons` · `/Shows/{Id}/Episodes` | 季 / 集列表 |
| POST | `/Items/{Id}` | 更新条目（元数据编辑） |
| POST | `/Items/{Id}/Refresh` | 刷新元数据 / 图片 |
| DELETE | `/Items/{Id}` | 删除条目；`DeleteItemAsync` |
| GET | `/Items/{Id}/Download` | 下载到设备 |
| POST | `/Collections` | 新建合集并加入条目；`CreateCollectionAsync` |
| POST | `/Collections/{Id}/Items` | 加入已有合集；`AddToCollectionAsync` |

## 图片

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| GET / POST / DELETE | `/Items/{Id}/Images/{Type}` | 条目图片：读 / 上传 / 删；当前上传正文为原始字节 |
| GET / DELETE | `/Items/{Id}/Images/{Type}/{Index}` | 指定索引的图片；章节图 GET 使用 `Type=Chapter` |
| GET | `/Items/{Id}/RemoteImages` | 远程图片候选 |
| GET | `/Images/Remote` | 由服务器代理下载远程候选图片；`GetRemoteImageBytesAsync` |
| POST | `/Items/{Id}/RemoteImages/Download` | 下载远程图片 |
| GET / POST | `/Users/{Id}/Images/Primary` | 用户头像：当前上传正文为 Base64 文本 |
| POST | `/Users/{Id}/Images/Primary/Delete` | 删除用户头像；`DeleteManagedUserImageAsync` |

`EmbyClient.ImagePath` 拼条目图片路径，`EmbyUrl.Image` 和 `ChapterImage` 添加 `maxWidth` / `quality` / `tag`。**`tag` 是缓存键**，别在没换图时改它。上传正文描述的是当前实现；规格与实现的编码差异见 [传输层与鉴权](http-and-auth.md#正文形状不是一套)。

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
| GET | `/Videos/{Id}/stream` · `/Videos/{Id}/stream.{Container}` | 原始流，容器有值时带扩展名；客户端加 `Static=true` + `MediaSourceId` |
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
| POST | `/ScheduledTasks/Running/{Id}/Delete` | 停止任务；`SetLibraryTaskRunningAsync` 采用 POST 别名，规格也支持 DELETE |
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
| POST | `/Users/AuthenticateByName` | 登录，拿 token；`EmbyServerGateway.AuthenticateAsync` |
| GET | `/Users/Query` | 分页读用户 |
| GET | `/Users/Public` | 公开用户列表；`EmbyServerGateway.GetPublicUsersAsync` |
| POST | `/Users/New` | 新建；复制数据用 `CopyFromUserId` + `UserCopyOptions` |
| GET / POST | `/Users/{Id}` | 读 / 改 |
| POST | `/Users/{Id}/Policy` · `/Password` · `/Delete` · `/Configuration/Partial` | 权限 / 密码 / 删除 / 个人 PIN |
| POST | `/Users/{sourceId}/CopyData` | 复制到已有用户；正文 `ToUserIds` + `CopyOptions` |
| GET | `/Users/CopyDataOptions` | 可复制项清单 |
| POST | `/Users/{Id}/Connect/Link` · `/Users/{Id}/Connect/Link/Delete` | Emby Connect 关联 / 解绑 |
| GET | `/Auth/Providers` · `/Features?FeatureType=User` · `/Localization/ParentalRatings` · `/Channels` · `/Devices` | 编辑页的选项来源 |

**两套复制参数不能混**：新建时 `CopyFromUserId` + `UserCopyOptions`，复制到已有用户时 `ToUserIds` + `CopyOptions`。混用会静默失败或复制错东西。

## 系统与通知

| 方法 | 路径 |
| --- | --- |
| GET | `/System/Info` · `/System/Info/Public` |
| GET / POST | `/System/Configuration` |
| POST | `/System/Restart` · `/System/Shutdown` |
| GET | `/System/ActivityLog/Entries` |
| GET | `/Sessions` |
| GET | `/Localization/Cultures` · `/Localization/Countries` |
| GET | `/Notifications/Types` · `/Notifications/Services/Defaults` |
| POST | `/Notifications/Services/Test` |

## 规格缺口

**客户端在用，但 `4.10.0.40` 规格未登记相应方法**。源码及离线测试只能证明请求形状，服务器支持情况须另有实测证据：

| 方法 | 路径 | 客户端位置 |
| --- | --- | --- |
| GET | `/Notifications/Services` | `GetNotificationServicesAsync`，带 `UserId` query |
| GET / POST / DELETE | `/Notifications/Services/Configured` | `GetNotificationsAsync` / `SaveNotificationAsync` / `DeleteNotificationAsync` |
| GET | `/Users` | `GetNotificationUsersAsync`，通知页的限定用户列表 |
| GET | `/Library/VirtualFolders` | `GetNotificationLibrariesAsync`，通知页的限定媒体库列表；规格登记了同路径的其他方法 |

规格里 `/Notifications/*` 只登记了 `Types`、`Admin`、`Services/Test`、`Services/Defaults`。通知选项的 `/Users` 和 `/Library/VirtualFolders` 返回列表；不要仅为匹配规格改成返回分页结果的 `/Users/Query` 或 `/Library/VirtualFolders/Query`。发现新缺口就补进这张表。

## 规格支持、当前客户端未采用的写法

这些是该版本规格中的备选或额外接口，不计入上面的当前调用清单：

| 方法 | 路径 | 与当前实现的区别 |
| --- | --- | --- |
| POST | `/Items/Delete` | 当前删除条目使用 `DELETE /Items/{Id}` |
| DELETE | `/Collections/{Id}/Items` | 当前仅实现加入合集的 POST 请求 |
| DELETE | `/Users/{Id}/Images/{Type}` | 当前删除头像使用 POST `…/Images/Primary/Delete` |
| DELETE | `/Users/{Id}` · `/Users/{Id}/Connect/Link` | 当前删除用户、解绑 Connect 使用 POST `…/Delete` 别名 |
| DELETE | `/ScheduledTasks/Running/{Id}` | 有效的停止任务接口；当前使用 POST `…/{Id}/Delete` |
| GET | `/Items/{Id}/CriticReviews` · `/Plugins` | 当前产品没有调用 |

## 容易猜错的

| 你可能会猜 | 实际 |
| --- | --- |
| 规格没有 `GET /Users`，所以服务器不支持 | 通知选项正在调用它；这是规格缺口。用户管理使用分页 `/Users/Query`，公开用户列表使用 `/Users/Public` |
| `/Items/{ItemId}/PlaybackInfo` | 路径是 `/Items/{Id}/PlaybackInfo`；而且**客户端根本没用它** |
| 章节图需要独立的 `/Images/Chapter/…` 规格条目 | 对应已有 `/Items/{Id}/Images/{Type}/{Index}`，`Type=Chapter`；参数名不同不改变路由 |
| 停任务只能用 `POST .../{Id}/Delete` | 这是客户端采用的别名；规格也支持 `DELETE /ScheduledTasks/Running/{Id}` |
| `/Users/{Id}/CopyData` 给新用户复制 | 那是复制到**已有**用户；新建走 `/Users/New` 的 `CopyFromUserId` |
| 当前两种图片上传可共用正文转换 | 当前条目图片发送原始字节，头像发送 Base64 文本；先核对对应实现与实测证据 |

## 校对方法

```bash
# 取规格（不需要凭据）
mkdir -p work
curl --fail --show-error --silent "${API_BASE%/}/openapi" -o work/emby-openapi.json
py -m json.tool work/emby-openapi.json > /dev/null
```

上面是 Bash 写法；先将 `API_BASE` 设为已核对、不含凭据的服务器基址，并逐步检查退出码，下载失败时不要拿旧文件继续校对。然后按**结构和 HTTP 方法**对路径，不要按字面量 grep：参数名在两边不一样（见上）。规格里找不到时登记缺口和证据状态，而不是改成"规格里有的写法"。
