# EmbyNian — 给 Claude 的常驻规则

动手之前读这份。**手上没做完的活看 [PROGRESS.md](PROGRESS.md)** —— 两份文件分工不同：这里是长期不变的规矩和命令，那里是这一阶段在干什么。

## 首要目标（用户定的，2026-09-02）

**在满足需求的前提下，代码要尽可能简单、清晰、可靠、可维护。**（原话：我想要让 claude 写的代码在满足需求的前提下，让代码尽可能简单、清晰、可靠、可维护，而不是被一些莫名其妙的规则束缚，因为我不是很懂代码，可能会瞎指挥。）

**这一条排在本文件其余所有条款之上，包括下面那节分层规则。** 怎么用：

- **技术规矩是手段，不是目的。** 分层、MVVM、DI、接口那几条是默认做法，因为多数时候它们确实让代码更清晰。**某一处照着做反而更绕、更长、更容易错的时候，走更简单的那条路，别硬套** —— 但要说出来：哪一处、为什么、改成了什么，并在 PROGRESS.md 里留一句。默默偏离和硬套一样坏。
- **用户的技术指令也可以被反驳。** 他自己说了可能会瞎指挥。所以他点名某种实现方式而你判断那样会让代码更糟时，**先用一两句人话讲清代价，再让他定**；不要闷着头照做，也不要闷着头不做。他重申一遍就照办 —— 那时候它是他的决定，不是误会。
- **「简单」的判据是这三样**：改一处功能要打开几个文件、一个文件还读不读得完、一个决定能不能被单测钉住。**不是行数少** —— 解释「为什么这么写」的注释占了全部源码的三成二，它们是留给下一个没有上下文的窗口的，删掉不叫简化，叫把维护成本转移给下一个人。
- **他看得见的东西以他为准，看不见的以你为准。** 这个项目里每个真被抓到的毛病都来自他看屏幕：海报被裁掉一条、左上角空一块、集列表一圈黑边。那是他的强项。反过来，用哪个类、拆不拆文件、要不要接口，是你的活，别拿去问他。
- **下面这几节不在本条的豁免范围内**：四道闸门（连 `-c Release` 和单节点构建）、验证时不要真实播放、凭据、Git、这台机器上的坑。它们约束的是流程和安全，不是代码形状，而且每一条都是拿一次真事故换来的 —— 绕过它们不会让代码更简单，只会让下一次回归没人看见。

## 这是什么

Emby 桌面客户端。WinUI 3 + Windows App SDK 2.4.0 + .NET 10 + C#，非打包、x64，播放内核是 libmpv（`libmpv-2.dll` 不要换版本）。三个工程：`src/EmbyNian.Core`（不引任何 NuGet 和 UI 包，因此是唯一能被单测直接覆盖的层）、`src/EmbyNian.Shell`（WinUI 外壳）、`tests/EmbyNian.Tests`（控制台测试运行器）。

## 分层规则（用户定的，2026-08-24）

服从上面那节「首要目标」：下面两条是默认做法，不是不能碰的法律。

1. **纯 UI 行为可以留在 code-behind。**（原文：纯 UI 行为可以使用 Code-Behind。）指 `Visibility` 切换、`Frame` 导航与回退栈、手搭的 `NavigationViewItem`/`MenuFlyoutItem`、窗口按钮，以及任何需要知道「事件是在哪个元素上发生的」的地方。理由：`MenuFlyout` 只有 `Items` 没有 `ItemsSource`，`NavigationView` 改用 `MenuItemsSource` 会丢掉分隔符 —— 这类东西硬做数据绑定只会更糟。
2. **业务能力一律走 Service。**（原文：项目整体采用 MVVM 架构；ViewModel 通过 Service 获取业务能力；Service 由 DI 容器管理；CommunityToolkit.Mvvm 用于实现 MVVM 的常规功能。仅在确有必要时引入额外抽象。）

落地方式（下面这几条是照现在的代码写实的，2026-09-02 复核过一遍）：

