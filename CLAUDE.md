# EmbyNian — Claude 常设规则

本文件是当前开发规则的权威出处；操作细节见 [docs/开发与验证.md](docs/开发与验证.md)，在途工作与历史验证记录见 [PROGRESS.md](PROGRESS.md)。按任务需要读取，不必每次先读进度。历史记录描述当时的决定，不替代本文件的现行规则。

## 首要目标

- **技术规则是手段，不是目的。** 分层、MVVM、DI 和接口是默认选择；当它们在某处让代码更绕、更容易出错时，采用更简单的路径，说明位置、原因与取舍，并在 PROGRESS.md 记一行。
- **用户的技术指示可以讨论。** 若点名的实现方式会让代码变差，用一两句平实的话说明代价；用户再次确认后照做。
- **简单不等于行数少。** 看改一个行为需要打开多少文件、单个文件能否读懂、重要决定能否用测试验证。保留解释约束与设计原因的注释，不为缩短代码删掉维护者需要的上下文。
- **用户决定产品范围和可见体验，助手负责实现与验证。** 不把“需要用户看一眼”当作省略可执行检查的理由，也不以测试通过代替视觉检查。
- **简化不能豁免流程与安全。** 验证与交付要求、真实媒体库保护、凭据和 Git 规则仍须遵守。机器相关说明是带适用条件的经验；出现不同证据时先核实，不把旧故障当作永久诊断。

## 项目与技能

Emby 桌面客户端：WinUI 3、Windows App SDK、.NET 10、C#，非打包、x64，播放内核 libmpv。SDK 版本见 `global.json`，包版本见各项目的 csproj，不在这里重复记录。

- `src/EmbyNian.Core`：不依赖 UI 框架。目前不引用 NuGet 包，这是保持轻量的选择，不是“有包就不能测试”的限制。
- `src/EmbyNian.Shell`：WinUI 外壳。
- `tests/EmbyNian.Tests`：目前只引用 Core 的离线控制台测试运行器。以后需要覆盖其他无 UI 逻辑时，可以调整测试边界，不必为了现有项目引用把所有判断搬进 Core。

**播放管线与后端是两件事。** 集成管线将 mpv 的 Composition 交换链嵌入 WinUI 视觉树；独占管线（Standalone，亦称独立模式）使用 mpv 自建的顶层窗口，控件来自 `assets/mpv-ui` 的 uosc，换片尽可能在同一个窗口里换源，兼容性由 `src/EmbyNian.Core/Mpv/InlineSwitch.cs` 判定。默认管线以 `AppSettings.cs` 为准。内置 libmpv 与外部 mpv 进程两个后端的细节见播放技能。

项目技能在 `.claude/skills/`，随代码入库：

- [embynian-winui-shell](.claude/skills/embynian-winui-shell/SKILL.md)：修改或审查 C# / XAML 前读取，说明分层、DI、Attach 与 UI 陷阱。
- [embynian-playback](.claude/skills/embynian-playback/SKILL.md)：播放后端、进度上报、控件、字幕音轨与换片。
- [embynian-verification](.claude/skills/embynian-verification/SKILL.md)：验证读数、截图、指针移动及其证据。
- [mpv-shader-quality](.claude/skills/mpv-shader-quality/SKILL.md)：着色器档位与画质链。

通用 `winui-*` 技能来自用户级技能目录或 win-dev-skills 插件。**与本文件冲突时以本文件为准，并修正冲突的项目文档。** 技能保存具体操作与技术依据，不另立一份升级、验证或安全政策。`.workbuddy/`、`.zcode/` 是其他工具的工作数据目录，不在那里复制规则。当前文件数、测试数、控件数从源码或工具输出获取，不在规则里维护数字副本。

## 分层与代码风格

以下是实现默认值，服从首要目标：

