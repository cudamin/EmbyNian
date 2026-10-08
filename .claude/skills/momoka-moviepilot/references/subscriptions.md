# 订阅管理、文件统计与入库播放

2026-10-08 定点核对了官方公开文档（`api.movie-pilot.org` 的 `openapi.json`）、官方后端 tag `v3.1.2-1` 的源码，以及一台实际运行 `v3.1.2-1` 的服务器（只读）。当时 18 条订阅中 17 条返回文件统计，入库内容全部匹配到当前 Emby 的条目；写入与播放均为 0。**这是带日期的证据，不是"服务器现在仍是这样"的证明。** 协议随版本变化，动手前按 SKILL.md 重新确认版本并对照对应 tag 的源码。

## 接口与版本差异

| 能力 | 接口（相对于 `/api/v1/`） |
| --- | --- |
| 全量订阅 | `GET subscribe/`，省略 `page` / `count` 保留全量语义 |
| 单条订阅 | `GET subscribe/{id}` |
| 文件统计 | `GET subscribe/files/{id}` |
| 编辑 | `PUT subscribe/`，请求体 `id` 加实际改动的字段 |
| 暂停／恢复 | `PUT subscribe/status/{id}?state=S` / `?state=R` |
| 立即搜索 | `POST subscribe/search/{id}` |
| 重置 | `POST subscribe/reset/{id}` |
| 取消订阅 | `DELETE subscribe/{id}`，不删已入库文件 |

- **搜索与重置的方法在各版本之间对不上。** 官方公开文档只登记 `GET`；官方后端 `v3.1.2-1` 的 `app/api/endpoints/submaintenance.py` 对这两个路径**同时**注册了 `@router.get` 和 `@router.post`，该版本前端也走 POST。客户端优先 POST，只在明确收到 HTTP 405 时改发 GET。超时、断线、5xx 都不是"方法不对"，不触发改发，也不重放。
- 分页靠 `resolve_compatible_pagination`：`page` 与 `count` 都省略才是全量，只给一个或都给了就是分页。集合总数走响应头（`openapi_extra` 声明的那个），不在正文。主页要全量就别带分页参数，也别把一批分页结果当全量。
- 列表字段（该版本实测）：`id`、`name`、`year`、`type`、`keyword`、`media_source`、`media_id`、`season`、`poster`、`backdrop`、`description`、`include`、`exclude`、`quality`、`resolution`、`effect`、`total_episode`、`start_episode`、`lack_episode`、`completed_episode`、`state`、`last_update`、`sites`、`downloader`、`save_path` 等。只投影界面要用的字段。
- 上述只读核对中，该服务器 `GET /api/v1/openapi.json` 返回 **404**，而同一会话里 `GET /api/v1/dashboard/system` 正常返回 `version`。这是该次部署的观测；项目的 `MoviePilotProbe.RunAsync` 在登录后读取 `dashboard/system`、`mediaserver/clients` 和 `download/clients`，没有 OpenAPI 请求。拿不到 schema 时，**契约以对应 tag 的官方源码为准**；不能跳过版本确认，也不能猜字段。

## 三个进度不是一回事

- `total_episode` / `lack_episode` 是**订阅目标范围内的下载进度**。`total_episode - lack_episode` 不等于已入库集数；界面上叫"订阅进度"，不要叫"下载进度"，更不能当入库进度。
- 已入库**只由** `subscribe/files/{id}` 的 `library` 决定。有 `library` 文件记录就是"已入库"，即使同时有 `download`；只有 `download` 有文件记录而 `library` 没有时才是"已下载 · 待入库"，两者都没有则是"缺集"。这些状态用于有效统计中的目标条目，统计不可用不套用缺集状态。
- 缺集是目标范围内**所有未入库**的集，包括已下载待整理的和尚未播出的。按 `start_episode` / `total_episode` 过滤，不混入范围外的集。
- `state`：`S` 暂停、`R` 订阅中、`N` 或 `P` 待处理。暂停不代表入库事实停止。

## 文件统计回话

`GET subscribe/files/{id}` 返回 `{ subscribe: {...}, episodes: { "1": {...}, "2": {...} } }`：

