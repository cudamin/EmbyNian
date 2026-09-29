# 来源与搜索（TMDB／豆瓣／IMDb）

按 2026-09-27 实测的 MoviePilot v3.0.8 OpenAPI 与官方 v3.0.1 前后端源码核对；协议随版本变化，动手前先按 SKILL.md 的办法确认实际版本。这里保存的是「容易把三种身份搅成一团」的那部分契约。

## media/search 的来源选择

- `GET /api/v1/media/search?title=<词>&type=media&page=1&count=N`。`media_source` 是**数组参数**：官方后端（`app/api/endpoints/media.py` 的 `search`）用 `MediaSourceQuery` 接收重复的 `media_source=<值>` 查询参数；**逗号分隔串只是旧客户端兼容**。客户端应发重复参数，不要自己拼逗号串。
- 不传 `media_source` 就是全部来源，由服务器按它自己的来源顺序设置排序后分页（同一 endpoint 内先排序后切页）。
- 合法值是开放集合：内置 `themoviedb`、`douban`、`bangumi`、`anilist`、`imdb`、`tvdb`、`musicbrainz`、`theaudiodb`、`doubanmusic`，加上插件注册的动态标识（格式 `^[a-z][a-z0-9._-]{0,63}$`，见 MediaSource schema）。**不要写死白名单**；显示名来自 `GET /api/v1/media/source` 的 `name`。
- `media/source` 每条带 `name`、`media_source`、`media_types`。官方前端按 media_types 过滤影视（电影/电视剧/movie/tv）与音乐；**类型为空视为可用**（`frontend/src/utils/mediaId.ts`）。来源下拉应以这个目录为准，失败时才退到内置清单。

## 三种身份各自是什么

1. **主身份**：`media_source` + `media_id` 成对。同一个数字在不同来源下是不同作品；TMDB 内部电影和电视剧还共享同一编号空间，所以除来源外还要媒体类型（`电影`/`电视剧`）。
2. **辅助编号**：`MediaInfo` 上的 `tmdb_id`、`imdb_id`、`tvdb_id`、`douban_id`、`bangumi_id`、`anilist_id`、`anidb_id` 只是元数据（`app/schemas/context.py`），**不能当主身份回传**——除非它就是当前主来源自己的编号（例如 `media_source=imdb` 时的 `imdb_id`）。
3. **资源**：`torrent_info` 是种子，站点名（`site_name`）不是媒体来源；种子的 `page_url` 是站点详情页，`enclosure` 是下载链接，两者都不能混作媒体详情或交给系统打开。

## IMDb 的特别之处

- `imdb` 是**独立的 MediaSource**（`app/schemas/types.py` 的 `MediaSource.IMDb`），由 `app/modules/imdb` 模块提供，走 IMDb 自己的 suggestion/GraphQL 接口，不经过 TMDB。
- IMDb 的编号是 **`tt` 开头加数字**（模块内 `_IMDB_ID_PATTERN` 校验；前端同一规则 `^tt\d+$`）。所以「IMDb 的编号」永远带 tt 前缀；把纯数字当 IMDb 编号发过去会被拒。
- 按显式编号识别时（`_recognition_plan`）：**给了 `media_id` 就要求 `media_source=imdb` 且编号合法**，否则该模块直接不响应。跨编号自动换算（例如 IMDb→TMDB）不是它的职责。
- 官方前端对来源编号有格式校验（`isValidMediaSourceId`）：imdb 要求 tt 格式，musicbrainz 要求 UUID，doubanmusic 要求 `数字:数字`；其余来源只要求非空且非 "0"。客户端校验与它保持一致。

## 搜索回话与订阅

- `media/search` 回的是裸数组（被信封或 `list`/`items`/`data` 包一层也要认）。每条是 `MediaInfo.to_dict()`：标题、年份（字符串或数字）、`type`（`电影`/`电视剧`）、简介、海报、主身份对和辅助编号。**没有标题的条目按噪声跳过**，别让一条脏数据把整页清空。
- 结果卡片必须显示来源：同一部片在 TMDB 和豆瓣下会各出一条，来源是区分它们的第一线索；来源名用 `media/source` 的 `name`，标识认识的说人话、不认识的原样。
- 订阅正文只带 `name`、`type`、`media_source`、`media_id`。**故意不发 year**：订阅 schema 的 year 是字符串，发数字整条 422。主身份对已唯一定位，服务器自己取全元数据。
- 不确定来源能否订阅时，不要静默换成另一个来源的编号「帮忙修」——那等于替用户换了库。让服务器自己的拒绝话术上屏。