- **纯 UI 行为可以留在 code-behind。** 显隐、Frame 导航和返回栈、菜单项构建、窗口按钮及需要知道事件来自哪个元素的行为，不必为 MVVM 增加中间层。
- **业务与外部交互经 Service。** HTTP/Emby 调用、持久化和播放生命周期不放进页面；视图模型从服务取得能力，普通 MVVM 管道使用 CommunityToolkit.Mvvm。`Views/ItemCommands.cs` 是现有旧债，不作为新增代码模板。
- 无 UI 依赖的业务能力优先住 Core；需要共享依赖或生命周期的服务注册于 `src/EmbyNian.Shell/Composition/ShellServices.cs`，然后注入。纯函数、模型和无依赖的小工具不需要为“进容器”包装成服务。
- **重要业务规则、容易回归的算法和可复用的计算优先成为 Core 中的命名纯函数，并用测试验证。** 局部展示判断就近保留；涉及续播、选集、设置迁移等业务语义时，不能以“只是一个判断”为由逃避测试。
- 不因为类进了 DI 就加接口。接口用于替换、隔离或收窄能力；`ISettingsService`、`IServerCapabilities` 等用于收窄访问范围的接口不要机械地换成具体类。
- 页面默认自建视图模型，通过 `Attach(...)` 接收依赖，并守卫尚未附加的调用。`PlayerViewModel` 由容器持有，因为播放状态需要跨页面导航存活。
- 复用现有类型；只有确实改善职责或复用时才抽新类，不按 `Manager` / `Helper` / `Factory` 的名字判断设计好坏。`View → ViewModel → Service → 外部世界` 是方向，不是每个文件都必须走完的链。
- **绑定到 `ItemsSource` 的集合对象保持稳定**：默认 `{ get; }` 加初始化器，允许原地增加、删除、移动和更新。需要整体刷新时才 `Clear()` 后重填；小改动避免全量重建，并注意选择与滚动状态。

格式以 [`.editorconfig`](.editorconfig) 为准：文件范围命名空间、私有字段 `_camelCase`、const / static readonly 用 `PascalCase`、不用 `this.`、LF、4 空格。空白检查见闸门 1；命名等 `IDE0###` 规则尚未作为构建门禁，不能把空白检查通过说成全部代码风格通过。

## 项目约定与自动检查

`tests/EmbyNian.Tests/AgreementsTests.cs` 与同目录 `Agreements.txt` 记录以下约定。**基线是要求复核的提醒，不是禁止合法重构。** 有意变更时先核对调用者、自动化脚本和相关文档，再逐行更新基线；不为让测试变绿直接接受整份新快照。

- **嵌套绑定**：Shell csproj 中对 `WUI2010` 的压制有明确前提，原因与失效条件写在 `NoWarn` 旁。新增三段绑定路径时先读那里；`bind` 基线会提醒复核。其他分析器警告也要调查，不能无依据地消音。
- **自动化定位**：没有显式 AutomationId 时，WinUI 会把 `x:Name` 作为句柄；已有它就不重复添加同值属性，`x:Uid` 不算句柄。普通交互控件应可稳定定位。模板与复用控件允许“所属列表项 + 局部句柄”的定位方式，不要求跨实例全局唯一，也不一概禁止添加标识。新增交互控件后运行 `tools/scan-automation-ids.js`；该脚本目前跳过模板与部分复用控件，所以这些范围仍需检查运行中的 UIA 树，不能把扫描通过当作全覆盖。`handle` 基线防止已有句柄无意消失，改名时同时更新使用者。
- **字号与颜色**：文本使用 `Theme/Styles.xaml` 的项目刻度（11/12/14/16/20/34/44）与 `Eg*Style`；主题颜色使用角色键。`FontIcon` 的数字是字形度量，按实际需要处理。`font-size` / `color` 基线逐文件记录内联数字和颜色，只减不增；必要例外或文件搬迁需复核后更新，不能把计数减少当作视觉正确的证明。
- **命令行**：`switch` 基线检查源码中的开关集合；开关变化时同步下方清单和开发文档。它不会替你验证文档用法是否正确。
- **分析器载荷**：构建使用仓库 `tools/analyzers` 的副本，`analyzer` 基线核对文件与哈希。`Directory.Build.props` 的 `Condition=Exists` 会让缺载荷的构建继续，所以只看“0 警告”不够，基线检查也必须通过。

## 依赖与分析器升级

**主动跟进稳定版，但作为独立的一轮改动，不顺手混入无关修复。** 不采用 preview / rc / beta。先说明升级范围，改版本后还原依赖，再走完整四道闸门；运行验证覆盖不到的部分照实报告。

