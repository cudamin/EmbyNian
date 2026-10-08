# 传输层与鉴权

`EmbyHttp.cs` 是唯一发 HTTP 的地方。这里记的是它已经做过的决定 —— 改之前必须先读懂，否则很容易把已经修好的坑重新挖开。

## 请求基址

`EmbyServerAddress.TryNormalize` 接受 `192.168.1.5:8896`、`http://host/emby`、`https://host/media/` 之类，一律归成 **`scheme://host:port/<path>/emby/`**：

- 没写 scheme 就补 `http://`；只允许 http / https；缺 host 报错。
- 路径末尾的 `/emby` 会被剥掉再拼回去，所以用户从浏览器粘 `…/emby` 不会变成 `/emby/emby/`。
- 相对路径靠 `new Uri(apiBase, relative)` 解析，**`apiBase` 的尾斜杠是这个解析成立的前提**。
- `ToDisplayString` 是反向操作：去掉 `/emby` 给界面显示。

## 鉴权

规格的 `components.securitySchemes` 登记两种：

| 方案 | 形式 |
| --- | --- |
| `apikeyauth` | query `api_key`，**或**请求头 `X-Emby-Token` |
| `embyauth` | 用户级 http 鉴权 |

客户端走头，不走 query：

```
X-Emby-Authorization: MediaBrowser Client="EmbyNian", Device="<机器名>", DeviceId="<稳定 id>", Version="<版本>"
X-Emby-Token: <access token>          # 仅在有 token 时发
Accept: application/json
```

- `DeviceIdentity.ToAuthorizationHeader` 负责拼这行。**Emby 按引号切分解析它**，所以 `Sanitize` 会丢掉 `"` `\` `,` `;` 和控制字符 —— 机器名里有一个引号就足以弄坏每一个请求。`DeviceName` 取机器名，`Client` 固定 `EmbyNian`。
- `DeviceId` 必须跨重启稳定，否则服务器会把每次启动当成一台新设备。
- **`Redact(url) = url.GetLeftPart(UriPartial.Path)`**：日志里永远只有路径，query 一律剥掉，`api_key` 不会落盘。任何新的日志点都走它。

## 重定向与凭据边界

`AllowAutoRedirect = false`、`UseCookies = false` —— **构造时强制覆盖，注入自定义 handler 也绕不过**（`EmbyHttp.cs:52-62`）。跳转由 `EmbyHttpRedirect` 手工跟，规则：

- 上限 5 跳。
- **认证只属于最初来源。** 一旦跳到别的源，`authenticated` 永久置 false，**整条链都不能再拿回认证**。
- 只允许 http / https，且地址不带 userinfo。
- 拒绝 HTTPS → HTTP 降级。
- 非同源时：登录请求和写请求**直接拒绝**；只有 GET / HEAD 允许跨源跟随。
- 写请求只接受**同源 307 / 308**（明确保留方法与正文）。301 / 302 / 303 的写请求拒绝 —— 不把写入悄悄降级成 GET，也不猜正文能不能重放。
- 跨源的 401 抛"重定向资源拒绝访问"，**不触发重新登录**。资源来源的 401 不是 Emby 令牌失效；让它触发重登会把用户从自己的服务器踢下线。

## 错误语义

`EmbyHttp.Translate`（`EmbyHttp.cs:362`）把状态码翻成异常：

| 情况 | 异常 |
| --- | --- |
| 401 且 `IsAuthenticationAttempt` | `EmbyAuthenticationException`「用户名或密码不正确」 |
| 401 其他 | `EmbyTokenExpiredException`「登录状态已过期，需要重新登录」 |
| 403 | `EmbyApiException`「当前账户没有访问该内容的权限」 |
| 404 | `EmbyApiException`「服务器上找不到该资源（已脱敏 URL）」 |
| 其他非 2xx | `EmbyApiException`「服务器返回 HTTP {code}」 |
| 超时 / 连接失败 / IO | `EmbyUnreachableException` |

- **401 的分类只靠 `IsAuthenticationAttempt`**，只有登录请求本身传 true。别在别处复用这个标志。
- 其他状态码**不带 ReasonPhrase、不带正文**：两者都可能回显请求凭据，而异常会被界面和日志直接使用。
- 错误正文照读但**不解码、不交给诊断**，且只读前 8000 字节 —— 目的是让连接能复用。
- 正文解析失败（`JsonException`）也是 `EmbyApiException`，带上行号和字节位置。
- `PostForJsonAsync` / `DeleteForJsonAsync` 是例外：正文缺失或读不出来算 `null`，**不算失败**。用户状态接口会回 `UserItemDataDto`（服务器自己的续播位置和剩余集数在那儿），但没有版本承诺它一定在 —— 所以"没有正文"和"调用失败"必须分开。

## 超时、正文与下载

- **普通请求 30 秒，且覆盖到正文读完。** `HttpCompletionOption.ResponseHeadersRead` 会让 `HttpClient.Timeout` 在**响应头到达时**就结束，所以 `SendAndReadAsync` 另建一个 `deadline` CTS（`EmbyHttp.cs:164-177`）。少了它，共享图片下载能永远占着连接和下载任务。
- **下载到设备用第二个 `HttpClient`**（`_long`，`Timeout.InfiniteTimeSpan`）：影片下载可以持续更久，只受调用方取消和 handler 的连接超时约束。两个 client 共用 handler（`disposeHandler: false`），`Dispose` 里只放一次。
- **下载先写 `path + ".part"` 再 `File.Move` 改名。** 服务器给了 `Content-Length` 就必须收满，少了当失败并把 `.part` 删掉（`EmbyHttp.cs:255-282`）—— 否则磁盘上留下的是一个名字正确、大小不对的影片。进度每 4 MB 报一次。
- **`PostBytesAsync` 走 `RawBody`**：正文是原样字节而不是 JSON，用 `ByteArrayContent` 保证准确的 `Content-Length` —— **Emby 对 chunked 分块传输吃不消**（`EmbyHttp.cs:90-112`）。写新的非 JSON 上传时照抄这条路。
- JSON 选项（`EmbyHttp.Json`）：`PropertyNameCaseInsensitive`、`AllowReadingFromString`（Emby 会把数字写成字符串）、`WhenWritingNull`。

## 正文形状不是一套

同一个"上传一张图"，两条路的正文完全不同，混用必然失败：

| 场景 | 正文 | 出处 |
| --- | --- | --- |
| 条目封面 | 图片**原始字节** + 真实 content type | `PostBytesAsync`，见 `EmbyUrl.Image` 一族 |
| 用户头像 | **Base64 的 ASCII 文本**，content type 仍写 `image/jpeg` / `image/png` | `EmbyClient.Users.cs:74-79` |

`EmbyClient.Users.cs:74` 那行注释就是为这件事留的。按网页端的行为抄，别按另一条路的直觉推。
