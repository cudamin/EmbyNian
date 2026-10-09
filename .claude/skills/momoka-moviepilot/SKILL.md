---
name: momoka-moviepilot
description: "Develop, review and verify Momoka's MoviePilot integration: media-source identity, TMDB/Douban/IMDb searches, torrent results, subscription management, library-availability statistics and library playback entries, and history-based reorganization. Use for MoviePilot C# or WinUI changes and safe live troubleshooting in this repository; the playback pipeline itself belongs to the playback skill."
---

# Momoka — MoviePilot integration

本技能保存 MoviePilot 接入中容易误判的协议与验证方法。**授权、凭据、协作、构建与交付要求以 [CLAUDE.md](../../../CLAUDE.md) 为准**，不以本技能或历史实测记录替代本轮授权。

## 开始之前

- 当前工作树里的 `src/Momoka.Core/MoviePilot/` 是协议与业务规则；Shell 的 `MoviePilot*ViewModel` / `MoviePilot*` 视图负责界面；服务注册在 `Composition/ShellServices.cs`。
- 先确认实际 MoviePilot 版本。过去某次连接成功不代表服务器仍是该版本。`dashboard/system` 的 `version` 是可靠入口；`openapi.json` 未必可用（在 API 基址下曾返回 404），拿不到就以对应 tag 的官方前后端源码为准，不要猜字段，也不要因此跳过版本确认。
- 不整份输出 OpenAPI、设置或资源回话。按当前接口投影字段；`torrent_info` 可能带站点 Cookie、下载凭据和私有 URL。
- 本仓库 ignored `work/` 的上游快照和诊断宿主只可作为经检查后的本机辅助；干净检出不能依赖它们。正式测试使用匿名夹具与 `StubTransport`。

## 按问题选择资料

- 搜片、来源选择、跨库编号、同名结果、订阅身份：[来源与搜索](references/search-identity.md)。
- 订阅列表与管理、文件统计（已入库／待入库／缺集）、已入库内容的播放入口：[订阅与入库](references/subscriptions.md)。
- 原记录、路径、预览、重新整理、部分成功：[整理与副作用](references/reorganization.md)。
- 资源列表与验证：下方规则足够；只有接口变化时再读上游 schema。

## 三种身份不要混为一谈

1. Emby 条目 ID 是库内导航身份，不是 MoviePilot 媒体 ID。MoviePilot 文件统计里的 `itemid` 属于它自己配置的那台媒体服务器，同样不是当前 Emby 的编号——跨服务器播放必须先按主身份或文件路径在当前账号下重新定位。
2. MoviePilot 媒体以 `media_source`、`media_id` 和必要的媒体类型联合定位。相同数字在不同来源、甚至 TMDB 的电影与电视剧中，可能表示完全不同作品。
3. `torrent_info` 是可下载资源；站点名称不是媒体数据源，种子 `page_url` 不是媒体详情页，`enclosure` 不是可安全交给系统的网页地址。

主身份和 IMDb 等交叉编号必须分开显示、解析和回传。不能只在画面上贴“豆瓣”标签，却仍发 TMDB 的编号。缺少可信身份时应禁用相关操作，而非按同名作品猜测。

## 资源列表

- `search/media/{id}` 是已选媒体的精确搜索；带 `media_source`、类型 `mtype` 和已指定的 `season`（0 是特别篇，缺失不猜 1）。`search/title` 是关键词搜索，不能借用前一次选择的媒体身份。
- 精确搜索只保留与所选来源、编号和类型一致的完整 `media_info`。即使身份一致，回话显式返回 `media_info_is_target=false`，或解析得到的非空 `match_status` 不是 `exact`，仍须禁用下载，不能把候选当精确命中。缺少这两个标志或 `match_status` 为空字符串本身不构成否决，仍须核对身份、类型和媒体快照；对应规则在 `MoviePilotMediaParser.ToResource`。
- 带可信媒体信息的下载走 `download/` 的 `media_in`，不能退回 `download/add` 再按种子标题猜片；后者没有影视 `mtype` 参数。
- 搜索结果绑定获取时的连接与账号。订阅、下载和整理的防重状态由服务持有，不随重搜、新行或窗口重建清空；网络结果不明时保留“请先核对”，不开放直接重试。当前防重仅在本进程有效，不是跨进程幂等或服务器最终完成证明。
- 资源字段来自 `torrent_info`：`page_url`、`description`、`labels`、`pubdate`、`downloadvolumefactor`、`uploadvolumefactor`、`freedate`、`hit_and_run`。清晰度通常来自 `meta_info.resource_pix`。
- 体积可能是浮点 JSON 数字，优惠因子可能是字符串；缺失不能默认为“免费”。原始数据保留在内存用于下载，显示模型只取必要字段。
- 过滤当前结果时保持行对象、集合实例与下载状态；显示“符合条件 / 总数”，清除恢复原结果，区分没搜到和被过滤为空。
- 新查询、空查询、来源变化与页面释放均须使旧请求失效。切到资源面板时隐藏底层可交互内容，新媒体搜索时收起旧资源层。
- “种子页面”只接受合法 http(s) 网页地址；不执行本地文件、自定义协议，不把 Cookie 或下载凭据拼进启动参数。外部浏览器登录状态不等于 MoviePilot 的服务会话。