- NuGet 版本来自项目文件；改包版本或在新工作树首次构建前，先完成 restore，不能拿旧 `obj/project.assets.json` 验新版本。
- WindowsAppSDK Runtime 升级要关注 onnxruntime 等新增载荷；报告实际包体积变化，让用户决定是否接受明显增重。
- `libmpv-2.dll` 是手动维护的原生二进制，不由 NuGet 更新。更新后验证构建、发布与受影响的播放管线，仍遵守下方三档安全边界。
- **发版验收使用仓库已确定的分析器与哈希，不以本机插件是否更新为准。** `tools/refresh-analyzers.ps1 -Check` 只是发现插件副本差异的维护工具；它按文件写时间寻找候选，不保证是语义版本最新，找不到插件时还会输出“跳过”并返回 0，不能代替载荷完整性验收。
- 升级分析器时明确来源（必要时传 `-SkillRoot`），运行不带 `-Check` 的刷新脚本，复核新诊断与哈希后更新基线，再完整验证。不要为迁就本机旧插件把仓库载荷降级。

## 四道闸门与日常交付

所有命令在**当前工作树根目录**运行。下面使用 Git Bash 语法；本机 SDK 为 `%USERPROFILE%\.dotnet\dotnet.exe`（命令里用 `$HOME`），不要使用 PATH 上的旧 SDK。构建保持 Release、单节点与所列 MSBuild 标志。

- **每轮代码改动**：先编译当前测试项目及 Core，再跑闸门 2；收尾跑闸门 3。只跑旧测试或只 build 不 publish 都不算交付。完整方案已在改动后成功构建且此后没有再改相关代码时，可直接使用那份测试输出。
- **发布新版本或升级依赖/分析器**：闸门 1～4 全跑，包括空白检查和测试中的分析器哈希检查。
- **验证工具自身变化**：除适用的日常检查外，实际运行受影响的检查路径；改自检不能等到以后发版才第一次跑它。
- **纯文档修改**：检查内容一致性、链接、命令与差异，不构建或重发应用；不要为了文档交付覆盖工作树里尚未交付的产品改动。

新工作树缺还原结果或包版本变化时，先运行：

```bash
"$HOME/.dotnet/dotnet.exe" restore ./EmbyNian.sln --disable-parallel -m:1 -p:BuildInParallel=false
```

### 闸门 1：完整构建与空白检查

```bash
"$HOME/.dotnet/dotnet.exe" build ./EmbyNian.sln -c Release --no-restore -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false -p:MSBuildNodeReuse=false
```

通过标准是 **0 警告、0 错误**。该构建经 `Directory.Build.props` 加载仓库内的 WindowsAppSDK 分析器。

```bash
"$HOME/.dotnet/dotnet.exe" format whitespace ./EmbyNian.sln --verify-no-changes --no-restore
```

Git 的行尾归一化可能隐藏工作区 CRLF 漂移，不能用 `git diff` 没变化替代空白检查；此命令也不是 PowerShell 等仓库所有文件的格式检查器。

### 闸门 2：先编译，再运行测试

日常未跑完整构建时，先执行：

```bash
"$HOME/.dotnet/dotnet.exe" build ./tests/EmbyNian.Tests/EmbyNian.Tests.csproj -c Release --no-restore -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false -p:MSBuildNodeReuse=false
```

**前一步失败就停，不运行旧测试冒充验证。** 成功后执行：

```bash
"$HOME/.dotnet/dotnet.exe" run --project ./tests/EmbyNian.Tests/EmbyNian.Tests.csproj -c Release --no-build
```

通过标准是失败为 0、退出码为 0，跳过项单独报告。`-c Release` 防止误取 Debug 输出；`--no-build` 只在当前代码已经编译成功时使用。`publish.ps1` 只构建应用，不能补上测试编译这一步。测试数量从本次输出读取，不存进规则或记忆。

### 闸门 3：发布到实际交付目录

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish.ps1 -NoArchive
```

一轮结束、把界面交给用户之前，发布件必须更新到桌面快捷方式指向的 `artifacts/publish/win-x64`。中间步骤可以运行当前工作树的开发构建，不必反复发布或反复要求关应用。发布前确认目标程序未开着；遇文件锁请用户关闭，不擅自杀进程。未能刷新时明确报告“快捷方式仍是旧版”。

**独立工作树里的默认 publish 只更新该树的 artifacts，不会更新主目录或已有快捷方式。** 并行任务由一个交付会话整合变更、重新验证，再从实际交付树发布；其他会话不要覆盖共同发布目录，也不要改快捷方式来掩盖未交付。`-OutputRoot` 或 `-SkipPublish` 的成功不能自动等同于用户看到新版。

### 闸门 4：自检与基线比较

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools/selfcheck-diff.ps1
```

