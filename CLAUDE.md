# EmbyNian — Claude 常设规则

本文件保存的是长期有效的规则和命令；仍在进行中的工作记录在 [PROGRESS.md](PROGRESS.md)，那份文件保存的是当前阶段在干什么。**有需要的时候才读取**——不必每次动手前都先读。


## 首要目标（Prime Directive）— 用户裁定，2026-09-02


**本条款高于本文件中其他所有条款，包括分层规则。** 用法：

- **技术规则是手段，不是目的。** 分层、MVVM、DI 和接口之所以是默认选择，是因为它们通常确实能让代码更清晰。**当在某处遵循其中一条会让代码更长、更绕、更容易出错时，选更简单的路径**——但要说明：哪个位置、为什么、改成了什么，并在 PROGRESS.md 里记一行。悄悄偏离和盲目服从一样糟糕。
- **他的技术指示可以争论。** 他自己说过可能会指错方向，所以当他点名的实现方式在你看来会让代码变差时，**用一两句平实的话讲清代价，让他来决定**。如果他再说一遍，就照做：那是他的决定，不是误解。
- **"简单"用三件事衡量**：改一个行为需要打开多少个文件、单个文件能否从头到尾读完、一个决定能否用单元测试钉死。**不是行数**——解释代码为何如此的是写给下一个毫无上下文的会话窗口的注释，删掉它们就是把维护成本转交给它。
- **他裁决他看得见的；你裁决他看不见的。** 本项目抓到的每一个真实缺陷都来自他盯着屏幕——那是他的强项。用哪个类、要不要拆文件、要不要加接口，是你的事。
- **以下章节不豁免**：四道闸门（包括 `-c Release` 和单节点构建）、播放验证的三档（默认那两档照样是禁区）、凭据、Git、这台机器上的坑。它们约束的是流程和安全，不是代码形态，而且每一条都是用真实事故换来的——绕开它们只会让下一次回归变得不可见。

## 这是什么（What This Is）

一个 Emby 桌面客户端。WinUI 3 + Windows App SDK 2.5.1（六个子包，见 Shell 的 csproj；版本号会跟着稳定版走，别在文档里钉）+ .NET 10 + C#，非打包，x64；播放内核是 libmpv。三个项目：`src/EmbyNian.Core`（无 NuGet、无 UI 包，因此是唯一能被单元测试直接触及的层）、`src/EmbyNian.Shell`（WinUI 外壳）、`tests/EmbyNian.Tests`（控制台测试运行器）。**文件数、测试数、控件数这类数字一律不写进本文件**——它们会过期，需要时跑一条命令去数（哪条写在每条规则旁边）。

**播放架构是双管线并存**（「独占模式」＝项目里的独立模式/Standalone）：集成模式（默认）将 mpv 的 Composition 交换链嵌入 WinUI 视觉树实现控件混排；独占模式让 mpv 自建顶层窗口、独占交换链直接呈现——以窗口模型分离换取极致性能。独占模式的屏幕控件是装箱的 uosc（`assets/mpv-ui`，宿主消息名与脚本绑定名分家），换片（选集/连播/换版本/换片）也在**同一个 mpv 窗口**里换源、不关窗重开（签名闸见 `Core/Mpv/InlineSwitch.cs`）。播放细节见 `.claude/skills/embynian-playback/SKILL.md`（按路径点名，不按名字——2026-09-21 之前这里写的是 `embynian-player-architecture`，那个技能不存在，两个会话窗口照着它去找了一轮空）。

代码风格记录在 [`.editorconfig`](.editorconfig)——文件范围命名空间、不用 `this.`、私有字段 `_camelCase`、const 与 static readonly 用 `PascalCase`、LF、4 空格。它写于 2026-09-05，依据是对代码树的实测而非个人偏好。**其中「行尾与空格」那一半从 2026-09-21 起真的会被查**：闸门 1 的第二条命令 `dotnet format whitespace .\EmbyNian.sln --verify-no-changes --no-restore`（加进来那天它先红了：三个大文件的工作区副本是 CRLF、2660 行，git 因为 `.gitattributes` 归一化而不报它）。`IDE0###` 那一族仍未接线——那天实测还有 163 条空格式与 7 条未用 using 散在树上，所以命名、`this.`、命名空间格式这几条仍是「参考」而不是门；要把它变成门，先清干净再照上面这条加上去。

## 技能在哪（四份在本仓库，别再开第五个家）