## 订阅管理与入库播放

- 列表、单条、文件统计，以及编辑／暂停／搜索／重置／取消的接口、字段与版本差异见 [订阅与入库](references/subscriptions.md)。搜索与重置的方法在各版本间对不上（公开文档只登记 GET，官方后端同时注册 GET 与 POST），客户端优先 POST、**仅在明确 405 时**改发 GET；超时不改发、不重放。全量列表靠省略 `page` / `count`，别把一批分页结果当全量。
- `total_episode` / `lack_episode` 是订阅目标范围内的下载进度，不是入库进度。已入库只由 `subscribe/files/{id}` 的 `library` 决定；只有 `download` 有文件记录而 `library` 没有时才显示“已下载 · 待入库”，两者都有仍是“已入库”；缺集包含已下载待整理和尚未播出的集。空回话、错误与 `subscribe:null` 都是“统计不可用”，不能显示成 `0 / N` 或全部入库。
- 订阅写操作按有副作用处理，与 HTTP 方法无关：只提交实际改动的字段，提交中锁住再次提交，结果不明时按订阅逐条保留状态、要求用户先核对服务器再显式解除，不自动重发。回执只代表请求已提交，不代表已找到资源或已入库。
- 已入库内容的播放入口必须先在**当前 Emby 账号**下重新定位：按主身份查 `AnyProviderIdEquals=<provider>.<mediaId>`，电视剧另核季号与集号；文件统计里的 `itemid` 属于别的媒体服务器，只能在 `server_type` 与文件路径同时对上时才接受，多候选先按入库文件路径筛。找不到、无权读取、待入库与缺集都不启用播放；页面只读取，点播放才起播。

## 验证要回答的问题

- 先用 Core 单测验证来源/编号配对、类型冲突、缺身份、数字或字符串字段、完整/部分失败回执；服务测试断言路径、参数与实际请求次数，而不只判断文案。
- 精确资源覆盖身份相同但 `media_info_is_target=false`、解析得到非空且非 `exact` 的 `match_status` 的场景，两个否决条件分别断言；另覆盖标志缺失或 `match_status` 为空字符串、且其余条件满足时仍允许下载。沿用 `MoviePilotQuerySafetyTests` 的匿名夹具，并断言下载请求体能否构造，而不只检查显示文案。
- 实际只读验证时，白名单限制为本轮需要的读取。某些 POST（如 `storage/list`、`preview:true`）在对应版本中是只读，必须按请求体核对；不能因为都是 POST 就混入提交，也不能因为都是 GET 就放行 `transfer/now` 等有副作用入口。
- GUI 分开验证搜索结果、来源标签、资源过滤、空/错/忙状态，以及下载和订阅的确认/取消。取消不是下载或订阅已成功的证据。
- 订阅按请求方法、请求体与**请求次数**断言：POST 成功、POST 405 才改发 GET、超时不得改发、结果不明锁定后不重发；编辑的请求体只含实际改动的字段。
- 文件统计覆盖电影零号条目、`library` 的字符串与数组两种形态、`subscribe:null`、空 `episodes`、范围外的集被过滤、未播出集被补齐，以及“统计不可用”不显示为全缺或全入库。
- 入库播放覆盖跨服务器 `itemid` 不被直接信任、同名多候选、路径唯一命中与大小写、无权或不存在条目的禁用；断言播放交给的是匹配到的那个 Emby 条目。
- 新控件检查自动化句柄；截图检查受影响尺寸，颜色变化按主规则覆盖主题。不要用属性断言代替实际画面。
- 真实整理只在明确授权的条目范围执行，完成后核对原源仍符合预期、目标文件、整理新记录和 Emby 索引；`accepted` 不等于 `completed`，Emby 收录不等于已播放。
- 子代理遇到暂时性限流且允许重试时，保留任务身份和已完成工作，适当退避再续跑；遵守用户的并行上限。权限拒绝、用户取消、确定性错误或有副作用请求结果不明，都不能盲目自动重放。

## 交付

按 [CLAUDE.md 的改动类型表](../../../CLAUDE.md#四道闸门与日常交付) 选择本轮验证与交付要求，具体命令见 [开发与验证](../../../docs/开发与验证.md)；订阅与入库功能的对外说明在 [MoviePilot订阅](../../../docs/MoviePilot订阅.md)。报告实测、未覆盖与失败项；不把历史失败当成通过，不用改基线消除未经解决的红项。项目规则和入口从源码获取，避免在技能里固化当前版本号、测试数、真实账号或某次媒体路径。