脚本启动自检、比较并判红绿：不能有失败、检查消失或“通过 → 信息”降级。它按检查名与状态比较，不比较易变读数；临时按非最大化运行后恢复 `settings.json`。加 `-Log <文件>` 只比较已有日志；确认新增检查后才用 `-UpdateBaseline` 更新 [基线](docs/selfcheck-baseline.txt)。原始报告中易变读数的解释见开发文档「逐行比报告：每次都会变的行」。

自检默认在副显示器，单显示器时自动回退；`--screen 1` 可明确指定主屏。普通自检可能登录已保存的账号并读取真实库，**不是离线播放探针，也不能证明真实播放成功**。截图与指针证据按验证技能执行。

**改颜色时，每个存活主题各拍一张。** 主题清单从 `src/EmbyNian.Core/Theming/UiThemes.cs` 获取，目前均为深色。内联颜色基线与调色板检查不能代替实际观感；保留浅色派生逻辑的理由见该文件注释，不擅自恢复已删除的主题。

## 播放相关改动的验证（三档）

内网 Emby 服务器是用户的真实媒体库，不是测试夹具。地址与端口不写进公开仓库；真实播放会改变观看历史与续播点。

1. **默认不播放。** 用 `--show-detail` / `--show-episode` / `--show-library` / `--show-osd` / `--show-cover` 展示界面，配合 `--theme` 与截图脚本。普通导航可能读取已登录服务器的数据，不要把“不播放”说成“不联网”。
2. **播放管线改动使用隔离本地探针。** `--probe-cursor [<本地视频绝对路径>]`、`--probe-player-motion [<本地视频绝对路径>]` 与 `--probe-composition [<本地视频绝对路径>]` 在独立数据目录运行，不登录、不启动 Emby 播放、不上报观看记录；本地素材拒绝 URL、网络盘、重解析路径及外部网络引用。**现有这三条探针固定使用集成管线**：可验证该管线的几何、过渡与指针行为，但不覆盖独占窗口、uosc 或独占同窗换片。独占改动需报告已有单测和运行验证缺口，不能拿集成探针通过声称独占已验证；新增离线入口也必须保持同样的隔离边界。
3. **真实服务器播放或有副作用的验证先获授权。** 可以按用户当次确认执行，或在用户明确授权的专用测试账号与操作范围内执行。测试账号应限制权限；它能隔离观看记录，不隔离服务器负载、共享媒体与元数据。建立账号本身也需授权，删除媒体、改共享数据等不由“测试账号”自动授权。

各档共同遵守：

- **不点卡片中央或海报本体**，那里会浮出真实播放按钮。用导航开关、hero 的“详情”、面包屑或卡片底部标题条到详情页；不要依赖 Escape 能撤销误起播。
- 用应用命令行开关导航，不用坐标点击。用户可能正在使用同一只鼠标；写任何移动指针的验证前，先读验证技能的 “Moving the pointer, and proving it moved”，并验证目标确实收到输入。
- 悬停才出现的浮层提供 `--show-*` 状态入口，截图不依赖停住的指针。
- `--play`、UIA invoke 播放按钮等都是真起播，同受第三档约束；工具名称不改变副作用与授权要求。

## 凭据

Emby 访问令牌用 DPAPI 加密存储，**不打印、不写日志、不进入自检报告、命令或 URL**。内嵌 Emby 网页控制台为兼容其客户端，在受信任服务器来源的 `localStorage` 中传递凭据，不用查询字符串，也不向其他站点注入。自检报告中的令牌出现次数必须为 0。

## Git 与协作