`.claude/skills/` 是入库的、跟着代码走（2026-09-21 复核过四份都在）：`embynian-winui-shell`（分层、DI、`Attach` 模式——本仓库任何 C# 或 XAML 编辑都会触发它）、`embynian-playback`（两个播放后端、进度上报、OSD、字幕与音轨、换片）、`embynian-verification`（四道闸门怎么做、指针移动与「证明它移动了」那张表、截图与探针）、`mpv-shader-quality`（着色器档位与画质链）。`winui-code-review`、`winui-ui-testing`、`winui-dev-workflow`、`winui-setup` 这类 `winui-*` 技能不在本仓库，在用户级 `~/.claude/skills` 与 win-dev-skills 插件里。**与本文件冲突时以本文件为准。**（`.workbuddy/`、`.zcode/` 是别的工具自己的家、已 ignore：规则不写在那里，抄过去的那一份会各自过期——2026-09-21 就发现 `MEMORY.md` 里抄着一个已经不存在的技能名。）

### 没有任何技能能知道的四个事实

这些**不是**上述优先序的例外——它们是这台机器和这位用户服务器的事实。

- **SDK 在 `%USERPROFILE%\.dotnet\dotnet.exe`**，`PATH` 上的 8.0.403 构建不了 `net10.0` 项目。`winui-setup` 会从它读出「8.0.100+ 已存在」然后宣布工具链健康——检查正确、输入错误，所以别在这里运行它去「修复」任何东西。
- **单节点构建。** 本机的 workload 解析器，实测如此。`winapp run` 接受 `-p Name=Value` 传 MSBuild *属性*，所以闸门 1 的三个 `-p:` 标志本可以走它，但 `-m:1` 不行；这就是闸门是 `dotnet build`、分析器是手工接线的原因（见闸门 1）。
- **真播放在默认档里也是禁止的，但播放管线的改动有一条允许的路**——`--probe-cursor` / `--probe-player-motion` 带一个本地文件（见「播放相关改动的验证」那三档）。**永远不要点卡片中央**——那是悬停浮出的播放按钮，它背后的服务器是他的真实媒体库。这与他的观看历史有关而非 WinUI，所以它同样约束 `winapp ui` 的 `click` / `invoke` / `hover` / `drag` 和 `winui-ui-testing` 的「一次遍历每个元素」模板，正如它约束 `tools/poke.ps1`。只读动词（`inspect`、`search`、`get-value`、`wait-for`）和截图清单照写的方式欢迎使用。
- **包版本主动跟稳定版**（用户裁定，2026-09-20，推翻之前的「钉死」）：有更新的**稳定版**（非 preview/rc/beta）就主动升；**升完先跑一次 `dotnet restore`**（闸门命令都带 `--no-restore`，改了版本号不 restore 会看着像没生效），再走一遍四道闸门才下结论。仍要记着两件当初钉死买到的事——它们现在是升级时要盯的点，不是禁令：① WindowsAppSDK 的 `Runtime` 子包升级可能重新带入约 39 MB 的 onnxruntime，升它时报一句包体积变化让用户决定值不值；② `libmpv-2.dll` 是原生 DLL、不归 NuGet 管，升级是手动换二进制，换完必须能构建，并按播放那一节的三档验一遍。

### 本项目自立规则之处（更好写法）

四条，每条都经过实测，而且**四条现在都有绊线**（`tests/EmbyNian.Tests/AgreementsTests.cs` 加基线 `tests/EmbyNian.Tests/Agreements.txt`，2026-09-21 加的）。此前它们全靠「记得去读」，而唯一会看它们的分析器规则又不响——于是第四条、被改名的句柄、新写的一处内联字号，全是隐形的。不在此清单上的一切遵循技能。

**`WUI2010` 关闭，仅在 `src/EmbyNian.Shell/EmbyNian.Shell.csproj`** ——分析器在那份 csproj 里点了十七处嵌套 `x:Bind` 路径会崩溃，实测说不会：生成代码对每段做判空，中间量只在构造与 `Attach` 之间为 null 且此后不再为 null，另一个是非可空 get-only 属性。该规则提供的两个修法买到的都比付出的少。**推理过程，以及什么会让这条压制失效，就写在 csproj 里 `NoWarn` 旁边——添加下一条之前先读那里**；`AgreementsTests` 的 `bind` 小节会在你多写一条三段路径时先红，它替代不了读那段，只是让「忘了读」变成一次失败。这是唯一被关闭的分析器规则；其余每个 `WUI####` 都是真实发现。