- **业务能力放 `src/EmbyNian.Core`，在 `src/EmbyNian.Shell/Composition/ShellServices.cs` 注册**（`ValidateOnBuild = true`），注入使用。**别按 `Core/Services/` 这个目录名去找**：真正的业务能力大半在 `Core/Emby`、`Core/Playback`、`Core/Configuration` 里（会话、Emby 客户端、图片缓存、播放、凭据、设置存档），`Services/` 只装着后来单独切出来的那几个。判据是「在哪个工程、有没有进容器」，不是目录名 —— 照目录名搬文件是白改。
- **能算出答案的判断也搬进 Core，哪怕它算不上一个「服务」。** 主页上有哪几排、窗口该摆到哪块屏、两批图是不是同一批、海报那一格多大、缓存该删哪几张 —— 这类「给定输入就有唯一正确答案」的东西写成 Core 里的纯函数，在测试工程里钉住。**理由是覆盖**：单测只够得着 Core，判断留在视图模型里就等于没人看着它。`HomeLayout.Plan` 那个会永久丢掉用户排好次序和勾选的洞正是这么来的 —— 四道闸门一条都没红，靠截图撞见的。
- **不要仅仅因为一个类要进 DI 就给它造接口**；没有第二种实现、也没有替换或隔离需求的，直接注册具体类。**例外是收窄**：`ISettingsService` / `IServerCapabilities` 各只有一个实现、测试里也没有替身，留着是为了让一个只想知道「每页多少条」的页面摸不到会话、图片缓存和播放器。**别把这两个「清理」成具体类** —— 那是把一片能随手摸到的东西重新摊开。
- **视图模型由页面自己造，依赖靠一个 `Attach(...)` 后补上去。** WinUI 的页面必须有无参构造、而且由框架构造而非容器构造，所以给页面做构造注入走不通。除播放那个以外的视图模型都是这个样子，代价是服务字段可空、外加一圈「还没接上就早退」的守卫 —— 那不是图省事，照着写就对了。**只有 `PlayerViewModel` 进容器**：电影在库页背后接着播，它的状态得比任何一次导航活得久。
- code-behind 不放业务逻辑、HTTP/Emby 调用、播放状态管理和持久化。**已知一处例外：`Views/ItemCommands.cs`**（卡片右键那套已看／收藏／编辑元数据）自己捏着 `EmbySession` 发请求，没进容器、也没有一条单测，其中「编辑元数据」还是读一份 JSON 改几个字段再整份写回。**那是旧账，不是样板，别照着抄。**
- `View → ViewModel → Service → 外部系统` 是方向，不是每个文件都要凑齐的链条。模型、转换器、小控件、helper 没有业务依赖就让它们保持简单。
- 动手前先看现有的 Service / ViewModel / Core 类型和 DI 注册，能复用就复用；别机械地抽 `Manager`/`Helper`/`Factory`/`IWhatever`。**这一条管不着上面「判断搬进 Core」那一条** —— 那些是有名有姓、有单测钉着的纯函数，不是给已有东西套一层壳。

## 四道闸门（改了代码就全跑一遍）

`dotnet` 不能直接用：`PATH` 上那个是 8.0.403，本项目要 .NET 10，SDK 在 `%USERPROFILE%\.dotnet\dotnet.exe`，没有进 `PATH`。**必须单节点构建** —— 本机 Windows SDK 的多节点 workload resolver 会让并行构建偶发无输出失败。

1. 构建

   ```
   %USERPROFILE%\.dotnet\dotnet.exe build .\EmbyNian.sln -c Release --no-restore -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false -p:MSBuildNodeReuse=false
   ```

2. 测试

   ```
   %USERPROFILE%\.dotnet\dotnet.exe run --project .\tests\EmbyNian.Tests\EmbyNian.Tests.csproj -c Release --no-build
   ```

   **`-c Release` 不能省。** `dotnet run` 默认找 Debug 那份输出，配上 `--no-build` 就会跑 `bin\Debug` 里那个不知道多久以前的旧程序，还照样打「全部通过」外加一个 0 退出码 —— 2026-08-31 抓到时，那份 Debug 二进制已经是两天前的，比源码少一百六十项测试。省掉这个开关等于把这一关关掉。

3. 发布：`powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish.ps1 -NoArchive`
4. 自检：`artifacts/publish/win-x64/EmbyNian.exe --self-check --dump-ui`，然后读 `%LOCALAPPDATA%\EmbyNian\logs\selfcheck-shell.txt`，要退出码 0、末尾是「结果：全部通过」。

关于闸门的几件事：