- `origin` 是 `https://github.com/cudamin/EmbyNian.git`；本机 Git Credential Manager 保管凭据，令牌不进命令、脚本或远程 URL。
- 长期主干是 `master`，不维护第二条跟随主干的长活分支。隔离工作可用临时任务分支；不要假定任意工作树的 HEAD 都在 master。主干交付推 `git push origin master`，不做 `git push . <branch>:<branch>` 的 ref 搬运。
- **同一棵工作树同时只有一个写入会话。** 先检查状态，保护已有未提交工作；并行写入使用独立 `git worktree`，在各自 PROGRESS 中记树与文件范围。构建、测试、临时产物也各自在本树；最终交付按闸门 3 串行整合。
- **命令定位到当前工作树，不固定跳回主目录。** 优先使用绝对文件路径；需要根目录时确认当前会话目录与 `git rev-parse --show-toplevel` 的结果一致。不要依赖上一次 shell 调用的 `cd`。
- 不 reset，不用 checkout / restore 覆盖工作树内容。撤销自己刚做的实验时先核对 diff，再定点手改，不能丢掉用户或其他任务的改动。
- 只有用户明确要求才提交。提交信息用中文正文，加 `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`；提交后立即推 origin，不必再问。未获提交授权时不提交也不上传。源文件使用 LF。

## 本机工具与故障排查

- **SDK**：使用上面指定的用户级 .NET 10 SDK，按 `global.json` 核实；PATH 上的旧版本不足以证明环境可用，不运行通用 setup 去修复一个没有确认的问题。保留单节点构建，本机 workload 解析器曾在多节点下间歇性无输出失败。
- **Python**：用 `py` 启动器；PATH 中 `python` / `python3` 曾被 Microsoft Store 存根遮蔽，静默退出不代表脚本执行成功。解释器版本以当场查询为准。
- **PowerShell**：脚本保存为 UTF-8 带 BOM、LF，兼容本机 Windows PowerShell 的中文读取。不要声称 `dotnet format whitespace` 会检查所有 `.ps1`。
- **读取与转义**：优先专用文件读取/搜索工具；本机 shell 曾有遮蔽系统命令的函数，行号或截断异常时核实实际执行的命令。需要精确行号用读取工具；复杂 `node -e` 转义优先改成文件脚本或正则字面量，检查实际命中而非只看退出码。
- **WMC9999**：曾由 WinUI 标记编译缓存引起，也曾报过期源码位置。保留错误输出后可原样重试一次；持续失败就正常定位，不能凭错误码断言“不是 XAML 的错”，也不盲目清空输出。
- **PRI**：若发布件报找不到 `Microsoft.UI.Xaml/Themes/themeresources.xaml`，检查 Shell csproj 的 `AddWindowsAppSdkFrameworkPriToPayload` 与发布验证脚本。框架资源缺失不是“多发布一次”能修好的；包升级后核实框架 PRI 是否仍被合并。
- **截图**：`tools/shot.ps1` 拍真实屏幕，`--dump-ui` 的 PNG 不合成 Mica，不能用来判颜色。普通 GDI / UIA 截图不包含鼠标光标；使用 `tools/cursor-watch.ps1` 的光标读数与叠加图。权限拒绝时不换工具绕过；先区分权限拒绝、运行失败和捕获方法的限制，证据与指针机制见验证技能。

## 命令行开关清单

源码入口是 `src/EmbyNian.Shell/Program.cs`；`AgreementsTests` 的 `switch` 小节提醒开关变化后同步文档。

- **导航与展示**：`--show-detail`、`--show-episode`、`--show-library`、`--show-settings <分类>`（默认“界面”）、`--show-menu`（首页第一张卡的“更多”菜单）、`--show-cover`（配 `--show-detail`）、`--scroll-end` / `--scroll-half`（配详情类开关）、`--theme <id>`、`--maximized`、`--screen <n>`。
- **不播放的状态展示**：`--show-osd [pinned|paused|playing]`、`--hide-cursor`（播放页心跳照常运行，观察自动隐藏）。
- **隔离探针**：`--probe-cursor [<本地视频绝对路径>]`、`--probe-player-motion [<本地视频绝对路径>]`、`--probe-composition [<本地视频绝对路径>]`。
- **自检与真起播**：`--self-check`、`--dump-ui`（配自检）、`--play`（真实服务器播放，须获授权）。

**`--show-menu`、`--show-osd`、`--hide-cursor` 各自单独用，互不混用，也不与 `--self-check` 同用。** 菜单/浮层会挡住自检换页；自检结束会退出，无法观察持续隐藏；固定 OSD 与自动收起正好相反。导航开关可能换页、抢激活使浮层消失；各探针也单独运行。

## 汇报

用中文、大白话讲结果，不贴代码。说明改了什么、实际验证了什么、哪些没有验证，以及用户需要知道的代价。实现细节自行决定；需要用户裁定时只问范围、优先级、可见行为、不可逆操作或规则取舍，不提供无关的技术选项菜单。