**`x:Name` 是本项目已有之处的自动化句柄。** `winui-code-review` 要求每个交互控件都有 `AutomationProperties.AutomationId`；WinUI 在没有显式设置时把 `x:Name` 报为 UIA AutomationId，所以带 `x:Name` 的控件已经可寻址，再加第二个属性就是会漂移的重复（今天有多少处：跑 `tools/scan-automation-ids.js` 去数——数字不写进本文件，加一个控件就过期）。**2026-09-06 实测，不是假设**——`winapp ui inspect` 对运行中的应用直接报出 `automationId=PaneButton`、`SettingsButton`、`PlayButton` 等，皆来自其 `x:Name`。所以这里的规则是：交互控件需要一个**句柄**，`x:Name` 算数。控件两者都没有时——`x:Uid` 不是句柄，它只喂资源加载器——就加显式 `AutomationProperties.AutomationId`（2026-09-06 给那一批加了，把「完全没有句柄」的计数清零）。**两类控件故意什么都不加**：`DataTemplate` 里的一切，以及标记中只写一次但屏幕上出现多次的 UserControl 内容（`PosterCard`、`EpisodeRow`）——一个 id 跨 N 行共享让 UIA 歧义而非可寻址，比没有更糟。`tools/scan-automation-ids.js` 打印完整清单，是添加交互控件后要运行的东西——分析器自己的 `WUI2020` 覆盖此规则，但在这棵树上保持沉默的原因没人查明，所以它当不了绊线。**绊线换了一条：`AgreementsTests` 的 `handle` 小节把今天的句柄钉成清单，改名或删掉就红**——只管消失、不管新增（新增是天天发生的，而改名会让 UIA 脚本与 `winapp ui` 从此找不到那个控件，且没有任何编译器会说话）。

**字号来自本项目自己的七级刻度，而非六个内建文本样式。** `winui-code-review` 说不许裸写 `FontSize`；媒体客户端需要 11/12/14/16/20/34/44，`TitleTextBlockStyle` 及其五个兄弟不提供。刻度与命名的 `Eg*Style` 文本样式在 `src/EmbyNian.Shell/Theme/Styles.xaml`。**今天引用它的处数与仍内联写数字的处数，由 `AgreementsTests` 的 `font-size` 小节说话**（基线逐文件记着，只减不增）：那一半是欠活不是偏离——对 `FontIcon` 字形（那是图标度量而非排版）写数字买不到什么，为它硬塞一个样式只会多一层间接。要清欠活就一处一处清，基线跟着往下调；**别把「今天有几处」抄回本文件**，那是这份文件自己定的规矩。

**`tools/analyzers` 那份副本是仓库里的手工拷贝，所以它配了两条守卫。** `Directory.Build.props` 用 `Condition=Exists` 引它（干净检出没有技能目录也能构建），代价是**少了它构建照样通过、闸门 1 照样报「0 警告 0 错误」，而其实一条 WUI 规则都没跑**——与已经修过的 PRI 事故同一类。守卫一：`tools/refresh-analyzers.ps1 -Check` 拿技能里那份比（插件按版本换目录，所以按写时间取最新的一个）；守卫二：`AgreementsTests` 的 `analyzer` 小节拿基线里记的哈希比，文件不在也算红。刷新是「跑一次不带 `-Check` 的脚本，把打印出来的哈希写进基线」，发版前跑一次 `-Check`。

## 分层规则（Layering）— 用户裁定，2026-08-24

从属于上面的首要目标——下面两条规则是默认值，不是不可触碰的法律。

1. **纯 UI 行为可以留在 code-behind。** 涵盖 `Visibility` 切换、`Frame` 导航与返回栈、手工构建的 `NavigationViewItem`/`MenuFlyoutItem`、窗口按钮，以及任何需要知道事件发生在*哪个元素*上的东西。原因：`MenuFlyout` 有 `Items` 而无 `ItemsSource`，`NavigationView` 一旦换成 `MenuItemsSource` 就丢失分隔线——把数据绑定硬套上去只会更糟。
2. **业务能力永远经 Service。** 全面 MVVM；视图模型从服务取能力；服务住 DI 容器；CommunityToolkit.Mvvm 做普通 MVVM 管道；只在真正需要处加抽象。

落到实践——各一行。**推理、换回每条规则的事故、背后的地形（哪些类型、哪些接口、活例子）都在 `embynian-winui-shell` 技能里，本仓库任何 C# 或 XAML 编辑都会触发它；动手前先读它。**

