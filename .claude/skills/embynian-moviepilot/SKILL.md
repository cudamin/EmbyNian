---
name: embynian-moviepilot
description: "Develop, review and verify EmbyNian's MoviePilot integration: media-source identity, TMDB/Douban/IMDb searches, torrent results, subscriptions, and history-based reorganization. Use for MoviePilot C# or WinUI changes and safe live troubleshooting in this repository, not general playback work."
---

# EmbyNian — MoviePilot integration

本技能保存 MoviePilot 接入中容易误判的协议与验证方法。**授权、凭据、协作、构建与交付要求以 [CLAUDE.md](../../../CLAUDE.md) 为准**，不以本技能或历史实测记录替代本轮授权。

## 开始之前

- 当前工作树里的 `src/EmbyNian.Core/MoviePilot/` 是协议与业务规则；Shell 的 `MoviePilot*ViewModel` / `MoviePilot*` 视图负责界面；服务注册在 `Composition/ShellServices.cs`。
- 先确认实际 MoviePilot 版本。过去某次连接成功不代表服务器仍是该版本。读取已授权服务的 `dashboard/system` 与 `openapi.json`，再对照对应 tag 的官方前后端源码。
- 不整份输出 OpenAPI、设置或资源回话。按当前接口投影字段；`torrent_info` 可能带站点 Cookie、下载凭据和私有 URL。
- 本仓库 ignored `work/` 的上游快照和诊断宿主只可作为经检查后的本机辅助；干净检出不能依赖它们。正式测试使用匿名夹具与 `StubTransport`。

## 按问题选择资料

- 搜片、来源选择、跨库编号、同名结果、订阅身份：[来源与搜索](references/search-identity.md)。
- 原记录、路径、预览、重新整理、部分成功：[整理与副作用](references/reorganization.md)。
- 资源列表与验证：下方规则足够；只有接口变化时再读上游 schema。

## 三种身份不要混为一谈

1. Emby 条目 ID 是库内导航身份，不是 MoviePilot 媒体 ID。
2. MoviePilot 媒体以 `media_source`、`media_id` 和必要的媒体类型联合定位。相同数字在不同来源、甚至 TMDB 的电影与电视剧中，可能表示完全不同作品。
3. `torrent_info` 是可下载资源；站点名称不是媒体数据源，种子 `page_url` 不是媒体详情页，`enclosure` 不是可安全交给系统的网页地址。

主身份和 IMDb 等交叉编号必须分开显示、解析和回传。不能只在画面上贴“豆瓣”标签，却仍发 TMDB 的编号。缺少可信身份时应禁用相关操作，而非按同名作品猜测。

## 资源列表

- `search/media/{id}` 是已选媒体的精确搜索；带 `media_source` 和类型 `mtype`。`search/title` 是关键词搜索，不能把所有结果强行盖上一个未经核实的媒体身份。
- 资源字段来自 `torrent_info`：`page_url`、`description`、`labels`、`pubdate`、`downloadvolumefactor`、`uploadvolumefactor`、`freedate`、`hit_and_run`。清晰度通常来自 `meta_info.resource_pix`。
- 体积可能是浮点 JSON 数字，优惠因子可能是字符串；缺失不能默认为“免费”。原始数据保留在内存用于下载，显示模型只取必要字段。
- 过滤当前结果时保持行对象、集合实例与下载状态；显示“符合条件 / 总数”，清除恢复原结果，区分没搜到和被过滤为空。
- 新查询、空查询、来源变化与页面释放均须使旧请求失效。切到资源面板时隐藏底层可交互内容，新媒体搜索时收起旧资源层。
- “种子页面”只接受合法 http(s) 网页地址；不执行本地文件、自定义协议，不把 Cookie 或下载凭据拼进启动参数。外部浏览器登录状态不等于 MoviePilot 的服务会话。

## 验证要回答的问题

- 先用 Core 单测验证来源/编号配对、类型冲突、缺身份、数字或字符串字段、完整/部分失败回执；服务测试断言路径、参数与实际请求次数，而不只判断文案。
- 实际只读验证时，白名单限制为本轮需要的读取。某些 POST（如 `storage/list`、`preview:true`）在对应版本中是只读，必须按请求体核对；不能因为都是 POST 就混入提交，也不能因为都是 GET 就放行 `transfer/now` 等有副作用入口。
- GUI 分开验证搜索结果、来源标签、资源过滤、空/错/忙状态，以及下载和订阅的确认/取消。取消不是下载或订阅已成功的证据。
- 新控件检查自动化句柄；截图检查受影响尺寸，颜色变化按主规则覆盖主题。不要用属性断言代替实际画面。
- 真实整理只在明确授权的条目范围执行，完成后核对原源仍符合预期、目标文件、整理新记录和 Emby 索引；`accepted` 不等于 `completed`，Emby 收录不等于已播放。
- 子代理遇到暂时性限流且允许重试时，保留任务身份和已完成工作，适当退避再续跑；遵守用户的并行上限。权限拒绝、用户取消、确定性错误或有副作用请求结果不明，都不能盲目自动重放。

## 交付

运行 [开发与验证](../../../docs/开发与验证.md) 中适用的检查并更新实际发布目录。报告实测、未覆盖与失败项；不把历史失败当成通过，不用改基线消除未经解决的红项。项目规则和入口从源码获取，避免在技能里固化当前版本号、测试数、真实账号或某次媒体路径。