- **自检默认开在副屏**，不会抢走正在用的屏幕；要盯着它跑就加 `--screen 1`，只有一块屏的机器上这个默认值自动失效。
- 报告里有十来行本来每次都不一样的数字（时间戳、两个 hwnd、字体过滤耗时、光标句柄与线程 id、空闲计时的 2000/2015 ms、任务栏等待、亚克力背景取色、首页轮播各类图的服务器计数、诊断页与日志行数）。只有这些变不算回归，完整清单在 PROGRESS.md。
- 通过项数会随开发一直增长，**别把某个具体数字写进文档或记忆里**，写了就等着它变旧。
- **界面是能截图的**：`tools/shot.ps1`（`-Exe … -ExeArgs "--theme daylight --show-settings" -SettleMs N`，负责启动、拍照、关闭）和 `--dump-ui` 都在，自检自己也会留一张。
- 改过颜色就按主题各拍一张：六套主题在 `src/EmbyNian.Core/Theming/UiThemes.cs`（`emby-dark` 默认、`oled-black`、`midnight`、`graphite`、`plum`、`daylight`），`daylight` 是唯一的浅色，外壳里写死的颜色和系统自绘的标题栏只在它身上露出来。

## 验证时不要真实播放

线上 Emby 服务器（192.168.31.230:8896）是用户的真实媒体库，不是测试夹具 —— 真播一次就会写进播放记录和续播点。

- **不要点卡片正中间**：那里是悬停时浮出来的播放按钮，点下去就是播放，`{ESC}` 停不下来，只能强杀进程。要进详情页就走 `--show-detail` / `--show-episode`，或者首页大图的「详情」按钮、面包屑、卡片底部的标题条 —— 别点画面本身。
- 这台机器上**程序化移动鼠标是无效的**（`SendInput` 返回 1 但光标不动，`SetCursorPos` 同样），所以 `tools/poke.ps1` 会点在光标恰好停着的地方。导航一律用命令行开关，不要用鼠标。
- `--play` 是唯一会真的开始播放的开关。

## 凭据

Emby 访问令牌以 DPAPI 包裹存放。**绝不打印、绝不写进日志、绝不写进自检报告**。要注入网页视图就走 `localStorage`，不要放在查询串里。自检报告里 token 的出现次数必须是 0。

## Git

- 远端 `origin` 是 `https://github.com/cudamin/EmbyNian.git`，**私有仓库**。凭据由这台机器的 Git Credential Manager 保管，不要把令牌写进命令、脚本或远端地址。
- **只有一条长期分支 `master`**，HEAD 在它上面。它原名 `winui3-rewrite`（WinForms → WinUI 3 那次改造留下的名字），2026-09-02 用户拍板改名为 `master`；旧名字在同一天从远端删掉，历史记录里提到 `winui3-rewrite` 的地方讲的都是改名之前的事。
- **不要再造第二条分支去跟随主干。** 从前有一条无条件跟随的镜像分支，白占每次提交后的一次快进和一次推送，已经删了。推送就一句 `git push origin master`，也不要再写 `git push . <分支>:<分支>` 那套改引用的挪法。
- **不要 reset / checkout / 回退工作树里的迁移成果**（WinForms → WinUI 3 那一整次）。
- 只在用户明确要求时提交。提交信息中文正文 + `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`。源码一律 LF。
- 提交完顺手推到 `origin`，不用再问一遍（2026-09-01 用户定的）。没有定时任务，推送只跟在提交后面发生；未提交的工作区改动不入库、也不上传。

## 这台机器上的坑

- bash 每次调用工作目录都会重置 → 命令前面带 `cd "C:/Users/89400/EmbyNian" &&`。
- 没有 `python`。
- PowerShell 脚本必须存成 **UTF-8 带 BOM**，否则中文输出乱码。
- shell 里有个自定义的 `cut()` 函数会盖掉 `/usr/bin/cut`；要截断就用 `awk '{print substr($0,1,N)}'`。
- 命令行开关（全部）：`--dump-ui`、`--maximized`、`--play`、`--screen`、`--scroll-end`、`--scroll-half`、`--self-check`、`--show-detail`、`--show-episode`、`--show-library`、`--show-settings`（可跟一个分类名，如 `--show-settings 关于`，默认开在「界面」）、`--theme`。

## 怎么汇报

用户不读代码：实现细节自己定、自己验证，报告用中文讲人话、**不要贴代码**。该问的只有真正属于他的决定 —— 范围、优先级、用户能看见的行为、不可逆的操作，以及**某条规矩正在把这一处的代码弄糟**（见「首要目标」）。给他可挑选的列表，不要给技术选项菜单。