- 能力住 `src/EmbyNian.Core`，注册于 `src/EmbyNian.Shell/Composition/ShellServices.cs`，然后注入。**判据是「哪个项目、在不在容器里」——永远不是目录名。**
- **对给定输入只有一个正确答案的判断，成为 Core 里的命名纯函数，用测试钉死**——即使它不是「服务」。单元测试只触及 Core，留在视图模型里的判断是没人看守的判断。
- **不因为类进了 DI 就加接口。** 例外是收窄：`ISettingsService` 与 `IServerCapabilities` 是有意为之的接口——别把它们「清理」成具体类。
- **页面自建视图模型，经 `Attach(...)` 接收依赖**，围一圈「未附加则提前返回」的守卫；那不是走捷径，就这么写。**只有 `PlayerViewModel` 在容器里**，因为电影在媒体库页面背后继续播。
- code-behind 不放业务逻辑、不放 HTTP/Emby 调用、不放播放状态、不放持久化。`Views/ItemCommands.cs` 是唯一例外，而且它是旧债而非模板。
- `View → ViewModel → Service → 外部世界` 是一个方向，不是每个文件都要走完的链；模型、转换器、小控件和无业务依赖的帮手保持简单。
- 复用现有 Service / ViewModel / Core 类型，而不是抽出 `Manager`/`Helper`/`Factory`/`IWhatever`——这不软化上面「判断进 Core」的规则。
- **绑定到 `ItemsSource` 的集合属性保持 `{ get; }` 加初始化器**，用 `Clear()` + 重填，永不重新赋值——这是 `winui-code-review` 自己的规则，也是让那些绑定上的 `Mode=OneWay` 成为形式而非承重订阅的原因。

## 四道闸门（The Four Gates）— 分两档跑

**分两档跑**（2026-09-18 他裁定「只在发版时全跑」，2026-09-21 把最便宜的那一道拆出来）：

- **每次代码改动**：闸门 2（单测——**实测一整轮 10.7 秒**、纯离线、不碰服务器）＋重发 publish。跳过它只省 11 秒，代价是发版时对着好几轮改动二分；这是四道里唯一又快又准的一道，正因为便宜才不该跟着贵的等发版。
- **发布新版本时**：四道全跑，另加两条——闸门 1 的第二条命令（风格）与 `tools/refresh-analyzers.ps1 -Check`。

自检（闸门 4）是为了抓其他东西看不见的——光标/隐藏腿与浮现规则是另外三道对它失明的机制——它也是四道里最慢的（对应用逐页完整走一遍），所以留在发版。

**但「不跑闸门」不等于「不用发布」——一轮改动收尾、要把屏幕交给他之前，必须重发 publish**：`powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish.ps1 -NoArchive`。桌面快捷方式指向 `artifacts\publish\win-x64`，只 build 不 publish，用户双击快捷方式看到的永远是旧版（2026-09-18「快捷方式怎么没变化」即此）。发前确认程序没开着，撞文件锁就请他关掉再发。**「一轮」不等于「每一步」**：同一条线里的中间改动可以直接 `dotnet run --project .\src\EmbyNian.Shell\EmbyNian.Shell.csproj` 跑 `bin` 里那个 exe 看效果（docs/开发与验证.md 里有这条路），发布留到收尾做一次——发布要他先把程序关掉，别为一次编译反复去打扰他。

`PATH` 上的 `dotnet` 不可用：它是 8.0.403，本项目要 .NET 10。SDK 在 `%USERPROFILE%\.dotnet\dotnet.exe`，不在 `PATH`。**单节点构建**——本机 Windows SDK 的多节点 workload 解析器让并行构建间歇性无输出失败。