- 电影用**零号条目** `episodes["0"]`，不是 `episodes["1"]`。
- 每集字段：`title`、`description`、`backdrop`、`download`、`library`。`backdrop` 是该集的横版剧照。
- `library` / `download` 是数组，元素带 `file_path`、`server` / `server_type`、`itemid`；旧版本可能直接给字符串路径。两种形态都要认。
- 服务端按 `for i in range(start_episode or 1, total_episode + 1)` 补齐目标范围内缺的集。客户端可以照此补出未播出的空条目，但这些条目**既没有文件也没有剧照**，只能作为"未入库"出现在缺集里。
- `subscribe` 为 `null`、`id` 对不上、`episodes` 不是对象、请求失败——全都是"**统计不可用**"，不是"全部未入库"。空响应和错误不能显示成 `0 / N`，也不能显示成已全部入库。该服务器上确实有一条订阅返回 `subscribe:null` 与空 `episodes`。

## 写操作的边界

- 编辑只提交**用户实际改动**的字段，`id` 必带。`keyword` / `include` / `exclude` / `quality` / `resolution` / `effect` 与电视剧的 `total_episode` / `start_episode` 之外的一切——站点、下载器、保存路径、其他客户端留下的设置——保持服务器当前值，不回写。清空某项发 `null`，不发空串。
- 所有订阅写操作（PUT / POST / DELETE）都按有副作用处理，与 HTTP 方法无关。提交中锁住再次提交；拿不到明确回执就标记"结果不明"，要求用户先去 MoviePilot 核对再显式解除限制，**不自动重发**。超时不证明服务器没做。
- 回执只说明请求已提交。搜索／重置成功不等于已找到资源，更不等于已入库。
- 按订阅 `id` 单独记账，不把一条的失败盖到整批。
- 连接与账号身份绑定到每批结果：切服、离页、取消之后，旧列表、旧确认与迟到的图片都不应重新生效。

## 入库播放

"MoviePilot 说已入库"和"当前 Emby 能播"是两件事，中间隔着一次跨服务器匹配：

- **文件统计里的 `itemid` 属于 MoviePilot 配置的那台媒体服务器，不是当前 Emby 的条目 ID。** 一条订阅可能关联多台服务器；直接拿它当 Emby 条目导航会播错内容。只在 `server_type` 是当前服务器类型、`itemid` 只含字母数字与连字符时才尝试，并且**必须再用文件路径核对**才接受。
- 主路径是按主身份在当前账号下重查：`Users/{userId}/Items?AnyProviderIdEquals=<provider>.<mediaId>&Recursive=true&IncludeItemTypes=<电影|剧集>`。provider 只认 `themoviedb`→`Tmdb`、`douban`→`Douban`、`imdb`→`Imdb`、`tvdb`→`Tvdb`，且编号格式要合法（IMDb 必须 `tt` 开头）；认不出来就不匹配，不按标题猜。
- 电视剧还要核季号与集号（含 `IndexNumberEnd` 区间），电影要求零号条目对电影类型。
- 多条候选时**先按入库文件路径筛**，路径唯一命中才用它；否则候选唯一才接受。路径只做字符串比较，不访问、不启动路径；Windows 驱动器／UNC 路径不区分大小写，Unix 路径区分。
- 找不到、无权读取、待入库、缺集都不启用播放入口。页面本身只读取，点播放才起播。

## 夹具与验证要覆盖

- **方法差异**：POST 成功、POST 405 才改 GET、POST 超时不得改发、PUT / DELETE 结果不明后锁定且不重发。
- **字段差量**：只改一个字段时的请求体、清空发 `null`、未改动的站点／下载器字段不出现。
- **统计语义**：电影零号条目、`library` 的字符串与数组两种形态、只有 `library`／只有 `download`／两者都有／两者都没有的状态判定、`subscribe:null`、空 `episodes`、范围外的集被过滤、未播出集被补齐、"统计不可用"不显示为全缺或全入库。
- **播放匹配**：跨服务器 `itemid` 不被直接信任、同名多候选、路径唯一命中、路径大小写、无权与不存在的条目。
- **界面**：进度写在封面内且不与更多按钮重叠、空统计不显示为全入库、离页与切服后旧按钮失效。
- `--probe-shell` 的三个订阅检查只操作假服务器与捕获的假播放动作；真实只读核对另行进行，且不写订阅、不起播放器。