1. 构建

   ```
   %USERPROFILE%\.dotnet\dotnet.exe build .\EmbyNian.sln -c Release --no-restore -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false -p:MSBuildNodeReuse=false
   ```

   **这道闸门承载 `Microsoft.WindowsAppSDK.Analyzers`。** `winui-code-review` 说普通 `dotnet build` 不会加载它并指了出路——项目自己的 `Directory.Build.props` 里的 `<Analyzer>` 与 `<Import>` 条目——所以它们就在那里，指向 `tools/analyzers/` 的副本而非插件更新会替换的技能目录。每条规则出厂即 `Warning`，**这道闸门读作「0 警告 0 错误」**，所以新的 `WUI####` 是要修的发现，不是要消音的噪声。刷新载荷：`tools/refresh-analyzers.ps1`（拷技能里那两份并打印哈希）；「那份副本还在不在」由 `AgreementsTests` 的 `analyzer` 小节盯着——少了它构建照样通过、这道闸门照样报 0 警告，所以「文件不见了」必须是红的。

   闸门 1 还有**第二条命令**（2026-09-21 加；`.editorconfig` 里行尾与空格那几条自此有了门）：

   ```
   %USERPROFILE%\.dotnet\dotnet.exe format whitespace .\EmbyNian.sln --verify-no-changes --no-restore
   ```

   加进来那天它先红了：三个大文件（`PlayerPage.xaml.cs`、`ShellSelfCheck.Reads.cs`、`PlayerMotionProbe.cs`）的工作区副本是 CRLF、2660 行，而 git 因为 `.gitattributes` 归一化**不报**它——一片没人看得见的漂移。清掉之后是绿的；再红就是这次改动的行尾或缩进有问题。`IDE0###` 那一族没接进来（当时还剩下 163 条空格式与 7 条未用 using），所以命名与 `this.` 那几条仍只是参考。

2. 测试

   ```
   %USERPROFILE%\.dotnet\dotnet.exe run --project .\tests\EmbyNian.Tests\EmbyNian.Tests.csproj -c Release --no-build
   ```

   **`-c Release` 不可省。** `dotnet run` 默认找 Debug 输出，所以带 `--no-build` 时它跑的是 `bin\Debug` 里的过期二进制，照样打印「全部通过」且退出码 0——2026-08-31 抓到时那个二进制比源码少 160 个测试。去掉开关＝关掉这道闸门。

3. 发布：`powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish.ps1 -NoArchive`

   **它写出的东西必须落在 `artifacts\publish\win-x64`——那是 `tools/shortcut.ps1` 把桌面快捷方式指向的目录，是用户唯一会启动的拷贝。** 用 `-OutputRoot` 发到别处（或 `-SkipPublish` 跳过）仍然验证了发布本身，但他的快捷方式留在旧构建上，屏幕上没有任何东西提示这一点。2026-09-14 实测：因应用开着发布进了临时根，他下一句报告是「貌似没有生效」——他看的拷贝比改动旧九分钟。要么先让他关应用，要么在交接里说明发布拷贝尚未刷新。
4. 自检：`powershell -NoProfile -ExecutionPolicy Bypass -File tools/selfcheck-diff.ps1` —— **它自己跑、自己比、自己判红绿**，不用再人肉读日志。绿＝本次没有 `[失败]`、没有检查消失、没有「通过 → 信息」的降级；红会把每一条连同详情打出来。它只比「哪些检查跑了、各答了什么」——详情里那些天生每次都变的数字不参与比对（清单仍是 [docs/开发与验证.md](docs/开发与验证.md) 的「逐行比报告：每次都会变的行」），窗口最大化那三条也在它内部处理掉了：它会临时按非最大化跑一次并原样还原 `settings.json`（2026-09-21 之前是人手动改两次）。基线在 [docs/selfcheck-baseline.txt](docs/selfcheck-baseline.txt)，只存「状态 + 检查名」，新增检查会报出来但不算红，认下来就 `-UpdateBaseline`。加 `-Log <文件>` 只比一份已有日志、不启动程序（程序开着时也能用）。

关于闸门的几件事：

- **自检默认开在副显示器**，免得抢走正在用的屏幕；加 `--screen 1` 可以看着它跑；单显示器机器上该默认自动失效。
- 「哪十行每次都会变」以及「最大化时三行必红」的原因（前两条拿存储尺寸比最大化的客户区、出生即内缩 8 物理像素，「屏幕像素」报的只是当前最前面的窗口）——清单与解释的唯一有效副本在 [docs/开发与验证.md](docs/开发与验证.md) 的「逐行比报告：每次都会变的行」。**`tools/selfcheck-diff.ps1` 已经把这两件事都吞掉了**（比名字与状态、不比详情；自己按非最大化跑），所以只剩一种场合需要人肉逐行：要看原始日志的时候。
- 通过的测试数随开发持续增长。**永远不要把具体数字写进本文件或记忆**——它会过期。需要数字就当场跑一次闸门 2 看输出。
- **UI 可以拍照**：`tools/shot.ps1` 启动、拍摄、关闭，`--dump-ui` 留一张截图加一棵可视树，自检也会留下自己的一张。流程与要拍哪些主题在 `embynian-verification` 技能。**`--dump-ui` 自己的 PNG 不合成 Mica**，出来是发灰的，不能用来判断颜色——`tools/shot.ps1` 可以，报告的「屏幕像素」行读真实屏幕。**`tools/shot.ps1` 以 `Add-Type` 开头，所以在拒绝它的地方（WorkBuddy 沙箱，2026-09-14 实测）整个脚本无输出死亡，PNG 干脆不写**——那里的可行路是 Python + `ctypes` GDI：`work/about-shot.py` 与 `work/badge-shot.py` 启动 exe，挑该 PID 拥有的最大可见顶层窗口（类名是 `EmbyNianHost`，不是 `WinUIDesktopWin32WindowClass`），置顶并 `BitBlt` 进 DIB；`work/pngzoom.py` 随后放大结果的任意角，20 像素徽章就是这样读出来的。
- 动了颜色 → 每主题一张截图。五个存活于 `src/EmbyNian.Core/Theming/UiThemes.cs`（`emby-dark` 默认、`oled-black`、`midnight`、`graphite`、`plum`），**五个全是深色**——第六个 `daylight` 于 2026-09-05 按用户指示删除，而它曾是唯一能抓到硬编码外壳颜色或系统标题栏黑底画黑的机制。**现在没有任何东西检查这些了。** 推导的浅色半边为何故意留在后面，见该文件的类注释。

## 播放相关改动的验证（三档，默认不碰服务器）

内网 Emby 服务器（地址与端口不写进本仓库——它在公开 GitHub 上）是用户的真实媒体库，不是测试夹具——一次真实播放就会写进观看历史与续播点。**但播放是这个程序的全部，四道闸门里没有一关会真的起播**，所以「怎么验播放」有一条允许的路，分三档（2026-09-21 才写进规则：此前这里是「永远不要真实播放」，而 PROGRESS 里 28 处真实播放说明那条绝对禁令早被日常绕开——一条天天被违反的禁令，代价是旁边三条真禁令也不值钱了）：

1. **默认：什么都不播。** 用 `--show-detail` / `--show-episode` / `--show-library` / `--show-osd` / `--show-cover` 把界面摆到要看的那个状态，用 `--theme` 换配色，用 `tools/shot.ps1` 拍照。
2. **改播放管线时走本地文件那一档**（不是新东西，只是此前没写进规则）：`--probe-cursor <本地视频绝对路径>` 与 `--probe-player-motion <本地视频绝对路径>` —— 两者都在普通登录、单实例激活与设置加载**之前**进独立数据目录，用正式播放页与它的 10 Hz 心跳，**不登录、不启动 Emby 播放、不上报观看记录**，只认本地绝对路径（拒 URL、网络盘、重解析路径）；`--probe-composition` 是合成交换链那一条的独立诊断，同样不建外壳服务、不登录。窗口化/全屏几何、换片接力、指针隐藏与显隐规则都该在这里验。
3. **非打真服务器不可时**（首页、详情、选集、续播点这些只有真数据才问得出来的事）：**先问他一句。** 要么他当场点头，要么给他做一个第二个 Emby 账号当测试账号 —— 观看史与续播点按账号分开，那样这一档就再没有代价。

下面几条**不随分档放宽**：

- **不要点卡片中央**：那是悬停浮出的播放按钮，`{ESC}` 关不掉它启动的东西，只有杀进程才能。用 `--show-detail` / `--show-episode` 到详情页，或经首页 hero 的「详情」按钮、面包屑、卡片底部标题条——永远不碰海报本体。
- **用应用自己的命令行开关导航，不用鼠标。** `tools/poke.ps1` 点在光标恰好停着的地方，而用户的手就在那只鼠标上。程序化指针移动在本机做什么、不做什么，2026-09-05 用四种方式测过；**表在 `embynian-verification` 技能**的「Moving the pointer, and proving it moved」之下，连同能到达 XAML 岛的那一种注入以及如何证明它到达了。写任何移动光标的东西之前先读它——旧的笼统「它什么都不做」就是让一个 no-op 在鼠标自动隐藏路径里待了一周的原因。
- **只有悬停才出现的浮层给一个 `--show-*` 开关**，不要停住的指针：指针可以移过去，但快门打开前它已经不在了。
- `--play` 是**打真服务器**那一档的开关，它真的要起播；`winapp ui invoke` 去点首页或详情页的播放键同样会起播（PROGRESS 里 real-t1…t5 就是这么做的），所以它一样受第 3 档约束——上面那句「不要点卡片中央」对它们一字不减。

## 凭据（Credentials）

Emby 访问令牌以 DPAPI 加密存储。**永远不打印、不写日志、不让它进自检报告。** 要交给 web 视图用 `localStorage`，不用查询字符串。令牌在自检报告中的出现次数必须为 0。

## Git

- `origin` 是 `https://github.com/cudamin/EmbyNian.git`，仓库。凭据由本机 Git Credential Manager 持有；令牌不进命令、脚本与远程 URL。
- **一条长活分支 `master`**，HEAD 在其上（2026-09-02 之前是 `winui3-rewrite`，所以该名字下的历史都早于改名）。**不要再造一条跟着主干走的第二分支**——以前有过一条，只多买到一次推送。用 `git push origin master` 推，不搞 `git push . <branch>:<branch>` 的 ref 搬运。
- **同一棵工作树里，默认只有一个会话在写。** 两条线并行是常事（PROGRESS 里出现过「同树另一条线」），可规则里从来没写过它该怎么走，代价是混合构建、混合发布、PROGRESS 并行追加、出了事分不清该回退谁。要并行就开一棵自己的检出：`git worktree`（`.claude/worktrees/` 已经 ignore），并在 PROGRESS 里写一句「哪棵树在动哪些文件」。不开工作树，就等于承诺「一棵树同时只一个会话在写」。
- **永远不要 reset、不要用 check out 覆盖工作树里的东西。** 这条当年是为了保住那次 WinForms → WinUI 3 迁移（那个早完成了），**今天它真正的理由是：工作树里同时压着多条线的未提交工作**，任何回退都可能销毁两边都恢复不出来的东西。要撤掉自己刚做的实验性改动，用手改回去，或先 `git diff` 出补丁再改。
- 只有用户明确要求才提交。提交信息：**中文正文**加 `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`。源文件一律 LF。
- 提交后立即推 `origin`，不必再问（他的裁定，2026-09-01）。推送只跟在提交之后——未提交的工作树改动既不提交也不上传。

## 这台机器上的坑（Traps On This Machine）

- bash 每次调用都重置工作目录 → 命令前加 `cd "C:/Users/89400/EmbyNian" &&`。
- **`PATH` 上的 `python` 和 `python3` 是 Microsoft Store 存根**，不是解释器：它们什么都不打印、退出码 0，正是这种静默成功让「这里没有 python」被写了进来。**真的装了一个**——3.13.2 在 `%LOCALAPPDATA%\Programs\Python\Python313`（旁边还有一个 3.10），都在用户 `PATH` 上，但被排在最前面的 `WindowsApps` 遮蔽。**用 `py` 启动器到达它**，它解析到 3.13.2。2026-09-05 实测；正是这一点让 media-os 技能与 `watch` 可用。
- **`--no-restore` 意味着改包版本之后要另跑一次 restore**：闸门 1 与闸门 2 都带这个开关（为了不每次联网），而 2026-09-20 起版本是主动跟稳定版的——所以「改了版本号却像没生效」时，先 `dotnet restore`（`%USERPROFILE%\.dotnet\dotnet.exe restore .\EmbyNian.sln`）再判。这条 2026-09-21 实测过：那次升 WinAppSDK 子包是 restore 之后 `obj\project.assets.json` 才换成新版本的。
- PowerShell 脚本必须保存为 **UTF-8 带 BOM**，否则中文输出变乱码。**这条也管你自己新写的脚本**，而且它们的行尾要 LF（`.gitattributes` 会在提交时归一化，所以工作区里是 CRLF 时 git 不报——闸门 1 的第二条命令会报）。
- shell 里的自定义 `cut()` 函数遮蔽 `/usr/bin/cut`；截断改用 `awk '{print substr($0,1,N)}'`。
- **`grep -n` 和 `sed -n` 在这里与文件真实行号不一致**——2026-09-05 在一个 982 行的源文件上它们少报了六行，575 行的编译错误就这样被读成一行注释。行号要紧时（追错误、定位编辑），从文件读取工具或 `node -e` 拿，不从这两个拿。
- **反斜杠经不过本 shell 的 `node -e '...'`。** 单引号里的 `"\\s"` 到达时是 `\s`，JavaScript 随后读成裸 `s`，于是字符串拼接出来的正则静默匹配错误的东西并报零命中而非失败。用 `String.raw`、正则字面量，或先把脚本写到文件里（2026-09-05，一次 19 处的编辑脚本静默什么都没干之后）。
- **WinUI 标记编译器在过期缓存上失败、重试即成功。** `WMC9999 Xaml Internal Error`——「未将对象引用设置到对象的实例」或「指定的参数已超出有效值的范围」——不是 XAML 的错；2026-09-05 它同时打在构建与发布闸门上，整个会话没碰过任何 .xaml 文件，两次都在下一次运行、零代码改动下通过。同一次运行还可能编译 **.cs 文件的过期快照**，为已经不存在的代码报错。重跑闸门一次再下结论。
- **清空 `bin\x64\Release` 曾让发布件产出无法启动的应用**（2026-09-05 抓到：`EmbyNian.pri` 只有 103 KB 而不是 2.2 MB，exe 死在 `App.xaml`，报 `Cannot locate resource from 'ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml'`）。**根因已修在源头**：`EmbyNian.Shell.csproj` 的 `AddWindowsAppSdkFrameworkPriToPayload` 把框架那三份 `.pri` 自己塞进 `PriOutputs`（2026-09-11 实测：换成 2.x 那套分体包之后「跑多少次都是 0 字节」，所以它不是「再跑一遍就好」的时序问题），`tools/verify-publish.ps1` 另外盯着索引名。**所以这条现在是一条教训而不是一条操作**：见到一个只有一百来 KB 的 `EmbyNian.pri`，去看那个 target，不要重跑发布。
- **鼠标光标拍不下来**——GDI 截图拍不到，`winapp ui screenshot` 也拍不到，`--capture-screen` 也不行（2026-09-05 实测：指针停在窗口内、裁剪图按其自身坐标检查过）。`tools/cursor-watch.ps1` 是唯一的绕法，`tools/shot.ps1` 干不了这事；怎么做、为什么在 `.claude/skills/embynian-playback/SKILL.md`（技能里没有那份「references 卷」，2026-09-21 查过；指针一张一动的表在 `embynian-verification` 技能里）。
- **命令行开关，全部**（19 个；2026-09-21 对着 `Program.cs` 逐个数过。**开关表一变动，`AgreementsTests` 的 `switch` 小节就红**——那条测试就是为这次发现的问题加的：上一版的「全部」清单漏了四个、又留着一个已经删掉的 `--show-rail`）：
  - **导航与展示**：`--show-detail`、`--show-episode`、`--show-library`、`--show-settings <分类>`（默认「界面」）、`--show-menu`（首页第一张卡的「更多」菜单）、`--show-cover`（配 `--show-detail` 把「修改媒体封面图」面板弹开）、`--scroll-end` / `--scroll-half`（配详情类开关）、`--theme <id>`、`--maximized`、`--screen <n>`。
  - **停在某个状态、不播任何东西**：`--show-osd [pinned|paused|playing]`（把播放浮层钉在屏上）、`--hide-cursor`（播放页停在屏上、只跑它的 10 Hz 心跳，看指针两秒自动隐藏）。
  - **探针**（独立数据目录、不登录、不上报）：`--probe-cursor [<本地视频绝对路径>]`、`--probe-player-motion [<本地视频绝对路径>]`、`--probe-composition`。
  - **自检与真起播**：`--self-check`（配 `--dump-ui` 留一张截图与一棵可视树）、`--play`（真起播，见播放那一节第 3 档）。
- **`--show-menu`、`--show-osd`、`--hide-cursor` 各自单独用，绝不与 `--self-check` 同用，也互不同用。** 原因（`Program.cs` 的 XML 注释里各写着一条，别只记禁令）：前两个留下的是一层浮层或一个对话框，会立在自检接着要走的那几页前面；`--hide-cursor` 要的是「留在屏上等指针自己藏」，而自检跑完就退进程；`--hide-cursor` 与 `--show-osd` 恰好相反——一个把浮层钉住不许它收（为了拍照），一个让计时器照常把它收掉（为了看它收）。`--show-settings` / `--show-library` / 两个详情开关还会抢激活或换页，那一下浮层自己就散了。

## 怎么汇报（How To Report）

用户不读代码：实现细节自己定、自己验证，**用中文、大白话汇报，不贴代码**。值得问的只有真正属于他的事：范围、优先级、用户可见行为、不可逆操作，以及**一条正在让这块代码变差的规则**（见首要目标）。给他一张可以挑选的清单，不是一张技术选项菜单。
