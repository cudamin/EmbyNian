# 开发进度

最后更新：2026-09-10

> **做完的活按月归档，现在有两份。** [`docs/progress-2026-08.md`](docs/progress-2026-08.md)（390 KB）装 08-31 之前的全部；[`docs/progress-2026-09.md`](docs/progress-2026-09.md)（268 KB）装 09-01 到 09-05 已经提交的全部。两份都是原文一字未改。留在这个文件里的只有活着的东西：**还没提交的活**、还没解决的、等用户在屏幕前确认的、验证入口，加一份「最近做完了什么」的索引。
>
> 这件事做过三次：本文件第一次长到 387 KB 时搬走了一批，第二次长到 97.7 KB 时搬走了第二批（2026-09-02，剩 32 KB），第三次长到 252 KB 时搬走了第三批（2026-09-05，剩 78 KB —— 其中新加的「等你在屏幕前确认的」那一节、补齐的十条索引、加上这一批自己那一段占了二十几 KB，都是有意留下的）。理由每次都一样 —— **每个新窗口都要把它整本读一遍**，而 252 KB 那一版一次读不完、得分两趟、约四万 token。
>
> **归档的界线从第三次起是「提交了没有」**，不是「做完了没有」：`git status` 干净的那些搬走，工作树里还压着的留下。这条界线比「做完了」好用，因为它和 `git status` 对得上，不用读文档去猜。
>
> 下文凡是「见下面第一件活」「见下面那件活」这类指路，除了本文件里真有的那一节，指的都是归档里最上面那几件（新的在前）—— 先翻九月那份，找不到再翻八月那份。

## 在途工作（中断后从这里接）

这一节写给「上一个窗口的上下文丢了」的读者，包括未来的我。**它是唯一的交接文档**：只有写进这个文件的东西能跨窗口活下来，会话记录、后台 agent、工作流的 run id 都不能。所以这一节要在每个工作单元落地时更新，而不是等一个阶段做完。

**规矩在别处**：分层规则、四道闸门的完整命令、验证时不要真实播放、凭据、Git 约定，全部集中在 [`CLAUDE.md`](CLAUDE.md)，那份是唯一出处，改规矩改那里。本文件只管「这一阶段在干什么」。

**⚠️ 2026-09-05 深夜到 09-06 之间又有两个窗口在同一份工作树上写这一节。** 下面「打包成 MSIX」「界面文字全部搬进 x:Uid」「按新的 winui 技能重构」三件是这一边做的；「以后按 winui 技能执行，除非有更好写法」那一件是另一边做的，它重写过下面这条「最新一件…」的链子，而那一次重写没有把这一边的两件算进去 —— 09-06 合成了一份。**落笔前先重读本节，别再各自重写这条链子。**

**最新一件是「轮播框成留边卡片（他澄清：框＝不占满上半页、四边留间隔），重发 v0.0.4」（2026-09-10 下半天，四道闸门绿、自检两项既有红与本次无关，未提交），见下面第一件活。上一件是「轮播圆角框＋上下各裁 5%，打包发布 v0.0.3」（2026-09-10 上半天，已提交并推送 77bcf17）。上一件是「播放器两修：窗口化再进全屏画面缩在左上角、进度条被跳转回声拖歪」（2026-09-10，未提交）。上一件是「轮播字块挪到左上角（收尾）＋插值算法可选＋插值关闭阈值输入框」（2026-09-10，三道闸门绿、自检一项服务器数据红，未提交）。上一件是「移除轮播右边的媒体库＋移除顶部标签栏、加主页按钮」（2026-09-08，四道闸门全绿，未提交）。上一件是「参考 QQ 快捷键图重构设置 UI（左侧分类加图标）＋ 新增播放器快捷键重绑功能」（2026-09-08，四道闸门全绿，未提交）。上一件是「换一副更好看的暂停/播放图标」（2026-09-06，四道闸门全绿，未提交，工作树里压着的那批）。上一件是「打包成 exe 安装包，发布到 GitHub Release v0.0.1」（2026-09-06，四道闸门全绿，未提交）。上一件是「字号、描边大小、阴影改成滑块加读数，设置文案顺一遍」（2026-09-06，四道闸门全绿，未提交）。上一件是「设置页卡片上方大片空白的修复：外层间距垫在隐藏卡的占位容器上」（2026-09-06，闸门全绿，未提交）。上一件是「描边大小、阴影从固定几档改成自由数字输入」（2026-09-06，四道闸门全绿，未提交）。上一件是「字幕示例加个逗号、默认字幕字体改回 Microsoft YaHei（v14）」（2026-09-06，四道闸门全绿，未提交）。上一件是「字体行默认收起并显示当前字体、字幕卡顶上加「字幕示例」外观预览」（2026-09-06，四道闸门全绿，未提交）。上一件是「字幕那张卡五件事：字体打包进程序、其他字幕预设、字体列表选完收起、缩放默认写明、颜色换 HTML 拾色器」（2026-09-06，四道闸门全绿，未提交）。上一件是「恢复默认按钮搬到页头右上角、恢复默认那张卡删掉」（2026-09-06，四道闸门全绿，未提交）。上一件是「整套重画那五步：间距刻度、页眉、卡片、设置页的行、圆角收口」（2026-09-06，四道闸门全绿，未提交）。上一件是「删掉侧边栏：换成顶部标签栏，账号挪到右上角」（同一天、同一份工作树，闸门连着跑的）—— 那是三件欠的活里的最后一件，做完了。上一件是「主页第一屏右栏从继续观看换成媒体库」。上一件是「打包成 MSIX：包已经打出来并签好名了」（2026-09-05，四道闸门全绿，未提交）。上一件是「界面文字全部搬进 x:Uid + Resources.resw」。上一件是「以后按 winui 技能执行，除非有更好写法：优先级改一句、自动化把手补齐」（2026-09-06，四道闸门全绿，未提交）。上一件是「按新的 winui 技能重构：分析器进闸门、调色板改键、CLAUDE.md 与记忆重排」（2026-09-05，四道闸门全绿，未提交）。上一件是「暂停/播放徽标去掉灰边、鼠标自动隐藏改走框架那条路」。上一件是「设置里的字幕那张卡，六件事一起改」。再上一件是「继续观看那一栏改成点击翻页」。再上一件是「轮播四条边缘渐变淡一档、面积收到三分之一上下」。再上一件是「从播放回来那一趟，集列表掉到纸上」。再上一件是「轮播那块字挪到左下角、徽标顶在剧名上、四条边加黑色渐变、晴昼主题删掉」。再上一件是「轮播四改：取继续观看＋最近添加、设置里能关、黑色渐变去掉、右栏自动框出台上那一张」（注意：那一件里「黑色渐变去掉」这半已经被「字挪到左下角」那一件按他的新话补回来了 —— 补回来的只压四条边，而浓度和面积又被「渐变淡一档」那一件调过一次）。再上一件是「封面顶到最上方不留上下黑边、继续观看那一栏缩小、轮播按版面次序取」（注意：那一件里「轮播按版面次序取」这半已经被「轮播四改」推翻了）。再往上是同一天早些时候的「主页第一屏改成并排两栏＋折叠侧边栏那颗图标重画」（**那颗图标 09-06 随侧边栏一起删了**）。「详情页第一屏黑边统一，阈值跟着显示器走」及其以下整节已随 `fe1403c` 提交并推到 origin，按「提交了没有」的归档界线该搬去 [`docs/progress-2026-09.md`](docs/progress-2026-09.md)，还没搬——这是下一件杂活。**

**三件欠的活全做完了。** 最后那一件（去掉 `NavigationView` 改成顶部标签栏）2026-09-06 落地，见下面第一件活。

**这一批连同之前压在工作树里的各批（优化建议七条、鼠标自动隐藏修复、记忆与技能整理、三轮代码审查那些条）已全部随 `fe1403c` 提交并推到 origin（2026-09-05）。下面各条里写的「未提交」都是提交前的旧话。**

**⚠️ 这一天有两个窗口在同一份工作树上写这一节**，所以上面那句「最新一件」和这两段警告各自出现过两遍（内容一样、措辞不同），2026-09-05 傍晚合成了一份。落笔前先重读本节，别再往下加第三份。

**⚠️ 工作树里还压着另一批不是这一件的改动：字幕语言优先级那一摊**（`AppSettings.SubtitleLanguages` 的出厂默认从三项收成「简体中文, 中文」、`TrackLanguagePriority` 认「Simplified Chinese / Chinese Simplified」这两种英文写法，加上对应的测试和设置页那一行的占位文字）。那是另一个窗口的活，本节没有它的段落。**下面这几件的四道闸门是连着它一起跑的**，所以「739 项全过」这个数里有它的两条新测试；要单独提交某一件的话，动的文件是那一件自己那几个（见下面各条），别把那四个文件捎进去。

**⚠️ 工作树里还压着第二批别人的活：删掉 设置 → 海报宽度**（`UiSettings.PosterWidth` 连 `CardSize.WideFor` / `CastFor` 一起删，卡片固定走 `CardSize` 那三个常数）。那也是另一个窗口的活，17:00 前后和下面第一件活**同时**在改同一批文件（`HomeViewModel`、`SettingsViewModel`、`HomeCarouselTests`），两边都读过对方的现场再落笔，所以合在一起是自洽的 —— **下面那句「742 项全过」是两批合在一起的读数**。它留下的 `SettingsTests.cs` 五处编译错误（三处 `PosterWidth` 断言）由第一件活顺手删掉了，否则闸门一道都跑不了；那三处删掉之外没替它做别的。

**轮播框成留边卡片，重发 v0.0.4（2026-09-10 下半天，未提交）。** 上一件把「弄个框把轮播图框起来」做成了描边；他这次把话说明白：「框」是**轮播不再占满上半页、和窗口之间留出间隔，框里包含徽标、片名、剧情说明**。这一件把那条带从通栏改成页面上一块四边留 24 的卡片。

- **布局**：`HomePage` 从「Scroller 负边距顶回 32 ＋ Hero 格子里页眉浮在图上」改成「页眉是普通页眉（Margin 24,12,24,0，OnScrim 绑定删掉，站回页面自己的纸）→ 轮播卡片（Margin 24,16,24,0）→ 底下横排」。**SyncBleed 整个删了**（顶回 32 那一档随「贴到窗口顶边」作废），`ChromeHeight` 32 留着 —— BleedRead 拿它判「卡片站在标题栏底下」，`ProbeChrome` 照旧对账。带宽从页宽变成页宽−48，带高跟着矮一截（1064 宽的窗口上：343 高，通栏那一版是 359）。
- **HomeBanner**：顶上给标题栏垫底那条 120 高的渐变删了（标题栏和页眉都不再压在图上，四层暗罩剩三层：渐融＋下、右两条）；字块顶距 116→40（116 是给浮在图上的页眉让的，页眉出了框，框里顶上没有别的东西）。圆角框、上下各裁 5%、渐融、渐变浓度全照上一件。
- **联动全拆**：`HeroFilled`（VM）删 —— 它喂的三个吃客都没了：页眉 OnScrim 绑定、`SetTitleStrip`（标题栏墨色随图换）、SyncBleed。`SetTitleStrip` 本身留着，详情页头图还在用。
- **自检跟着改**：「主页大图贴边」→「**轮播卡片留边**」（量 Banner 在窗口里的位置，左、右、上三边都得让出来；收起那一档同样成立）；「标题栏墨色随大图」→「**标题栏墨色固定**」（必须是主题那支，从前的联动没删干净就红）；`Probe` 的暗罩断言四层→三层（`Sinks` 连句删）；`FoldRead` 判「卡片高＝按它自己的宽算出的那个数、放得进第一屏、横排接在卡片下沿后」；新加一条只报不判的「页眉墨色」（OnScrim 必须关着）。实测：卡片起点 (24,111)、1016×343、左让 24 右让 24 上让 111、字块从框顶 40 起。
- **发布**：升版本 0.0.3→0.0.4（`Directory.Build.props`），Inno 安装包走 `tools/installer.ps1`，GitHub Release `v0.0.4` 上传 `EmbyNian_windows-x64_0.0.4.exe`（脚本 `work/gh-release-0.0.4.ps1`）。
- **闸门**：Release 单节点 0 警告 0 错误；791 项测试全过（无新增）；发布 474 文件 300.6 MB；`--self-check --dump-ui` 两项红均为既有问题（服务器剧集数据、屏幕像素合成，09-10 上一批起就红着）。本批相关读数全绿：「轮播卡片留边」「主页首屏」「标题栏墨色固定」「页眉墨色」「主页大图不裁切」「主页轮播（Probe）」。
- **动的文件**：Shell —— `Views/HomePage.xaml`（页眉独立＋卡片边距）、`Views/HomePage.xaml.cs`（SyncBleed/HeroFilled/Strip 联动删，BleedRead/FoldRead 重写，SlateRead 新增）、`Views/HomeBanner.xaml`（删 120 渐变、Info 顶距 40、注释）、`Views/HomeBanner.xaml.cs`（Probe 三层＋InfoTop 40＋Sinks 删）、`ShellSelfCheck.Run.cs`/`ShellSelfCheck.Reads.cs`/`ShellSelfCheck.cs`（检查名、ReadInk 重写、元组加 Slate）、`Views/ShellPage.SelfCheck.cs`（首屏注释）、`ViewModels/HomeViewModel.cs`（删 HeroFilled）；Core —— `Emby/HomeCarousel.cs`（WindowAspect 注释改口）。另：`Directory.Build.props` 升 0.0.4，本条目进 `PROGRESS.md`。
- **截图**：`artifacts/shots/selfcheck-mid.png`。截图验证本会话做不了像素级的（模型不吃图），几何读数由自检钉死。

**轮播圆角框＋上下各裁 5%，打包发布 v0.0.3（2026-09-10，未提交）。** 他两句话：①「弄个框把轮播图框起来（圆角）」②「轮播图上下各裁切百分之五」，做完后重发发布件。

- **① 圆角框**：`HomeBanner` 的 `Band`（原来是个 Grid，零圆角、只画下沿一根发丝线）外面套一圈 `Frame`（Border）—— **圆角只有 Border 剪得动**（Styles.xaml 里 `EgBleedCornerRadius` 那段注释记的就是这条），四角跟着圆、连两层竖向放大过的剧照一起剪。角走 `EgPosterCornerRadius`（8，和封面同一档），线换成一整圈发丝线（`EgHairline`），线色跟主题（`EgBorderBrush`），底色照旧 `EgBannerBaseBrush`。从前留方的理由（「三条边就是窗口的边，圆角会在窗口角上剪出三角缺口」）随他这句话作废；顶上给标题栏垫底那条 120 高的渐变留着 —— 标题栏和页眉还浮在框上。`EgBleedCornerRadius` 键本身留着（设置页色板还在用），只改了注释里「主页轮播也在此列」那句。
- **② 上下各裁 5%**：两层剧照（`LayerA`/`LayerB`）竖向从贴顶改成**居中**，`Resize` 把两层的高写成「带高 ÷ `KeptShare`(0.9)」—— 比带子高出上下各 5%，多出来的两截由那圈圆角框剪掉，屏上看到的是原图中间的九成。图宽跟着大了一成（带高÷0.9×16÷9），渐融（`Fade`）的站位公式跟着换成从「放大后的高」换图宽。窄窗口那一档图吃满带宽、裁不满 5%，那一档也是裁（吃紧的换成宽了，`PictureRead` 两档都认）。
- **自检跟着改**：`Probe` 加 `framed` 一判（角、整圈线、两层高＝带高÷0.9、竖向居中四样），读数报「圆角框在（圆角 8、整圈发丝线），两层 412 高＝带高÷0.9、竖向居中（上下各裁 5%）」；`PictureRead` 从「整张画出来」改成「画到带高÷0.9、上下对称溢出」—— 带子量出来比带矮两根发丝线（外面那圈框的），高判容差放宽到 3。真实页面上量到的：剧照 709×399、带 1062×357、上下各溢 21（对称，5.3%）、贴右沿。
- **带高一个数没动**：`HomeCarousel.Height`、`PictureShare`、字块站位、渐融形状全照旧，只有 `HomeCarousel.Height` 的注释改口（「图再放大到带高÷0.9」）。颜色没碰，没按主题逐套拍（圆角和裁切跟颜色无关；线色跟主题走、五套深色主题下发丝线都是同一档灰）。
- **发布**：升版本 0.0.2→0.0.3（`Directory.Build.props`），Inno 安装包走 `tools/installer.ps1`，GitHub Release `v0.0.3` 上传 `EmbyNian_windows-x64_0.0.3.exe`（沿用 `work/gh-release.ps1` 的做法，tag 参数和资产路径改成 0.0.3，脚本存为 `work/gh-release-0.0.3.ps1`）。
- **顺手修了 `tools/installer.ps1` 一个真缺陷**：它调 `publish.ps1` 那句用了「`$(if ($FrameworkDependent) { '-FrameworkDependent' })`」—— 开关没开时那个子表达式给出空串，Windows PowerShell 5.1 把空串当参数传下去，撞上 `publish.ps1` 的 `-Configuration` ValidateSet 当场死（打 0.0.3 时撞的一次，绕过办法是先跑 publish 再 `-SkipPublish`）。改成 splatting（`$publishFlags` 哈希表），开关关着时不生成那个参数；不带开关整趟跑通即验证。
- **闸门**：Release 单节点 0 警告 0 错误；791 项测试全过（无新增——改的两处都在 Shell 视图层，测试工程够不着）；发布 474 文件 300.6 MB；`--self-check --dump-ui` **两项红、都与本批无关**：「跨季相邻单集」（服务器数据，《伪恋》两种问法答案不一致，09-10 上一批就红着）和「屏幕像素」（整片单色，`--dump-ui` 自己的 PNG 不合成 Mica，换屏跑会过）。本批相关读数全绿：「主页轮播（Probe）」带着圆角框新读数过、「主页大图不裁切」（改名后仍叫这个）量到对称裁切、「主页首屏」「轮播版式」照旧。
- **动的文件**：Shell —— `Views/HomeBanner.xaml`（Frame 那圈 Border、两层剧照 VerticalAlignment 改 Center、Band 子树深一级缩进、文件头注释）、`Views/HomeBanner.xaml.cs`（`KeptShare`、`Resize` 写两层高＋图宽公式、`Probe` 的 framed、`PictureRead` 重写判法、几处注释）、`Theme/Styles.xaml`（`EgBleedCornerRadius` 注释改口）；Core —— `Emby/HomeCarousel.cs`（Height 注释）。另：`Directory.Build.props` 升 0.0.3，本条目与索引行进 `PROGRESS.md`。
- **截图**：`artifacts/shots/selfcheck-mid.png`（自检连拍那张，主页轮播带圆角框、剧照上下裁过）。截图验证本会话做不了像素级的（模型不吃图），几何读数由自检钉死。

**播放器两修：窗口化再进全屏画面缩在左上角、进度条被跳转回声拖歪（2026-09-10，未提交）。** 他报的两个 bug：①「有时候窗口化然后再进入全屏画面会保持原尺寸固定在左上角」②「有时候播放进度会与进度条进度不一致」。

- **① 全屏左上角的病根**：`VideoWindow.Fill` 对 mpv 那块子窗口的改尺寸走 `SWP_ASYNCWINDOWPOS`（那窗口归 mpv 自己的线程，只能投递不能等），而**待处理的异步窗口位置操作会被任何一次同步 `SetWindowPos` 丢弃**——mpv 自己追赶尺寸时发的正是同步调用。时序凑巧时（「有时候」的由来）我们的改尺寸被吃掉，暂停着的 mpv 没有下一帧来自己纠正，画面就停在旧尺寸缩在左上角。修法两层：`Fill` 先读子窗口的实际矩形、尺寸已经对了就不投（同一方法从此可安全反复调）；`HostWindow` 加「落定」定时器——每次 `WM_SIZE` 后以 150ms 一次、持续约 1.2 秒重调 `Fill`，尺寸对了每趟只是三次矩形读取、零开销。这一件对着待办清单里那条「窗口化暂停之后进全屏，画面还留不留在左上角」——settle 层不管请求被谁吃掉都能补上，**但照旧只有他自己放一集才验得了**。
- **② 进度条不一致的病根**：拖动松手后跳转要等下一个 100ms tick 才发给 mpv，而 hr-seek 落定之前（字幕轨重的文件要 1 秒以上）mpv 一直报「正在离开的旧位置」；旧代码只有 400ms 拖动保护，过后这个回声就把拇指拖回起点、跳转落定后又跳到目标位置。修法是「在途跳转」：发出的跳转记下目标比例，mpv 报的位置到目标 ±0.75 秒内才算落定，其间进度条和时钟都不写回（两者同一道门槛，不会再显示两个不同时刻）；5 秒没到（跳转失败）就放弃等待、恢复跟随。跳过片头/片尾的跳转同样标记；换集（`OnNowPlayingChanged`）和 `LeavePlayer` 都清掉在途状态，免得拿上一部的比例把新一部的进度条按住最长 5 秒。
- **闸门**：Release 单节点 0 警告 0 错误；**791 项测试全过**（无新增——两处都在 Shell 那层，测试工程够不着；791 是合着工作树里其它没提交批次一起的读数，与 09-10 那批记的同一个数）。发布件重发与 `--self-check` 这两道没单独跑：改的两处都不在包内容或自检读数里。
- **动的文件**：Shell —— `Windowing/HostWindow.cs`（落定定时器：`VideoSettleTimer` 常数、`OnSize` 挂上、`Route` 的 `WmTimer` 分支、`WmDestroy` 收）、`Windowing/VideoWindow.cs`（`Fill` 先核对实际矩形再投递＋注释重写）、`ViewModels/PlayerViewModel.cs`（`_seekSent`/`_seekSentAt` 与三个常数）、`PlayerViewModel.Events.cs`（`ApplyStatus` 的门挪进新方法 `SeekBarFollows`、`AcceptSkip` 标记在途、换集清理）、`PlayerViewModel.Overlay.cs`（`Tick` 发跳转时记在途）、`PlayerViewModel.Transport.cs`（`LeavePlayer` 清理）。
- **没碰**：工作树里其它没提交的批次一个字没动。

**轮播字块挪到左上角（收尾）＋插值算法可选＋插值关闭阈值输入框（2026-09-10，未提交）。** 他三句话：①「把轮播图左侧的徽标、片名、剧情说明等东西移到左上角」②「为当前的插值功能设置更多的可选项」③「在设置中新增自定义输入框，显示器刷新率大于该数值时关闭插值」。

- **⚠️ 接手的工作树里压着 09-09 那批没有条目的轮播改动**（他三句：剧照缩到带宽的六成靠右、左沿加同色渐融、带子弄扁 —— `HomeCarousel` 换 `PictureShare`/`UnmeasuredHeight` 480、`HomeBanner` 双层靠右加 `Fade`、`HomePage` 全宽，测试跟着重写）。那批是另一个窗口做完就断的，PROGRESS 里没有段落；09-10 的左上角挪位它只写了标记那一半（`Info` 的 `Margin="60,116,0,0"` 加 `VerticalAlignment="Top"`），**代码后台还是贴下沿那一套**（`Resize` 写 `(60,0,0,46)`、`Probe` 断言 `Bottom`、`State` 报「离下沿」）—— 那个状态自检必红。本条把它收尾，闸门是合着那批一起跑的，没法分开。
- **① 字块挪到左上角（收尾）：** `InfoBaseline`（下边距 46）换成 `InfoTop`（顶距 116 —— PageSlate 从 44 起、占着页面抬头，116 让字块站在它下面并留一口气，PageSlate 自己不动）；`Resize` 改写 `Margin=(60,116,0,0)`；`Probe` 的 `corners` 断言换成 `Top`＋徽标仍是第一行，旧的那句「字块下边距让开小横条」（`InfoBaseline > DotsBaseline+DotHit`）随挪位作废，换成「标记和 Resize 两份顶距对得上」；`State` 改报「从顶上 116 起（底下余多少）」—— 负数就是简介和按键那一头伸出下沿（下限 240 那一档正是这样）。`HomeCarousel.MinHeight` 的注释照新位置改口（被剪的从「片名那一头」变成「简介和按键那一头」）。左边距 60 照旧（让开箭头窄栏），`Rise` 抬字动画一个数没动。
- **② 插值算法可选（mpv `tscale`）：** 从写死 `oversample` 变成设置页「视频输出 → 插值算法」下拉，六档：过采样（最省、运动最干净，**装机默认还是它**，没碰这一行的人发出去的东西一个字不变）、Mitchell（mpv 自己的默认）、Catmull-Rom、双三次、Spline36、Lanczos —— 后五档是真正的重建滤波，运动更顺、代价是锐利移动边缘周围可能振铃，都写在各档标签里。`VideoSettings.Tscale` 存值；**没有「不设置」档**：`Normalize` 把认不出的值落回第一档（`Kernel()`，同 `Preset()` 的形状），免得 mpv 留在自己的 mitchell 上而设置页写着别的；`Build` 只在插值开着时随它一起发。
- **③ 插值关闭阈值输入框：** `MpvOutputOptions` 里写死的 `HighRefreshThreshold`=120（连同那篇实测注释）搬进 `VideoSettings.HighRefreshRateLimitHz`（**装机默认 120，行为不变**；范围 24–1000 的三个常数也在 `VideoSettings` 上，设置行和 `Normalize` 用同一对数）。`ResolveSync` 的回退规则改读它，StandDown 那句话带上填的数而不是写死的 120。设置页「视频输出」加一行 Number 输入框「插值关闭阈值（Hz）」（排在回退开关下面，说明写明实测账和「只在回退开关开着时生效」；它是「视频同步」那行说明的第四个写手，改完跟着 `Restate`）。三处引用 120 的旧文案全改口：启用插值的说明、回退开关的说明、`SyncNote` 的例外句（自检里那行现在读「或屏幕刷新率超过 120Hz（『插值关闭阈值』那一行）」）。
- **旧文件升级零迁移**：两个新键缺省读出来就是装机值（`Tscale`="oversample"、`HighRefreshRateLimitHz` 由 `Normalize` 把 0 读成 120），schema 不升（同 `ShowHomeBanner` 那样）。「恢复默认」和重置测试走反射，新字段自动覆盖。
- **闸门**：Release 单节点 0 警告 0 错误；**791 项测试全过**（新增 3 条：插值算法档位、阈值读设置、规整落回）；发布 474 文件 300.6 MB；`--self-check --dump-ui` **一项稳定红、一项偶发**——见下一条 bullet。本批相关的读数全绿：「主页轮播 — 字块贴左上角、三行字靠左……字块从顶上 116 起（标记和 Resize 两份对得上）」「设置页面 — 105 行设置，已渲染 105/105」「屏幕刷新率读得到 — 这块屏 60Hz；设置里插值=关、回退=开、阈值 120Hz」。
- **⚠️ 自检两项红的说明（都与本批无关，重跑一次屏幕像素已过）**：「跨季相邻单集」稳定红 —— 跨季相邻本身是对的（S00E04→S01E01 ✓），红的是后半句「选集 20 集，服务端按季返回 23 集」：《伪恋》这个条目，服务器对「整季要全部剧集」和「按季要 S01」两个问法自己给出的答案不同。走的代码（`EmbyClient.GetEpisodesAsync`、`EpisodeNavigation`）本次工作树没动过，是服务器数据的事，等他在服务器上看看那部剧。「屏幕像素」第一次跑红（整片单色，采样时机问题）、第二次过 —— 已知的环境抖动。
- **动的文件**：Core —— `AppSettings.cs`（`VideoSettings` 加 `Tscale`/`HighRefreshRateLimitHz`＋三个常数）、`MpvOutputOptions.cs`（`InterpolationKernels` 目录、`Build` 发存值、`ResolveSync` 读阈值、删 `HighRefreshThreshold`）、`SettingsMigration.cs`（`Kernel()`＋阈值归一化）、`HomeCarousel.cs`（MinHeight 注释）、`PlaybackContracts.cs`（`DisplayRefreshHz` 注释指向新家）；Shell —— `HomeBanner.xaml.cs`（`InfoTop`/`Resize`/`Probe`/`State`）、`SettingsViewModel.cs`（两行新行＋三处文案改口）、`SettingRow.cs`/`ShellSelfCheck.Settings.cs`（注释里「三处写手」改四处）、`ShellSelfCheck.Reads.cs`（帧同步读数带阈值）；测试 —— `PlaybackTests.cs`（+2）、`SettingsTests.cs`（+1）。
- **没碰**：工作树里 09-09 那批轮播改动（收尾除外）和再往前的没提交批次，一个字没动。颜色角色没碰，没按主题逐套拍。
- **截图**（发布件）：`artifacts/shots/banner-top-left.png`（主页，字块站左上）、`artifacts/shots/video-output-card.png`（设置 → 视频输出，插值算法和阈值两行新行）。

**移除轮播右边的媒体库、移除顶部标签栏、标题栏加主页按钮（2026-09-08，四道闸门全绿，未提交）。** 他两句话配两张截图：①「移除轮播图右边的媒体库，参考上图修改轮播图」②「移除图2中 主页、电视剧、电影哪一栏」。动手前当面问定两件：导航替代（**库入口＝主页那排媒体库卡片，回主页＝标题栏新加的主页按钮**，账号按钮不动）；轮播改到什么程度（**大图铺满整宽即可**，不做缩略图选择条）。

- **大图铺满整宽，改动大多是删。** 「关掉轮播」那条已有路径本来就把媒体库排成横排，所以这一批等于把那条路径变成唯一路径：`HomeCarousel` 删 `RailWidth`/`RailCard`/`RailCardCap`/`RailInset`/`MatchIndex`，`UnmeasuredHeight` 640→800；`HomeViewModel` 删 `Rail`/`RailVisibility`/`Frame`，`BuildShelves` 里媒体库就是普通一排宽卡；`HomePage` 删右栏那一整块（栏、翻页条、渐隐、悬停网、自检读数）。
- **⚠️ 顺着他 09-05 说过的「轮播滚到哪张框右栏对应那张」整个删了**——右栏没了那功能无处落（上一件里那个「⚠️ 等他一句话」的悬案，这回随栏一起了断）：`CardItem.Framed`、`PosterCard` 的 Framed DP、四条 `MatchIndex` 单测都删了。胶片格的指针/焦点亮框（`EgFrameActiveStyle`）是另一回事，保留。
- **标签栏删掉、第 1 行留着。** `ShellPage` 的 `SelectorBar`、`SyncSelection`、`OnTabSelected`、`GlyphFor`/`SizeFor`、四个字形常数全删；`LoadLibrariesAsync` 只填 `_libraries`/`_libraryViews`（`TryOpenLibrary` 和主页媒体库排还靠它）。**第 1 行（48 高）和账号按钮不动**——外壳 80（`ChromeHeight`）是轮播贴顶算术的地基，动它波及太大。新 `HomeButton` 排在标题栏最左（手画房子线条，`tools/line-icons.awk` 加了 `house()`），在主页时 `SyncChrome` 把它置灰（同箭头「走不动就暗」），`x:Uid` 走 resw。
- **导航语义**：`_current` 照旧驱动 `GoTo` 的「已在」门和主页按钮亮暗；下钻（详情/子网格）清 `_current`，所以钻进海报后主页按钮亮着、一按就回。媒体库只能从主页卡片进 —— `TryOpenLibrary` 走的还是原来的路由，面包屑照旧。
- **自检改三关**：「顶部标签栏」→「外壳顶栏」（第 1 行在屏、80 高、标题栏以下、账号贴右上角）；「主页首屏两栏」→「主页首屏」（大图 16:9 铺满、页宽＝客户区宽、横排接在下沿后）；删「主页右栏翻页」那关（右栏没了）。`--show-rail` 开关连着右栏一起删了（CLAUDE.md 的开关清单已订正）。
- **后果（他看截图定）**：16:9 窗口上全宽大图正好一屏高，第一排货架滚一下才露出来 —— 不再有「货架从下沿探头」。窗口比 16:9 高时才探头。要探头就得给大图封更矮的高度（换回一点上下留白或裁切），他没开口就不做。
- **闸门**：Release 单节点 0 警告 0 错误；测试 788 项全过（删 `MatchIndex` 四条和 `RegisterRail` 四条，`UnmeasuredHeight`/16:9 那两条改写）；发布 474 文件 300.6 MB；`--self-check --dump-ui` 退出码 0、末行「全部通过」，token 0 次。
- **动的文件**：Core —— `HomeCarousel.cs`；Shell —— `HomePage.xaml(.cs)`、`HomeBanner.xaml(.cs)`（注释与右沿渐变的理由重述）、`ShellPage.xaml(.cs)`、`ShellPage.SelfCheck.cs`、`PosterCard.xaml(.cs)`、`CardItem.cs`、`HomeViewModel.cs`、`SettingsViewModel.cs`（设置文案两处改口）、`ShellSelfCheck.cs/.Run.cs`、`App.xaml.cs`、`Program.cs`、`Theme/Palette.xaml`、`Theme/Styles.xaml`、`PageSlate.xaml`、`Resources.resw`、`EmbyNian.Shell.csproj`（WUI2010 压制的站点数二十→十七）；测试 —— `HomeCarouselTests.cs`；工具 —— `tools/line-icons.awk`（加 `house()`）；文档 —— `CLAUDE.md`（开关清单）、`PROGRESS.md`（本条）。
- **截图**：见下方验证小节（五个主题各一张，副屏）。**没碰工作树里其它没提交的批次。**



- **一、左侧分类加图标。** 分类还是一串字符串（`SelectedCategory`、`--show-settings` 跳转、自检十来处共用那份），没为了图标改成对象 —— 那要动 ~20 处、不值。给侧栏 `ListView` 加了 `ItemTemplate`（FontIcon＋TextBlock）＋新 `SettingsCategoryGlyphConverter`（分类名→Segoe 码点，认不出给齿轮兜底；码点用 `0xE768` 那套整数，同 PlayerViewModel）。图标和标题都不写 Foreground，跟着 ListViewItem 选中态一起点亮。
- **二、快捷键判断全进 Core、单测钉。** 新 `ShortcutCatalog` ＋ `KeyStroke`（token 串，不碰 VirtualKey）：19 个动作各带 Id／中文名／默认键（默认 1:1 保留改造前那张 switch 表），纯函数 `Resolve`／`Lookup`／`Rebind`（**冲突就拦下、返回占用者、一个绑定都不改**）／`Clear`／`Serialize`／`Parse`／`Format`／`Clean`／`DefaultsAreUnique`。**存储只存改动**（缺键＝默认、空串＝显式解绑、否则一个 token）——装机空字典，日后加动作也不用迁移。**Esc、Y 两个固定键不参与重绑**：Esc 是全局「退出」＋重绑方框的取消键，Y 只在出现跳过提示时有效、名字还印在画面提示上（那句提示的测试在没提交的 `PlaybackTests` 里）——列为保留键、捕获时拒收、在卡里以只读 Fact 行显示让人看到全貌。
- **三、播放器 `OnKeyDown` 从写死 switch 改成表驱动。** 先处理固定的 Esc/Y（原样、不查修饰键），再把按下的键经 `KeyStrokeInterop`（全项目唯一碰 VirtualKey 的地方，方括号靠 219/221）＋此刻修饰键（`Native.Ctrl/Alt/ShiftHeld`，新加 Ctrl/AltHeld）拼成 `KeyStroke` → `ShortcutCatalog.Lookup` → 页面那张 `_shortcutHandlers`（动作→处理器，音量/静音仍走会闪音量条的页面包装、全屏/置顶走窗口方法）派发。`_typing` 护栏原样留（`ProbeSubtitleFont` 盯着它）。**匹配从「只看键」变成「精确匹配组合键」**——重绑必需（否则分不出 F 与 Ctrl+F），代价是 Ctrl+F 这类修饰变体不再触发裸键动作（没人靠这个，算改进）。已开着的播放器现读同一单例设置，改完当场生效、无需通知管线。
- **四、设置里的卡。** 新增「快捷键」分类（排「界面」「关于」之间）：19 行可重绑（新行类型 `SettingShortcutRow` ＋ 新控件 `ShortcutCaptureBox` —— 点一下进「按快捷键…」、抓下一个键、Esc/Tab 取消、× 清除；**一次性模态捕获**避开 Button 空格激活会跟「绑空格」打架的坑）＋ 2 行固定（Esc/Y 只读）＋ 1 行「恢复默认快捷键」（只清快捷键、不动别的，作用域比页头那颗窄）。冲突就拦下、顶上现成 InfoBar 提示「该键已是『XXX』的，先清了再绑」，那一行方框还显示原来的（ComboText 没动）。
- **动的文件**：Core —— `ShortcutCatalog.cs`(新)、`AppSettings`(加 `ShortcutSettings` 节)、`SettingsReset`(整组回默认加一行)、`SettingsMigration`(Repair 护栏＋Normalize 走 `ShortcutCatalog.Clean`)；Shell —— `KeyStrokeInterop.cs`／`ShortcutCaptureBox.xaml(.cs)`／`SettingsCategoryGlyphConverter.cs`／`PlayerPage.SelfCheck.Keys.cs`(都新；最后一个另起文件避开没提交的 `PlayerPage.SelfCheck.Input.cs`)、`Native.cs`(Ctrl/AltHeld)、`SettingRow.cs`(`SettingShortcutRow`＋Probe)、`SettingRowTemplates.cs`、`SettingsPage.xaml`(转换器＋侧栏模板＋快捷键行模板)、`SettingsViewModel.cs`(卡＋Rebind/Clear/刷新)、`PlayerViewModel.cs`(`ShortcutBindings` 访问器)、`PlayerPage.Input.cs`(OnKeyDown 表驱动＋`_shortcutHandlers`)、`ShellSelfCheck.Settings.cs`(两关)、`Resources.resw`(× 的读屏名)、README；测试 —— `ShortcutTests.cs`(新，避开没提交的 `PlaybackTests`)、`SettingsTests.cs`(重置手动断言＋round-trip)、`Program.cs` 挂上。**没碰工作树里没提交的那几批**（暂停/播放图标、字幕语言、海报宽度）。
- **schema 没升**：全新节、旧文件缺它＝默认（同 `ShowHomeBanner` 那样）。
- **闸门全绿**：Release 单节点 0 警告 0 错误、**796 项测试全过**（新增快捷键 15 条＋设置 2 条）、发布件重发（474 个文件 300.6 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0、末行「结果：全部通过」。自检新读数：「设置页面 — 10 张卡片 + 3 个内嵌页面、103 行设置」「快捷键 本卡 22 行」「快捷键行 — 19 行可重绑」「快捷键派发 — 19 个动作、处理器 19 个；默认键互不相同」。
- **截图**（发布件，`--show-settings 快捷键`）：`artifacts/shots/shortcuts-card.png` —— 侧栏 13 个分类都带图标、快捷键卡一行一个组合键方框加 ×。**颜色角色没碰**（图标和方框都走主题画刷），没按主题逐套拍。
- **一件留给他看屏定夺**：侧栏那 13 个图标的字形是我挑的（快捷键=键盘、播放器=播放、关于=信息、服务器=云、诊断=文档…），不满意哪个说一声就换，都在 `SettingsCategoryGlyphConverter` 一处。

**换一副更好看的暂停/播放图标（2026-09-06 起，另一个窗口开的头；这一窗把它修到四道闸门全绿，未提交）。** 他一句话：「播放页面暂停和开始的图标太丑了，你换一个」。上一副是「尖角多边形＋圆接头描边」，圆角被钉死在半个描边宽（6），136 的方框里两条直挺挺的竖条一颗尖三角。这一副把圆角画进轮廓：暂停两条胶囊竖条（圆角 22＝条宽一半），播放尖角圆 22、底角圆 12 的三角。

- **接手时那份改动编译不过，而且几何是错的 —— 三处，不是一处**。① `PlayerPage.xaml.cs` 的 `Build` 用了 WPF 那套 `new ArcSegment(点, 尺寸, …)` / `new LineSegment(点, isStroked)` 构造，WinUI 的 `Microsoft.UI.Xaml.Media` 这两个类只有无参构造、靠属性赋值，`PathSegment` 上也没有 `IsStroked`（那是 WPF 的）—— 改成属性初始化。② `PlaybackTests` 那一段还在调已经删掉的 `PulseArt.Stroked` / `PulseArt.Ink`，还把 `PulseFigure` 当数组索引 —— 整段按新几何重写（外框、居中、胶囊/三角圆角半径、弧数）。③ **播放三角没居中**：那份数据 `(7,7),(119,68),(7,129)` 的角点多边形中心在 x＝63、圆角后切点包围盒中心更是滑到 57.7，方框中心是 68 —— 上一副是居中的，这一副偏了一边。挪成对称的 `(12,7),(124,68),(12,129)`（112 宽、122 高、正中在 68）。
- **一处「更好写法」，记下**：`PulseArt.Bounds` / `Centre` 改成量**没圆角前的角点多边形**（`PulseFigure.Corners`，`Round` 原样留着），不量圆角后的切点。接手那份的注释写「外框从成品轮廓直接量」，但切点包围盒既不是设计尺寸也不是屏上真尺寸（三角 101×115，两头都不对），是最没用的一个读数。拿多边形当外框：一组整数、一条测试钉死「用户认下的那一档」，圆角往里切、画出来只会落在框内 —— 把「要多大」和「圆角多软」分开，最省事也最钉得住。矩形（暂停）多边形框＝切点框＝屏上框都是 122×122，没差别；只有三角要分。
- **一件要他看屏定夺的事**：圆角往里切，且尖角圆得多（22）、底角圆得少（12），所以**播放三角屏上真实包围盒是 102×115、比多边形的 112×122 小一圈，紧包围盒中心也在 x≈63、比暂停的正中偏左约 5 像素**（暂停两条胶囊仍是满 122×122、正中）。多边形按老办法居中在 68（和上一副同一个基准），偏这 5 像素是「圆角进几何」这套画法自带的，右尖三角本来视觉重心就偏左。自检只卡「高≥90、徽标≥130」，过了；屏上这一档要不要再往右挪或把尖角圆调小，等他看一眼再说。
- **动的文件**：Core —— `PulseArt.cs`（`PulseFigure` 加 `Corners`、`Bounds`/`Centre` 改量多边形、播放数据挪正、注释重写）；Shell —— `PlayerPage.xaml.cs`（段构造改 WinUI 写法、`Build` 注释）、`PlayerPage.xaml`（徽标注释）、`PlayerPage.SelfCheck.Input.cs`（接手那份已把描边断言改成 `Stroke is null`，没动）；测试 —— `PlaybackTests`（徽标那四条重写）。
- **闸门全绿**：Release 单节点 0 警告 0 错误、**779 项测试全过**（徽标四条都在）、发布件重发（474 个文件 300.5 MB、11 个 GLSL）、`--self-check` 退出码 0、末行「结果：全部通过」；自检那行读出「暂停 外框 122×122、恢复 外框 102×115、都纯白一层」。

**打包成 exe 安装包，发布到 GitHub Release v0.0.1（2026-09-06，四道闸门全绿，未提交）。** 他一句话：「打包为安装包EmbyNian_windows-x64_0.0.1.exe发布到github」。

- **产物**：`artifacts\EmbyNian_windows-x64_0.0.1.exe`（83.1 MB；300.5 MB 的发布件用 Inno Setup 的 lzma2/max 压到 83 MB）。发布件先按四道闸门重新出过一遍（779 项测试全过、`--self-check` 全过）再编的安装包。Inno Setup 6.7 本来就装着（winget 的 JRSoftware.InnoSetup，装在用户目录）；**官方 6.7 的 Languages 目录里没有中文**，`ChineseSimplified.isl` 从 issrc 的 `is-6_7_3` 标签抓的，收在 `tools/installer/Languages/`。安装器按用户装（不弹 UAC，默认 `%LOCALAPPDATA%\Programs\EmbyNian`）、开始菜单快捷方式、桌面快捷方式可选（默认不勾）、卸载不碰 `%LOCALAPPDATA%\EmbyNian`。
- **新增工具**（都未提交）：`tools/installer/EmbyNian.iss`（Inno 脚本；版本号可用 `/DMyAppVersion` `/DVersionQuad` 传入）、`tools/installer.ps1`（包装：默认先跑 publish.ps1 再编安装包，`-SkipPublish` 拿现成发布件；版本号只认 Directory.Build.props）、`tools/installer/Languages/ChineseSimplified.isl`。两个脚本都是 UTF-8 带 BOM（老坑）。
- **验证链**：静默试装到临时目录 → 474 个文件逐一 MD5 与发布件一致（安装目录只多 `unins000.exe/.dat` 两个）→ **装出来的那份** `--self-check` 退出码 0、「结果：全部通过」→ 静默卸载干净。Release 资产上传后从 API 资产端点回传下载，SHA256 与本地一致。**两个坑记下**：私有仓库的 `browser_download_url` 不认 API token（404），下载校验要走 `GET /releases/assets/{id}` + `Accept: application/octet-stream`；PS 5.1 的 Invoke-WebRequest 会把 Authorization 头转发给 S3 跳转、被 403，curl 不会（头文件最后一行必须带换行，否则被丢）。发布流程在 `work/gh-release.ps1`（凭据走 `git credential fill`，token 不落盘不打印）。
- **发布**：[Release v0.0.1](https://github.com/cudamin/EmbyNian/releases/tag/v0.0.1)，tag 由 API 打在远端 master 当时指向的 `12f498fa`。发出去后他说「改成测试版」，已把它标成预发布（prerelease，页面上带测试版标识；要改回正式版同一个 PATCH、`prerelease=false`）。安装包没有签名，SmartScreen 会拦一次（Release 说明里写了「仍要运行」）。
- **⚠️ 两件要他知道的事，都没替他动**：① **tag 上的代码不等于安装包里的代码**：安装包从本地工作树编，工作树里压着 09-06 的几批未提交改动；tag 的 `12f498fa` 含全部已提交代码（本地 HEAD `3ac16af` 是它的祖先，compare API 量过：远端只比本地多两笔、都是网页端改 README），差的正是这批未提交的活。要代码和安装包一致，把这批提交推上去、把 tag 挪过来就是。② **本地落后远端两笔网页端 README 提交，直接 push 会被拒；而本地 README 也压着未提交的改动（那两笔删了「上手」整节和三条要点），pull 会因 README 冲突被拒** —— 先提交或先放下一份 README 改动再 pull，他的决定。顺带：README 还写着「没有安装程序，也没有自动更新」，安装包发出后这半句已经不成立，同上没动。

**字号、描边大小、阴影改成滑块加读数，设置文案顺一遍（2026-09-06，四道闸门全绿，未提交）。** 他两句话：①「在 设置-字幕 中新增滑块调整字幕粗细，把字号、描边大小，阴影也改成滑块」②「优化设置中的文本描述，机翻味道有点大」。

- **「粗细滑块」这一半没做，理由是量出来的，不是省事**：把自带的 libmpv 选项表整个探了一遍（`artifacts/sub-probe/weight-probe.ps1`，顺手修好它 `mpv_set_option_string` 返回值的声明才能跑），字重一类里收得上的只有 `sub-bold` 一个 yes/no 开关，`sub-font-weight` 返回 rc=-5 —— **选项根本不存在**，mpv 那一头没有连续的字重可调，滑块底下没有东西可拉，硬做就是一个只有两档的滑块，比开关还不诚实（「界面在骗人」正是这份代码最恨的坏法）。所以「字幕加粗」保留开关，行说明改成把这件事说破：「只有常规和加粗两档 —— mpv 没有更细的字重可调」。**他要滑块的形状，一句话就换**。
- **三行滑块，全部滑块都有了数值读数。** 字号 16–160（步进 1）、描边大小 0–10（步进 0.05）、阴影 0–10（步进 0.05）；上一批的自由数字输入行（`MpvNumber` 工厂）整个删掉。滑块没有「不填」这个状态，所以装值时把「不设置」落到 mpv 自己的默认上 —— 字号 0→38、描边空→1.65（两个常量从 `SubtitlePreviewPlan` 提成公开的 `MpvDefaultFontSize` / `MpvDefaultBorderSize`）、阴影空→0 —— 那是同一个样子换了个写法，拖一下才写实。写回的数字照 `ClampSubtitleUnit` 落盘的格式（0.###），手改设置文件越界的照样在每次读盘时被它拦回来；设置文件里的空串仍是合法的「不设置」，只是界面上不再有入口。原有的 `Slider` 工厂从 int 改成 double，三个老调用点（缩放、不透明度、网络缓冲）跟着换。
- **数值读数是顺手补的**：滑块本来不显示数字（连原有的三行也没有），描边这种 0.05 一格的细档拖完不知道落在哪个数上。`SettingSliderRow` 加 `ValueLabel`（0.##，跟着拖动走），模板里滑杆右边挂一个右对齐的读数。
- **文案顺了一遍**：播放器卡说明、IPC 进度通道、播放行为卡五个开关、没有匹配语言时使用默认字幕、拉伸图形字幕、启用反交错、网络缓冲、音频独占模式、底板不透明度、主页版面那行的标题（「主页上排哪几排」→「主页上放哪几排」）；给两行没说明的补了一句（标记已观看阈值、每页条目数）。自检和测试盯着的字（「此刻生效」「不会退出登录」、预览示例「字幕示例，」、颜色行读数）一个没动。
- **动的文件**：Core —— `SubtitlePreviewPlan`（两个常量转公开）；Shell —— `SettingsViewModel`（三行换滑块、`UnitSlider` 新、`MpvNumber` 删、文案十几处）、`SettingRow.cs`（`ValueLabel`）、`SettingsPage.xaml`（滑块模板加读数）。测试零改动。
- **闸门全绿**：Release 单节点 0 警告 0 错误、**779 项测试全过**、发布件重发（474 个文件 300.5 MB、11 个 GLSL）、`--self-check` 退出码 0、末行「结果：全部通过」，token 出现 0 次。**红过两趟都是老时序抖动，重跑即绿**：一趟「服务器页面已渲染 0」（虚拟化没铺完就被量了），一趟「播放页字幕字体栏——字体还没扫完、只有 1 个字体族」。截图：`artifacts/shots/subtitle-sliders-rows.png`（字号 50、缩放 100 的读数在屏上）、`subtitle-sliders-shadow.png`（描边 0.5 的读数和新说明在屏上）。**一处颜色都没动**（读数走 `EgSettingLabel`），没按主题逐套拍。

**设置页卡片上方大片空白的修复：外层间距垫在隐藏卡的占位容器上（2026-09-06，闸门全绿，未提交）。** 他一句话（配三张截图：关于、界面、着色器三张卡上方各留 200–310px 空白）：「为什么有些页面上面留了一大片空白」。

- **根因是量出来的，不是猜的**：分类切换把没选中的卡折进 `Visibility.Collapsed`，但 ItemsControl 给每项包的占位容器（ContentPresenter）不跟着折叠 —— 零高、可见，外层 ItemsPanel 的 `Spacing="36"` 照样在每张隐藏卡前面垫一次。空白 = 36 × 该卡在清单里的序号减一：关于（第 9 张）8×36=288px，截图实量内容起点 y=416、滚动区顶 y=130，差 286，对上；界面（第 8 张）252、着色器（第 6 张）180，三张截图全都对得上。所以「有些页面」空白大小不一、越靠后的卡空白越大，第一张（播放器）永远没有 —— 不是滚动位置，是布局里真有这么多空气。
- **修法**：外层 ItemsPanel `Spacing` 归零；分组之间的 36 挪到模板根上的 `Margin="0,0,0,36"` —— 折叠时边距跟着内容一起归零，才是真不占位。坑的原委写进了 XAML 注释。修复实测：关于卡内容起点 y=416 → y=128，卡顶与左列第一项齐平。
- **动的文件**：Shell —— `SettingsPage.xaml`（外层 ItemsPanel Spacing 0 + 注释、模板根加下外边距）。纯 XAML 改动，无 C#。
- **闸门全绿**：Release 单节点 0 警告 0 错误、**779 项测试全过**、发布件重发（474 个文件 300.5 MB、11 个 GLSL）、`--self-check` 退出码 0、末行「结果：全部通过」。前后对比截图：`artifacts/shots/blank-before-guanyu.png` / `blank-after-guanyu.png`。

**描边大小、阴影从固定几档改成自由数字输入（2026-09-06，四道闸门全绿，未提交）。** 他一句话（配描边大小下拉展开的截图）：「这个不用弄成固定的选项，改成输入数字」。阴影那行是同一个模式，一起改了。

- **一、Core 只剩一个答案**：`MpvOutputOptions.ClampSubtitleUnit` 取代原先 `SubtitleBorders` / `SubtitleShadows` 两张档位表（两张删了）。空串 = 「不设置」（描边落到 mpv 自己的 1.65、阴影默认没有）；数字照收，逗号当小数点；范围外拉回 0–10 —— 上下限是 mpv 自己的，mpv.exe 拿到范围外的选项值是拒启动而不是夹住，所以上限不能松；不是数的退「不设置」，和颜色那三行同一条规矩。Normalize 每次加载都走它，手改的设置文件也拦得住，不需要版本号。存发仍是 `sub-border-size` 旧名（mpv 留作 `sub-outline-size` 的别名）。
- **二、Shell 新增 `MpvNumber` 行工厂**（`SettingTextRow` 一层皮）：框里收任意写法，落盘和推给正在播的片子的永远是规范值；能解析成数字的写法（「0.」打了一半的、「1,0」等价的）原样留在框里别打断输入，真的不是数了才退回「不设置」。两行走 `Live<T>`，预览和正在播的片子一起跟。占位文字写着「不设置（mpv 自己是 1.65）」／「不设置（mpv 默认没有阴影）」，行说明写明范围和退回规则。
- **动的文件**：Core —— `MpvOutputOptions`（两张档位表删、`ClampSubtitleUnit` + 范围常数新）、`SettingsMigration`（Normalize 两行换解析器）、`AppSettings`（两条注释）；Shell —— `SettingsViewModel`（`MpvNumber` 工厂新、两行换、`System.Globalization` using）；测试 —— `SettingsTests` 新一条（自由数字、逗号、夹范围、非数）。预览不用动：`SubtitlePreviewPlan.Parse` 本来就收任意数字。
- **闸门全绿**：Release 单节点 0 警告 0 错误、**779 项测试全过**（新增 1 条）、发布件重发（474 个文件 300.5 MB、11 个 GLSL）、`--self-check` 退出码 0、末行「结果：全部通过」。

**字幕示例加个逗号、默认字幕字体改回 Microsoft YaHei（2026-09-06，四道闸门全绿，未提交）。** 用户两句话：①「给字幕实例后面加个，逗号」②「默认字体改为Microsoft YaHei」。

- **一、示例那行连标点一起照。** `SettingSubtitlePreviewRow.Sample` 从「字幕示例」改成「字幕示例，」（全角逗号）—— 逗号和正文一样是描边八份加阴影的拷贝叠出来的，标点在当前外观下的样子从此也在预览里。自检那一关是拿屏上文字和这个常量互比的，两边一起变，读数跟着改成「字幕示例，」。
- **二、默认字体是 v14，回微软雅黑。** `PlaybackSettings.SubtitleFontFamily` 和 `FontFamilies.Default` 都换成 Microsoft YaHei —— 当初 v11 以前选它的理由（每台 Windows 都有，兜底族名一定找得到）原样成立；方正中等线简体照样随程序走（assets/fonts、sub-fonts-dir）、照样可选，只是不再替「没挑过字体」说话。**schema v14**：v12 写进文件的那份方正中等线简体（中文写法和英文族名 FZZhongDengXian-Z07S 都算）跟到新默认；别的值（思源黑体等）是挑过的，不动；**v14 之后再存方正中等线简体就是他的决定，迁移不再碰**。v11 的旧文件先过 v12 再过 v14，终点还是新默认。**他这份 settings.json 闸门后已是 v14、`SubtitleFontFamily` 已是 "Microsoft YaHei"。**
- **顺带把「和独立版 mpv 字幕不一样」的根因定死了（这一批之前的一个回合，未入档）**：他 mpv.conf 的 `sub-font="方正中等线简体常规"` 匹配不到任何字体（「常规」是字重不是族名；字体又没装系统、包的 fonts 目录里也没有），独立版回退到包 fonts 目录里的 Noto Sans CJK Bold，EmbyNian 画的是自带的方正中等线 —— 渲染比对在 `artifacts/subtitle-font-probe/`（composite.png、zoom-bold.png）。**这次改默认跟那件事无关**：他没说要对齐独立版；两边要一致，仍按那次给的两条路（包里放方正字体并把 sub-font 名改对，或 EmbyNian 里选 Noto 并把字体文件放进 exe 旁 fonts 目录）。顺带 mpv.conf 第 478 行 `sub-bold=y=no` 是坏行这事也告诉过他了。
- **动的文件**：Core —— `AppSettings`（默认、schema 14）、`SettingsMigration`（v14 一步、v12 注释补一句去向）、`FontFamilies`（兜底族名）；Shell —— `SettingRow.cs`（Sample 常量）、`ShellServices`（sub-fonts-dir 注释一句）；测试 —— `SettingsTests`（v12 那条改写成「v14 换回 Microsoft YaHei」一条）、`SubtitlePreviewTests`（出厂族名断言）、`PlaybackTests`（两条说明文字按新默认改口）、`FontTests`（自带字体那条改写死「方正中等线简体」拼路径 —— 默认和自带字体从此是两回事，不再借默认常量）。
- **闸门全绿**：Release 单节点 0 警告 0 错误、**778 项测试全过**（数量没变：v12 那条测试改写，其余是断言与文字改口）、发布件重发（474 个文件 300.5 MB、11 个 GLSL）、`--self-check` 退出码 0、末行「结果：全部通过」，token 出现 0 次。新读数：`字幕字体列表 — 静止时输入栏显示着「Microsoft YaHei」…`；`字幕示例预览跟着外观走 — 屏上那条画的是「字幕示例，」（Microsoft YaHei）…`。

**字体行默认收起并显示当前字体、字幕卡顶上加「字幕示例」外观预览（2026-09-06，四道闸门全绿，未提交）。** 用户两句话：①「默认收起来不要展开，然后输入栏要显示当前正在使用的字体」（配一张字体行列表展开着的截图）②「参考图2新增字幕外观功能」（配设置页整窗截图，红框画在字幕卡顶上，是一条大字「字幕示例」）。

- **一、字体行照播放页那个框的样子改了静止状态。** 播放页右上角那个字体栏本来就有「平时显示当前字体、点进去清空成搜索、走开放回去」，而设置页这一行（同一行类型 `SettingFontRow` 的另一实例）没有 —— 这一批把这套行为搬进行本身：`ListVisibility` 出厂从「开」改「收」（这就是「默认收起来不要展开」）；输入栏绑的是新属性 `BoxText`（静止时放当前字体），筛子还是 `Query` —— 拆成两个的理由：静止的输入栏里是一个族名，拿它去跑筛子会把整张名单滤成一条，点开就只剩这一个字体可挑。`OpenList`（点进框）清空输入栏、筛子归零、名单全量；选中收起并把新字体放回输入栏（照旧）；`CloseList`（LostFocus）收回并把当前字体放回去 —— **它先问焦点落了哪儿**：点名单里那一行是先夺焦点后选中（press 上移焦、release 上选中），收早了那一行就再也点不中了，所以它拿 `FocusManager.GetFocusedElement` 向上走树找 `ListView`，找到就不收（选中那条路自己会收）。播放页那个框一行没改 —— 它走的是页代码自己的路，行为本来就一致。
- **二、「字幕示例」预览是设置页上唯一只画不写的行。** Core 新增 `SubtitlePreviewPlan.Plan`（纯函数，单测钉着）：字号（0 → mpv 自己的 38）、描边（不设置 → mpv 自己的 1.65）、阴影（不设置 → 0）、三个颜色各自「不是颜色就退 mpv 的默认」（白字、黑边、底板黑约 69%），全部乘调用方给的 scale —— 行上定的 **1.3**，是让出厂 50 号字在 91 高的条里站得满那张参考图的示意比例，行说明写明「不是播放画面，实际多大跟片源分辨率走」。底板三档：关＝只有描边和阴影；`background-box`＝按 底板不透明度 画板、阴影照旧；`opaque-box`＝板画 100%、阴影收掉（实心板后面的阴影是白画）。**描边在屏上是八份同文字的拷贝叠出来的一圈**（XAML 没有「给字描边」，八份而不是四份是免得 0.5 那种发丝宽在对角上露豁口），层次就是清单的次序，所以模板只有一个叠着画的 `ItemsControl`、底板是垫在下面的 Border；层的文字对读屏是 Raw（十份拷贝不念十遍），整条的名字走 x:Uid。**重画的线只有一条：`Live<T>`** —— 外观每一行写完设置都从那儿过，顺带喊 `Refresh`；语言、显示模式那些「下次播放生效」的行不走 Live，动了也不该重画。行形状十种（选择器加 `Preview` 一案）。
- **⚠️ 顺带量到一件要跟他说清的事：他设置文件里字幕字体存的是「63H59RPF」。** 上一批跑完闸门时自检读数还是「方正中等线简体」，中间变成了这个乱名 —— 是他机器字体列表里真有的坏族名（截图中 58PCEIA5、63H59RPF 那一档），大概试字体的时候点中的；自检只读不写。按「挑过的值不动」没替他改，预览和播放现在画的都是它（解析不到就回退 mpv 的兜底字体）。**要回方正中等线简体：设置里点一下，或者说一声我替他改。** 顺带一句：这一批之后列表默认收着，这种误点会难发生得多。
- **动的文件**：Core —— `SubtitlePreviewPlan.cs`（新）；Shell —— `SettingFontRow`（收起、BoxText/Query 拆分、焦点护栏，探针两条按新生命周期重写）、`SettingRow`（`SettingSubtitlePreviewRow` + `PreviewLayer` + 假行探针）、`SettingsViewModel`（`Live<T>` 挂重画、字幕卡插预览行）、`SettingRowTemplates`（Preview 一案）、`SettingsPage.xaml`（Font 模板换 BoxText + LostFocus、Preview 模板新）、`ShellSelfCheck.Settings`（「字幕示例预览跟着外观走」新）、`Resources.resw`（预览读屏名一键）；测试 —— `SubtitlePreviewTests.cs`（新 8 条，Program.cs 挂上）。
- **闸门全绿**：Release 单节点 0 警告 0 错误、**778 项测试全过**（新增 8 条；红过一趟，是预览测试自己的断言写错 —— 描边「不设置」落到 1.65 是一圈八份不是一层，改断言即绿；另有一趟 WMC9999 老坑跟着一次重跑没的）、发布件重发（474 个文件 300.5 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0、末行「结果：全部通过」，token 出现 0 次。新读数：`设置卡片 — 「字幕」本卡 18 行`；`字幕字体列表 — 静止时输入栏显示着「63H59RPF」…`；`字体列表选完收得起 — 假行：静止时收起且显示「Consolas」、点搜索框展开并清空、选中后收起…走开收回并放回当前字体`；`字幕示例预览跟着外观走 — 屏上那条画的是「字幕示例」（63H59RPF）；假行：出厂样式画出阴影加一圈八份的描边、整行方框按不透明画且阴影收掉、描边关掉后只剩底板`。
- **截图一张**（发布件，`--show-settings 字幕`）：`artifacts/shots/subtitle-preview.png` —— 字幕卡顶上那条「字幕示例」（白字、无底板、出厂样子），说明在条下，卡片读数 18 项。**一处颜色都没动**（预览的颜色全来自设置值，条本身走主题画刷），没按主题逐套拍。

**字幕出厂外观整套定成他给的那八项：加粗默认关、底板颜色默认黑（2026-09-06，四道闸门全绿，未提交）。** 他一句话给了整套 mpv 写法：`sub-font="方正中等线简体常规"`、`sub-font-size=50`、`sub-bold=no`（原文写成 `sub-bold=y=no`，按「关」处理）、`sub-color="#FFFFFF"`、`sub-outline-size=0.5`、`sub-outline-color="#000000"`、`sub-shadow-offset=0.5`、`sub-back-color="#000000"`。

- **八项里六项本来就是出厂值**（字体就是自带的那款，字号 50、文字颜色 #FFFFFF、描边 0.5/黑、阴影 0.5），真正要改的只有两处：`SubtitleBold` true → false、`SubtitleBackColor` "" → "#000000"。他写的 `sub-outline-size` / `sub-outline-color` 是 mpv 0.39 的新名，客户端存发的还是旧名 `sub-border-size` / `sub-border-color`（mpv 留着当别名，同一个选项），不用动。**「方正中等线简体常规」那半个名字上一批已经对过**：「常规」是字重不是族名，族名存「方正中等线简体」—— 带后缀的名字 mpv 一个都匹配不到，真那么存反而回退成 sans。
- **schema v13 把这两处带过旧设置文件**：存着旧出厂值的（粗体开、底板颜色空或不是颜色 —— 旧「无背景」的 `none` 也算）换成新默认；自己挑过的（关过粗体、挑过颜色）一律不动；**v13 之后再存回旧默认就是他的决定，迁移不再碰**。v4 那步按 mpv.conf 时代补回来的粗体也被 v13 一起压掉 —— 新指令压过旧配置。底板样式那一行照旧出厂关，所以那行黑色出厂只给阴影上色（mpv 自己的阴影本来就是黑的，屏上样子不变，变的是那一行从「不设置」变成一个真颜色）；底板不透明度没动，还是 60%。
- **闸门后他这份 settings.json 已是 v13**：`SubtitleBold: false`、`SubtitleBackColor: "#000000"`，字号 50、文字/描边颜色、阴影 0.5 都和他给的一致。**一处旧存值照规矩没动，汇报里要说清**：他文件里描边大小存的是 `0`（无描边）—— 那不是任何一版的出厂值（v4 以来一直是 0.5），按「挑过的值不动」迁移不碰它；他要按这套新默认走，设置里那一行点回 0.5 就行。
- **动的文件**：Core —— `AppSettings`（两处默认、schema 13）、`SettingsMigration`（v13 一步）；测试 —— `PlaybackTests` 出厂样式那条改断言（`sub-bold=no`、`sub-back-color` 出厂就发 `0.000/0.000/0.000/0.600`）、「底板要发 sub-border-style」那条的「颜色不设置」用例显式写 `SubtitleBackColor = ""`（以前靠出厂空串成立），`SettingsTests` 改 v4 那条的粗体断言、「无背景」那条按新结果改、新增 v13 一条。
- **闸门全绿**：Release 单节点 0 警告 0 错误、**770 项测试全过**（新增 v13 迁移一条；头一趟红过一条 —— 就是上面那条「底板开着颜色不设置」，它以前靠出厂空串成立，改用例即绿）、发布件重发（474 个文件 300.5 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0、末行「结果：全部通过」，token 出现 0 次。自检读数一行变新话：`字幕颜色行装的是 HTML 颜色代码 — …<底板颜色>=#000000`。

**字幕那张卡五件事：字体打包进程序、其他字幕预设、字体列表选完收起、缩放默认写明、颜色换 HTML 拾色器（2026-09-06，四道闸门全绿，未提交）。** 用户五句话一起来：①「字幕默认用 sub-font=方正中等线简体常规，这个字体打包进程序里；sub-font-size=50、sub-outline-size=0.5、sub-shadow-offset=0.5；显示的数值不要用轻中重啥的，直接用数字」②「字幕优先级新增预设可选的其他字幕」③「字体的搜索功能，点击选择字幕后要把下方的列表收起来」④「给字幕缩放设置一个默认值」⑤「字幕颜色改为使用HTML颜色代码，参考 rapidtables 那个网页添加个一样的功能」（附 HTML 颜色选择器截图）。

- **一、字体打包走的是 mpv 自己的 `sub-fonts-dir`，不是装进系统。** 方正中等线简体.ttf（3.0 MB，从这台机器的用户字体目录拷进 `assets/fonts/`）随 shaders、vulkan-1.dll 同一条路走：csproj `None/CopyToOutputDirectory` 拷进 `fonts\`，`publish.ps1` 的资产断言加上 `assets\fonts`。`ShellServices` 把「exe 旁边的 fonts 目录」交给 `PlaybackPlanner`（新构造参数）→ `MpvBaseline` 新增 `sub-fonts-dir` 一对 —— 两个后端都吃这一份。**离线渲帧验过**（`artifacts/sub-probe/fontprobe.ps1`，复用那一批的 libmpv 探针）：中文名「方正中等线简体」和文件里那个英文名「FZZhongDengXian-Z07S」走 sub-fonts-dir 渲出来的帧哈希一字不差、且都跟「随便一个不存在的名字」的回退帧不同 —— 两个名字 mpv 都解析得到。
- **装机默认和兜底族名都换成它**（`PlaybackSettings.SubtitleFontFamily`、`FontFamilies.Default`）：自带字体跟着程序走，比「每台 Windows 都有的微软雅黑」更是「一定在」。字号 50、描边 0.5、阴影 0.5 本来就是出厂值，没动。**他写的「方正中等线简体常规」里「常规」是字重样式不是族名**，族名存「方正中等线简体」；这一半在汇报里讲明。**v12 迁移**换掉两种存值：`Microsoft YaHei`（旧装机默认，存着它就是没挑过字体）和 `.Heiti J`（他自己的设置文件里真躺着的那个 —— Windows 没有任何字体叫这个名字，它从来没按本名画出来过）；别的值（比如思源黑体）是挑过的，一律不动。跑完闸门后他这份 settings.json 已是 v12、字体已是方正中等线简体。
- **这款字体牵出一串「一个文件两个名字」的活**：目录扫描以英文名为主名（`FZZhongDengXian-Z07S`，和 Microsoft YaHei 一个规矩），中文是别名。于是 `FontEntry.AnswersTo`（主名或别名任一认账）、`FontCatalogue.Including` 不给别名再补重影行、`SettingFontRow` 的选中与 `Resolve` 都按 AnswersTo 认、`Value` 与选中拆开 —— **行上报的值是设置文件里存的那份写法**，选中项指向同一族，重挑同一行才写主名。这些各有断言：`FontTests` 新增「自带的字体文件解析出的名字要认得装机默认、存别名不冒重影行」，`PlaybackTests` 的出厂 `sub-font` 断言跟着常数走。
- **二、「其他字幕」是优先级列表里的一个具名预设**（`TrackLanguagePriority.Any`）：`Matches` 对任何轨道都真（连语言字段都空着的也算「其他」），`Codes` 答空表所以**从不进交给 mpv 的 slang**（mpv 自己的落空兜底就是「其他」的意思，翻译过去只会添一个永远匹配不上的词）。选轨那头不用改一行：`ChooseSubtitle` 的循环里它跟任何轨道都匹配，排在最后就是「前面的都要不到时，剩下的里面挑最好的」—— 这也是它跟「没有匹配语言时使用默认字幕」那颗开关的差别（开关只肯拿文件自带那条，名字能从剩下全部里挑）。音轨那边同一个规则顺带可用。设置页那一行加了说明（「填在最后兜底」），卡片说明带一句；两条选轨测试钉着（兜底取最好、排前头就轮不到点名语言、不进 slang、归一化留它）。
- **三、字体列表选完收起**：`SettingFontRow.ListVisibility`（模板里 ListView 跟着它走）—— 选中一个字体就收、点搜索框（x:Bind 事件绑到 `OpenList`）或敲字再展开。播放器右上角那个字体栏用的是同一个行类型的另一实例，AutoSuggestBox 的开合走它自己的路，不受影响。自检新增「字体列表选完收得起」（`SettingFontRow.CollapseProbe`，假行拨两遍：选中收起且写盘一次、点框展开、敲字保持）——**头一趟自检就是它红的**：探针原来重选了同一个引用，而「重挑同一个字体」在设计上就是静默空操作（不许写第二遍盘），探针先腾空再挑即对。
- **四、字幕缩放的出厂默认本来就是 100（=「不缩放」，100 不发 `sub-scale`）**，所以这一件按「写明」落地：那一行说明补了「出厂默认 100，就是『不缩放』；想回去就拖回 100」。**这句话是五件里唯一按我的解释做的**：他没给数，我不发明一个；要是他想要的是「播放器菜单里 ±0.1 调过的缩放记住当默认」或者「恢复初始回到设置里那个数而不是 1.0」，那是一句新话，报告里问了一句。
- **五、颜色三行（文字/描边/底板）从六个预设的下拉换成任意 `#RRGGBB`**。算术进 Core（`HtmlColor`：读写、RGB↔HSV，判据就是参考页那组 #AC5D5D = H0 S46 V67）；屏上是新控件 `HtmlColorPicker`（饱和度×明度的方块、竖色相条、R/G/B/H/S/V 六个数字框、#HEX 输入、「不设置」按钮），**布局照他给的参考图**；行类型 `SettingColorRow` 拿色块开着那个 Flyout，拾色器经 TwoWay x:Bind 直通行上的 `Color` —— 模板没有一行代码后置。**拖动期间只改屏上不写盘**（松手、数字框落定、HEX 敲够六位才提交），因为过绑定就是存盘，拖一趟存四十次不像话。「不设置」（空串）是三行都有的真值，所以是控件本身的按钮。NumberBox 清空报 NaN，转 byte 会炸，轴各自夹各自的天花板。
  - **迁移那半跟着换规则**：颜色不再跟旧色板比对（那会把用户挑的任何新颜色都扔掉），改认 `#RRGGBB` 本身（`SettingsMigration.Rgb`，统一大写写回；认不出的退「不设置」，旧文件里的「none」还是落到那儿）；`MpvOutputOptions` 的三张颜色目录（`SubtitleColors`/`SubtitleBorderColors`/`SubtitleBackColors`）整个删掉。**描边大小和阴影两行按他的话改成数字标签**（「0（无描边）/0.5/1.5/3/4.5」「0（无阴影）/0.5/1/2」，值一个没变）。
  - 拾色器画没画出来没法进自检（Flyout 不开就没有子树，这台机器注不进鼠标）——**这一眼是拿真点击补的**：设置窗口上 `winapp ui invoke` 点开文字颜色的色块（不碰播放、不碰卡片中间那条老规矩管的是库页面），抓屏一张 `artifacts/shots/settings-color-flyout.png`：方块、色相条、六个数、HEX、「不设置」全在，当前值 #FFFFFF 的圆圈落在该在的左上角。行本身的三件事由自检新关「字幕颜色行装的是 HTML 颜色代码」钉着（三行都在、存值全合法、假行上选色写盘一次、清掉也写一次 —— `SettingColorRow.Probe`）。
  - 新界面文字全走 x:Uid（16 个键进 `Resources.resw`：六个轴标签、六个数字框和两块拖动面的读屏名、HEX 框、#、「不设置」、色块的读屏名和悬停说明）。把手按老规矩：模板里的东西不给（三颗色块都在 DataTemplate 里），拾色器里那六个数字框给了显式 AutomationId —— 它们只在 Flyout 开着的时候才在树上，而浮层同时只开一个，id 不会真的撞车。
- **动的文件**：Core —— `HtmlColor.cs`（新）、`AppSettings`（默认字体、schema v12）、`SettingsMigration`（v12、颜色按格式认）、`FontFamilies`（兜底族名）、`FontCatalogue`（AnswersTo、Including 认别名、`ScanInstalled` 删了）、`FontLibrary`（扫描带上 exe 旁的 fonts 目录，所以拾色器列表里它带着真预览）、`MpvOutputOptions`（三张颜色目录删、描边/阴影数字标签）、`MpvBaseline`（sub-fonts-dir）、`PlaybackPlanner`（字体目录参数）、`TrackLanguagePriority`（Any）、`PlayerMenuCatalog` 没动；Shell —— `ShellServices`（目录进规划器）、`SettingRow.cs`（SettingColorRow + Probe）、`SettingFontRow`（收起、AnswersTo、Value 拆分、CollapseProbe）、`HtmlColorPicker.xaml/.cs`（新）、`SettingsPage.xaml`（Color 模板 + Font 模板收起）、`SettingRowTemplates`（Color 一案）、`SettingsViewModel`（三行换 ColorRow、说明三处）、`PlayerPage.SelfCheck.Menus`（按回车那条按别名判）、`ShellSelfCheck.Settings`（两关新）、csproj、`publish.ps1`；测试 —— `HtmlColorTests.cs`（新 9 条）、SettingsTests（v12 两条）、PlaybackTests（sub-fonts-dir 一条、其他字幕两条、出厂断言跟常数）、FontTests（自带字体一条、机器那条拆开）；资产 —— `assets/fonts/方正中等线简体.ttf`。
- **闸门全绿**：Release 单节点 0 警告 0 错误、**769 项测试全过**（新增 14 条，见上）、发布件重发（**474 个文件 300.5 MB** —— 多的那一个就是字体、重的 3 MB 就是它、11 个 GLSL）、`--self-check --dump-ui` 退出码 0、末行「结果：全部通过」，token 出现 0 次。**红过两趟，都是新关卡自己逮的**：第一趟两条（探针重选同一引用、播放页输入栏显示主名而不是存档写法），都在字体那一摊，按上面各条的改法修掉；第二趟起全绿。自检新读数：`字幕字体列表 — 当前「方正中等线简体」，共 3272 个字体族…`、`字体列表选完收得起 — …选中后收起并写了一次…`、`字幕颜色行装的是 HTML 颜色代码 — 3 行颜色…<文字颜色>=#FFFFFF、<描边颜色>=#000000、<底板颜色>=不设置…`。
- **截图两张**（发布件）：`artifacts/shots/settings-subtitle-colors.png`（字幕卡上半：语言优先级那行的新说明在屏上）、`artifacts/shots/settings-color-flyout.png`（拾色器浮层开着 —— 就是上面说的那一次真点击）。**一处颜色角色都没动**（拾色器全走主题画刷），所以没按主题逐套拍。
- **两件留给他的话**：①缩放那一条（第四件）如果按错了意思，一句话重做；②`artifacts/EmbyNian-0.0.1-win-x64.msix` 还是 09-05 那个旧包（发布脚本这次照规矩留着没删），要带字体的新包得再跑一次 `publish.ps1 -Msix -Sign`。

**恢复默认按钮搬到页头右上角、恢复默认那张卡删掉（2026-09-06，四道闸门全绿，未提交）。** 用户一句话配一张截图：「移到途中红框的位置，下方的恢复默认页面删除」—— 红框画在设置页页头标题右侧（`PageSlate` 的 Trailing 格）。2026-09-04 那张卡是为了「够不着」才开的（它先是「关于」卡的第八行，那张卡第七行就到底）；页头是整页最靠上的位置，比任何一张卡都够得着，于是卡连着左边名单里的分类一起删了。

- **动的文件**：`SettingsViewModel`（`CardCategories` 去掉「恢复默认」、`ReloadAsync` 少加一张卡、`ResetCard()` 整个删掉，换成 `[RelayCommand] private Task ResetAsync() => RestoreDefaultsAsync()` 生成的 `ResetCommand` —— 按钮的命令现在是视图模型自己的，不再借道一行一对象）、`SettingRow.cs`（`SettingActionRow` 整个类删掉 —— 它就是为那一行造的，没有第二个用主）、`SettingRowTemplates.cs` 和 `SettingsPage.xaml`（Action 模板删掉，选择器少一个 case —— 现在十种行形状变九种，行容器 81 → 80）、`SettingsPage.xaml.cs`（`internal Button ResetButton => SettingsResetButton;`，XAML 为 `x:Name` 生成的字段是 private，自检隔着类够不着，包一层）、`ShellSelfCheck.Settings.cs`（那一关改查法）。
- **说明没丢，从卡上搬进了按钮的 ToolTip**（`SettingsPage_ResetButton.[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip`，`Resources.resw`）：「把所有设置还原为装机时的默认值。只影响设置本身：服务器、账号和登录状态一律不动（不会退出登录）。按下后会先问一次。」卡删了之后，按下之前屏上把这件事说全的地方就是这句话；按下之后对话框再把同一件事讲一遍。按钮上的字（「恢复默认」）也从词表来，没往标记里写字面值。
- **自检那一关改了两个问题**：从「那一行在不在『恢复默认』卡上」变成「树上有没有那颗按钮（走 `page.ResetButton`，x:Name 的把手）＋ 它的 ToolTip 里还有没有『不会退出登录』那句（`ToolTipService.GetToolTip` 读回来）」；`CanConfirm` 那一半原样保留。读数：`恢复默认设置那颗按钮问得出确认 — 「恢复默认」，说明里写明了服务器和账号不动；确认对话框已接上页面`。
- **闸门全绿，一次过**（第一趟红过一次：生成的 x:Name 字段是 private，自检那一句 CS0122 —— 包一层后重跑即绿；同趟还捎了一个 WMC9999，按 CLAUDE.md 是老坑，跟着那次重跑一起没的）：Release 单节点构建 0 警告 0 错误、**755 项测试全过**（没新增：动的是外壳那一层，Core 里没有它们的位置）、发布件重发（473 个文件 297.5 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」，token 出现 0 次。设置页那三行读数变新话：「9 张卡片 + 3 个内嵌页面、80 行设置」「9 张卡片逐个打开，行容器累计 80 个」「已渲染 80 / 80」。`tools/scan-automation-ids.js`：没有把手的那一档照旧是 0（这颗按钮的把手是 x:Name）。
- **截图一张**：`artifacts/shots/settings-reset-topright.png`（设置窗口 1086×753，界面那一类）—— 绿色的「恢复默认」在页头右上角、通栏线之上，左边名单以「关于」收尾、下面直接是服务器 / 诊断 / 服务器控制台。**一处颜色都没动**（按钮走的还是 `AccentButtonStyle`），没按主题逐套拍。
- **「内嵌页面上那颗按钮还在不在」没做成开关**：服务器 / 诊断 / 控制台三个页面显示时，页头和这颗按钮照旧在屏上 —— 它是这一页自己的动作，不是某张卡的；要藏的话得再接一根 `CardsVisibility` 那样的线，买不来什么。

**装第三方技能（2026-09-05，不算一件代码活，所以上面那条「最新一件…上一件…」的编号链没动）。** 照 [`artifacts/技能安装-2026-09-05.md`](artifacts/技能安装-2026-09-05.md) 那份说明书做完了四件事：两个 Claude Code 插件装在用户级（`winui@win-dev-skills` 0.3.0，八个技能；`watch@claude-video` 0.2.0），`damionrashford/media-os` 里只拷了三个文件夹进 `%USERPROFILE%\.claude\skills`（`ffmpeg-hdr-color`、`ffmpeg-probe`、`hdr-dovi-tool`，它那个 109 技能的插件没装），winget 装了 WinApp CLI 0.6.1 和 yt-dlp（ffmpeg 跟着一起来，这次 PATH 里加的是 bin 目录、没踩那个已知的坑）。

- **那份说明书里的 `装技能.ps1` 没跑，是拆开一件一件做的。** 它有几处 `Read-Host` 在没人应答的时候会默认同意往机器上装东西，从这里跑就等于替他答了「装」。脚本原样留着，要重来跑它无害。
- **工作树里因此多了一处改动：`CLAUDE.md` 的 Skills 节末尾多一段封条**（未提交）。拦的是微软那套技能里会误导的几处：`winapp run` 代替不了发布和自检那两道闸门、`winapp ui` 跟 `winui-ui-testing` 一样不许点卡片中间（那是播放键）、不打包是他的决定而不是 `winui-packaging` 该修的缺陷、`winui-wpf-migration` 不要。**这一段当天下午按他的第二句话整体重写了，见下面那件。**
- **要关掉 Claude Code 再开一次**，新装的技能才会出现在会话里 —— 装完的这个窗口自己认不到它们。
- WinApp CLI 默认收匿名用法遥测，已按它自己给的办法关掉（用户级环境变量 `WINAPP_CLI_TELEMETRY_OPTOUT=1`）。

**新技能跟 CLAUDE.md 的冲突逐条对掉，`winui` 那套成了标准（2026-09-05，只动文档，未提交）。** 他前后说了三遍，最后一遍是「新技能不要改，以后统一按照新的 winui 技能来」。把十一个第三方技能连各自的 references 全读了一遍，跟 `CLAUDE.md`、四个自家技能、两条记忆逐条对了一次，**优先级整体翻过来**：WinUI 3 的做法从此以 `winui-*` 为准，**并且不设例外清单** —— 技能跟这份文件顶上了，是这份文件改。第三方技能一个字都没动，也不该动：插件一更新就被覆盖。改的文件：`CLAUDE.md`（第三方技能那段整段重写＋两条坑改正）、`embynian-winui-shell`（通用 XAML 坑交给 `winui-design`/`winui-code-review`，本文件只留这套代码自己的四条）、`embynian-verification`（`winapp ui` 的只读动词可以拿来断言）、`mpv-shader-quality`（判读画质 A/B 之前先用 `ffmpeg-probe` 问清片源）。

- **留在 `CLAUDE.md` 里的四条不是例外，是技能无从知道的事实**：SDK 在 `%USERPROFILE%\.dotnet`（`winui-setup` 会把 PATH 上那个 8.0.403 读成「够用」，而它建不了 `net10.0`）；构建必须单节点（`winapp run` 的 `-p` 只能传 MSBuild 属性，传不了 `-m:1`）；验证时不碰他真实的 Emby 库，所以 `winapp ui` 的 `click`/`invoke`/`hover`/`drag` 和 `winui-ui-testing` 那套「一趟跑遍每个元素」跟 `poke.ps1` 受同一条约束（只读动词和那份截图清单照收）；包版本钉死不解（那是代码形状，我判的，他要翻随时说）。
- **四件按技能改的事他都选了，而且四件都还没动手**，`CLAUDE.md` 里是「欠的活」而不是现状描述。一、**打包成 MSIX**：`WindowsPackageType=None` 去掉、加清单加签名证书，`%LOCALAPPDATA%\EmbyNian` 会被重定向，所以**已有的设置和播放记录必须迁移而不是丢掉**，而且那天起「不许直接跑 exe」开始成立 —— 第四道闸门得改成走打包身份启动。二、**界面文字搬进 `x:Uid` + `Resources.resw`**：量了一下，25 个 xaml 里有 195 处中文属性值、元素正文 0 处，另有 C# 那一侧。三、**去掉 `NavigationView`**：版面只在 `ShellPage.xaml`，但 `ShellSelfCheck.Run.cs` 数它的目的地、`HomeCarousel` 里那个 48 就是 `CompactPaneLength`，两处跟着走。四、**`Palette.xaml` 的 `Default` 词典改叫 `Dark`**。
- **第四件比我告诉他的小得多，这里更正一句。** 问他的时候我说的是「加一个高对比度主题」，其实 `Palette.xaml` 里**早就有**一份 21 支画刷、全部映射到 `SystemColor*` 的 `HighContrast` 词典，`Light` 也在，注释还写着高对比度下为什么不上亚克力。所以跟 `winui-design` 那条「Light + Dark + HighContrast，不要 Default」之间剩下的差别只是那个键名。**但不能当成改个名字**：`App.xaml` 把 `RequestedTheme` 钉在 `Dark` 就是为了让应用级查找落进一个确定的词典，`ThemeHost` 往 `Default` 和 `Light` 两份里都写颜色，`DiagnosticsViewModel.Resolve`、`PlayerPage.BrushFor` 两个读者加自检那条「应用主题」都认这个键。
- **两条坑当场改正，都是这次装东西时量到的。** 一、**「这台机器没有 Python」是错的**：`PATH` 上的 `python` / `python3` 是微软商店的占位程序，什么都不印、退出码还是 0 —— 正是这个「静默成功」把结论写歪了；真的装着 3.13.2（`%LOCALAPPDATA%\Programs\Python\Python313`，旁边还有个 3.10），用 `py` 就能跑，三个 media-os 脚本当场都跑起来了（`--help` 全过），所以「只能当参考书」那句作废 —— 只有 `hdr-dovi-tool` 还差 `dovi_tool` 这个外部程序。二、**光标那条坑扩了一句**：`winapp ui screenshot` 连 `--capture-screen` 也拍不到指针（拿一个指针停在里面的窗口量的，按指针自己的坐标裁出来看，那儿是空的），所以 `cursor-watch.ps1` 仍是唯一的办法；`winapp ui record` 那条视频路子没试，是这件事上还剩的唯一一招。
- **`MEMORY.md` 那两条一处都没动** —— 逐条对过：一条讲记忆按启动目录归档、项目事实该写进仓库，一条讲他会主动邀请我加东西、并且把先后顺序交给我。两条跟 `winui` 那套没有交集，也没有因为这次改动变旧。

**打包成 MSIX：包已经打出来并签好名了（2026-09-05，四道闸门全绿，未提交）。** 三件欠的活里的第三件。**装它还差一步，只有他能做**（要管理员终端），见下面最后一条。

- **一、先说这件活为什么一直欠着 —— 原因不是没人做，是这条路在这台机器上从来跑不通。** `publish.ps1 -Msix` 早就在，但它找的是 Windows SDK 里的 `makeappx.exe`，而**这台机器上没装 Windows SDK**，所以那个开关只会抛「找不到 makeappx.exe」。改成走 `winapp package`（`winui-packaging` 那份技能的 Quick Reference 就是这么写的）—— 它自己把布局、PRI、打包、签名四件事做完，不要 SDK，于是这条路第一次通了。
- **二、包身份从脚本里的一段字符串变成仓库里的一个文件**（`src/EmbyNian.Shell/Package.appxmanifest`）。从前那段 XML 只活在打包脚本的 here-string 里：没人读得到，`winapp cert generate` 也没有文件可以推 Publisher，而 `winui-packaging` 那条「不要删掉 Package.appxmanifest」根本没有对象。现在脚本读这一份、把版本号按 `Directory.Build.props` 改写再写进暂存目录 —— **版本号仍然只有一处**，程序集和包不可能各报一个数（包里量到的是 `0.0.1.0`）。清单里顺手补了 `runFullTrust` 能力（进程里加载 libmpv、自己建顶层窗口、嵌一个子窗口放视频，都不是受限能力集允许的）和 `MaxVersionTested`。
  - **踩到一个 XML 的老规矩**：注释里不允许出现两个连续的减号，而我在说明里写了 `--manifest` 这样的开关名，于是脚本读清单时当场炸。整份文件里的命令开关从此不带前面那两个横线，理由写在清单自己的注释里。
  - **还踩到一次编码**：Windows PowerShell 5.1 的 `Get-Content -Raw` 对没有 BOM 的文件按 ANSI 代码页读，于是清单里的中文进来是乱码、写进包里的显示名也跟着乱。加 `-Encoding UTF8` 修掉了，**凭据是把包里那份清单抠出来按 UTF-8 读**：`Description="Emby 的 Windows 播放客户端"`，一个字没歪。
- **三、`--skip-pri` 不是省一步，是必须的。** 发布目录里那个 `EmbyNian.pri` 已经把框架那三份并进来了（就是这一天早些时候逮到的那个坑），让 `winapp` 再生成一遍会盖掉它、出一个 103 KB 的版本 —— 装出来的程序一启动就死在 `App.xaml`。**包里量到 `EmbyNian.pri` 是 2,314,040 字节**，也就是并进来那一份，这条是验过的不是想当然。
- **四、`<WindowsPackageType>None</WindowsPackageType>` 留着，这是我判的、他可以否。** 这不是「让 SDK 替你生成 appx」那种打包方式，而是「把松散的桌面构建原样套一层包」（`EntryPoint="Windows.FullTrustApplication"`）。**两个理由**：libmpv 是在进程里加载的原生 DLL、视频是我们自己那个 HWND 的子窗口，这就是全权信任桌面程序的形状；而且松散构建仍是主产物才保得住第三、四道闸门 —— 它们直接跑 `artifacts\publish\win-x64\EmbyNian.exe`，一个只能从已安装的包启动的构建，等于验证之前先得有人在这台机器上信任一张证书。**所以 `winui-dev-workflow` 那条「不许直接跑 exe」在这里仍然不成立**，理由从「欠的活」变成了「有意的设计」，写进了 `CLAUDE.md`。
- **五、数据迁移做了，但只有能测的那一半被测了 —— 这一条照实说。** 装成 MSIX 之后 `LocalApplicationData` 可能被重定向到包自己的 `LocalCache\Local`，那样他现在这份设置、服务器、存着的密码和令牌、Emby 那个设备 id 全都不在新目录里，程序看着像全新安装。`AppPaths.PriorRoots` 现在把「不打包那一份」排在最前面试，`MigrateFromAny` 按顺序抄第一个有 `settings.json` 的候选（照旧是拷贝不是移动，也照旧不抛异常）。**能变成纯函数的那一段抽出来单测了**：`UnvirtualizeLocalAppData` 把 `…\Packages\<族名>\LocalCache\Local` 还原成真的 LocalAppData，认不出的路径要答「不是」而不是硬切四段 —— 新增 5 条测试（755 项全过）。**验不到的那一半**：真装一次、真被重定向、真迁移成功，这要先信任证书再装包，也就是要他动手。自检报告里那行「数据目录：…」和日志里那句「已从旧版数据目录迁移设置与缓存」就是他装完之后该看的两处。
- **闸门全绿**：Release 单节点构建 0 警告 0 错误、**755 项测试全过**（新增 5 条，见上一条）、发布件重发（473 个文件 297.6 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」，token 出现 0 次，一次过。**包本身也验了**：481 个条目、`AppxSignature.p7x` 在（签上了）、`libmpv-2.dll` 在、三张 logo 由 `app.ico` 现生成、压缩后 118.4 MB。
- **证书和包都在 `artifacts/`，而那一整个目录是 `.gitignore` 忽略的**（`git check-ignore` 核过）—— `devcert.pfx` 带私钥，绝不能进仓库。别为了「方便」把它挪到别处。
- **⚠️ 还差的那一步是他的，一台机器只用做一次**：拿**管理员权限**的终端跑 `winapp cert install "C:\Users\89400\EmbyNian\artifacts\devcert.pfx"`，然后双击 `artifacts\EmbyNian-0.0.1-win-x64.msix` 就能装。**交出去的版本还要加时间戳**（`-Timestamp http://timestamp.digicert.com`），否则证书一过期签名就失效 —— 脚本在没加时间戳时会把这句话印出来。

**删掉侧边栏：换成顶部标签栏，账号挪到右上角（2026-09-06，四道闸门全绿一次过，未提交）。** 用户两句话：「1.参考这个（microsoft/WinUI-Gallery）重新设计 ui 界面 2.删掉侧边栏」。**这一批只做了第 2 句加第 1 句里外壳那一层**；第 1 句他挑的是「整套重画」那一档（卡片圆角、间距统一 24/36、设置页那十张卡改成 Fluent 那种行、轮播渐变和详情页黑边阈值按新样子重定），那是接下来的活，见本节末尾那份分批清单。

**动手前问了他两件事，两件都是他的**（改到哪一层、账号放哪儿）。第一件他选「整套重画」，而选项里明写着这一档会推翻他自己定过的十几处样子；第二件他选「顶部标签栏右端」。**别再问第二遍，也别照 09-05 那句「窗口左下方」把账号搬回去** —— 左边那条栏没了之后左下角只是内容的一角。

- **屏上现在是两行外壳，一共 80 高（`HomePage.ChromeHeight`）**：第 0 行还是那 32 像素的标题栏（设置 · 搜索 · 后退 · 前进 ＋ 系统那三颗），第 1 行是 48 高的标签栏（`主页 │ 电视节目 │ 电影`，右端是账号）。页面从窗口最左边开始，一条侧边栏都没有；主页那张大图照旧把这 80 顶回去、贴住窗口顶边（`SyncBleed`，从前顶的是 32）。
- **标签用 `SelectorBar`，不是 `NavigationView` 顶部模式。** `winui-design` 的控件表写着「2–3 个模式 → SelectorBar」，而这里正好是 主页 加服务器上那两个库；那份技能的应用形状表里「媒体 / 画布 / 英雄图」一行也直接写着 **no `NavigationView`**，所以这一步是照技能做而不是绕开它。标签跟服务器的媒体库列表走（`LoadLibrariesAsync` 一格一格加），不写死。
- **溢出那一层留了：`SelectorBar` 套在一个横滚、不出滚动条的 `ScrollView` 里。** 它自己不管溢出（`NavigationView` 顶部模式那套「…」菜单它没有）。他这台服务器上三格、最窄的窗口 900 也放得开，所以平时一点作用都没有；真有六七个库时最后那一格是「滚过去就有」而不是「画不出来」，键盘 Tab 也会把它带进视野。**这不是「ScrollViewer 套 ListView」那种反模式** —— SelectorBar 不是会自己虚拟化滚动的集合控件。
- **跟着一起删掉的五样，每一样都不是顺手**：①那颗折叠按钮连它那个「两个连起来的长方形」的手画图标（没有栏可折）；②媒体库那行分组眉字和它上面那条分割线（一条横标签栏没有分组）；③侧边栏和工作区之间那道竖发丝线，加框架给「工作区」画的那道边、那个圆角、那圈边距（那三个键是 `NavigationView` 模板的）；④设置 → 界面 → **「默认收起侧边栏」整行连 `UiSettings.CollapseSidebar` 一起**（那个键不留存而不用的空壳，反序列化碰到没处放的键本来就不出声）；⑤`HomeCarousel.SideRail`（49 = 收起来的窄条 48 加那道线）连 `HostWindow.SideInset` 一起 —— **而页宽一个像素都没变**：从前是 1471 的窗口配 1422 的页面，现在是 1422 配 1422。
- **`NavigationView` 顺手带走的三条线，各自重新接上了，这是这一批最容易漏的地方。** ①**Alt+←** 从前走 `NavigationView.BackRequested`，现在是 `BackButton` 自己的 `KeyboardAccelerator`（前进那颗本来就有 Alt+→）；②**鼠标上那两颗侧键**没有加速器可挂，改成在根上听 `PointerPressed`，`handledEventsToo: true`（卡片、列表、滚动视图都会把这个事件标成已处理，不加这一句就只有点空白处才退得回去），判的是 `PointerUpdateKind.XButton1Pressed` 而不是 `IsXButton1Pressed`（后者在按住不放的每一次移动上都是 true，那就是按一下退好几页）；③**「点已经高亮的那一格要回主页」**（需求 6）：`SelectorBar` 只发「选中项换了」，够用的原因是下钻那几条路本来就把 `SelectedItem` 清成 null，所以「钻进一张海报之后点 主页」是一次真的选中变化。**不需要防重入的旗子**：`Open()` 先写 `_current` 再同步高亮，同步引出来的那一趟撞在 `_current == tag` 那道门上就回去了。
- **墨这一摊反倒简化了，这是删掉那条栏白拿的。** 从前 `PaintTitleInk` 有两道门：我们那五颗按键要等侧边栏收成窄条才算「压在图上」，而面包屑不经那道门。现在外壳两行整个浮在页面上，四拨（按键、标签、账号、面包屑）走同一句判断 —— `_titleStrip == OnScrim`。`SyncPane`、`ApplyPaneDefault`、`OnPaneToggled`、那两个缩进常数一起没了。
- **标签栏和账号的颜色一律走角色表**（`PaintTabs`，抄的是从前 `PaintNavigationPane` 的做法）：没选中的字是暗墨、选中是亮墨、悬停和按下是标题栏那四颗同一层白、**选中那条药丸是强调色**。药丸那一支必须换成共用对象，理由和从前侧边栏那根指示条一模一样：框架默认从 `SystemAccentColor` 派生，而 `Palette.xaml` 覆盖的是那个 *Color*，框架的画刷在解析自己字典时就把它取走冻住了，换主题不会动。
- **自检：删一关、改两关、加一关。** 删的是「设置入口」（它问的是 `NavigationView` 内建设置项关掉了没有，那个控件已经不在树上）。「主页首屏两栏」从「收起和展开各量一次」变成量一次（页宽只有一档了），顺带多报一句「页宽就是客户区宽」—— 这句话从前是假的（要减 49），现在是真的，而它变假就说明谁又在页面左边塞了一列；「标题栏按键」从五颗改成四颗、去掉那两档收放的位置对账。**新增「顶部标签栏」一关**，判五件事，每一件的坏法在截图里都不出声：格数 = 1 + 媒体库个数且每格带 tag（少接一个 tag 就是「点了没反应」）、这一行在屏上、**整条落在标题栏那 32 像素以下**（画在窗口标题栏区域里的控件收不到点击，一按就是拖窗口 —— 这个项目的老账）、外壳合起来正好 80（少让就是页头被切半行，多让就是图上一条底色）、账号贴着右上角、第一格和页面左边距对齐。
- **闸门全绿，一次过**：Release 单节点构建 0 警告 0 错误、**755 项测试全过**（没新增：删了 `SideRail` 那一条断言、`CollapseSidebar` 那两处断言，另两条按新算法改了注释和被减数；新长出来的东西全在 Shell 那一层，测试工程够不着）、发布件重发（473 个文件 297.5 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」、141 项 0 失败、token 出现 0 次。新那一关的读数：`标签 3 格（主页 + 2 个媒体库），3 格带 tag，第一格 tag「home」；从 y=32 起、高 48，外壳共 80 高（该 80）；整条在标题栏 32 以下；第一格从 x=16 起；账号 123×40 右沿 1044／窗口宽 1064，贴着右上角`。
- **截图两张**（都在副屏 1066×793）：`artifacts/shots/tabs-emby-dark.png`（默认墨绿：标签栏在窗口顶上，`主页` 那格底下一条绿色药丸，账号「donxuelian / 果服」在右上角，大图从最左边开始）和 `tabs-plum.png`（李子紫，**这张是判据**：药丸和播放键都跟着变成紫色，说明标签栏真的走了角色表而不是写死的颜色）。
- **他该看一眼、我没动的一处**：标签栏上已经写着「主页」，而页面顶上那块牌子又写一次「HOME / 主页」—— 现在是两处同名。**这一处归下一批**（「外壳 + 每页页眉」那一层本来就要重排页头），所以没在这一批里自作主张删掉他看过的那块牌子。

**「整套重画」那五步做完了（2026-09-06，四道闸门全绿，未提交）。** 他一句「全做」。上面那一批（删侧边栏）之后的第二批，动的是设计令牌、页眉、卡片间距、设置页的行，加上一次收口的判断。**这一批和上一批是同一天同一份工作树，闸门是连着跑的**，所以那句「755 项全过」两批共用。

- **一、间距刻度进了词表，页边距 28 → 24（`Theme/Styles.xaml`）。** 新增六个键（XS 4、S 8、M 12、L 16、XL 24、2XL 36）加两个 Thickness（`EgPageMargin` 24 四边、`EgPanelPadding` 24）—— 全是 4 的倍数，而这套界面从前是 8/10/12/14/16/18/20/28 混着，其中 **18 和 28 根本不在那张格纸上**。`Padding` 要 Thickness 而刻度是 Double，两者之间资源引用不会替你转换类型（写错就是运行时崩在解析那一句上），所以那两个 Thickness 是单独的键，理由写在它们旁边。
  - **跟着 24 一起走的有六处，少改一处就是屏上几条差 4 像素的错位竖线**：面包屑那一行的左右内缩、主页那块牌子的左右、详情页正文那一层、主页轮播的浮层、标题栏那一排图标的缩进（**21 → 17**，17 + 托盘自己的 7 = 24）、标签栏第一格的左边距（**16 → 12**，12 + 格子内边距 12 = 24）。四样东西现在落在同一条竖线上。
  - **自检那一关当场逮到一处，这是这一批唯一真出错的地方**：`ShellSelfCheck.Frame` 里「标题栏左端那块留白答不答标题栏」写死在 x=20 采样，而留白从 40 收到 17 之后 20 掉进了按键那个洞里 —— 报的是「左端留白 客户区」，也就是「拖动区被挖没了」。改成按量出来的位置算（整排左沿的一半），**一个跟着现场走的数字不会再有第二次**；启动那一读发生在探针之前、手上还没有矩形，只能写死，改成 8 并把「为什么 8 一定在洞左边」写在旁边。
- **二、页眉按 Gallery 那套重排（`PageSlate`）。** 从前是「眉字在左上、读数在右上、标题在第二行」；现在是 Gallery 的两行加一条线 —— **大标题在第一行，`Eyebrow` 和 `Note` 合成的一行说明在第二行**（中间一个「·」，走读数字），然后分隔线。屏上是「主页 / HOME · 果服 · 29 项」、「设置 / SETTINGS · 改动即时保存」。
  - **两个属性一个都没删**，所以七个页面的标记一个字都不用改，`LibraryRequest.Eyebrow` 那份「这是搜索还是剧季还是类型」的信息也照旧在屏上 —— 合成的规则只在 `SyncNote` 一处。工具条那一格跟着标题从第 1 行挪到第 0 行（`TrailingBelow` 那一档不变）。
  - **「标签栏写着主页、页面又写一次主页」这处重复我上一批标给他看了，这一批的结论是不动，而且理由变了**：Gallery 自己就是这样（左边导航写 Button、页面标题也写 Button）—— 一个是控件说「你选了哪一格」，一个是内容说「这一页是什么」，而页面滚下去之后只有后者跟着走。写进了 `PageSlate.xaml` 顶上。
- **三、卡片和货架那几个间距归到格纸上**：货架牌子到卡片带 10 → 12、右栏里那两处 10 → 12、主页最后一排到底 40 → 36、货架牌子里那 6 → 8、卡片底下两行字 2 → 4。圆角一处没动（8 本来就是 Fluent 卡片那一档）。
- **四、设置页从「一张大卡里一叠没有边界的行」变成「一个分组 + 一叠各自带边的行」（Gallery 里那叫 SettingsCard）。** 八十一行设置从前只靠 10 像素的间距分家，说明折两行之后上下两行的字就连成一片。现在每一行自己是一张卡：底色和边**用的是 Gallery 那两个键**（`CardBackgroundFillColorDefaultBrush` / `CardStrokeColorDefaultBrush`，`Palette.xaml` 早就映好了、一直没人引），圆角 4（8 是「一块能放东西的板」那一档，一行只有 60 高，8 在它身上是个胶囊），内边距 16,10，最低 60 高；行距 10 → 4。外面那张大卡的底色、边框、圆角、内边距**全部撤掉** —— 卡不该套卡，两层圆角套在一起外层那圈边就成了一条谁都解释不了的线。
  - **做法是两个样式，不是给八个模板各包一层 `Border`**：WinUI 的 `Grid` 和 `StackPanel` 自己就有 Background / BorderBrush / BorderThickness / CornerRadius / Padding 这五样，而那些行模板的根本来就是这两种 —— 所以这一步是把每个模板上那句 `Margin="0,3,0,3" ColumnSpacing="24"` 换成一句 `Style`（八处 Grid ＋ 三处 StackPanel：主题色板、开关组、主页版面那张表）。
  - **没装 `CommunityToolkit.WinUI.Controls.SettingsControls`，这是我判的、他可以否。** 那一包会把 `SettingRowTemplates` 那套模板工厂、每行的 `x:Uid`、自检里「数行容器」那一关全部重写一遍，而屏上要的那个样子两个样式二十行标记就够。**这是「更好写法」那一条**：判据是首要目标的三条（改一处要开几个文件、一个文件还读不读得完、判断钉不钉得住），三条都赢。他要那个包是一句话。
- **五、轮播、详情页、播放器浮层：只统一了圆角，浓度和阈值一个数都没动 —— 这一条是有意的，照实记。** 统一的是三处「小浮层」的圆角（播放器两处 6 和 10、服务器页一处 10 → 都换成卡片那一档 8）。**没动的是他自己一条条定过的东西**：轮播四条边缘渐变那五个 ARGB（他 09-05 调过两趟，自检的 `Rims` 只钉形状不钉浓度，正是为了「他调一眼颜色不用改断言」）、详情页黑边那三档显示器阈值（1920×1080 / 1600×900 / 1366×768，他 09-05 亲口给的数）、播放器浮层那二十来处量出来的偏移（跳过键离控制条一个间距、统计面板的位置、音量条 300 高）。**理由是「按新令牌重定」在这三处没有一个新令牌可依**：Gallery 没有轮播、没有黑边阈值、没有 OSD，把 10 改成 12、22 改成 24 只会让他调过的手感各偏两像素，而屏上看不出任何「更 Fluent」。他要真按格纸把这三处也扫一遍，说一句我就做。
- **闸门全绿**：Release 单节点 0 警告 0 错误、755 项测试全过（没新增：改的是标记里的间距和样式键，Core 里没有它们的位置）、发布件重发（473 个文件 297.5 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」、141 项 0 失败、token 出现 0 次。**红过一趟，就是上面第一条那处采样点**（真错，已修）。样式词表那一关从 44 个键变成 **46 个**（六个刻度键加两个 Thickness，减掉的没有）—— 那一关是新键唯一的守卫：`{StaticResource}` 撞上不存在的键会在解析页面时抛，那一页于是根本不出来。
- **截图两张**（副屏）：`artifacts/shots/gallery-home.png`（主页 1066×793 —— 新页眉「主页 / HOME · 果服 · 29 项」，页边 24，货架间距归位）和 `gallery-settings.png`（设置窗口 1018×753，界面那一类 —— **这张是这一批的判据**：分组标题「界面 / 5 项」下面是说明，再下面「主题」那一整块自己带底色和细边，也就是行变成了卡）。**没按主题逐套拍**：这一批一处颜色角色都没换，新引的那两个 Gallery 键本来就映在本项目的角色表上。

**接下来那几批（「整套重画」拆出来的）。** 他选的那一档要动的地方远超一批，按「改一处、四道闸门跑一遍、拍一张」拆成下面这几步，一步一报 —— **五步全做完了，见上面那一节**：

1. **设计令牌**：圆角、间距（统一 24 / 36）、卡片底色加一圈细边（`CardBackgroundFillColorDefaultBrush` / `CardStrokeColorDefaultBrush` 这两个键 `Palette.xaml` 里早就有，现在没人用）、七档字号刻度里那 32 处能直接对上的写死数字换掉。
2. **每一页的页眉**：`PageSlate` 从「小号眉字 + 大标题」改成 Gallery 那种「大标题 + 一行灰色说明 + 一道分隔线」，顺带解掉上面那处「主页」写两遍。
3. **卡片和货架**：`PosterCard` / `ShelfStrip` / `EpisodeRow` 按新的圆角和间距重排。
4. **设置页那十张卡**：改成 Fluent 那种「图标 + 标题 + 说明 + 右边一个控件」的行。**打算用自家 `SettingRow` 那套模板改样子，而不是装 `CommunityToolkit.WinUI.Controls.SettingsControls`** —— 那一包会把 `SettingRowTemplates` 那套工厂、每行的 `x:Uid`、自检数行容器那一关全部重写一遍，而屏上要的那个样子二十行标记就够。这是「更好写法」，落地时会连理由一起写进 `CLAUDE.md`；他要那个包就是一句话。
5. **轮播、详情页、播放器浮层**：按新令牌重定那四条渐变的浓度、黑边阈值、浮层的圆角和间距。**这一步最贵也最容易推翻他定过的样子**，所以放在最后，改完逐张拍给他看。

**主页第一屏右栏从继续观看换成媒体库（2026-09-06，四道闸门全绿，未提交）。** 用户两句话：「1.侧边栏也改 2.把首页轮播图右边的继续观看更换为媒体库列表」。**这一批只做了第 2 句**；第 1 句（去掉侧边栏改成顶部标签栏）是下一件，见下面「三件欠的活」那一条。两句是一件事：左边那条栏里的媒体库入口没了之后，这一栏就是它们在第一屏上的落点。

- **换过去在形状上是白拿的，所以改动只有一个钥匙名。** 右栏那一列一直是「按钥匙认哪一排」而不是按位置认（理由见 `HomeViewModel.Rail` 那段：栏宽只有一张卡，海报那种排在这个宽度上只放得下两张），而**媒体库那一排本来就是 16:9 的宽卡**（`wide` 那个判断里早就有它）—— 所以栏宽、卡宽、一栏放得下几张、翻页规则一个数都不用动，改的是 `HomeLayout.Resume` → `HomeLayout.Libraries` 那五处。继续观看跟着变成横着的一排，站在它自己在版面表上的位置（他那张表里排第二，所以屏上在「最近添加」下面）。
- **自检那三行跟着说新话，这是判据不是装饰**：`主页版面` 现在写「最近添加✓、继续观看✓、媒体库✓（右栏）…；横着排的 最近添加、继续观看、最近添加 · 电视节目、最近添加 · 电影；右栏 媒体库 2 项，宽 280」；`主页首屏两栏` 报「右栏 280×413 从 784 起，贴着大图，完整露出 2 / 2 张卡」；`主页右栏翻页` 照旧全过。**「勾掉媒体库之后右边那一栏还在」这种坏法仍然当场红** —— 那一关判的是「版面里那一行说的和屏上那一栏在不在」必须一致，只是它现在盯的是媒体库那一行。
- **⚠️ 一个他自己要过的功能因此失效了，等他一句话：轮播「滚到哪一张就框出右栏里对应的那一个」。** 那是 2026-09-05 他的第 4 句（「轮播图滚动到对应媒体时右边要自动框出对应媒体」），而右栏现在装的是媒体库、不再是媒体条目，所以**永远匹配不上**：报告里那一行现在写「『地球之夜』在右栏里没有对应的条目，一张都没框（右栏 2 项）」。**代码一行没删**（`HomeCarousel.MatchIndex`、`CardItem.Framed`、那四条单测都还在，也都还绿），因为这是他的功能、该由他决定：①就这样放着（屏上只是从此不出现那个绿框，无害）②把那段匹配连自检读数一起删掉（少一条以后会让人困惑的死逻辑）③改成框别的东西 —— 但右栏里只有媒体库，「当前这张幻灯片属于哪个库」倒是算得出来，也就是可以框出它所在的那个库。**我没替他选。**
- **闸门全绿**：Release 单节点构建 0 警告 0 错误、755 项测试全过（没新增：改的是「哪一排进右栏」这一个钥匙名，而那一排的形状规则和翻页规则原来就有测试钉着）、发布件重发（473 个文件 297.6 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」，token 出现 0 次，一次过。
- **截图一张**：`artifacts/shots/rail-libraries.png`（副屏 1066×793）—— 右栏牌子写着「媒体库 2 项」、下面是电视节目和电影两张宽卡，继续观看已经不在第一屏右边、跑到下面横着排的那一叠里去了。**一处颜色都没动**（右栏那块深底和那套浅墨还是原来的画刷），所以没按主题逐套拍。

**装完之后当场验的三件事（2026-09-06，他信任证书之后接着做的）。** 上面那一批把包打出来就停了，第五条还挂着「验不到真装一次」。他 07:57 信任了证书，于是这三件当天就有答案了 —— **都是量到的，不是推断**。

- **一、包装得上，而且是签名验得过的那种。** `Add-AppxPackage` 一次成功，注册出来是 `EmbyNian_0.0.1.0_x64__mre2em1sb0g7g`，装在 `C:\Program Files\WindowsApps` 下面。证书落在 `LocalMachine\TrustedPeople`（旁加载该去的那个库，不是 `Root`，2027-09-06 到期），`Get-AuthenticodeSignature` 报 `Valid`。
- **二、打包身份下自检全过。** 用 `Invoke-CommandInDesktopPackage` 带着包身份跑 `--self-check`（**不放任何影片**），报告末行「结果全部通过」、143 项，日志里「自检全部通过」接着「正常退出」。也就是说套上包之后程序照旧起得来、页面照旧走得完 —— 这是「不打包那一份仍是主产物」这个决定能站住的实证。
- **三、`%LOCALAPPDATA%` 根本没有被重定向，所以那件担心了一整轮的迁移其实不需要。** 包身份那一趟报告里写的数据目录是 `C:\Users\89400\AppData\Local\EmbyNian` —— 和松散那一份一模一样，日志里也没有「已从旧版数据目录迁移设置与缓存」这句话，因为没什么可迁的。**全权信任的桌面包不吃那层虚拟化**，这一条从此是量到的事实而不是假设。上一批加的迁移代码因此变成一层白拿的保险（`UnvirtualizeLocalAppData` 那 5 条单测照旧钉着它），**不要因为「用不上」就删掉** —— 换个 Windows 版本或者哪天改成非全权信任，它就是唯一挡住「看着像全新安装」的东西。
- **四、顺手修了打包脚本上两个真的会咬人的地方，都是这一趟撞出来的。**
  - **从前每次发布都无条件删掉上一次的 `.msix`**，于是「打个包 → 再跑一遍闸门」这个再普通不过的次序会把刚交出去的安装包悄悄弄没 —— **真发生了**：我把路径告诉他，他去装的时候文件已经不在了。现在只有真要重打包（`-Msix`）时才删；留着不删就 `Write-Warning` 说一声「这是更早那次构建的」，因为版本号一样而内容更旧的安装包比没有安装包更坏。
  - **新增 `-SkipPublish`**：程序开着的时候它自己那些 dll（`clrjit.dll` 第一个）删不掉，于是清 publish 目录这一步会让整个脚本在第一句就死 —— 而「程序正开着」恰恰是想单独打个包给人装时最常见的状态。**这一趟就是这么炸的，而且炸得半途而废**：清理删到一半停下，publish 目录少了整个 shaders，第三道闸门当场红。修完之后是这么救回来的：先用 `-OutputRoot` 换个目录发一份完整的（`-CertPath` 指着他已经信任的那张证书，**不能让脚本另生成一张**，否则他刚信任的那张就白信任了），再把缺的文件补回原目录（`cp -rn`，被占用的那几个文件本来就一模一样），第三道闸门重新绿。**下次遇到就直接 `-SkipPublish`。**

**界面文字全部搬进 x:Uid + Resources.resw（2026-09-05，四道闸门全绿，未提交）。** 三件欠的活里的第二件，他一句「全做」。**做完了：18 个界面文件里 193 处中文属性值，标记里现在一处字面值都不剩**（只剩 1 处，见下面最后一条）。屏上一个字都没变。

- **先拿一个字符串证了「不打包能不能用 x:Uid」，再动那 193 处。** 这一步不能省：仓库里 `EmbyNian.pri` 和 `MRM.dll` 都在，理论上 MRT Core 够得着，但这句话在这台机器上没人验过，而赌注是 193 处改动。探针挑的是搜索框那句灰字提示（`LibrarySearchBox.PlaceholderText`）—— 标记里删掉字面值、只留 `x:Uid`，然后拍照：屏上照旧写着「搜索影片、剧集、演员」。**证通了才继续。**
- **还有一个前置条件，不设的话探针也会绿一半、真机上全空：csproj 里的 `DefaultLanguage=zh-Hans`。** PRI 生成时会往 `priconfig.xml` 里写一个默认语言限定符，不设就是 `en-US` —— 而这里只有 `zh-Hans` 一张表，于是每次查找都问一个没有表的语言、拿回空字符串。`Directory.Build.props` 里那个 `NeutralLanguage` 管的是托管程序集，到不了 PRI，两件事。设完 `priconfig.xml` 里那两处从 `en-US` 变成 `zh-Hans`，是这一条的凭据。
- **分两批做，因为两批的失败方式完全不同。** 第一批 111 处是普通属性（`Text` 49、`Header` 18、`Content` 14、`Title` 9、`PlaceholderText` 7、`CloseButtonText`／`PrimaryButtonText`／`OffContent`／`OnContent` 各几处）—— 键名就是 `<x:Uid>.<属性名>`，写错的话屏上当场空白，看得见。第二批 82 处是**附加属性**（`ToolTipService.ToolTip` 47、`AutomationProperties.Name` 33、`HelpText` 2），键名是 `<Uid>.[using:命名空间]类型.属性` 这种形式，**写错一个字不报错也不留痕迹**：属性只是没被设上，于是读屏软件念不出那颗按钮的名字、悬停没有提示 —— 屏幕上一切正常。
- **第二批的判据是自检里现成的那一条：「读屏与焦点 — 20 个控件报出了名字，没有一个是哑的」。** 那 20 个名字正是播放器上由标记设的 `AutomationProperties.Name`，键名形式错了它们会变哑、这一关当场红。改完两趟自检都报「20 个控件报出了名字」，所以那个 `[using:…]` 的形式在这台机器上是成立的。
- **47 处悬停提示是靠推断成立的，不是靠自己的探针 —— 这一条照实记。** 它和自动化名字用的是同一种键名形式、同一套 MRT 解析，名字那 20 条既然绿了，提示这一半没有别的失败机会；但**提示本身没有任何一关在看**，而这台机器拍不到悬停。**要真钉住它，一行自检就够**：拿一个具名控件（比如卡片上那颗「更多」）调 `ToolTipService.GetToolTip` 读回来、断言不是空 —— 那一行值得下一批顺手加上。
- **两个脚本跑完就删了**，改动逐个 diff 核过。生成的 `x:Uid` 取法是「文件名_元素的 x:Name」，元素没有名字时退回「文件名_类型序号」（`DashboardPage_TextBlock1` 这种）—— 不好看，但它是机械生成的，而机械生成的名字不会因为有人改了屏上的字就变旧。这条规则和那 82 处为什么不在表里，都写在 `.resw` 顶上的注释里。
- **剩下 1 处没搬**：`ShelfHead` 上那个自定义的 `Note` 属性。它不是框架属性，`x:Uid` 能不能设自定义依赖属性这件事要自己的探针，而它只有一处 —— 不值得为一个字符串再走一遍「证通了再改」。
- **闸门全绿**：Release 单节点构建 0 警告 0 错误、750 项测试全过（没新增：搬的是标记和资源表，Core 里没有它们的位置）、发布件重发（473 个文件 297.6 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」，token 出现 0 次。**第二批之后头一趟红了 3 条，都不是这一件的**：「详情单集形状（文件页）」「集页首屏（文件页）」「播放回来那一页」—— 三条全是那一带集的虚拟化还没铺完就被量了（读数写着「模型 10 集、实渲染 0 行 / 0 张卡」、「集带 281–281」），重跑即全过，紧接着又跑了一趟确认，两趟都是「全部通过」。**这三条以后再红，先重跑一次再当回归**。
- **截图三张**（都在副屏）：`artifacts/shots/xuid-probe.png`（搜索页，探针那一句灰字提示在屏上）、`resw-diagnostics.png`（设置 → 诊断，那一页 26 处字全部来自资源表：会话／状态／账号／服务器／版本／播放器／进度／mpv 配置／后端／mpv.exe 路径／着色器档位／最近日志／实时跟随）、`resw-detail.png`（详情页，「媒体源」「音频」两个标签来自资源表）。**一处颜色都没动**，所以没按主题逐套拍。

**「以后按 winui 技能执行，除非有更好写法」：优先级改一句、自动化把手补齐（2026-09-06，四道闸门全绿，未提交）。** 他先问「现有 winui 技能有没有和 CLAUDE.md、MEMORY.md 冲突的地方」，我把七份技能连各自的 references 重读一遍、拿实际代码核了一次，报了四处；他一句「我要求以后按照 winui 技能执行，除非你有更好写法」，那既是一条新规矩，也是四处里唯一等他定的那一件（自动化 ID）的答案。

- **`CLAUDE.md` 的优先级条款从「不设例外清单」改成「强默认 + 可辩护的更好写法」。** 原来那句是 09-05 定的绝对令，而它和首要目标那条「规矩是手段不是目的」在同一份文件里各说一套。现在的形状和首要目标对齐：照技能做，除非能按它自己的三条判据说明替代写法更好，然后**必须说出来**（哪一处、为什么、改成了什么、本文件记一行）。加了两句防滑：不是「照做很费事」就能豁免，以及**「还没做到」不算偏离、算欠的活** —— 后面这句正是这次四处里三处的真实性质。
- **「唯一一处压过技能」那一节改名成「更好写法」，现在有三条**：`WUI2010` 那条抑制（原样）；`x:Name` 就是本项目的自动化把手；字号走自家七档刻度而不是内置那六个文字样式。三条都注明是量出来的。
- **`x:Name` 当把手这件事是量的，不是猜的（这一条是这一批的地基）。** WinUI 在没有显式 `AutomationProperties.AutomationId` 时把 `x:Name` 报成 UIA 的 AutomationId —— 用 `winapp ui inspect` 对着真在跑的应用问了一遍，`PaneButton`、`SettingsButton`、`PlayButton`、`OpenButton` 这些直接就是它们的 `x:Name`。所以那 67 个带 `x:Name` 的控件本来就点得着，再给它们加一个属性只是会各自变旧的重复。**`x:Uid` 不是把手**：它只喂资源表，那批只有 Uid 的控件在 UIA 里报的是空字符串（同一趟量到的）。
- **两头都没有的补了 36 处显式 `AutomationProperties.AutomationId`，「一个把手都没有」从 37 降到 0。** 涉及 9 个 xaml：仪表盘 2、详情页 7、诊断 9、筛选栏 1、媒体库 1、服务器 5、设置 1、外壳 2（`NavHome` 和账号菜单里的退出）、登录 8。命名是「页名＋用途」的 PascalCase（`DetailPlayButton`、`ServersDiscoverButton`），不进 `.resw` —— 把手是给测试用的，翻译过的把手就是坏把手，这一句也写进 `CLAUDE.md` 了。**改完当场验了**：`NavHome` 在 UIA 里从空字符串变成了 `NavHome`。
- **两类控件故意不给**：`DataTemplate` 里的 28 处，以及标记里只有一份、屏上有 N 份的 UserControl（`PosterCard`、`EpisodeRow` 共 5 处）。一个 ID 摊在 N 行上让 UIA 变成有歧义而不是可定位，比没有更糟。`EpisodeRow` 那颗悬浮播放键是这条界线上唯一需要想一下的：它正是「不许点卡片中间」那条规矩里的键，但排除它的理由是重复而不是危险。
- **判据是 `tools/scan-automation-ids.js`**（新增，一次性巡检脚本留下当常备工具）：走遍 xaml、按元素分类「已有显式 ID／靠 x:Name／在模板里／在重复控件里／一个都没有」，最后一栏必须是 0。**为什么不能靠分析器**：微软那套里本来就有 `WUI2020`（「可交互控件缺 AutomationId」，警告级，管 Button/ComboBox/Slider/ToggleSwitch/NavigationViewItem/MenuFlyoutItem/GridView 这些），可它在这棵树上一条都不报 —— 改之前整棵树 0 个显式 ID、闸门一照旧 0 警告。**为什么不报没查出来，照实记**：那条规则带 `CompilationEnd` 标记，而同一份分析器的 `WUI2010`（也读 xaml）报得出 20 条，所以不是 xaml 没喂进去。
- **另外两处是欠的活，不是偏离，都记在这里而不是当成现状**。①**不打包**：`WindowsPackageType=None`、没有 `Package.appxmanifest`、闸门三四加 `shot.ps1` 都直接跑 exe，这破了 `winui-dev-workflow` 四条 Critical Rules 里的三条（第四条「不许 AnyCPU」本来就守着）。`CLAUDE.md` 里补了一段，因为原来这件事只写在本文件里，而只读 `CLAUDE.md` 加技能的窗口会看到三条硬规矩被破却没有解释。②**字号里那 55 处写死的数字**：34 处已经引 `Eg*FontSize` 那七档刻度，剩下 55 处直接写数字，其中 18 处是给 `FontIcon` 定字形大小（那是图标度量、不是排版）。**能直接对上现有档位的 32 处（12/14/16/20/34）值得下一批顺手换掉**，剩下 23 处（15×9、18×5、10×4、26×2、40／32／13 各 1）没有对应档位，换过去会改变屏上尺寸 —— **那要他看过再说，别自己四舍五入**。
- **闸门全绿**：Release 单节点 0 警告 0 错误、750 项测试全过（没新增：改的是标记和文档，Core 里没有它们的位置）、发布件重发（473 个文件 297.6 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」、0 项失败、token 出现 0 次。自检那条「读屏与焦点 — 20 个控件报出了名字」照旧（那一关问的是自动化**名字**，这一批动的是**把手**，两件事）。**一处颜色和一个字都没动，所以没按主题逐套拍照。**

**按新的 winui 技能重构：分析器进闸门、调色板改键、CLAUDE.md 与记忆重排（2026-09-05，四道闸门全绿，未提交）。** 用户两句话：「根据新的 winui 技能重新构 CLAUDE.md 和 MEMORY.md」「根据新的 winui 技能重新构整个项目」。第二句是四件欠的活里的头两件，加上一件之前没人提过、但决定了后面每一件成色的事 —— **让技能的判据真的跑起来**。

- **一、微软那套分析器第一次真的进了闸门（`Directory.Build.props` ＋ `tools/analyzers/`）。** `winui-code-review` 那份技能里写着：分析器随 `winui-dev-workflow` 出箱，由那个技能的 `BuildAndRun.ps1` 注进去，而**光跑 `dotnet build` 加载不到它**。这个项目正好不能用那个包装脚本 —— 第一道闸门必须带 `-m:1`（这台机器的坑），而 `winapp run` 只转发 MSBuild 属性、转发不了开关。技能自己给了出路（「把 `<Analyzer>` 和 `<Import>` 写进项目自己的 `Directory.Build.props`」），照做了。**载荷是拷进仓库的，不是指向技能那个文件夹**：插件一更新那个文件夹就被换掉，而一个依赖仓库外文件的构建在干净签出上就是坏的 —— 同 `libmpv-2.dll` 那条 `Condition="Exists"`，没有它照旧构建、只是少了这些诊断。
- **二、它当场报了 39 条，全清了，第一道闸门照旧「0 警告 0 错误」。** 分成两类，处置不一样，这是这一件里唯一需要判断的地方。
  - **19 条 `WUI2011`（「x:Bind 默认 OneTime，加载完就不再更新」）加上了 `Mode=OneWay`。** 逐条查过：全是 `ItemsSource="{x:Bind 某个集合}"`（外加播放器音量条那个 `Maximum`），而**每一个被绑的属性都是 `{ get; }` 加初始化器、实例从来不换**（`ObservableCollection` 靠自己的集合变更通知出货，正是 `winui-code-review` 那条「不许换掉 `ObservableCollection`」的另一面）。也就是说 OneTime 在今天是对的、加 `Mode=OneWay` 只多挂一个永远不会响的通知。**还是加了**：代价近于零，而换来的是分析器这条规则在这份代码上从「有争议」变成「成立」，不用留一条要向下一个窗口解释的抑制。那条不许换实例的约定顺手写进了 `CLAUDE.md` 的分层实践，因为它现在是这 19 处的地基。
  - **20 条 `WUI2010`（「嵌套的 x:Bind 路径中间那一段为空就会崩」）抑制掉了，只在 Shell 那个 csproj 上，理由整段写在 `NoWarn` 旁边。** 这是全项目唯一一条关掉的规则。量出来的三件事规则看不见：详情页四条货架加主页右栏那五个属性是 `CardShelf?` 而且**只在 `Attach` 里赋一次、再也不会被置回空**（注释里本来就写着为什么不能更早存在 —— 它要图片仓库和设置里的卡宽）；第二十条那个 `SubtitleFont` 是 `{ get; }` 且构造函数里就赋值，纯误报；而**生成的 x:Bind 代码本来就逐段判空再往下走，不抛**。规则给的两条改法都更贵：拉平成一个 `EpisodeTitle => EpisodeShelf?.Title` 会在货架自己的 `Title` 变化时变旧（外层对内层的变化不发通知），要写对就得订阅每个货架再转发一遍 —— 十五个属性加一圈订阅，去换一件框架已经在替我们做的事；而给它一个非空的 `CardShelf.Empty` 占位会报出**错的** `RowHeight` 和**真的** `RailVisibility`，把「还没到」变成「到了但不对」，那正是这份代码吃过两次的「界面骗人」。**什么情况下这条抑制就错了，也写在那儿了**：谁把那五个属性在 `Attach` 之后置回空，或者谁在一个真会两头变空的属性上再加一条嵌套路径。
- **三、`Palette.xaml` 的 `Default` 词典改叫 `Dark`（四件欠的活里的第四件，做完了）。** 这不是改个名字：`Default` 是「没有哪份字典对得上当前主题」时 WinUI 的兜底，所以一个深色条目从前是**碰巧**被找到的、而不是被指名要来的 —— 别处写错一个 `Dark` 会悄悄落到它身上还看着正常。改完之后 `App.xaml` 那句 `RequestedTheme="Dark"` 从「让应用级查找落进一个确定的词典」升级成「唯一让它落得进去的东西」，两个按应用级解析的读者（`DiagnosticsViewModel.Resolve`、`PlayerPage.BrushFor`）因此更依赖它，这一层因果写进了 `App.xaml` 顶上那段。**跟着走的有五处**：`ThemeHost.Painted`、自检的 `PaintedDictionaries`、以及自检里**两处** `PaletteKeys("Default")`（`ShellSelfCheck.Run.cs` 的「调色板浅色主题完整性」和 `ShellSelfCheck.Theme.cs` 的「主题角色覆盖」）—— 后面这两处是第一趟自检当场红出来的，也就是说这个键有几个读者不是读代码数出来的、是闸门数出来的。
- **四、新增 `.editorconfig`。** `winui-code-review` 那份 references 里写着「项目的 `.editorconfig` 是代码风格的唯一出处」，还点名三条（文件级 namespace、不用 `this.`、私有字段 `_camelCase`）—— 而这个仓库**根本没有这个文件**，230 个 `.cs` 全靠习惯守着，没有任何东西会注意到第 231 个破规矩。**写下来之前先量了一遍**：文件级 namespace 230/230、用 `this.` 的 0 个、用制表符缩进的 0 个、LF 230/230。所以设成 `warning` 的那几条是代码本来就过的，第一道闸门照旧 0 警告 —— 一条代码本身就在违反的风格规则设成警告，只会教下一个读者忽略警告。`EnforceCodeStyleInBuild` 仍是 false（那一整族 `IDE0###` 没在这棵树上量过，那是另一件活，理由写在文件里）。
- **五、`CLAUDE.md` 按「技能是标准」重排（第一句话）。** 从前那一节是一大段叙述，把优先级、装在哪、四条例外、四件欠的活混在一起；现在是一条明确的优先级（`winui-*` 管 WinUI 3 做法且不设例外 → 本文件管其余 → 自家四份技能管各自的行当）加四个具名小节：第三方技能、我们的四份、**技能无从知道的四条事实**、**本项目唯一一处压过技能自己规则的地方**（就是上面那条 `WUI2010`，指回 csproj 看理由）。另外三处新加的都是这次量到的：第一道闸门带着分析器、最大化窗口上必红的那三行怎么处置、`--dump-ui` 那张 PNG 不合成 Mica 所以判不了颜色。
  - **搬走了两块，都是搬到该在的地方而不是删掉。** ①指针那两大段测量搬进 `embynian-verification`（新增一节「Moving the pointer, and proving it moved」，四种做法列成一张表）—— 那是验证的地形，而 `CLAUDE.md` 只留一句规则加一句指路。②**四件欠的活的状态搬进本文件**：`CLAUDE.md` 开头第一句就写着「在途的活在 PROGRESS.md」，把状态清单留在那儿就是让两份自己去打架。它现在只留「这四件是技能赢的、他 2026-09-05 定的」加一句指过来。
  - **净结果是它变大了，这一点照实说**：22.4 KB，比 HEAD 那份 13.7 KB 大、也比这次动手前那份大。加进去的是上面那些真的会用到的判据；搬出去的约 2.5 KB。他 09-05 说过「给 Memory files 瘦身」，所以**这是一次有意的反向操作，嫌大就砍这几处**：分层那八条实践（道理已在 `embynian-winui-shell`）、开关清单（可以整段搬进验证技能）、首要目标那 2 KB（他自己的话，我不动）。
- **六、记忆库只改了一条，没有新增。** 那个库自己的规矩就是「只留仓库装不下的」，而技能优先级现在写在 `CLAUDE.md` 里、和这个库的加载条件一模一样 —— 照规矩抄过来就是纯重复。**真正需要改的是一句会误导的话**：那条记忆写着「durable facts 都进仓库」，而 2026-09-05 之后**读**一个 WinUI 3 问题的权威不在仓库里（在用户级那套 `winui-*`）。补了一段把两个方向分开：**写**永远进仓库（插件一更新就覆盖），**读** WinUI 做法先看技能。索引那一行跟着改。
- **七、顺手逮到一个一直都在、而且四道闸门看不见的坑：清掉 `bin` 之后第一次发布，发出来的程序打不开。** 这一件不是计划里的，是我为了看分析器诊断跑了一次 `-t:Rebuild`、又为了排查删掉 `bin\x64\Release` 才炸出来的 —— 而炸法非常难认：**构建 0 警告 0 错误、测试全过、发布件「验证通过」，然后 exe 一启动就死在 `App.xaml`**，日志里是 `Cannot locate resource from 'ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml'`。
  - **根因量清了**：`EmbyNian.pri` 必须把框架那三份 `.pri`（`Microsoft.UI.Xaml.Controls.pri` 等）并进来，而并进来这一步的输入是 obj 里那个 `pri.resfiles`，它由「PRI 生成那一刻输出目录里已经有哪些 `.pri`」决定。不打包 ＋ 非自包含时，那三份走 `runtimes-framework` 那套资产，**只在 publish 时才落进目录**，所以对着一个刚清空的 `bin` 构建必然并不到：`pri.resfiles` 是 0 字节、`EmbyNian.pri` 只有 103 KB（并进来是 2.2 MB）、发布件正好小 2 MB。**第二次发布就好了**，因为上一趟把那三份留在了目录里 —— 也就是说这台机器上它一直是好的，只因为 `bin` 里躺着历史遗留。
  - **验的过程记一句，省下一次重走**：先怀疑过 `.editorconfig`、怀疑过分析器那个 `<Import>`、怀疑过调色板改键，三样都一一排掉了（各自关掉重新干净构建，`EmbyNian.pri` 照旧 103 KB）。真正定案的是两个读数：`pri.resfiles` 从 0 变成 408 字节、`EmbyNian.pri` 从 103,000 变成 2,286,120 —— 手动把那三份 `.pri` 拷进 `bin` 再构建一次就复现了「好」的那一侧。
  - **修法是给第三道闸门加一条断言，不是改构建。** `verify-publish.ps1` 现在读 `EmbyNian.pri`，索引里没有 `themeresources` 就当场失败并写清「再跑一次这个脚本就好」。**没去改 MSBuild**：能想到的干净改法要么把包版本号在 csproj 里抄第二遍（那就多一个会变旧的数），要么打开 WinUI 包自己那个 `_AddWinUIAssembliesToReferenceCopyLocalPaths` 开关 —— 而那个开关会把整套 WinUI 的 dll 一起拷进输出，正是这个项目当初拆分包为了甩掉的那一类体积。**一条把「静默打不开」变成「闸门当场红」的断言，比一个更脆的构建修补值钱。** 正反两向都验过：好的发布件过、把索引里那两处 `themeresources` 换成同长度的别的字节就退出码 1。
- **闸门全绿**：Release 单节点构建 0 警告 0 错误（**这个 0 现在含分析器**）、750 项测试全过（没新增：改的是标记里的绑定模式、一个词典键名和三份文档，Core 里没有它们的位置）、发布件重发（473 个文件 297.5 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」，token 出现 0 次。**红过三轮，三轮的原因都不一样，都记在上面**：第一轮 5 条 —— 2 条真错（第三条那两处 `PaletteKeys("Default")`）、3 条是最大化窗口上一律会红的那几条；第二轮 1 条 —— 就是第七条那个 PRI 坑；第三轮全过。最大化那三条按老办法把 `WindowMaximized` 临时改 false 重跑，跑完原样改回去了 —— 这条处置从前只在交接文档里，现在也写进了 `CLAUDE.md`。
- **截图一张**：`artifacts/shots/theme-dark-key.png`（发布件，`--screen 2`，1080×1872）—— 主页整套照旧是深色、绿色强调色在「继续播放 1:23:32」那颗键和右栏被框住的那张卡上。**这一张是必须拍的**：改的是主题词典的键，而自检报告里那句「应用级资源按深色解析」只证明查找落对了地方，证明不了屏上还是那个颜色。**顺带量到一件以后省事的事**：`--dump-ui` 自己留下的 `selfcheck-shell.png` 是一片发灰的浅色，那是 GDI 截图不合成 Mica，**不是主题坏了** —— 判颜色要用 `tools/shot.ps1`，或者读报告里「屏幕像素」那一行（这一趟读的是 `#202327、#202020、#15171B`）。
- **剩下三件欠的活，连各自的拦路石一起记在这儿**，接手的人不用再量一遍：
  - **打包成 MSIX —— 已完成，见上面第一件活（2026-09-05）。** 包打出来并签好名了，`publish.ps1 -Msix -Sign` 一条命令。**还差他在管理员终端里信任一次证书**，装完之后要他看两处：自检报告里「数据目录：…」这一行指到哪儿，以及日志里有没有「已从旧版数据目录迁移设置与缓存」。
  - **界面文字搬进 `x:Uid` + `Strings\zh-Hans\Resources.resw` —— 已完成，见上面第一件活（2026-09-05）。** 193 处搬完、只剩 `ShelfHead` 上那个自定义 `Note` 属性一处。还欠一行自检：拿一个具名控件调 `ToolTipService.GetToolTip` 读回来断言不空，把那 47 处悬停提示钉住。
  - **去掉 `NavigationView`。** 版面只在 `ShellPage.xaml`，但 `ShellSelfCheck.Run.cs` 数它的目的地、`HomeCarousel` 里那个 48 就是 `CompactPaneLength`，两处跟着走。**他已经答了这一件的入口问题（2026-09-05）**：给了三张草图让他挑「没有左边那条栏之后，媒体库从哪里进去」，他选的是**顶部标签栏** —— 标题栏那一排图标（折叠/设置/搜索/后退/前进）右边接一条横向标签：`主页 │ 电视节目 │ 电影`，整个窗口没有左侧那一条、画面从最左边开始。**别再问第二遍，也别改成另外两种**（主页那一排媒体库卡当唯一入口／标题栏一颗按钮弹菜单）。落地时要注意的几件：那条标签用 `SelectorBar` 或 `NavigationView` 顶部模式（`winui-design` 的控件表里「2–3 modes → SelectorBar」），标签内容跟服务器的媒体库列表走而不是写死；标题栏那个挖给按钮的洞是按 `TitleActions` 量出来的矩形，加了标签之后那个矩形要连标签一起算，否则标签按不动（那是「按钮画在标题栏区域里收不到点击」那条老账）；`HomeCarousel` 里那个 48 和 `HomePage` 顶上的 −32 负边距都要重算；自检「导航目的地」那一关按新版面重写。

**暂停/播放徽标去掉灰边、鼠标自动隐藏改走框架那条路（2026-09-05，四道闸门全绿，未提交）。** 用户两句话：「播放页面点击画面暂停和开始的图标要纯白色，去掉灰色」「鼠标停在画面上又不会自动隐藏了，参考其他成熟的开源项目进行修改」。

- **徽标那圈灰边删了，剩纯白一层。** 灰的那一圈是同形、粗一档（22 对 12）的半透明黑描边，屏上是贴着白形状的一道 5 像素灰边；它当年是为「白三角压在白墙上等于没画」加的。**这是他第二次要这件事**（第一次是「不要黑色的圆形边框，只要白色的三角形」，那次去掉的是底板），所以照他的话删干净：`PulseArt.Rim`、`PlayerPalette` 里的 `PlayerPulseRimBrush`、标记里那条 `Path` 一起没了。**代价照实记在 `PulseArt` 的类注释里**：一帧几乎全白的画面上，这颗徽标现在看不见 —— 嫌不行的改法是加一层软阴影而不是把灰边加回来。**方框 136 一个数没动**（那是屏上徽标占多大，他认过的那一档），形状、大小、动画一律不变。跟着一起消掉的还有「一个 `Geometry` 不能同时挂两个 `Path`」那笔账：四个几何对象变两个。
- **鼠标那件的根因终于定住了，而且不在「什么时候藏」那一半。** 规则一直是对的：真片子的日志里有连着藏 11.5 秒和 2 分 05 秒的记录，线程形状=无、显示计数 −1、五个窗口类都换成透明、`ProtectedCursor` 也设上了透明光标 —— 屏上照旧一支箭头。**缺的是「让框架重新念一遍」那一下。**指针压在 XAML 内容上时屏上那只光标由 WinUI 的输入管线画，而它只在**处理指针输入**的时候才去读 `ProtectedCursor`（ElementCursor 那份 spec 的设计说明写着：子类在 `PointerEntered` 这类状态变化时赋值，不像 WPF 那个被频繁轮询的 `OnQueryCursor`）。藏起来恰恰发生在「什么都不动」的时候，于是那支透明光标是个没人读过的值。
- **而那一下原本是空的，两种写法都空。** 老写法是同点 `SetCursorPos`：它只挪坐标、不产生输入，岛一个事件都听不见（每一版报告里的「XAML 事件 0 次」，`Native.MovePointerTo` 的注释早就记着这件事）；更早的写法是零位移 `SendInput`，那个连消息都不产生（自检里印的「真实输入注不进」）。所以「让系统重新问了 N 次」这句话可以为真而屏幕一动不动。
- **改法：一像素出去、一像素回来，走真实输入队列**（`Native.NudgeCursorState`）。净位移是零，所以十赫兹那条轮询读不到移动；两条真会到的 XAML 事件由 `PlayerPage.Moved` 认成「自己的回声」（距离 < 2 且离催出去那一刻 200 毫秒内，`NudgeEcho`），`PollPointer` 里也加了同一条 —— 一拍正好夹在两条腿之间的时候不能把它读成手回来了。**一次藏最多催三下**（`NudgesPerHide`），因为这回是真输入：十赫兹催一整部电影会把系统的空闲计时器一直按着不放。
- **成熟项目那条路本来就在这儿，只是够不着。** mpv、VLC、Kodi 都是「`SetCursor(NULL)` ＋ 在 `WM_SETCURSOR` 里回 TRUE」，这两半这个项目早就有（主窗口的 `Route`、岛的 `IslandDispatch`）——问题是 WinUI 3 的岛根本收不到 `WM_SETCURSOR`（自检量到的：岛消息上百条、`WM_SETCURSOR` 一条没有），光标归框架画。框架那条路的对照物是 Qt 的 `setCursor(Qt::BlankCursor)`、Electron 的 `cursor: none` —— 都是「告诉框架」，而告诉完还得让它去念，这一件补的就是最后这一步。
- **自检第一次量到了因果，而不是只量到「我们喊了」。** 那一关的「真实输入」这一读现在用的就是这同一下催促：报告里从每一版的「注不进」变成**「真实输入到位」**（窗口化那一腿，空事件 6 次）。同一趟里桌面的光标形状也从 `0x10003（系统箭头）`变成了 `0xC90FB0（别的形状）`——一个本进程里的句柄，也就是框架真的换上了别的形状；`ScreenHasNoCursor` 因此判到「没碰它的时候屏幕上没有系统箭头=True」并且**这一读第一次是判的**（不是「不判」）。**这仍然不等于「他那台机器上一定好了」**：判得成要三个前提（岛听见了、前台不是刚抢来的、这会儿挪得动指针），全屏那一腿这次就因为第一条没成而只作参考，而真手真片子的读数只有他能给。
- **闸门全绿**：Release 单节点构建 0 警告 0 错误、**747 项测试全过**（徽标那两条改写：只剩一档描边＋方框不跟着改、画刷表里只剩纯白一支；鼠标那件全在 Shell 层，测试工程够不着），发布件重发（473 个文件 297.5 MB、11 个 GLSL），`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」，token 出现 0 次。**头一趟红过三条，一条都不是这一件的**：`WindowMaximized` 存着 true（用户上一次是最大化关的），于是「窗口尺寸记得住」拿存档比最大化后的现场、「播放帧顶边贴齐」量到最大化窗口天生的 8 像素内缩；「屏幕像素」是 Chrome 挡在前面（报告自己写着「被遮挡，与本进程画了什么无关」）。把那个开关临时改成 false 重跑即全过，跑完原样改回去了。**这三条对最大化窗口一律会红**，不是这一件引入的，也没顺手去动 —— 那是窗口那一摊的判据，改它要连黑边那条老账一起验。
- **截图两张**：`artifacts/shot-pulse-paused.png`（默认墨绿，`--show-osd paused`，两条白竖条）和 `artifacts/shot-pulse-playing.png`（李子紫，`--show-osd playing`，白三角）—— 两张上都看不到灰边了。**光标拍不到**（GDI 截图里没有指针），所以鼠标那件屏上那一半得他自己看，或者用 `tools/cursor-watch.ps1`。

**设置里的字幕那张卡，六件事一起改（2026-09-05，四道闸门全绿，未提交）。** 用户原话：「检查设置中的字幕，提出优化建议」，看完七条建议之后「7 不做，开始 1-6　你决定顺序」。查的过程没播真片子：用 ffmpeg 造了一段本地测试画面配自写的 srt / ass，拿项目自带的 libmpv（v0.41.0-923）离线渲了八帧对比，脚本留在 `artifacts/sub-probe`，六格对比图 `artifacts/subtitle-settings-evidence.png`。

- **两处「设置里选了、屏上没有」的真洞，都是 mpv 改过语义而我们没跟上。** 一、**底板**：mpv 0.39 起 `sub-back-color` 和阴影颜色合成同一个值，画成阴影还是画成底板全看 `sub-border-style`，而这个选项一次都没发过 —— 于是老的「背景颜色 + 背景透明度」两行永远只是给阴影上色，任何颜色任何不透明度都没有板。新增 `PlaybackSettings.SubtitleBackStyle`（关 / 贴着字的底板 / 整行不透明方框），出厂仍是关，屏上样子不变。二、**外观应用范围**：`sub-ass-override` 默认 `scale`，ASS/SSA 字幕只认 `sub-scale`，字体、字号、加粗、颜色、描边、阴影、底板一律被字幕自带样式盖掉 —— 番剧和压制组的内封字幕大量是 ASS，也就是说那九行对它们一个字都改不动，而卡片上从没说过。新增一行「外观应用范围」（跟随 / 强制），出厂跟随。
- **字幕编码出厂从 `gb18030` 改成自动识别，并加了 schema v11 把存着的 `gb18030` 清掉。** 填了具体编码就把 mpv 的检测关了：Big5 的繁体字幕被按 GB18030 读成「硂琁□∽代刚□辊」，而 `auto`（uchardet，编进了自带的 libmpv）读得对；简体 GBK 那一半两种读法逐字节相同，所以是净赚。同 v8 的图形接口、v9 的音频同步 —— 没人选过的值不算偏好。v4 那一步里补 `gb18030` 的那行顺手删了，不然是「补一遍再清一遍」。
- **新增一行「字幕缩放（%）」**（50–300，出厂 100 不发）。字号那一行对 ASS 无效，而 `sub-scale` 是 mpv 默认就放给 ASS 的唯一尺寸旋钮 —— 不改渲染样式也能把 ASS 字幕调大，只有这一条。播放器菜单里那个 ±0.1 照旧，只是现在有能存下来的默认值了。
- **外观改一行，立刻作用到正在播的片子。** `PlaybackService.ApplySubtitleStyleAsync`，交给设置页的是这一个方法而不是 `PlaybackService` 本身（那一页还是不能播、不能停、不能跳）。**每个选项都显式发一遍，包括改回「不设置」的**：起播时「不发」等于让 mpv 默认站住，在跑着的播放器上却等于「沿用刚才那个值」，所以不再指定的那几个是去问 `option-info/<name>/default-value` 要默认值再发回去 —— 问 mpv 而不是在代码里抄一张默认值表，因为这个项目已经抄错过（字号写 55，实际 38）。字幕编码和图形字幕拉伸不在这条路上（一个是解码那一刻用的，一个要看片源画幅），卡片上就写「下次播放生效」。
- **`sub-font` 从此只有一个写入方。** 它以前单独挂在 `PlaybackRequest.SubtitleFont` 上、走命令行，和其余十个字幕选项两条路；现在一起进 `PlayerOptions`，那个属性、`PlaybackPlanner.ResolveFont` 和两个后端里的那行都删了，「族名还是文件路径」的规则搬去 `FontFamilies.Resolve`。
- **三句和实际不符的话改了**：描边大小的「不设置」写着 mpv 默认 3、实测 1.65；`sub-font-size` 注释写 55、实测 38；底板颜色的「不设置」写着「沿用字幕自带样式」、实际拿的是 mpv 那个约七成不透明的黑。另外**字号那一行的规则收进 `PlaybackSettings.ClampFontSize`**，设置页和迁移用同一条 —— 以前只有迁移夹，屏上填 5 这一次播放就是 5，下次启动才变 16，用户看见的值既没留下也没被拒绝。
- **闸门全绿**：Release 单节点构建 0 警告 0 错误、**747 项测试全过**（新增七条：底板要发 `sub-border-style` 才画得出来、外观应用范围只在强制时发、缩放 100 不发别的换成小数、实时选项表覆盖得住外观能发的每一个、v11 迁移三条），发布件重发（473 个文件 297.5 MB、11 个 GLSL），`--self-check --dump-ui` 退出码 0、末行「结果：全部通过」、token 出现 0 次，报告里「字幕」这张卡从 14 行变 17 行、82 个行容器全部落到树上。
- **截图一张**：`artifacts/settings-subtitle.png`（`--show-settings 字幕`，设置窗 1086×753）—— 卡片标题写着 17 项，新那行「外观应用范围」和它的说明在屏上。**下半张拍不到**：字体那一行自带一个 252 高的字体列表，把后面十二行顶到窗外，而 `--scroll-half` / `--scroll-end` 只管详情页。所以「17 行都真的画出来了」是自检那三行读数作证的，不是照片。**一处颜色都没动**，所以没按主题各拍一张。
- **没做第七条**（字幕位置默认值、双字幕中英对照、去听障字幕标注），他说不做。

**继续观看那一栏改成点击翻页（2026-09-05，四道闸门全绿，未提交）。** 用户原话：「把继续观看改成点击翻页的」。说的是第一屏右边那一列（一次装 12 项，而那一栏只放得下三张卡）。

- **屏上：滚动条藏起来，上下两头各一条翻页条，一次翻整整一屏卡片。** 就是横带那一套翻过来 —— 横带的箭头是「沿滚动方向 38 宽、横跨卡片图片的高」，这一列滚的是竖向，所以它的翻页条是 38 高、横跨这一栏的宽（顺带比一颗 38×38 的箭头好按）。指针进到这一栏才浮出来，翻不动的那一头收着，一屏放得下就两条都不浮。
- **翻多远、翻过去落在哪、哪一头该露出来 —— 三条规则一条都没新写**，直接用横带那一套（Core 的 `CardStrip`，`CardStripTests` 钉着），竖着用只是把「宽」换成「高」。所以这一件没有新单测：新长出来的东西全在 Shell 那一层，而测试工程够不着它。
- **没把 `ShelfStrip` 拿来竖着用，这是我判的、他可以否。** 那个控件从头到尾是横的（`HorizontalOffset` / `ScrollableWidth` / `ViewportWidth`、贴左右两边的两颗箭头、左右方向键上那一整套虚拟化焦点补救），加一个 `Orientation` 就是十几处三元判断 —— 而它同时是主页每一排和详情页四条带的唯一实现，一处判断写反就是六个地方一起坏。这一栏要的只有「点一下翻一屏」，所以借规则、不借控件。**代价说清**：翻页条的样子成了第三份局部副本（`ShelfStrip.xaml` 一份、`HomeBanner.xaml` 一份浅一档的、`HomePage.xaml` 这一份），三支画刷加一个样式；并成一份要同时改三个控件、连详情页那四条带一起验，那是另一件活，理由写在标记里了。
- **滚轮一个字没动**：这一栏照旧滚得动（那是 `ScrollView` 自带的），改掉的是「要看后面几张就得去拖那条几像素宽的滚动条」。他要连滚轮一起改成只认翻页，那是另一句话。
- **悬停走 `HoverWatch`（横带和卡片那张十赫兹的网），不是裸的 `PointerExited`**：指针从这一栏直接移出窗口时那个事件报的位置还在栏里，光信它就是「翻页条留在屏上不走」——「鼠标移出窗口后不会自动恢复」那条老账。这一页离开时把它摘掉，不然十赫兹那一拍会继续问一个量不到的矩形。
- **自检多一关「主页右栏翻页」**：滚动条真藏着（谁改回 `Auto`，屏上又多一条可拖的东西，而别的读数一个都不会变）、标记里那个间隔和算步长用的常数是同一个数、**悬停两档各摆一次**（悬停时至少浮出一条、不悬停时两条都收）、点一下真的要到规则说的那个位置，加上那张悬停网量得到自己的矩形。**悬停那两档是摆出来的、不是等现场** —— 自检跑的时候真鼠标可能正停在那一栏上（第一趟报的就是「现场指针在这一栏上」，最后一趟报「不在」），拿现场当判据就是把它读成回归，那正是「鼠标真等两秒就藏」那一关的教训。整页有没有跟着滑只报不判：翻页走的是这一栏自己那个 `ScrollView` 的 `ScrollTo`，冒不到外面去，而真要验异步冒上去的那种请求得隔一拍再量，那条路由「点卡片不挪页」盯着。
- **新增开关 `--show-rail`**：把那两条翻页条摆到屏上留着好拍照，同 `--show-menu` / `--show-osd`。**先试过挪真指针**：`SetCursorPos` 把它挪到那一栏上是挪成了（两趟都成，从终端跨到另一块屏），可照片上翻页条还是没有 —— 用户的手在同一只鼠标上，快门开之前指针又走了（同一批照片里轮播自己的翻页箭头反倒亮着，所以指针事件是进得到 XAML 岛的）。`CLAUDE.md` 那条坑跟着补了一句：`SetCursorPos` 带位移是真能挪，但**别拿它去拍悬停**，悬停的东西给一个 `--show-*` 开关。
- **闸门全绿**：Release 单节点构建 0 警告 0 错误、**740 项测试全过**（没新增，理由见上面第二条）、发布件重发（473 个文件 297.5 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」，token 出现 0 次，**一次过**（这一批三趟自检都没红）。自检那一行的读数：自检窗口上右栏看得见 326、还能滚 995、一张卡连间隔 210、一页 1 张 —— 他那台窗口高得多，一页是 3 张。
- **截图一张**（`--show-rail`，副屏 1066×793）：`artifacts/shots/rail-pager.png` —— 右栏没有滚动条了，下沿浮着一条带下箭头的翻页条、宽度正是一张卡；上面那一条不在，因为这一栏正停在头上（规则说的）。**一处颜色都没动**（翻页条那三支画刷是写死的 ARGB，跟主题无关），所以没按主题逐套拍。

**轮播四条边缘渐变淡一档、面积收到三分之一上下（2026-09-05，四道闸门全绿，未提交）。** 用户原话：「轮播界面的渐变弄淡一些，面积弄少一些」。**这是同一天第三趟碰这几层渐变**（原先三层暗罩 → 按他一句全删 → 按他下一句只压四条边 → 现在这一档），前两趟见下面第三、第四件活。动的只有 `HomeBanner.xaml` 里五个写死的 ARGB，加 `HomeBanner.xaml.cs` 里一句新的报告读数。

- **左、下、右三条各淡一档、各收一截**：贴边那一头的浓度收到原来八成上下，伸进来的深度收到七成上下 —— 左 70%/0.50 → **55%/0.33**、下 76%/0.50 → **60%/0.35**、右 35%/0.28 → **29%/0.19**（前一个数是贴边那头的不透明度，后一个数是走到哪儿全透明，按带的宽或高算）。
- **顶上那条 120 高的一个数都没动，这是我判的、他可以否。** 它不是画面上的装饰，是系统自己画的那三颗窗口按钮和页眉右边那行读数唯一的底 —— 那三颗我们只换得了墨色、换不了形状，淡到一张顶上接近纯白的剧照（当年逼出这一条的是「卓别林」那张）上那行读数读不出来，它就白垫了。**他要连它一起淡是一句话的事**，已记进下面「等你在屏幕前确认的」。
- **自检那条判据没跟着收紧，这是有意的**：`Rims` 照旧只守「贴边那头 alpha > 0x40、半张之前散尽、半张之后一点不压」——**它钉的是「只压边缘」这件事本身，不是他挑的浓度**，跟着收紧等于每次他调一眼颜色就要改一次断言。**换成让报告把这两个数说出来**（新增 `HomeBanner.RimRead`）：那一行现在写「另外三条各压一条边…：下 60%→0.35、左 55%→0.33、右 29%→0.19」。没有这一句，屏上淡一档而报告一个字不动，就等于没人看着他挑的这两个数。贴哪条边是问渐变自己从哪个角起、不按标记里的次序猜，所以三层换了次序这一读照旧说得对。
- **右边那 29% 离下限只剩一档**（`Rims` 那句 alpha > 0x40，也就是 25%）：他要再淡，得连那句断言一起改，否则自检当场红。这条写在 XAML 那一层的注释里，就在那三个数旁边。
- **闸门全绿**：Release 单节点构建 0 警告 0 错误、**740 项测试全过**（没新增：改的是标记里五个 ARGB 加一句报告读数，Core 里没有它的位置）、发布件重发（473 个文件 297.5 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」，token 出现 0 次。**红过一趟且不是这一件的**：「鼠标真等两秒就藏」——`不符：` 那行写的是「全屏没碰它的时候屏幕上没有系统箭头」，而同一行里写着「静止 2000ms 就藏了、轮询问出 1 次移动（也就是全程没人碰）」，真鼠标停在自检窗口里面就会这样，重跑即过。
- **截图两张**（都在副屏，1066×793）：`artifacts/shots/banner-rim-lighter.png`（第一张幻灯片「地球之夜」，深底剧照，看的是「淡了之后画面露出来多少」）和 `banner-rim-lighter-bright.png`（走到第二张那张粉色动画剧照 —— **这张才是判据**，拿它比上一版的 `banner-left-rim-bright.png` 一眼就看出淡了多少、缩了多少）。**一处颜色角色都没动**（四层全是写死的 ARGB、不跟主题走），所以没按「改了颜色每套拍一张」逐套拍。
- **他该看一眼的代价，已经写在汇报里**：那张粉色剧照上，简介第一行的右半截正好压在最亮的那片脸上（「享受着约会，新学」那几个字），淡了之后那里是这一屏最弱的地方 —— 截图上还读得出，但已经到边缘。嫌读不出的改法是把左、下两条各加回一档（比如左 55% → 62%、下 60% → 68%），面积不用动。

**从播放回来那一趟，集列表掉到纸上（2026-09-05，四道闸门全绿，未提交）。** 用户原话：「点击开始播放后点击左上方的返回，集列表会跑到下方去」，附两张截图 —— 坏的那张里那一带集掉在第一屏之后（头图那一叠字还被切在视口上沿外面），好的那张是它该在的样子：剧情说明底下、压在剧照上。

- **根因是一句 `EpisodePanel.Parent is Panel`（`DetailPage.PlaceEpisodes`）**：那个属性要等这一页 `Loaded` 之后才有值，而这个方法最要紧的那几次调用都可能早于它 —— 那一带集摆在图上还是纸上跟着「这一页讲的是哪一类东西」走，而那句话是 `DetailViewModel.Preview` 和 `Apply` 喊出来的，两次都在 `OnNavigatedTo` 那一趟里。`Parent` 是空的时候整个 `if` 不成立，于是**搬家悄悄没发生**，那一带集留在标记里写的那一层（纸上的第一块）。
- **为什么只有播放回来那一趟会中招**：停止播放时外壳先照着当前页面重新导航一遍（`ShellPage.RefreshActive`，服务器上的已看和断点刚变过），**之后**才把导航外壳放回来（`ShowPlayer(false)`）—— 新那一页整个 `OnNavigatedTo` 加两次通知全在一棵收着的树上跑完，`Loaded` 排在它们后面，一次都没赶上。正常那一路（点一张卡片进来）之所以看着没事，只是因为完整条目那一趟往返通常慢过 `Loaded`，第二次通知刚好落在「已经有 Parent」那一侧：一场谁先到的赛跑。诊断是拿临时日志把两次通知和 `Loaded` 的先后打出来看的（临时日志已删）。
- **改法一行半**：新增 `DetailPage.EpisodeHost` —— 「现在在哪一层」问 `HeroTail.Children` / `BodySheet.Children` 自己，不问元素的 `Parent`；`PlaceEpisodes` 和 `ReturnRead` 都走它。**全仓只有这一处读过 `FrameworkElement.Parent`**，所以这个坑没有第二个落点。
- **自检多一关：播放回来那一页**（`DetailPage.ReturnRead` ＋ `ShellSelfCheck.ReturnFromPlayer`）。它走真的那三步 —— 播放层摆上来并接过焦点、收起导航外壳、`RefreshActive()`、再把外壳放回来 —— **一个字节都不放**，用户服务器上的已看和断点一处不碰。两条都判：那一带集在规矩说的那一层上、页面没有自己滚下去。修之前这一关当场红（「那一带集在纸上（规矩说图上）、从内容 752 起」），修之后是「在图上、从内容 571 起、页面滚在 0」。第 2 阶段的预算跟着从 60 拍加到 72（后面几档顺移）。
- **他截图里那半截「页面自己滚下去」我没能在自检里复现**（修前修后都读到「滚在 0」，连播放层接过焦点那一版也是）。最可能是那一带集掉到第一屏外面之后、焦点或框架的「露出来」把整页拽下去的连带反应，也可能就是他自己滚下去找那一带集。新那一关同时钉着「滚在 0」，真还有第二处的话下一次它会自己红。
- **闸门全绿**：Release 单节点构建 0 警告 0 错误、**740 项测试全过**（这一件没加单测：改的是「元素坐在哪块板上」，Core 里没有它的位置）、发布件重发（473 个文件 297.5 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」，token 出现 0 次。**红过一趟且不是这一件的**：「鼠标真等两秒就藏」—— 真鼠标停在自检窗口里面就会红（上一次运行的光标探针把它挪了进去），把指针挪开重跑即过。
- **截图一张，看过就删**（`--show-episode`，副屏 1457×793）：那一带集紧接在剧情说明底下、压在剧照上，纸面还在第一屏外面。一处颜色都没动，所以没按主题逐套拍。

**轮播那块字挪到左下角、徽标顶在剧名上、四条边加黑色渐变、晴昼主题删掉（2026-09-05，四道闸门全绿，未提交）。** 用户三轮话：「1.把红框框出来的移到右下角 2.删掉晴昼主题」，看过截图之后「不对，移到左下角，然后把徽标移到剧名上面，然后给轮播页面边缘加上黑色的渐变」。红框圈的是主页轮播大图上那一整块字：片名、集号那行读数、两行简介、播放和详情两颗键。

**先记下这一件里唯一的返工，因为树里好几处注释写着「走过两趟」**：第一轮真的挪去了右下角（字和键一起靠右、徽标让到左下角），拍给他看之后他改主意要左下角。**屏上现在是左下角那一版，右下角那一版一行都没留**；两趟之间只有一件事是共同的 —— 「贴着下角、不再居中下沉」，所以下面第三条那条删掉的规则两趟都不需要。

- **一、字块贴着左下角**（`HomeBanner.xaml` 的 `Info`：`HorizontalAlignment=Left`、`VerticalAlignment=Bottom`、边距 `60,0,0,46`），字和两颗键都靠左（也就是不设 `TextAlignment`，走默认那档）。
- **二、徽标从「压在角上的一张独立的图」变成字块的第一行，顶在片名头上**，抄的是详情页头图上那块 `TitlePlate`（这个应用里另一处「徽标顶在片名头上」）。**上限跟着收成 280×56**（右下角那一版是 240×86）：一叠五行东西，而带子最窄那一档只有 321 高（900 宽的窗口减掉侧边栏和右栏再按 16:9 算），整块字得放得进去 —— 实测 86 那一档字块 262 高，收到 56 之后 232 高。徽标抬起来的方向和延迟也跟着改（从「反过来从上面落下 12、延迟两拍」改成「和字一样从下面抬 22、延迟 0」）：它现在是这一叠的第 0 拍。
  - **`HomeBanner.PlaceLogo` 整个删了**（连 `LogoInset` / `LogoBaseline` 两个常数）。那个方法算的是「剧照居中之后带子和图之间那两条留白」，好让徽标贴住图的角；徽标进了字块之后位置由布局给，那两条留白（本来也只剩取整的一点点）没人再需要。`HomeCarousel.WindowAspect` 的注释跟着从「三处用」改成「两处用」。
- **三、`HomeCarousel.InfoDrop` 连它的两条单测一起删了**（从前那条「字从正中往下沉多少，按带高算」的纯函数，加字块外面那个 `InfoShift`）。贴着下角已经是「字在下半张」的极端，再沉就沉出带子 —— 留着就是一条屏上没人兑现的规则加两条还绿着的测试。删的理由和替代写进了 `HomeCarousel.MinHeight` 的注释：那个下限（240）从此是字块唯一的保险，而「字块头顶还剩多少」由自检在屏上报（见第六条）。
- **四、四条边各一道黑色渐变，画面正中不压。** 这是他删掉那三层暗罩（同一天早些时候）之后留下的那笔账的解法，也是他自己给的答案。四层：顶上那条给标题栏垫底的**没动**（写死 120 高、贴上沿），新加左、右、下三条铺满整条带、按比例取渐变的。三条共用一个形状：**贴边那一头最浓，到半张处已经全透明、半张之后一点不压** —— 渐变都调过头让 `Offset 0` 落在自己那条边上，三层因此长得一样，好比对也好断言。浓度按各自的活分：左边最浓（`#B3`，字块整块站在这一边，也是徽标和片名唯一的靠山）、下边中等（`#C2` 起，但半张就散尽，管的是简介、两颗键和底边那排小横条）、右边最薄（`#59`，只把图的右沿收进右栏那道竖缝里）。
  - **「半张处全透明」这条界线是我自己那条断言当场逼出来的**：第一版下边那层伸到 0.72 才透明（想把整块字连徽标一起罩住），断言写的是「半张之前散尽」，于是自检当场红。**红得对**——伸到 0.72 就已经不是「只压边缘」而是把画面压掉了大半张，正是他要删掉的那三层的样子。所以改的是渐变，不是断言：下边那层收回半张以内，字块上半截（徽标和片名）交给左边那一层。
- **五、自检那一关的暗罩判据重写。** 从前是「这一格里只允许一个 Border，而且必须写死高度、贴着上沿」（那是「渐变全删掉了」那一版的守卫）。现在判的是**每一层的形状而不是层数**：四层，其中恰好一层写死高度、贴上沿、由浓到淡（顶上那条），另外三层不设高度、且各自过 `Rims`（贴边那头 alpha > 0x40、半张处有一个 alpha 0 的档、半张之后所有档都是 0）。**被删掉的那三层里横着那一层是从左沿一路淡到右沿的，它过不了 `Rims`** —— 所以「谁把整层暗罩请回来」这件事照旧当场红，而这一关同时还能说出「四条边缘渐变还在不在」。
- **六、自检那一关另外多两条、改两条**：新增「字块贴左下角、三行字靠左，徽标是它的第一行、顶在片名头上」（读的是标记里设死的对齐和 `Info.Children` 的前两位，不量坐标 —— 徽标只要被谁挪回角上就红）和「字块离下沿 46（横条那排占掉 34）」；屏上那句从「字块高 166 往下沉 60」改成「字块高 232 离下沿 46（**头顶余 135**）」—— 头顶那个余量是新的看点，它变成负数就是片名被带的上沿剪掉了。
- **七、两个常数各自有话说。** 字块离下沿 46（`InfoBaseline`）必须大于底边那排小横条占掉的 34（`DotsBaseline` 18 加点击区 `DotHit` 16，后者顺手从一个字面量提成了常数），否则播放键正压在横条上 —— 这条不变式由 `Probe` 拿三个常数当场对一遍。左边距 60 照旧要让开翻页箭头那条窄栏（46 加一口气）：「翻页的按钮会挡住字体」那句话仍然成立，因为带子缩到下限那一档时字块的上半截正好爬到带的竖向正中、也就是左箭头那一行 —— **让开的是列，不是高度**。
- **八、晴昼（`daylight`）从主题表里删掉，五套全是深色。** 只删了那六行数据：**`Make(dark: false)` 那条浅色推导和 `UiTheme.IsDark` 都留着**，理由写在 `UiThemes` 的类注释里 —— 外壳里有十几处读 `IsDark`（元素树的 `ElementTheme`、窗口边框的深浅、几个对话框、设置页那排色板），删掉那条路等于把「再加一套浅色」从改六行数据变成改十几个文件。
- **代价一句话说清，别当成漏做**：**浅色那一套曾经是「外壳里有没有漏下一处写死的深色」唯一的照妖镜**（写死的 hex 在四套深色下都看着没事），删掉之后这类毛病没有东西逮得住了。跟着改的文档：`CLAUDE.md` 那条主题规矩、两个技能（`embynian-verification`、`embynian-winui-shell` 连它的 description）、`docs/开发与验证.md` 里那段「`--theme daylight` 拍一张」、本文件底下验证入口那一段、`画质档位重构-任务书.md` 的验收清单。**`UiThemes` 里那个 0.52（最淡的字）的注释也改了** —— 那个数当年是被晴昼的 2.88:1 逼出来的，剩下五套都宽裕得多，所以它现在没有测试守着。
- **老设置文件不会卡住**：`SettingsMigration.Normalize` 把认不出来的 id 写回默认那套，`SettingsTests` 里新钉了一句「存着 `daylight` 的会落到 `emby-dark`」——那条测试原先正是拿 `daylight` 当「认得出来的 id」的例子，所以它同时是这次唯一一处会真编译不过的地方。
- **闸门全绿**：Release 单节点构建 0 警告 0 错误、**740 项测试全过**（`InfoDrop` 那两条删掉；`ThemeTests` 那条「深浅两边都有」翻成「现在全是深色的」；`PlayerPaletteTests` 里拿晴昼当靶子那句删掉 —— `PlayerPalette.Ink` 恰好和默认那套的正文色同色，换成任何一套都是句假话；`SettingsTests` 新增一句；`StartupArgsTests` 里那个当占位用的 id 换成 `midnight`）、发布件重发（473 个文件 297.5 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」，报告里「主题目录 — 共 5 套」、「主题色板 — 5 块（主题表 5 套）」、「画面上的暗罩 4 层（顶上给标题栏垫底那条 120 高，另外三条各压一条边、半张之前散尽，正中不压）」，token 出现 0 次。**红过两趟**：一趟是上面第四条那条渐变（真错，已修），一趟是「鼠标真等两秒就藏」——`不符：` 那行写的是「全屏没碰它的时候屏幕上没有系统箭头」，而同一行里写着「轮询问出 1 次移动（也就是全程没人碰）」，重跑即过；那一趟他正在这台机器上打字。
- **截图三张**（都在副屏，1066×793 和 1018×753）：`artifacts/shots/banner-left-rim.png`（主页，第一张幻灯片就是他上次停下的「地球之夜」：徽标顶在片名上、整块贴左下角、左下两条边暗着而画面右半边原样露着）、`banner-left-rim-bright.png`（走到第二张那张粉色动画剧照 —— **这张是渐变管不管用的唯一凭据**：同一张图在加渐变之前那块字几乎读不出来，见 `banner-corner.png`）、`theme-five.png`（设置 → 界面：五块色板，晴昼不在了）。**一套主题的色值都没动**（四层渐变全是写死的 ARGB，不跟主题走），所以没按「改了颜色每套拍一张」那条规矩逐套拍。
- **他看得见、我没动的一处**：五块色板底下现在都写着「深色」，五个一样的字。删掉那个标签是一行的事，但那是他没提的可见改动，等他说。

**轮播四改：取继续观看＋最近添加、设置里能关、黑色渐变去掉、右栏自动框出台上那一张（2026-09-05，四道闸门全绿，未提交）。** 用户四句话：「1.首页的轮播图有继续观看就用继续观看，没有或者继续观看不够就用最近添加 2.在设置中新增关闭轮播图的功能 3.轮播图上的黑色渐变去掉 4.轮播图滚动到对应媒体时右边要自动框出对应媒体」。

- **一、幻灯片改成两个具名来源：继续观看先上，不够由最近添加补齐到八张**（`HomeCarousel.Slides` 从「收一串按版面排好的列表」改回「收两排」）。**这一句推翻了同一天早些时候的「按版面次序取」**（见下面第二件活的第四条）—— 他这一次把两个来源直接点了名，所以**接下来看和每个媒体库自己那一排从此都不参加**：前者本来就是「继续观看的下一集」、和第一排讲的是同一件事，后者是最近添加按库切开的一份。轮播现在有自己的开关（下一条），所以它取哪两排也不再跟着那张拖拽表上的勾走。「不够」是按**筛完之后**算的（要有宽图、一个剧集只占一张），所以继续观看里六集全是同一部剧时只占一张、剩下七张由最近添加补。
- **二、设置 → 主页 多一行「显示主页轮播大图」**（`UiSettings.ShowHomeBanner`，装机默认开，摆在那张拖拽表**上面**，因为它管的是整个第一屏）。**关掉之后第一屏整个没有**：大图和它右边那一栏继续观看一起收起，继续观看回到下面横着排的那一叠里、按它在版面表上的位置站着，整页就是一叠普通的货架（截图 `home-banner-off.png`）。这一档由 `HomeViewModel._banner` 一个字段管着三处：造哪一排的卡（右栏那一档卡宽封顶、字走浅墨）、挑不挑右栏、造不造幻灯片。**每次 `BuildShelves` 重读而不是 `Attach` 时取快照** —— 它和拖拽次序、勾选同一张表，改完当场生效（`ShellPrefs`）。
  - **顺带修掉一处一直都在的账**：这一页顶上那个 −32 的负边距（把 `ContentHost` 留给标题栏的 32 像素顶回去，好让图贴住窗口顶边）从前写死在标记里，现在由 `HomePage.SyncBleed` 按「顶上那一块到底是不是一张图」给。**没有图的时候那 32 必须留着**，不然「HOME / 主页」那块牌子会塞进标题栏里 —— 而「没有图」从此有两种：设置里关掉了，以及服务器上一个带宽图的条目都没有（后一种以前就会这样，只是没人撞见）。`BleedRead` 那一关跟着两档都判。
- **三、压在剧照上那三层暗罩整个删掉**（横向托字那层、右边沿那层薄的、竖着压下沿那层）。`HomeBanner.ShadeEdge` 于是只剩「徽标贴着图的右下角」这一件事，改名 `PlaceLogo`（一个叫 ShadeEdge 而什么都不 shade 的方法，是下一个窗口去找那三层的原因）。**顶上那条 120 高的没删**：它垫的是标题栏那五颗按键、系统那三颗窗口按钮和页眉那两行，而那三颗是系统画的、我们只能换墨色 —— 去掉它，一张亮画面上那八颗按钮就都看不见了。他要连它一起去掉，删掉 `HomeBanner.xaml` 里那个 `Border` 就行。
  - **代价看得见，已经拍给他了**：字块（片名、集号、简介）和底边那排小横条现在直接压在画面上。深底的剧照没事（`home-banner-nogradient.png` 那张「地球之夜」左边是暗的），**亮画面上简介那两行读不出来** —— `home-banner-nogradient-daylight.png` 那张（走到第二张，一张粉色动画剧照）就是。**两条不用把渐变整层放回来的救法**：给那几行字加投影，或者只放回一层很淡的。等他挑。
  - `HomeBanner.Probe` 里那两条断言（三层的下边距、竖着那层三段越往下越浓）换成一条**数 Border**：这一格里只允许剩一个，而且必须是「写死高度、贴着上沿」那一个。被删的三层都是「不设高度、铺满整条带」的样子，所以谁把它们加回来当场红。报告里现在印「画面上的暗罩 1 层（只剩顶上给标题栏垫底那条 120 高，画面本身一层不压）」。
- **四、右栏自动框出台上那一张**（`HomeCarousel.MatchIndex` 定谁对应谁、`CardItem.Framed` 是那一位、`PosterCard.Paint` 把整圈胶片格换成强调色 —— 和指针悬停、键盘焦点同一根线，三个旗子或起来）。屏上就是右栏里那一张卡的边框变绿，八秒换一张时框跟着走（截图两张都看得见）。
  - **对应关系两档**：先认同一个条目（id 一样），认不到再认同一个剧集。松的那一档是给「幻灯片来自最近添加」那种情况留的 —— 那时同一部剧两边各是一集，框右栏里那一集才是「对应媒体」。严格的那一档永远赢，所以要走完整个列表才交答案。四条单测钉着。
  - **框在屏外没有意义，所以还要滚**：右栏一次装 12 项、只放得下三张卡，而幻灯片有八张。滚那一下**在右栏自己那个 `ScrollView` 里做完、按索引算不按元素量**，两条都照 `ShelfStrip` 的先例（`CardStrip.RevealFor` 直接复用，一行都没新写）：不用 `StartBringIntoView` 是因为那种请求会一路冒到整页那个竖着滚的 `ScrollView` 上，于是整页往下滑一大段 —— 「点击封面之后会先跳转到页面下方」就是那件事，自检里「点卡片不挪页」那一关盯的也是它；按索引算是因为目标容器可能还没生成、更没量过，问它自己在哪儿问到的是 0。
  - **`Framed` 绑在卡片自己那一位上，不由页面挨个去设**：卡片容器是回收复用的，页面去设的话回收那一下会有一张不该框的卡带着框回来。`PosterCard.ProbeFrame`（自检读那圈线的两种颜色）跟着多存一样：主页右栏里被框着的那一张本来就亮着，不先摆平它，读到的「静止色」就是亮的。
- **报告里多了三处读数**：`主页版面` 前面加一句「轮播开/关」（它一关，继续观看就从右栏变成横着的一排，而别的读数一个都不会说这件事）；`主页轮播` 那一行末尾加「右栏 『地球之夜』对上右栏第 1 张，框着 1 张（右栏 7 项，滚到 0）」；`主页大图贴边` 两档分开判（有图上沿落在 0，没图落在 32）。
- **闸门全绿，一次过**：Release 单节点构建 0 警告 0 错误、**742 项测试全过**（幻灯片那八条按新签名重写、新增四条 `MatchIndex`、新增一条「恢复默认之后轮播是开的」；这个数里有海报宽度那一批的改动）、发布件重发（473 个文件 297.5 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」，报告里 token 出现 0 次。
- **截图四张**：`artifacts/shots/home-banner-nogradient.png`（默认主题，第一张就是他上次停下的「地球之夜」、播放键写着「继续播放 1:20:32」，右栏第一张框着）、`-daylight.png`（晴昼那套，走到第二张，同时是「亮画面上简介读不出来」的证据，右栏第二张框着 —— 也就是同一个条目对上了）、`settings-home-card.png`（主页卡两项，新那一行开着）、`home-banner-off.png`（关掉之后的样子）。**没动颜色角色表**（删的三层和顶上那条都是写死的 ARGB，不跟主题走），所以没拍另外四套主题。
- **~~还剩他在屏幕前才定得了的一件~~：亮画面上那两行简介要不要救，救的话选投影还是很淡的一层渐变。他当天就答了，答的是第三种 ——「给轮播页面边缘加上黑色的渐变」，见上面第一件活第四条，已经做完。**

**封面顶到最上方、上下不留黑边；继续观看那一栏缩小；轮播按版面次序取（2026-09-05，四道闸门全绿，未提交）。** 用户两句话：「封面固定到最上方，上下不要有黑边，把继续观看缩小一些」「轮播图优先使用排第一个的（目前排第一个的是继续观看，没有继续观看就往下顺延）」。第一句正是上一件（见下面第二件活）留给他的那两条路里的第一条 —— 他用自己的话挑了它。

- **一、带高从「一屏」改成「照带宽按 16:9 算」**（`HomeCarousel.Height` 多收一个带宽参数）。于是图正好铺满大图那一块、贴着窗口顶边、上下一条底色都不留，而**第一屏剩下的那一截露出下面第一排横着的货架** —— 那同时也变成了「底下还有东西」唯一的招牌（从前是滚一下才有）。剧照那两层同时加了 `VerticalAlignment="Top"`，管的是取整剩下的不到一像素。**「轮播页面占满窗口」这句旧要求从此不成立了**，是他这一句换掉的，别照旧话改回去。
- **一屏是上限不是目标**：超宽屏上「带宽 ÷ 16 × 9」会比一屏还高，那时带高被一屏封住、图改成吃满带高、底色留在**左右**（`PictureRead` 两档都认）。一条扫过 320～3600 各种带宽的单测钉住「带高永远不超过带宽 ÷ 16 × 9」，也就是「上下不许留底色」的全称写法。
- **二、`VerticalAlignment="Top"` 这一下必须加在 `HomePage.xaml` 的 `HomeBanner` 上，这是这一件里唯一真出错的地方。** 默认的 `Stretch` 会把那个 `UserControl` 拉到整格那么高，于是它的 `ActualHeight` 报的是「格子多高」而不是「带子多高」，而右栏又跟着这个数走（`SyncRail`）—— 两边就在旧高度上互相扶着不动了。第一趟自检抓到的读数是「大图 735×792，该高 413」。
- **三、右栏缩小：卡宽上限 300 → 240**（`HomeCarousel.RailCardCap`），所以那一栏从 340 宽变成 280 宽。**这个数是两栏一起的旋钮**：右栏越窄，左边大图就越宽、跟着也越高，两个数是连着的，别只改一个。
- **四、轮播的幻灯片改成按 设置 → 主页 那张表的次序取，排第一的那一排先上**（`HomeCarousel.Slides` 从「收三个具名列表」改成「收一串按版面排好的列表」）。从前写死的是 最近添加 → 继续观看 → 接下来看（「海报要用最近添加」），一句不看版面的话。**媒体库那一排不参加**：幻灯片上有一颗播放键，而一个媒体库按下去什么也放不了（它多半也没有宽图、本来就会被筛掉，但那是运气不是规矩）。次序由 `HomeViewModel` 排好交进来，那里顺手把「一排装什么」抽成了一个局部函数，货架和幻灯片从此问同一个人。
- **屏上看得见的结果**：现在第一张幻灯片就是他上次停在哪儿那一集，播放键写着「继续播放 1:20:32」。**代价是同一个条目会同时出现在大图和右栏第一张上** —— 那是「排第一的是继续观看」这句话的直接后果，他要是嫌重复，改法是「轮播跳过右栏已经在放的那一个」，一行判断。
- **五、自检那一关重写并改了名：「主页首屏只露继续观看」→「主页首屏两栏」。** 判据从「第一排在屏外」换成六条：大图那一块的高就是那条规则按它自己的宽算出来的那个数（也就是上下不留底色）、整块放得进第一屏、右栏贴着大图没有缝、右栏和大图一样高、右栏里至少一张卡完整露出来、横着的第一排**接在大图下沿之后而不是压在它上面**（最后这一条是那叠删掉的玻璃不会悄悄回来的唯一保证）。`HomeBanner.Probe` 里带高那一段跟着改成按带宽算，徽标那一条改判「图下面那条底色小于一像素」。
- **闸门全绿**：Release 单节点构建 0 警告 0 错误、**739 项测试全过**（新增三条：带高按带宽算、一屏是上限、每种带宽上都不留上下底色；幻灯片那一组按新签名重写并新增「排第一的空着就往下顺延」）、发布件重发（473 个文件 297.5 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」。红过两趟：第一趟是上面第二条那个真错，第二趟是「鼠标真等两秒就藏」——交接文档挂名的已知抽风，重跑即过。
- **截图**：`artifacts/shots/home-rail-emby-dark.png` 和 `-daylight.png`（都在主屏、开窗那一档 1457×793）。没动颜色，所以另外四套主题那四张还是上一件拍的。自检读数：收起侧边栏时大图 735×413（正是那个宽度该有的高）、右栏 280×413、媒体库从 431 起露进第一屏 361；展开侧边栏时大图 535×301。

**主页第一屏改成并排两栏：左边轮播大图、右边一条竖着的继续观看；左上角折叠侧边栏那颗图标重画（2026-09-05，四道闸门全绿，未提交）。** 用户两句话加一张标注图：「参考上图修改轮播页面」「左上角隐藏侧边栏的图标太丑了，改成两个连起来的长方形，左边更窄右边更宽」。图上两个红框把第一屏分成两块，左边写着「轮播页面」、右边窄的那一条竖着写「继续观看」。

- **动手前问了一句，这一句省下一次白做。** 那张图有两种读法差别极大：①图缩到左栏、右栏是自己的底色；②图照旧铺满整宽，右边那条继续观看是半透明玻璃压在图上（也就是把现在那层玻璃从下面挪到右边）。画了两张 ASCII 草图让他挑，他选了①。**别照第二种改。**
- **一、第一屏现在是一个两列的格子**（`HomePage.xaml` 的 `Hero`）：左列是那条大图轮播、右列是继续观看。右列的宽和高都由代码给（`HomePage.SyncRail`），标记里一个数都没写：宽是 `HomeCarousel.RailWidth`（一张 16:9 卡加两边各 20 的留白，装机那一档 340），高跟着大图**量出来**的高走 —— 那一列是竖着排的卡片，不给高度这一格会长到十二张卡那么高，把整个第一屏顶出窗口。带高只有 `HomeBanner.Resize` 一个写手，所以这里跟着屏上那个数走、不再算一遍。
- **二、继续观看从「压在图上的一叠玻璃」整个搬走了，那一整套连着删。** `HomeCarousel.ShelfLift`（连三条单测）、`HomeBanner.SetShelfInset`、`HomePage.SyncShelfOverlay`、货架那个负的上边距、竖向暗罩跟着货架上沿走的那段计算 —— 全没了。屏上因此再没有哪一排压在图上：横着的那几排从第一屏外面开始，`CardShelf.OnScrim` 现在只有继续观看那一排是开的（它站在和大图同一支深底上）。**右列的底色和带子的底色必须是同一支画刷**，所以 `EgBannerBaseBrush` 从 `HomeBanner.xaml` 自己的资源搬进了 `Theme/Palette.xaml`（跟 `EgOnScrim*` 做邻居，同样故意不跟主题走）—— 两栏并排，差一档就是中间一道竖着的接缝。
- **三、哪一排进右栏是按钥匙认的，不是按位置。** 从前「压在图上」的永远是排第一的那一排（拖拽表说了算），现在右栏永远是继续观看（`HomeLayout.Resume`）。**理由是形状**：右栏一列只有一张卡宽，而拖拽表里排第一的可能是海报那种排（最近添加），一列 2:3 的海报在那个宽度上只放得下两张。设置 → 主页 那一行的说明跟着改了：勾掉继续观看那一栏就没有、大图铺满整个第一屏，但拖它的位置不会有变化。
- **四、右栏的卡宽跟着 设置 → 海报宽度 走，但封顶在装机那一档**（`HomeCarousel.RailCard`，300）。**这一条是我自己那条新单测当场逼出来的**：滑杆能拖到 340，那一档 16:9 卡是 600 宽、右栏就要 640，在最窄的窗口（900）上留给大图只有 211，而字块最窄那一档要 340 —— 单测按四种海报宽各算一遍，一红就说明该给右栏加让位规矩。封顶之后右栏最宽 340，最窄的窗口上大图还剩五百上下，所以**右栏不需要「窄窗口上让位」这条规矩，也就不存在第二种第一屏版面**。
- **五、屏上唯一的代价，值得他知道**：右栏占掉一段宽之后，左列比 16:9 高，而剧照照旧一个像素都不裁，所以**图的上下会各留一条底色**（开窗那一档约 96 像素，他那台 998 高的窗口上约 145；从前没有右栏时只有 49）。徽标跟着抬起来贴住图的右下角（`ShadeEdge` 现在把上下那条留白也算进去），底边那排小横条不抬 —— 它是控件不是画面。**要去掉那两条底色只有两条路**，都得他点头：①让轮播那一块只占第一屏的上面一截（图正好铺满，底下露出下一排货架）②图裁到左列的形状（两边各切掉约一成半）。**他当天就挑了①，见上面第一件活 —— 所以这一条描述的样子已经不在屏上了。**
- **六、图标：两个连起来的长方形，左窄右宽。** 外框比这一排别的图标宽一点点（15.6 对 14.2），为的是两格都摊得开 —— 20 像素上两个长方形，比例不到 1:2 就看不出哪个更窄，而左格窄到 3 以下就成了一条缝。现在左格内宽 3.4、右格 8.0（约 1:2.35），竖线落在整块的三分之一稍多处。**顺手拆掉一颗地雷**：上一版让竖线的圆头「埋进」上下两条边里，而那正是两个子路径真正叠在一起的地方，`Data` 的默认填充规则是 EvenOdd、叠起来的地方互相抵消 —— 现在三段子路径一处不叠，两种规则画出来一模一样。拍了放大九倍的 `artifacts/shots/pane-icon-zoom.png`。
- **七、自检那几关跟着改，这是最容易做错的地方。** ①「主页首屏只露继续观看」整条重写：从前判「第一排完整落在第一屏里、第二排在屏外、那一排压在大图上」，现在判「大图那一块下沿落在窗口下沿、右栏贴着大图没有缝、右栏和大图一样高、右栏里至少一张卡完整露出来、横着的第一排从窗口下沿之外开始」。右栏不在屏上不算错，但要和数据对得上 —— 继续观看空着或被勾掉才允许它不在，**有内容却没显示是这一栏唯一一种不出声的坏法**。②「主页大图不裁切」一个字没改（它本来就判「贴住吃紧的那一边」，现在吃紧的是宽），报告里那句话从「吃满带高」变成了「吃满带宽（带子比图高，上下留底色）」。③`HomeBanner.Probe` 里「货架压住 300 时三样都让开」那一段换成「整条带都是自己的」，另加徽标要多让一条底色。④「点卡片不挪页」改成专挑横带里的卡：那一条问的是「横带会不会替卡片要一次 BringIntoView 把整页拽下去」，拿右栏的卡去问等于换了个题目（右栏那个 `ScrollView` 里没有 `CardStrip.RevealFor` 这条路）。⑤`HostWindow.BrowseFoldMeasurable` 那道门没动 —— 新的判据其实什么形状都成立，但那道门是个更严的超集、自检窗口过得去，动它要连 HostWindow 一起改，不值。
- **闸门全绿**：Release 单节点构建 0 警告 0 错误、**736 项测试全过**（`ShelfLift` 那三条删掉，新增四条：右栏宽、卡宽封顶、没有卡就没有这一栏、最窄窗口上大图站得下字块）、发布件重发（473 个文件 297.5 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」，报告里 token 出现 0 次。**中间红过两趟，都不是这一件的锅**：第一趟是上面第四条那条新单测（真错，已修），另一趟是「鼠标真等两秒就藏」——交接文档挂名的已知抽风（报的是「没碰它的时候屏幕上没有系统箭头=False」，而同一行里写着「静止 2000ms 就藏了、全程没人碰」），重跑即过。
- **动了颜色（右栏那块深底、右栏那套浅墨），所以六套主题各拍了一张**：`artifacts/shots/home-rail-emby-dark.png`、`-daylight.png`（这两张在主屏、开窗那一档 1457×793）、`-oled-black.png`、`-midnight.png`、`-graphite.png`、`-plum.png`（这四张在副屏、1066×793）。晴昼那套是重点：右栏是深底配浅墨，「继续观看 / 7 项」和卡片底下那两行都读得出。
- **拍照时撞见一次一闪而过的错版**（图整块往下沉了两百像素、截图只有 649 高）：那一趟的窗口矩形和成像时的窗口对不上，也就是窗口还在落位。加长等待重拍一次就正常了，自检两档都判过稳态。**新引入的那一点点同类抖动记在这儿**：右栏的高度跟着大图量出来的高走，所以它比大图晚一个布局回合 —— 拖窗口的那一帧里第一屏可能高出一点，下一帧就对了。

**设置三改：锁定比例整条删掉、着色器装机默认关、画质预设挪到着色器开关上面（2026-09-05，四道闸门全绿，未提交）。** 用户三句话：「删除设置中锁定比例的功能」「恢复默认 后着色器默认关闭」「把画质预设移到着色器开关上面，着色器开关不影响画质预设」。三件互不相干，一起做的。

- **一、「锁定窗口比例大小」连功能带代码一起删。** 只删设置页那一行是不行的：那个键留着不动就是「窗口永远锁着、没处关」。删掉的是 `UiSettings.LockWindowShape`、界面卡那一行、`HostWindow.BrowseAspect` / `FitToShape` / `LockedAspect` / `LockedInset`、App 启动时那两句接线、`ShellPrefs` 上那个订阅（连 `WM_DESTROY` 里的退订一起），以及 `AspectLock.Apply` / `Fit` 上那个 `insetWidth` 参数（「计算比例时要排除侧边栏」只有这条锁在用，唯一的调用点从此一律传 0，所以参数跟着走）。`FitToAspect` 那个两处共用的私有方法并回 `FitToPicture`——现在只有一个调用者。**放片子时那条形状约束一个字没动**（`PictureAspect`：拖边跟着画面比例、开播即配比例），删掉的只有浏览时那一条。旧设置文件不用管：反序列化碰到没处放的键本来就不出声。
- **屏上唯一的代价，值得他知道**：窗口从此拉成什么形状都行，而主页那张大图是「整张画出来、不裁一个像素」，所以窗口比 16:9 更高的时候，图的上下会露出一条底色（那条锁买到的正是「上下左右都不留底色」）。开窗那一档的默认尺寸仍然是「16:9 的浏览区 + 侧边栏那一条」，所以新装、或者没拉过窗口的人一点变化都看不到；他那份记下来的窗口是 1649×998（浏览区 1.6:1，比 16:9 高），上下各约 25 像素，而下半截本来压着继续观看那一叠玻璃，实测截图（`artifacts/shots/home-no-lock.png`）几乎看不出来。拖一下窗口就没了。
- **自检那两关跟着改，这是这一件里最容易做错的地方。** ①「锁定窗口比例」那一关删了（没有锁可判），换成一行只报不判的「浏览区形状」读数——它是「主页首屏」那一读跳过时唯一说得出「差了多少」的地方。②「拖边保持比例」原来在自检里只跑得到「没锁的时候别乱改矩形」那半边（自检里没有片子，`PictureAspect` 是 0），删掉浏览那条锁之后另半边就永远跑不到了；现在这一读**自己摆一个 16:9 上去、读完放回 0**，两档都验（摆着要按 16:9 改写且左边沿不动，放回要原样送回）。摆的是属性不是真播放：`WM_SIZING` 只改一个矩形，窗口一个像素都不动。③「主页首屏只露继续观看」原来的门是「浏览区正好 16:9」（那时是锁保证的），删锁之后在这台机器的自检窗口上（副屏竖屏、被工作区夹成 1015×792）就永远跳过了——门改成「**不比 16:9 更扁**」：那一叠货架要的是高度，更高的窗口只会让这句话更容易成立，而更扁的窗口可能连一排货架都放不下，那才是只报不判的那一档。这一关因此照旧在跑、照旧全绿。④「主页大图不裁切」里写死的「高度吃满带子」改成「**贴住吃紧的那一边**」（带子比图扁就吃满高、比图高就吃满宽），否则窗口不是 16:9 就直接报错——第一趟自检就是这么红的。
- **二、着色器装机默认从「开」改成「关」**（`ShaderAutomationSettings.Enabled` 去掉初始化器），于是「恢复默认」交出来的也是关——那颗按钮抄的就是这个初始值（`SettingsReset`），不用另加特例。**顺带补了一手迁移**：v7 那一段原来只把 v6 的「所有视频默认启用=false」抄过来，现在两个方向都抄——默认变成关之后，一份 v6 文件里明明开着的着色器不抄就会被新默认悄悄关掉。存过的文件不受影响（v7 起这个键就在文件里，他自己那份写着 `false`，本来就是关的）。
- **三、画质预设挪到「启用着色器」上面**，两行的说明都重写了。**这两件在代码里本来就是两条路**：预设是一句 `profile=`，每次开播都发，跟开关无关；链是 `glsl-shaders` 加它自己那套缩放器。新加一条契约测试把这句话钉住（三种预设 × 开关两档，交给 mpv 的 `profile` 必须一样）。**唯一重叠的是缩放器那三项**：着色器链本身就是一套缩放器，开着链的时候 `scale`/`cscale`/`dscale` 归链，预设的其余部分（抖动、光域、HDR 峰值那些）照旧生效——这一条是链的定义决定的，改不掉，两行说明里都照实写了。
- **闸门全绿**：Release 单节点构建 0 警告 0 错误、735 项测试全过（删掉两条只测 `insetWidth` 的、新增一条「画质预设跟开关无关」的契约测试，另有四处装机默认相关的断言跟着改）、发布件重发（473 个文件 297.5 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」。**第一趟红在「主页大图不裁切」**（真错，见上面第①④条），改完第二趟红在「鼠标真等两秒就藏」——交接文档挂名的已知抽风（这一趟报的是窗口化那半「没碰它的时候屏幕上没有系统箭头=False」，而同一行里写着「静止 2000ms 就藏了、全程没人碰」），重跑即过。没动颜色，所以没拍六套主题；拍了三张：`artifacts/shots/settings-shader-card.png`（画质预设在第一行、启用着色器第二行显示「关」，这张卡 8 项）、`settings-interface-card.png`（界面卡 7 项，锁定那一行没了）、`home-no-lock.png`（上面说的那条底色）。

**详情页第一屏黑边统一，阈值跟着显示器走（2026-09-05，四道闸门全绿，未提交）。** 用户三张截图起的头（「同窗口大小，有的页面下方有黑边覆盖有的则没有，我要求把页面改的统一」；裁剪对比的事实：同一窗口上电影页（简介长）和集页（集带填满尾部）没有黑边，简介短的剧页有——尾部撑到 280 封顶就停，纸面上沿浮进第一屏四五十像素、把「更多来自」的牌子切了半行），中间按他的话撤过一版（见下面「做了又撤」那条），最后钉在他两句话上：「窗口大于1600*900后开始显示下方的黑边，小于1600*900时海报占满整个窗口」，加「显示器分别为4k时设定为1920×1080 2k时1600×900 1080p时1366×768」。

- **改法：尾部那个 280 的高上限整个换成一条「纸面上沿的线」。** `DetailHero.TailCap` 删掉，新增 `PaperLineFor(显示器宽,高)`——判的是显示器这张屏多大（屏高 ≥2000 → 4K、≥1300 → 2K、其余 → 1080p），给的是黑边从多高的窗口开始回来（1080 / 900 / 768；宽度不参与，黑边是竖着的事）。`TailHeight` 多收一个线参数（视口坐标）：视口不到线就一直撑到屏幕的下沿——四种页面（带子 460 的电影/剧/季、两百多的集页）一律「画面铺满第一屏」；过线之后尾部停在线上，纸面带着 媒体信息/更多来自 那一叠内容一像素一像素地回来，无台阶（「不许跳」那条照旧成立）。剧情说明底下那段透着画面的暗区最长是「线 − 带子 − 内容高」，8 月圈过的空画面不会跟着窗口长。
- **接线沿 PlayerPage 的先例**：`ShellPage.AttachWindow` 挂 `ContentFrame.Navigated` 把窗口递给每个详情页实例（`detail.AttachWindow(window)`）——挂在 OpenDetail 里就会漏掉后退/前进重建的实例。页面领到窗口后订阅 `GeometryChanged`（改尺寸、拖动结束、全屏、DPI 变化都会响，正好盖住「同尺寸拖到另一台显示器」），加上 `LiftBackdrop`（面包屑显隐、换主题都挪它）一起重算那条线：阈值窗口高减标题栏加面包屑那截（自检量出来 68 上下），送进视图模型的 `PaperLine` 属性。页面 `Unloaded` 时解绑——每次导航都是新实例，不解绑就是把旧窗口拴到下辈子。这条线和「显示同步跟刷新率」「着色器档位跟输出宽」是同一类「跟着显示器走的数」，都从 HostWindow 那几只读数器来。
- **闸门全绿**：构建 0 警告 0 错误、735 项测试全过（TailHeight 那一组重写：分界断言从 740/741 改成线本身（832/833）的符号写法，新增 `PaperLineFor` 三档分档的测试；无跳变扫 500→1400 原样保留）、发布件重发、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」——第一趟「鼠标真等两秒就藏」红了（全屏那半报「没碰它的时候屏幕上有系统箭头」），是交接文档挂名的已知抽风，重跑即过，详情三条读数都绿：小窗口上「第一屏要 43」（视口 503 − 带子 460），纸面上沿 772 在第一屏外。
- **截图验过**：1594×869 的剧页（99.9，之前露边的那种版面；这台 2K 屏阈值是 1600×900，窗口在阈值之下）画面一路铺到屏幕下沿、无黑边。**已知的小缺口**：窗口不换尺寸、只在不同分辨率的显示器之间拖的时候，线要等下一次改尺寸才跟上（`GeometryChanged` 不响的场景没有——同尺寸跨屏拖动它会响，但两屏同尺寸同 DPI 时监视器变了值也该变，那一刻读数是旧的）——真遇到了拖一下窗口就好。

**点了第二块屏上的应用之后鼠标不再自动隐藏：「让系统重新问一次」那一下从来没扣动过（2026-09-05，四道闸门全绿，未提交）。** 用户原话：「第一个全屏播放时点击第二个屏幕的 telegram 或者 claude app，然后鼠标再移回第一个屏幕，会导致鼠标无法自动隐藏」。

- **量出来的根因**：藏鼠标分两半 —— 一半是把「没有光标」这个答案摆好（这条队列的形状、五个窗口类、框架的 `ProtectedCursor`、mpv 对它自己那块子窗口的设定），另一半是**让系统去收集这些答案**。系统只在指针动的时候收集，而藏起来的前提正是没人动指针，所以第二半必须由程序自己扣一下扳机。那一下从 08-29 起写的是「位移为零的 `SendInput`」，而**它一个消息都不产生**：今天在指针底下摆了一个数消息的窗口量的，五次零位移注入换来 `WM_MOUSEMOVE` 0 次、`WM_SETCURSOR` 0 次；同一支脚本里五次**同点 `SetCursorPos`** 换来两样各 5 次，**而且本进程不在前台时照样成立**。`SendInput` 两种情况都返回 1，所以日志和自检里那句「让系统重新问了 N 次」从头到尾报的是一个没发生过的动作。
- **改动就一行**：`Native.NudgeCursorState()` 从零位移注入改成「读一次指针位置、原地再放一次」（`GetCursorPos` + `SetCursorPos`）。位移仍然是零这一点没丢，所以判「动没动」的两处（`PlayerPage.Moved` 的事件过滤、`PollPointer`）照旧在计数之前把它挡掉 —— 这一下不会打断它自己参与的那段静止。其余四条杠杆一个没动。
- **为什么它偏偏在「点了第二块屏」之后露出来 —— 这一条是推断，不是量到的。** 真放片子时指针压的是 libmpv 自己那块子窗口（日志写 `指针上的窗口=mpv`），那是个普通 Win32 窗口，一声 `WM_SETCURSOR` 就能把「没有光标」问到它头上；平时那一声由别的真实输入顺带带来（前台是我们、框架刚为上一次移动重算过），而用户把前台交给第二块屏上的应用、再把手移回画面之后就只剩这个扳机一条路 —— 而它是空的。**验它要一只真手加一部真片子，而验证时不许真实播放**，所以这一句留给他确认（已进下面那张单子）。
- **自检那句假证据改成了看得见的读数**：`ProbeCursor` 现在把「原地重设指针：发得出=…，我们这两个过程问到 N→M」印进报告。**只印不断言**，理由报告自己写着：指针压在 XAML 内容上时这声 `WM_SETCURSOR` 由框架自己那个内层窗口答掉，既不冒到主窗口的 `Route`、也不冒到岛的过程上来 —— 同一行里那句「真移动问到 0→0」说的正是这件事（那一趟指针真的挪到了画面中心，我们这两处一次都没被问到）。`Screen()` 里原来那三下探底并成两下 ——「同点重设」和「零位移注入」现在本来就是同一件事了。
- **`CLAUDE.md` 里那条坑改对了，这是这一件里最值钱的一行。** 原文是「这台机器上程序里挪鼠标一律没用（`SendInput` 返回 1 但指针不动；`SetCursorPos` 同样）」，**而正是这句话让「让系统重新问一次」被写成一次注入**。现在写的是量到的分寸：零位移注入什么都不产生、同点 `SetCursorPos` 会产生一次移动加一次问光标且不在前台也成立、带真实位移的注入两趟里一趟动一趟没动所以别指望它；「用命令行开关导航、别用鼠标」这条结论没动。三处代码注释里把「零位移注入」这个说法一并改掉（`InputCursors`、`PictureSurface`、`HostWindow`），并在那段「真片子日志证明四条 Win32 杠杆都没用」的证据旁边加一句：那一趟的「催过一次」是个空动作，所以那四条既没被证伪也没被证实。
- **闸门全绿**：Release 单节点构建 0 警告 0 错误、**734 项测试全过**（没新增 —— 改的是 Shell 那层一句 P/Invoke，测试工程够不着）、发布件重发（473 个文件 297.5 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」。**「鼠标真等两秒就藏」那一关这两趟都写「中途有人动了鼠标，这一轮只作参考」**（用户当时正在用鼠标，报告数出真移动 19～25 次），所以那一关这两趟没给出干净读数 —— 它按设计对「有人碰了鼠标」宽容，全屏那条腿仍然报了「静止 2000ms 就藏了、让系统重新问了 2 次」。
- **`--hide-cursor` 配 `tools/cursor-watch.ps1` 那趟旁站观测没做成，别照抄我的做法**：脚本报的是「指针一路压在 Claude 那个窗口上、前台也是它」，也就是播放层根本没开在指针底下（我给的是 `--screen 1 --maximized`），而且观测期间指针在动。这一趟作废、窗口已经关掉。要拿它当证据，得让窗口真的开在指针底下、并且手离开鼠标八秒 —— 而那八秒里屏幕会被一个最大化的播放层占着，所以这一步值得先跟他说一声。

**详情页第一屏黑边统一：尾部封顶 280 → 460（2026-09-05，做了又按用户的话撤掉）。** 用户三张截图：「同窗口大小，有的页面下方有黑边覆盖有的则没有，我要求把页面改的统一」。把三张图的底部各裁下来比出来的事实：同一个窗口（875 高）上，电影页（简介长，尾部被内容撑过第一屏下沿）和集页（尾部被集带填满）都没有黑边，简介短的剧页有 —— 尾部撑到 280 封顶就停，纸面上沿浮进第一屏四五十像素、把「更多来自」的牌子切了半行。统一成哪种问过他，他定的规则原话：「定一个窗口大小（参考电影页面），在特定窗口大小下，下方的黑边不出现，窗口变得太大的时候就恢复」。

- **按他的规则做了**：`DetailHero.TailCap` 280 → 460（= ArtHeight，和带子同高），公式一个字没动 —— 视口 ≤ 920（窗口高约 ≤ 990）时尾部撑到第一屏下沿、四种页面都没有黑边，再高纸面一像素一像素地回来。四道闸门全绿（734 项测试），截图验过剧页黑边消失、电影页集页没动。
- **然后他一句「改回去」，当天整个撤了。** `DetailHero.cs`、`DetailPage.xaml`（一行注释）、`ItemDetailTests.cs` 三个文件还原到 HEAD —— 动手前核过这三个文件没有别人的未提交改动。撤完四道闸门重跑仍然全绿，发布件跟着换成撤完之后的构建。**他没说为什么。现在的屏上样子（封顶 280：简介短的剧页在常见窗口上会露出那条带牌子的黑边）就是他要的样子，别自作主张再动；哪天他重新提「统一」，这一条的方案和代价都在这一节里，直接照做就是。**
- **留了两样不随撤走的东西**：①三张截图的底部裁剪（`docs\shots\u1-movie-bottom.png`、`u2-episode-bottom.png`、`u3-series-bottom.png`）和撤掉前的剧页对照图（`series-uniform.png`）—— 那条黑边的来龙去脉看图最快；②测试里怎么钉这条公式（无跳变扫 500→1400、分界断言用「带子加封顶」的符号写法）的写法样例还在归档的差异里，重做时照抄省事。

**优化建议七条，用户一句「你来决定顺序」，七条全做（2026-09-05，四道闸门全绿，未提交）。** 他先说「针对当前项目的后续开发提出优化建议」，我读完交接文档、四份技能、Core / Shell / 测试三层的实际形状之后报了七条并排了序，他一句「你来决定顺序」—— 和 09-02 那次一样，那句话的意思是「顺序你定、都做」。**做的顺序是：先把后面每一件都变便宜的那两条（归档、验收单子），再把两条零风险的（探针归因、扫描类型），然后是这一批真正的活（那十几个接口第一次有测试），最后是纯机械的拆文件。** 第八条不是建议、是风险：**手上这一批仍然没有提交**，那要他一句话。

- **一、`PROGRESS.md` 252 KB → 67 KB，第三次归档。** 我这次读它工具一趟读不完、分两趟约四万 token，而**每个新窗口在动手之前都要付这笔钱**。已经提交的那二十来批原文搬进 [`docs/progress-2026-09.md`](docs/progress-2026-09.md)（68 KB → 269 KB），相对链接跟着改前缀。**归档的界线这一次定成「提交了没有」而不是「做完了没有」** —— 它和 `git status` 对得上，不用读文档去猜，写进了本文件开头那段说明。顺带把索引补齐了：09-04 那十批（音视频输出四批、恢复默认、播放器三件、控制台主题、代码审查六处、mpv_config 清理、视频同步、mpv 选项名、Vulkan、ICC）从前一条索引行都没有，搬走之后就等于失踪，现在各有一行。
- **二、新增一节「等你在屏幕前确认的」，十七件收成一张单子。** 从前每批做完都留一句「还剩用户在屏幕前才验得了的」，写完就沉进叙述里，**没有任何一处在跟踪** —— 从 252 KB 的正文里数出来横跨十来轮。它们的共同点是四道闸门验不到：注不进鼠标事件、不许真实播放、或者会写服务器而自检一律不碰。分成四组（要真放一集的、播放器手感的、会写服务器的、要他点头或动手的），做完一件划掉一件。**这一节是这次七条里他最该先看的。**
- **三、「鼠标真等两秒就藏」那一关的归因是错的，改对了并顺手修了判据。** 交接文档三处把它的红归因成「那两秒里有人碰了鼠标 —— 报告自己写着『轮询问出 1 次移动』」。**代码里 1 正是干净一趟的读数**：探针在计时前把 `_polledKnown` 清成 false，第一次轮询必然记一次；而真不动的指针（dx=dy=0）在计数之前就被 `PollPointer` 挡掉了。所以那个数证明的是没人碰。判据本来只看「末位置有没有变」，看不见「中途动过又回去了」；现在**多看轮询次数**（>1 就是真位移 —— 显示时超过噪声阈值、藏起来之后任何一个像素都算，那本来就是「有只手伸过来了」的定义），并在报告里直接写出是哪一种。真被扰动的那一趟从此写「这一轮只作参考」而不是红。**回归躲不进这条宽容里**：藏不掉会报 1 次并且红，而「指针没动却被叫回来」走的是 XAML 空事件、根本不碰这个计数器。技能里那段「已知抽风、重跑一次」改成「读 `不符：` 那一段，别读诊断数字」。**这一轮自检报的正是「1 次移动（只有开头那次播种，也就是全程没人碰）、空事件 1 次」，一次过。**
- **四、外部 mpv.exe 那条路的令牌 —— 我选「把话说出来」，代码没动。** 这是上一轮摆给他还没答的那条 P1。设置 → 播放器 → 「mpv.exe 路径」那一行现在写着「走这个后端时访问令牌会出现在 mpv 的进程命令行上（任务管理器、进程工具、崩溃转储都读得到）；内置播放器不经过命令行」。**理由是三条路里只有这一条在这台机器上验得住**：临时配置文件等于把令牌明文落盘、正好抵掉 DPAPI 换来的那条性质；IPC 注入安全上最干净但会长出第二条起播路径和一种新的卡死方式，而且「关掉 IPC」那档就没法播 —— 而两条都要真实播放才验得了，本机也没装外部 mpv.exe。**自检多一关钉这句话**（和「动态范围压缩只对 AC-3 有效」同一类：最可能的坏法是哪天被人「整理」掉，而单测进不到外壳）。他要真修，选 IPC 那条，我会连代价一起做。
- **五、全服务器扫描的类型并成一份 `ItemQuery.SweptTypes`，顺手修掉一个真的会露出来的东西。** 从前是三份名单：`ItemQuery.Search` 那个工厂（五种、含音乐视频、**全项目只有它自己的测试在用**）、`ItemQuery.CreditedTypes`（五种、含音乐视频、演职人员那一格在用）、以及媒体库视图模型里搜索那一支手写的四种（不含音乐视频、注释写着为什么）。**而 `EmbyItemType.IsMusic` 把音乐视频算成音乐、外壳到处都在藏它**（音乐库进不了导航栏、主页那三排丢掉音乐条目）—— 演职人员那一格是唯一没有库可筛的查询，也就是**一张外壳专门要藏的卡片唯一能落到屏上的路**：一个有音乐视频作品的人，他的作品格子里就有一张。工厂删掉，两处并成一份四种类型的名单。**测试不去比第二份四个名字的抄本**（那只是把「两处不一致」换成「三处」），而是把这份名单和 `IsMusic` 钉在一起 —— 谁把一个要藏的类型放回去，当场红。
- **六、「更多」菜单背后那十几个接口第一次有了测试，新增 22 条。** 这是七条里最该做的一件：那是整个客户端唯一一片会往服务器上写东西的代码（删除连磁盘文件一起删、刮削覆盖手改过的元数据、下载、换封面、加合集、删字幕、扫库），而**十四个接口一条测试都没有** —— 自检只敢按只读的方式问四个，会写东西的一律不碰，理由正当，可那就意味着它们从来没被任何一道闸门看过。**它们本来就在 Core、测试工程一直够得着，缺的只是有人来写。** 钉的是动词（一次删除必须是 DELETE 一趟，不是 POST 也不是两趟）、路径（错一段服务器答 404 而屏上写「失败」）、以及决定语义的那几个参数。**最要紧的一条是「刮削和刷新两档都不许动图片」** —— `ReplaceAllImages` 要是跟着 `ReplaceAllMetadata` 一起变 true，用户刚挑的封面会被紧接着的一次刮削扔掉；我把它临时改成跟着变，那条测试当场红，改回来就绿。还钉了自检那一趟刮削真的是 `ValidationOnly`（这一句没人看着的话，自检会开始每轮真刮他的第一个条目一次）。
  - **假传输层搬出来共用了**（`SessionTests` 里那个私有类 → `tests/EmbyNian.Tests/StubTransport.cs`），并且**加上了方法、完整地址和请求体的记录** —— 会话那一批只需要「这个片段被问了几次」，而「删除是 DELETE 而不是 POST」「刮削带哪几个参数」要问的正是动词和参数。**地址记的是 `AbsoluteUri` 而不是 `ToString()`**：后者交回的是给人看的那一份、把 `%XX` 还原成原字符，于是「合集名要转义」这类断言会看到一个解码过的地址、读起来像根本没转义（我头一版就栽在这儿）。另加一个 `Understate`（说好了有 N 字节、实际只给一半），专为上一轮代码审查刚加的那道下载完整性检查 —— 真断线要一个真连接，编不出来。
  - **四段判断搬进 Core 并各有测试**：一叠单集该拿哪两个 id 去问（剧用自己的、季用它所属剧的加一个季筛选，**季却没有 SeriesId 时要答「问不出来」而不是拿季的 id 硬试** —— 后者会以「这一季一集都没有」的样子回来）、进度那一行、下完之后那句话的四个分支（一个都没下成是一句警告，不是「成功了 0 个」；跳过几个必须说出来，否则「已下载 19 个」在一部 24 集的剧上读起来像下全了）、以及**删除之前那句问话**（`ItemMenu.DeletePrompt`）—— 那是整个客户端唯一一句「按下去就没得恢复」的话，而一叠单集要多说一句「里面的所有单集都会被删除」：少了它，一次点击和用户以为的事情差着二十四个文件。
- **七、`PlayerViewModel` 2227 行拆成五个 partial（652 / 572 / 393 / 378 / 361），正文一字节没动 —— 逐片 `diff` 核过。** 切的位置就是那个文件里作者本来画好的分节线（18 条），所以这一次没有任何一处需要判断某个成员归谁；字段和构造函数留在主文件里。和 08-31 拆自检那两个大文件同一件事、同一个理由：首要目标里「简单」的三条判据之一就是「一个文件还能不能从头读到尾」。
  - **`HostWindow`（2275 行）和 `DetailPage.xaml.cs`（1672 行）这一批没拆，这是我判的、他可以否。** 那两个文件**一条分节线都没有**，拆它们意味着我先把四千行 Win32 互操作和页面代码后置整读一遍、再自己发明分组 —— 那是另一件活，有把成员放错位置的真实机会（放错了照样编译过），而且会把这一批里几处真的行为改动埋在四千行搬迁底下。**机械照办会让这一批更难读，而不是更好读**，所以留成下一步的具名待办。
- **闸门全绿**：Release 单节点构建 0 警告 0 错误、**734 项测试全过**（新增 22 条接口测试加几条纯函数测试；这个数里有两条是按数据注册的，所以它和源码里 `Test(` 的条数差 2）、发布件重发（473 个文件 297.5 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」，报告里 token 出现 0 次，**一次过**。没动颜色，所以没拍六套主题；屏上唯一看得见的变化是设置 → 播放器那一行多了三行说明，拍了 `artifacts/shots/player-card-token-note.png`（三行说明全在、这张卡照旧 3 项）。
- **两条测试各自验过会红**（不是自称）：`ReplaceAllImages` 改成跟着 `replace` 走 → 「刮削覆盖元数据…两档都不许动图片」当场红；把音乐视频放回 `SweptTypes` → 「全服务器扫过的类型里不许有外壳要藏的」当场红。两处都改回来之后 734 全绿。

**集页头图向电影页对齐（2026-09-05，做了又按用户的话撤掉）。** 用户一句「参考电影页面，修改剧页面和集页面的背景缩放还有下方覆盖逻辑」。先拍了三张截图对出来的事实：**电影页和剧页在这两件事上用的是同一套代码、屏上一个样**（都是 460 的带子、纸面都压在第一屏下沿），真正跟电影页不一样的只有集页 —— 头图那一格只有 240 上下、整块内容贴着页顶。两处拿不准的问了他：①头图怎么对齐，他选了「剧照放大、头图随之加高」；②他说的「下方接缝/黑底」是旧版程序的样子，不是现存的代码问题。

- **按他的选择做了**：Core 新增 `DetailHero.EpisodeStillMaxes(pageWidth)`（页面 ≥1440 时集页剧照上限抬到 533×300、带子随之长到 344 上下；窄页维持 300×169），`DetailViewModel` 四处上限改走它、解码宽提到 533、页宽跨线时重算这一格。四道闸门全绿（713 项测试），截图验过集页带高 344、下方无接缝、剧页一个像素没动。
- **然后他一句「改回去」，当天整个撤了。** `DetailHero.cs`、`DetailViewModel.cs`、`ItemDetailTests.cs` 三个文件还原到 HEAD —— 动手前核过这三个文件没有别人的未提交改动，撤的就是这一件，一个字节不多。撤完四道闸门重跑仍然全绿，发布件跟着换成撤完之后的构建。**他没说为什么。别猜，也别自作主张再改回去或改回去的回去；他想看的样子就是现在这份。**
- **两件不随撤走的东西**：①「下方覆盖」本来就没有代码可改 —— 他看到的接缝（集带压在一块黑底上、和剧情说明之间一道硬边）出自 `artifacts\publish` 里那份旧程序，集带压回画面上是 9 月 1 日 `d34275d` 就落地的；发布件这半天重发了两回（先发了改完的、又发了撤完的），现在这份是撤完之后的。②顺手修掉上一批留下的一处编译错误：`EmbyTests.cs` 里新加的 `Assert.DoesNotContain(EmbyItemType.MusicVideo, ItemQuery.SweptTypes)` 编译不过（测试框架那个断言只收两个字符串；之前测试项目没重编所以一直没暴露），改成 `Assert.False(SweptTypes.Contains(...))`，按仓库里「不在名单里」的惯用写法。**这一行不随撤走 —— 撤它就是重新编译不过。**
- **⚠️ 这件事和另一个窗口在同一份工作树上同时干**：我干活时（10:49–10:56）那边改了 `ItemQuery.cs`、`LibraryViewModel.cs`、`PlayerPage.SelfCheck.Cursor.cs`、`docs/progress-2026-09.md`（搜索类型并成 `SweptTypes` 那一批的延续，也就是上面那处编译错误的来处），我这边 11:05–11:13 的闸门覆盖的是**两边合在一起**的树，713 项全过。**PROGRESS.md 两边都在写**：我这条落笔时，下面「记忆与技能瘦身」那条刚被那边续了四段 —— 谁落笔前都先重读本文件这一节。

**记忆与技能瘦身（2026-09-05，未提交）。** 用户一句「给记忆和技能瘦身」。上一件把六份技能并成四份、把内容补齐，这一件反过来砍重复，判据只有一条：**技能触发时 `CLAUDE.md` 本来就在上下文里，所以技能里凡是抄它的段落都不值一个字，还会各自变旧 —— 而技能里又写着「和 `CLAUDE.md` 冲突时以它为准」，抄来的那份一旦落后就成了自相矛盾**。技能 371 → 283 行（51.2 KB → 42.8 KB），`CLAUDE.md` 16.4 KB → 12.9 KB，记忆正文七条 → 两条、两个文件夹 → 一个（14.8 KB → 3.5 KB）。应用「Memory files」面板那两行合计 24.3 KB → 16.4 KB。**一行代码都没动，所以没跑四道闸门**；改的是 `CLAUDE.md`、`.claude/skills/` 四份、本文件，加上仓库外的记忆文件。原文全份备份在 `%USERPROFILE%\.claude\backups\2026-09-05-slimming`。

- **真正的成果是 `CLAUDE.md` 多的那条规矩：技能不许重述本文件。** 闸门命令、SDK 路径、命令行开关清单、凭据、Git、怎么汇报，从此**只在 `CLAUDE.md` 里存一份**；技能只写本行当特有的东西，其余指回去。没有这条，下一个窗口会本着「让技能自成一体」的好意再抄一遍。
- **`embynian-verification` 93 → 46 行**，砍得最狠的一份。四道闸门的命令块、SDK 路径、单节点构建、六个主题的名单、`cursor-watch` 那一整段、开关清单、no-python／BOM／`cut()`、Git、汇报 —— 全是 `CLAUDE.md` 的原话，删。留下它独有的：四关各自「过」长什么样（改成一张表）、`-c Release` 不带就等于把这一关关掉、`--screen` 那一趟不读不写窗口存档所以报告里的尺寸有基准、十行「本来就会变」先划掉再逐行比、「鼠标真等两秒就藏」是已知抽风不是回归、拍设置窗口要带 `--screen 2`（漏了会把用户正在玩的游戏拍进去）、脚本清单。
- **`embynian-winui-shell` 97 → 71 行。** 整节「首要目标」（三段，几乎逐句是 `CLAUDE.md` 的原话）压成一句指路，「判断搬进 Core」的道理压成一句、只留那份**可复用纯函数清单**，「动手前」「动手后」两节删掉。十个页面／八个控件／四个对话框、DI 那三条硬事实、`Attach` 的两种守卫写法、七个接口里哪六个是为了收窄、代码后置的活例子与反例、颜色角色表、四条 XAML 坑 —— 一条没动。
- **`embynian-playback` 99 → 86 行，`mpv-shader-quality` 82 → 80 行。** 这两份本来就几乎全是本行当的内容。播放那份删掉「不许真实播放」里属于 `CLAUDE.md` 的六条，留下它独有的三条：播放器自检不加载任何影片就能跑、`libmpv-2.dll` 在仓库根且发布脚本会拦、令牌为什么进不了 URL（`EmbyUrl.Stream` 不带 `api_key`、走 `X-Emby-Token` 头、`EmbyHttp` 记日志前把查询串削掉）。
- **记忆第一步：正文七条 → 四条，而且只剩一个文件夹。** 先删掉两份整份重复的（`embynian-credential-rule` 和 `embynian-verify-without-real-playback` 都是 `CLAUDE.md` 的原话，而**能碰到令牌、能启动播放的会话必然在动仓库里的文件，那时 `CLAUDE.md` 已经在上下文里**；两条的要害各压成一句，并进那份指路记忆），剩下四条逐条压短。
- **随后用户一句「现在怎么有两个记忆文件」逼出了早上真正的错，已修。** 记忆按会话启动目录分桶，而这台机器上 **EmbyNian 那个桶有 60 个会话、家目录那个只有 10 个且三天没动过**（桌面应用是按项目文件夹开会话的）—— 早上却把四条正文放进家目录桶、项目桶里只留一张指路条，**正好放在几乎不会被读到的那一边**，等于四条记忆白写。现在四条全搬进 EmbyNian 桶、指路条撤掉、家目录桶清空：**只有一个记忆文件夹，就在会话真正启动的位置**。「记忆按启动目录分桶」那条按新方向重写（60 比 10 这个数一起记下），并写明**不许再在别的桶里开第二份**。这一条推翻早上那句「起会话请从家目录起」—— 那是在跟工具较劲。搬动前后各存了一份备份。
- **接着用户点着面板说「给 Memory files 瘦身」，于是动了 `CLAUDE.md` 本身：16.4 → 12.9 KB。** 那个面板只数两份东西 —— 仓库的 `CLAUDE.md` 和记忆索引 `MEMORY.md` —— **而上午砍掉的十 KB 全在技能里，技能根本不在这个面板上**，`CLAUDE.md` 反因为多了那条新规矩涨了 0.7k；等于优化的地方正好是这块表不测的地方。这一趟十个小节一节没少，点名核过十四条要紧规矩都在原处；搬走的是规矩背后的道理、当初的事故、历史沿革：分层那七条实践从整段叙述压成一行一条（道理去 `embynian-winui-shell`）、`cursor-watch` 的原理去验证技能、技能从前住哪儿的整段历史删掉（本文件里有）、Git 里两处「从前是什么样」删掉。**剩下 12.9 KB 里八成动不了**：闸门完整命令加 SDK 路径 2.5 KB（全项目就这一份）、用户自己定的首要目标与两条分层规矩 4.7 KB、开关清单与这台机器的坑 1.4 KB。
- **最后用户一句「`CLAUDE.md` 和 `MEMORY.md` 是不是重复了」—— 是，而且这是全天最关键的一条判据。** 它是上一步并桶之后才成立的：**记忆库和 `CLAUDE.md` 现在的加载条件完全一样**（都是「会话从 `C:\Users\89400\EmbyNian` 起」），所以**不存在「读到记忆却没有手册」的会话，记忆里任何一条项目规矩都是纯重复**。这同时推翻早上留那两条安全提示的理由（「手册还没翻就先踩」—— 手册不是翻开的，它本来就在上下文里）。逐项核过：指路那份 6 条全在 `CLAUDE.md`、shell 坑那份 4 条全在、用户那份 6 条里 4 条在。于是**记忆四条 → 两条（6.0 → 3.5 KB）**，只留仓库装不下的：「记忆按启动目录分桶、这个库故意几乎是空的、别再开第二个」，加上用户那份里 `CLAUDE.md` 没说的两件（「你定顺序」把排序交回来、允许自由发挥加东西）。删掉的两份另存在 `…\backups\2026-09-05-slimming\before-dedup-against-claude-md`。
- **⚠️ 再一句「修复」时把记忆库整个清空过一次，当天又按他一句「改回去」原样退回了。** 那一趟的做法是：把用户那两件事写进 `CLAUDE.md`（「你定顺序」进「怎么汇报」、允许自由发挥加东西进「首要目标」），再加一条「记忆库故意留空、别再往里写」的规矩，然后清空记忆库 —— 面板一度只剩 `CLAUDE.md` 一行、13.8 KB。**他说改回去，就全退了**：三份记忆从 `…\backups\2026-09-05-slimming\before-emptying-store` 放回原位，`CLAUDE.md` 里那三处新增逐处撤掉、回到 12.9 KB，面板回到两行 16.4 KB。**他没说为什么。别猜，也别自作主张再清一次** —— 现在这份两行的样子就是他要的。当时那套论证（记忆库和 `CLAUDE.md` 加载条件相同，所以留空最省）留在上一条里，作为道理仍然成立，但**不构成再动手的理由**。
- **没有再往下砍。** 再砍就得动技能里那些「代码本身也说了一遍」的东西（字段名、端点、阈值），而那正是这四份的地图价值所在 —— 上一件事故恰恰是**清单残缺**（页面漏了五个）害的，不是内容太多害的。屏上一处变化都没有。

**上一件是「记忆与技能整理：六份技能并成四份搬进仓库」，见下面（2026-09-05，一行代码都没动，所以没跑闸门）。**

**记忆与技能整理（2026-09-05，未提交）。** 用户一句「检查当前的记忆文件和技能，提出修改建议」，我把六份技能和记忆全读了一遍、逐个对着仓库核过，报了七条，他一句「全做」。**这一批一行代码都没动**：改的是 `CLAUDE.md`、`docs/开发与验证.md`、`.gitignore`，加上仓库外的记忆与技能文件 —— 所以没跑四道闸门。**新加的 `.claude/skills` 不参与构建这一点是核过的，不是假设**：Shell 的 csproj 只有三处指到仓库根的通配符（`libmpv-2.dll`、`assets\shaders`、`assets\mpv-runtime`）。

- **技能从六份并成四份，全部搬进仓库 `.claude/skills/`。** 原来分住两处、两两重叠：`emby-playback` 和 `embynian-playback` 都管播放，`winui-mvvm-architecture` 和 `embynian-winui-shell` 都管外壳，**同一次改动两份都会触发**。但两边内容是互补不是重复，所以是合并不是删除 —— 中文那两份里独有的坑（切集靠 `_generation` 兜住晚到的回调、章节表有两份、10 Hz ticker 不能停、别在每跳里 new 东西、`x:Load` 会打断 `Storyboard`、指针事件那两条、颜色必须走角色表）整条并了进去。现在四份：`embynian-winui-shell`、`embynian-playback`、`embynian-verification`、`mpv-shader-quality`。
  - **搬进仓库的理由是「别人清不掉」**：那四份英文的原来住在 Anthropic 托管的技能插件目录里（和 docx／pdf／pptx 做邻居），应用一更新整个目录就换掉；两份中文的真身在第三方工具 `.cc-switch\skills\` 下，`~\.claude\skills` 和 `~\.codex\skills` 都只是符号链接。**这个坑已经踩过一次**：记忆里那条「技能已从托管目录搬出来」记的是 08-31 那回，而 09-03、09-04 新写的四份又落回了同一个目录。
  - `.cc-switch` 那两份改成指路桩（别的工具照旧读得到，不至于读到一份变旧的正文），六份原文备份在 `%USERPROFILE%\.claude\backups\2026-09-05-skills-consolidation`。
- **`emby-playback` 那份有四处实打实的过期，合并时一并修掉。** ①它还在教 `AudioTrackMode` 是「ServerDefault 或 Language」—— 那个枚举 09-04 第四批**整个删掉了**，现在是 `AudioLanguages` 优先级列表、空列表就是「跟随服务器默认」；②它把 `AssToSrt` 列进 `Core/Playback` 的可复用清单，仓库里没有这个文件；③它点名的三个测试文件（字幕、mpv 配置、mpv 工作区）**一个都不存在**；④音视频输出这一整批它完全没提。四处都按现在的代码重写，并补上音量上限 130 那个「界面骗人」的陷阱、上报音量只在有控制通道时才填、音频设备枚举、截图模板为什么必须带序号、`FillWideSources` 只对宽片出手、`OutputWatch`。
- **`embynian-winui-shell` 的页面清单从五个补齐到十个**（漏了 `ShellPage`、`SignInPage`、`SettingsPage`、`ServersPage`、`DashboardPage`），控件从五个补到八个，另加四个对话框。**照残缺的清单推理，就会出现「改了五个页面、漏了另外五个」**。两份中文技能都没提 `PROGRESS.md`，也补上了 —— 而那是唯一的交接文档。
- **「每次都会变的行」那份名单搬出按月归档。** 它原来夹在 `docs/progress-2026-09.md` 的一段叙述里，而 `CLAUDE.md` 指的是 `PROGRESS.md`，**指错了文件** —— 而技能里又写着「和 `CLAUDE.md` 冲突时以它为准」，照那句走正好走去错的地方。名单现在是 `docs/开发与验证.md` 里独立的一节（十项），`CLAUDE.md` 和技能都指它。**根子上的毛病是归档按月滚**：下个月换文件，任何指过去的路都自带保质期；归档里那一份留作当时原文，不再是现行名单。
- **`CLAUDE.md` 多一节「Skills」**：四份在哪、以这份文件为准、别再开第五个家。`.gitignore` 加一条 `.claude/worktrees/` —— `.claude` 现在要入库，而同目录下会话临时开的工作树不该跟着进去。
- **记忆：这个会话读到的桶是空的。**（**这一条的处置当天就被上面「记忆与技能瘦身」推翻了 —— 正文现在全在 EmbyNian 那个桶里，家目录桶清空，别照本条重建。**）记忆按启动目录分桶，从 `C:\Users\89400\EmbyNian` 起会话拿到的是空桶，真正那六条在 `C--Users-89400` 那个桶里 —— **而其中恰好有一条专门警告这件事，它躺在读不到的那一边**。现在空桶里放了一条指路记忆（只放一条、不抄内容：两个桶会各自变旧），并把另外两条改对 —— 「技能住在哪儿」那段整段重写，「不许真实播放」那条补上 `--show-osd`／`--hide-cursor`／`--show-menu` 和「鼠标指针拍不到，得用 `cursor-watch.ps1`」。
- **屏上一处变化都没有**，程序一个字节都没重新构建。

**上一件是「第三轮代码审查：四条全修」，见下面（2026-09-05，四道闸门全绿，未提交）。**

**第三轮代码审查：四条全修（2026-09-05，四道闸门全绿，未提交）。** 用户一句「审查代码并提出修改建议」，我把第二轮审查落地后的整份工作树又读了一遍（关键文件全部对着完整源码核实，不是只看 diff），报了四条（两条 P2、两条可选），他一句「全修」。修完新增 1 条单测。

- **一、退出登录赶上悄悄重登，新会话会被迟到的重登顶掉（`EmbySession`，本轮真正的硬骨头）。** 上一轮加的 `_generation` 修对了主场景，但作废分支无条件把 `_client` 置空，在一种更深的嵌套时序里会误伤：重登的 `AuthenticateByName` 还在路上 → 用户退出登录 → 用户**又在登录页登录成功** → 迟到的重登响应这才回来 → 置空。直接照原修法「只清自己装上的那个 client」都不够 —— 迟到的登录在检查之前就已经 `Adopt`，把用户刚建的 client 顶掉了。**修法是把登录拆成两半**：网络那一半（`AuthenticateAsync`）和落地那一半（`CommitSignIn`，档案、vault、client、落盘），重登那一趟在两半之间于 `_lifecycleGate` 锁下重新对一遍轮次 —— 对不上就**什么都不做**：不 Adopt、不写 vault、不落盘。他会话已经不在了，什么都不留正好；他退出之后又登录了，什么都不动正好保住他刚建的会话，调用方照「有人刷新过」重试（作废分支交回 `_client is not null`）。锁是必需的：没有它，「退出登录」可以正好落在轮次检查和落地之间，几条指令宽的窗口，落进去就是「已退出登录」喊过了、会话却又活了；锁里没有 await，最长一次持有是一次落盘。
- **二、退出登录现在把访问令牌清掉并落盘（`EmbySession.EndSession`）。** 这是审查摆出来确认意图、他点头修的那条既有行为：从前 `EndSession` 只把 `_client` 置空，`ProtectedAccessToken` 留在档案上，下次启动 `TryRestoreAsync` 拿它把用户悄悄拉回来 —— 「退出登录」跨不过一次重启。现在令牌跟着会话一起走。**记住的密码不动**：那是他在登录页上亲自勾的选项，留着它登录页才能让他一键回来；而且 `TryRestoreAsync` 的「没令牌就什么都不做」写在前几行，密码路径只在「令牌存在但过期」时才接手，所以清令牌就足够挡住自动恢复。这一条落进 `EndSession` 后，作废分支那句「什么都不写」也顺了 —— 迟到的重登不再往 vault 里塞一条新令牌。
- **三、菜单行部分成功时屏上那句实话按行挑词（`PlayerViewModel.RunMenuNodeAsync`）。** 「重置」一行六个 `set` 里五个成功一个失败，从前说「没成功」，比实际重；现在多命令行说「没全部成功」，截图那样的单命令行照旧说「没成功」。运行到底的设计没动。
- **四、筛选面板重建取消源时只丢没跑完的缓存（`FilterPanelViewModel.Open`）。** 上一轮把整个 `_requests` 清掉，其实令牌只把关请求的开始、管不着已经回来的结果 —— 现在用 `IsCompletedSuccessfully` 挑，完成了的留着，下一回开面板少跑几趟。
- **审查里核过没动的**（都对着源码验过，不是没看）：`Repair` 的节／列表／字典覆盖逐个数过是全的，语言列表里 `[null]` 元素在 `FromTokens` 一路可空容忍；下载完整性检查在自动解压场景安全（.NET 解压时把 `Content-Length` 置空，检查自动跳过）；`IsMuted` 可空依赖的 `WhenWritingNull` 是 `EmbyHttp.Json` 的全局配置且无其他消费方；`CommandAsync` 布尔链三处后端落点都对；`SettingsPage` 的 `Select`/`ShowHosted` 去重四个场景成立。
- **新单测**：「退出登录赶上重登、用户又已重新登录」—— `When` 钩子在重登的响应送达前同步跑完退出加重新登录（里面那趟靠一面旗跳过同一个钩子），断言重试一次就成、会话活着、令牌是用户的、`SignedOut` 只喊一声。旧的那条竞态测试照旧过。
- **闸门全绿**：Release 单节点构建 0 警告 0 错误、**711 项测试全过**（新增 1 条）、发布件重发（473 个文件 297.5 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」，报告里 token 出现次数 0。屏上一处看得见的变化都没有，没拍主题。
- **留了一句话没修**（核过、无害、与本轮四条无关）：`TryRestoreAsync` 在「令牌过期、密码重登也失败」那条路上清了内存里的令牌但不落盘，文件里那份旧令牌要等到下一次保存才消失 —— 代价是每次启动多试一趟死令牌，自愈，不动。

**上一件是「第二轮代码审查这一整批工作树，七条落地、五条判为不改、一条摆给用户」，见下面（2026-09-05，四道闸门全绿）。**

**第二轮代码审查这一整批工作树（2026-09-05，四道闸门全绿，未提交）。** 用户把一份审查报告交下来（八条 P1/P2 加八条次要），报告自己说「本轮只做审查，没有修改任何文件」。我把每一条都对着代码核了一遍，**十六条里真的会出错的有七条，五条读起来像 bug 但当前代码到不了那个状态或者代价大于收益，一条已经修好了（报告漏了），一条不是我该替他决定的**。新增 6 条单测，闸门 710 项全过。

- **一、`{"Playback": null}` 这样一份设置文件会让程序打不开**（`SettingsMigration` 新增 `Repair`）。每一节都有属性初始化器，可反序列化听 JSON 的 —— 文件里写 null 就真是 null，于是 `Normalize` 第一句 `settings.Playback.MarkWatchedPercent` 抛 `NullReferenceException`。**而那个异常不在 `SettingsStore` 认的「文件坏了」名单里（原来只认 JSON／IO／权限三类），所以备份、隔离、退回默认三条路一条都没走，窗口根本没出来。** 现在反序列化之后先补齐整节和整份列表／字典，再往下走；列表里的空洞（`"Servers": [null]`）一并丢掉。
  - **两条边界是故意的**：列表写成 null 补成空列表而不是那一项的装机默认值（`null` 和 `[]` 在文件里分不出来，而「一个都不选」本来就是这几行合法的答案）；**一个字段里的 null 不补**（`{"Servers":[{"Url":null}]}`），那要把每个设置类的每个字符串属性都列一遍，而那份名单必然跟不上以后新加的字段 —— 这一档改由 `SettingsStore` 兜。
  - **`SettingsStore.TryRead` 的 catch 从三类异常放宽到全部**。这个类的承诺是「Load 永不抛」，而那句承诺就是「程序打得开」。代价写在注释里：迁移代码自己的 bug 也会被当成「文件坏了」，于是用户的设置被改名收起来、程序回默认值 —— 所以那一份**始终留在磁盘上**（`Quarantine` 只改名），异常整条进日志。拿不准的时候，「打得开但设置回到默认」比「打不开」值钱。
- **二、服务器给的容器名能把下载的影片写到下载目录外面去**（`DownloadPlan.Extension`）。文件名那一半有 `Safe()` 挡着分隔符，**容器那一半从前一个字都没检查过**：`Container` 是服务器说什么就是什么，一个 `..\..\x` 拼在名字后头就跑出去了，而 `EmbyHttp` 那一头的 `File.Move` 是 `overwrite: true`。现在两支（路径、容器）共用一份规矩 —— 最多八个 ASCII 字母数字，不合规矩就当没有后缀（本来就写着「猜一个错的比没有更糟」）。一条单测拿六种恶意值走 `Path.GetFullPath` 断言落点仍在根目录里。
- **三、退出登录赶上悄悄重登，会话会被复活**（`EmbySession`）。令牌中途过期时那一趟重登是在 await 上等网络的，**那一刻界面线程是空的，用户按得到「退出登录」** —— 迟到的登录成功接着 `Adopt` 一个新 client：外壳已经回到登录页，而这个对象手上还捏着一个能用的令牌，之后每个请求都照旧通。现在 `EndSession` 递增一个 `_generation`，重登回来对不上就把这次登录作废（直接置空，不再喊第二声 `SignedOut`）。**这一条能写成单测全靠假传输层多了一个 `When(片段, 动作)` 钩子** —— 那个时间窗从外面根本命中不了。
- **四、设置页第一次打开「服务器」「诊断」「服务器控制台」，内嵌页面会白建一个**（`SettingsPage`）。`OnNavigatedTo` 先 `Select(分类)`，那一下的属性通知已经把 `ShowHosted` 喊过了，紧接着又无条件喊一遍 —— 头一个页面刚 `OnNavigatedTo` 就被第二次导航 `OnNavigatedFrom` 掉，控制台那一页还白起一次 WebView2。现在 `Select` 交回「选择动没动」，动了就不再自己喊。
- **五、设置页走兜底那条路时，离开不释放内嵌页面**（`SettingsPage.Release`）。原来是个空实现，注释写着「没什么要放的」—— 可**嵌套 Frame 里的内容在外层页面被导航走／被丢掉时收不到 `OnNavigatedFrom`**，于是控制台那个 WebView2（一个用户看不见的浏览器进程）会跟着日志订阅一起活下去。设置窗口那条路本来就自己喊 `ReleaseHosted`，漏的是「窗口建不出来、设置页开在主框架里」那条兜底路（`ShellPage.ShowSettings`），而它既会被导航走、也会在退出登录时被 `ReleaseContent` 丢掉。现在 `Release() => ReleaseHosted()`。
- **六、控制台上一轮的 15 秒看门狗会把刚开始的这一轮判成失败**（`DashboardPage`）。刷新把 `_settled` 放回 false，而看门狗只看这一个字段 —— 于是页头写上「15 秒内没有结果」，而新的一趟才刚发出去一秒。现在导航和刷新各递增一个 `_generation`，两条带延时的后续（取样 2.5 秒、看门狗 15 秒）醒来先对一下轮次。**管不着的那一半写在注释里**：`NavigationCompleted` 自己不带轮次，一次被顶掉的导航仍会以「失败」的身份回来。
- **七、错误响应体是整份读进内存之后才截断的**（`EmbyHttp.ReadBodySafelyAsync`）。**这一条不需要恶意服务器才碰得到**：反代或者门户网关在一个 502 上塞回来一整页 HTML 是常事。现在只读开头 8000 字节。顺手还钉了一条：下载完成时**服务器说了有多少就必须收到这么多**，少了就当失败（`.part` 由原来那个 catch 删掉）—— 传输层多数情况下自己会为提前断开的连接抛异常，这一句是把「多数情况」写成规矩，否则磁盘上留下的是一个名字正确、大小不对的影片。
- **八、筛选面板取消过的那个 `CancellationTokenSource` 不能再用**（`FilterPanelViewModel.Open`）。`Dismiss()` 只取消不换新的，接着开的话每次 `Lookup` 拿到的都是一个已取消的令牌，三张按需去问服务器的表会一直空着、日志里一个字都没有。**今天到不了这个状态**（面板跟着 `LibraryPage` 一起生一起死，而那一页没开缓存），所以这四行是拆一颗地雷、不是修一个看得见的病 —— 哪天有人给 `LibraryPage` 加上 `NavigationCacheMode`，那件事会以「筛选面板打不开了」的样子回来。改动比解释「为什么不改」的注释还短。
- **报告里已经修好的那一条**：`Audio.Device = "auto"` 归一成空串，`Normalize` 里已经有了（大小写不敏感），就是上一批留在文档里说「一条没做」的那一条 —— 它在这一批工作树里已经补上了。
- **判为不改的五条，理由记在这里**（都核过代码，不是没看）：
  - **图片缓存的 key 不带服务器 origin**。Emby 的条目 id 是按库里的路径推出来的，所以两台服务器 id 撞车的前提是路径也一样 —— 而那时候图片的 tag（也在 key 里）也一样，取到的就是同一张图。`Safe()` 那个「删掉非字母数字」确实能让 `a-b` 和 `ab` 撞成一个 key，但 id 和 tag 都是十六进制串，到不了。**改 key 的代价是所有人的海报缓存（可以到几个 G）当场全部失效重下一遍**，换一个碰不到的碰撞，不值。
  - **`HttpClient` 跟着跨源重定向会把 `X-Emby-Token` 带过去**（.NET 只对标准 `Authorization` 头做剥离）。真实分岔口是：能让 Emby 回一个 302 到别的主机的人，已经拿到这台服务器了 —— 而这个客户端本来就只连局域网里那一台。**关掉自动重定向的代价是真的会坏事**：反代后面 http→https 的重定向是常见部署，那一关就播不了。留着，写在这里。
  - **图片响应没有大小上限**（`GetBytesAsync`）。上面那条错误体的修法在这儿不成立：加一个「超过就丢」的上限等于给正常的大图（未缩放的 4K 背景图）加一条新的失败路径，而这一路本来就已经在 512 KB 上写日志了。要做的话该是「先看 `Content-Length` 再决定要不要下」，那是另一件事。
  - **`AudioDeviceCatalogue.LoadAsync` 没有 in-flight 去重**。全仓库只有一个调用方（`SettingsViewModel`，每个会话一次），自检读的是 `Known`（不 await）。两个并发调用会开出两个 libmpv 句柄，但没有任何代码路径产生那两个调用 —— 为一个谁都造不出来的状态加锁，那是给下一个读这段代码的人多一个要想明白的东西。
  - **`SettingsMigration` 的字段级 null**：见第一条那两条边界。
- **摆给用户的那一条（P1 里的第一条）**：外部 mpv.exe 这个后端把 `X-Emby-Token` 写在进程命令行上（`--http-header-fields-append=...`），任务管理器、进程工具、崩溃转储都读得到。**默认后端（内置 libmpv）不经过命令行**（`http-header-fields` 是在进程里设的），外部后端还要用户自己填 mpv.exe 的路径才用得上，所以这是一条要主动打开的路。两条改法各有代价，都不是「顺手」：写一份临时 mpv 配置文件传 header，等于把令牌明文落到磁盘上（正好抵掉「settings.json 泄了也不是一个可用凭据」这个 DPAPI 换来的性质）；改成 IPC 连上之后再注入、`--idle=once` 加 `loadfile`，安全上最干净，但会长出第二条起播路径、一种新的卡死方式（通道建不起来就永远待机），而且「关掉 IPC」那档就没法播了 —— 而这一切在这台机器上都验不了：验证时不许真实播放，本机也没装外部 mpv.exe。**所以这条留着没动，等他说要哪一种。**
- **闸门全绿**：Release 单节点构建 0 警告 0 错误、**710 项测试全过**（新增 6 条：设置文件整节 null、列表字典里的 null、结构对不上走坏文件那条路、容器名跑不出下载目录、路径后缀同一条规矩、退出登录赶上重登）、发布件重发（473 个文件 297.5 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」，一次过。**没动颜色，所以没拍六套主题**；屏上一处看得见的变化都没有 —— 报告里「设置页面 10 张卡片 + 3 个内嵌页面」「设置页重进不重建」「服务器控制台已就绪、已注入免登录」几行照旧，那三行正是第四、五、六条改到的地方。

**上一件是「代码审查 `856c215` 里那一整批」，见下面（2026-09-05，四道闸门全绿，未提交）。**

**代码审查 `856c215` 里那一整批（2026-09-05，四道闸门全绿，未提交）。** 用户一句「审查代码并提出修改建议」，我把那一批（当时还在工作树里，38 个改动文件加三个新文件）整份读了一遍，报了八条，他一句「全修」。前三条会真出错，后五条是注释、格式和覆盖。

- **一、上报给 Emby 的音量在「说不出来」的时候仍然报 100**（`PlaybackService.Build`）。判「有没有控制通道」用的是「这个句柄是不是控制类型」，可**外部 mpv.exe 那个句柄永远是控制类型**，通道有没有开另由 `HasControlChannel` 说（设置里关掉「启用 IPC」时就没有）。于是那条路上报的恰好是一份全默认快照 —— 音量 100、没静音，正是这一批要消掉的那件事，而注释写着这种情况报 `null`。现在按 `HasControlChannel` 判，并且句柄只读一次（`_current` 会在播放结束时被置空）。
- **二、同一帧连截两张，第二张不会存下来，而屏上说存了**（`MpvBaseline.ScreenshotTemplate`）。**mpv 不覆盖已经存在的截图，而只有模板里带序号时它才会另找一个名字**，否则那一张干脆不写。而这儿的时间码只到秒，暂停时一秒都不走 —— 「屏上这一帧」接着「原始画面」（那两行存在的全部理由就是比同一帧）必然重名。失败还被吞了两层：`CommandAsync` 只写日志，`RunMenuNodeAsync` 无条件弹「已保存到…」。模板末尾加 `-%02n`。两位而不是 mpv 自己那四位：这个数只是用来破平局的。
- **三、音频输出设备那一行有两个「跟随系统默认」**（`SettingsViewModel.DeviceChoices`）。`audio-device-list` **第一项永远是 mpv 自己的 `auto`**（英文「Autoselect device」），而我们又在最前面加了中文那一项 —— 一个行为两行，一中一英挨着。点了英文那个存下来的是 `auto` 而不是空串，下次进来这一行就显示成「Autoselect device」。现在按名字把 mpv 那一项滤掉，用的正是 `AudioDeviceCatalogue.AutoDevice`。**自检那一行是证据：从「下拉 10 项」变成「下拉 9 项」，而 mpv 报的还是 9 个设备。**
- **四、`AudioDeviceCatalogue.AutoDevice` 的注释说有两处在读它，两处都不是**（设置行用的是空串加「跟随系统默认设备」，兜底走的是 `Options` 那个通用分支），全仓库只有一条单测引用。和上一轮删掉的 `ShaderDecision.Label` 是同一类。第三条修完它就有了真正的读者，注释照实改。
- **五、两处注释还把「恢复默认设置」写在「关于」卡上**（`SettingRow.cs` 的 `SettingActionRow`、`SettingsPage.xaml` 那个模板）。那是头一版，正因为够不着才改成自己一张卡 —— 照注释搬回去不会有任何一关变红。两处都改成指名 `ResetCard`，并写上「别搬回去」和为什么。
- **六、设置页里两行工厂挤到了一行上**（网络缓冲和抖动之间的换行在某次编辑里丢了，成了一条两百列的长行）。视频输出那十四行读不成一列，而这个文件是整页顺序的唯一出处。
- **七、恢复默认那条反射扫描漏了「音频」整组**（`SettingsTests`）。逐个属性扫的是播放器/播放行为/视频/着色器四组，音频除了「音量要留下」一条断言都没有 —— 偏偏它是唯一一组用「列出要留的」写法的（以后新加的字段一律会被清掉），也是这一批刚长出三行的那一组。用反射的理由本来就是「以后加一项测试自己会算进来」，对它不成立。现在音频也进那一趟，比对的是 `new AudioSettings { Volume = 改过的那个值 }`。
- **八、播放器菜单里音频组和字幕组之间多了一行空行**，纯格式。
- **一条没做，记在这里** —— **后来做了，见上面那一批的「报告里已经修好的那一条」**：修完之后没人再选得到 mpv 的 `auto`，但**设置文件里如果已经存着 `auto`**，那一行会显示成「auto（设置文件中的值，这台机器上没找到）」—— 一句不实的话，因为它其实可用。当时没加迁移的理由是这个功能一行都还没提交、他自己的文件里那个键是空串，所以到不了这个状态。这一批工作树里已经补上了（`Normalize` 里一句大小写不敏感的 `auto` → 空串）。
- **闸门全绿**：Release 单节点构建 0 警告 0 错误、**702 项测试全过**（数没变：改的是三条现有断言加一趟扫描，没新增测试）、发布件重发（473 个文件 297.5 MB、11 个 GLSL）、`--self-check --dump-ui` 退出码 0 且末行「结果：全部通过」，一次过。**没动颜色，所以没拍六套主题**；屏上唯一看得见的变化是音频输出设备那个下拉少了一项，自检那一行已经把它读出来了。

**上一件是「设置里新增恢复默认设置」（2026-09-04，四道闸门全绿，已随 `856c215` 提交并推到 origin）。它和它下面那二十来批的原文都搬进归档了，见 [`docs/progress-2026-09.md`](docs/progress-2026-09.md)。**


## 等你在屏幕前确认的（2026-09-05 收成一张单子）

**这一节是新加的，理由是这些事从前散在二十来批汇报的最后一行里。** 每一批做完都留了一句「还剩用户在屏幕前才验得了的」，写完就沉进叙述去了，没有任何一处在跟踪 —— 收单子那天从 252 KB 的正文里数出 **二十件**，横跨十来轮。它们的共同点是四道闸门验不到：要么这台机器上注不进鼠标事件，要么**验证时不许真实播放**（那会写进他真实的观看历史和续播点），要么会写服务器上的东西而自检一律不碰。

**做完一件就划掉一件。** 一次二十分钟坐在屏幕前，能清掉五六轮的账。

**要真放一集才验得了的（画质那一摊）**

- [ ] **动画三档换成 ArtCNN 换得对不对。** 这是整张单子上最要紧的一件 —— 换放大器是档位表里唯一「改画面长相」而不是「改锐度」的改动。**现在程序里没有对照组**：Anime4K 那六个文件跟着出箱了，要 A/B 得先装回来（备份在 `artifacts/shader-attic/Anime4K/`，办法写在 `assets/shaders/README.md`；连它那条「关掉 sigmoid 光域」的前置条件一起）。差别最大的是 480p 动画放到 1440p 那一档。
- [ ] **真人甜点档换成 `ravu-lite` 之后，1:1 播放有没有变软。** 觉得软就把 `adaptive-sharpen` 加进低档缩小那一格，一句话的事。
- [ ] **色度重建（CfL）在真实片子上看不看得出。** 合成测试图上的对照是 `artifacts/shader-probe/cfl-ab-zoom.png`（左关右开）。真片子上最容易看出来的是动画字幕、片头 logo、纯色背景上的红字 —— 播放器「更多 → 着色器」里关掉再开一次就能对比。
- [ ] **图形接口换成 Vulkan 之后真的能播。** 我量的是他自己装的那个 `mpv.exe`（2026.08.12），不是发布件里那份 `libmpv-2.dll`（8-23 的构建）。那个 dll 里编进了 vulkan，所以能用，但只有放一集才算验过。
- [ ] **进播放器那半秒，画面两边还有没有那两条黑边。** 改法是点播放时先按服务器给的视频宽高整一次窗口形状。自检里没有真播放，所以这一处四道闸门看不见。
- [ ] **窗口化暂停之后进全屏，画面还留不留在左上角。** 四轮外部 mpv 探针都复不出来，所以这一件只有他自己放一集才验得了。万一还在，下一步是临时加一行日志把「客户区尺寸」和「mpv 那块子窗口的尺寸」一起打出来。

**播放器上手感那几件**

- [ ] **点了第二块屏上的应用之后，鼠标还会不会自动隐藏**（2026-09-05 报的那件，改法已落地）。走法就是他自己那一遍：第一块屏全屏放一集 → 点第二块屏上的 Claude 或 telegram → 手回到画面上不动两秒。**这一件四道闸门验不到**：藏鼠标的最后一段要指针压在 libmpv 那块子窗口上，而那要真放片子；自检里那一关又对「有人碰了鼠标」宽容。要是还藏不掉，`%LOCALAPPDATA%\EmbyNian\logs` 当天那份日志里搜「鼠标藏起来了」，那一行会写出静止了多久、指针上是哪个窗口、系统那时说屏上是什么形状 —— 有那一行就够定位下一步。
- [ ] **跳过片头之后是不是真的不自动暂停了。**
- [ ] **音量滚轮现在顺不顺**（一格从 5 改成了 mpv 自己的 2），**上方那个数字要不要带百分号。**
- [ ] **音量拉到 130 是不是真的更响。**
- [ ] **音量均衡三档**（不启用／连续跟随／对齐到固定响度）**哪一档是他要的。** 播放器右键菜单里那一行是唯一能在一部片子里当场对比三档的地方（不存盘）。
- [ ] **5.1 下混归一化**「不炸了但整体变轻一档」**值不值。**
- [ ] **音频输出设备**：耳机插上、在设置里那一行选到耳机、独占模式开着 → 声音在不在耳机上。这一行本身自检有证据（下拉 9 项、选中「跟随系统默认设备」），验不了的是真出声那一半。
- [ ] **真截一张图**，看落点（`%LOCALAPPDATA%\EmbyNian\screenshots`）和文件名对不对。菜单里那三行屏上没拍到 —— 右键菜单只能靠指针打开。

**会写到服务器上、自检一律不碰的三件**

- [ ] **真下载一个文件**（落点是系统「视频」目录下的 `EmbyNian\`）。
- [ ] **真删一次。** `DELETE /Items/{Id}` 会连服务器磁盘上的文件一起删，所以这一件请拿一个不要的条目试。
- [ ] **真刮削一次。** 刮削会覆盖手改过的片名和简介（刷新不会），图片两档都不覆盖。

**要他点头或动手的**

- [x] ~~**主页轮播上那块字，在一张亮画面上读不出来**~~（2026-09-05 按他一句「轮播图上的黑色渐变去掉」删掉三层暗罩之后的直接后果）。**同一天他自己给了答案，也做完了**：「给轮播页面边缘加上黑色的渐变」—— 四条边各一道、画面正中不压，见上面第一件活第四条。凭据是 `artifacts/shots/banner-left-rim-bright.png`（那张粉色动画剧照，加渐变之后片名、集号、两行简介和两颗键都读得出）比 `banner-corner.png`（同一张图、还没有渐变，那块字几乎没了）。
- [ ] **轮播顶上那条 120 高的渐变要不要也淡一档**（2026-09-05「渐变弄淡一些，面积弄少一些」那一趟只动了左、下、右三条）。**没跟着动是我判的**：那一条不是画面上的装饰，是标题栏那五颗按钮、系统那三颗窗口按钮和页眉右边那行读数唯一的底，而系统画的那三颗只换得了墨色、换不了形状 —— 淡到一张顶上接近纯白的剧照上那行读数读不出来，它就白垫了。**他说一句就改，改的是 `HomeBanner.xaml` 顶上那层三个 ARGB。**
- [ ] **主页右栏那一列用滚轮滚一下**（2026-09-05 新加的那一栏）。**这一条 09-05 缩小了一半**：那一栏当天改成了点击翻页（滚动条藏起来、上下两头浮出翻页条），而翻页这条路自检从此看得见、也判得动（「主页右栏翻页」那一关）。滚轮照旧滚得动那一列，只是这台机器注不进鼠标事件，所以「滚轮在那一列上滚的是那一列、滚到底再接给整页」这句话仍然只有他自己滚一下才验得了 —— **区别是它现在不再是看后面几张卡的唯一路**。
- [ ] **设置里主页版面那张表，真用鼠标拖一下。** 这台机器注不进鼠标事件，所以拖那一下从来没验过；每行那两颗上下箭头是眼下唯一有证据说它换得动次序的路。
- [ ] **服务器控制台里那两个主题下拉现在被我们按住了**（他在里面改只管这一次载入），是不是他要的；**强调色要不要也跟着走** —— 那一项是 `enableOnServer=true`，会写到服务器上，也就是会改他手机和网页上的 Emby。
- [ ] **「高帧率或高刷新率时使用音频同步」他现在是关着的**（他自己在界面上改的），也就是那次实测能把显卡占用从 50.1% 砍回 24.7% 的保护被他亲手关掉了。**想要它生效，得把这一行打开。**
- [ ] **字幕搜索这台服务器上答 0 条** —— 要服务器上装并启用字幕刮削插件（如 OpenSubtitles）才搜得到，接口本身通着。这一条不是客户端的事，但得让他知道别当成 bug。

## 最近做完的活（一行一件，细节看两份归档里的同名小节：[`2026-09`](docs/progress-2026-09.md) 和 [`2026-08`](docs/progress-2026-08.md)）

新的在前。归档里那一节的标题以这里的说法起头（后面可能多一句「复核第几条」），搜前半句就能落到原文 —— 先搜九月那份，找不到再搜八月那份。

- 2026-09-04 · 音视频输出补齐四批（[`音视频输出补齐-任务书.md`](音视频输出补齐-任务书.md)）：①音量天花板 100 → 130（mpv 自己的 `volume-max` 默认值，六处引用同一个常量）、动态范围压缩补上适用范围、新增音量均衡三档与 5.1 下混归一化；②截图从「完全不做」到菜单三行加「关于」卡上一行落点，模板由 C# 拼（mpv 的 `%F` 会得出 GUID、`%p` 里的冒号在 Windows 上非法）；③音频输出设备可选，临时开一个 libmpv 句柄枚举，播放信息与诊断页都写「本次实际用的」；④音轨语言从单选变成优先级列表（schema v10、`AudioTrackMode` 删掉）、上报音量与静音填上、新增宽片裁切填充（`856c215`）
- 2026-09-04 · 设置里新增「恢复默认设置」：判据是「设置页上有没有这一行」——用户挑的那类回默认，程序替他记的（窗口尺寸、上次看哪个库、各库排序筛选、播放器音量）和身份那一摊（设备 id、服务器、账号、令牌）一个字不动；四组整组用反射逐个属性抄，混着两类的两组反过来「列出要留的」；一定要往原来那几个子对象里覆盖而不是换新对象（容器和几个闭包攥着它们，换了就是屏上全默认、行为照旧），一条按引用相等的单测钉着（`856c215`）
- 2026-09-04 · 播放器三件（用户看片时报的）：跳过片头后自动暂停的病根是那一下点击被读了两遍（按钮自己收起来，同一次松手随后的 `Tapped` 落到画面上），修法是命中判断多问一句「这一下落在哪个元素上」；音量条补回上方的数字（天花板抬到 130 之后滑杆位置说不出是不是 100 以上）、滚轮步长改成 mpv 自己的 2 并让十赫兹轮询在跟手时让开、滑杆 240 → 300 高；暂停后进全屏画面留在左上角，改成 `Fill()` 连 mpv 那个子窗口一起改尺寸（`856c215`）
- 2026-09-04 · 服务器控制台的主题跟随外壳深浅：直接写 `light` 走不通（`skinmanager.js` 对非默认主题要过 Emby Premiere 注册检查，本机那个缓存键是 `false`），走得通的是 `auto` —— 它是 Emby 给「跟随系统深浅」留的免费口子，当场把 `requiresRegistration` 置为 false；两个键都是本机浏览器配置，一个字节不写到服务器的用户偏好上，强调色故意不跟（`856c215`）
- 2026-09-04 · 代码审查 `d9380c5` 那一批找出六处并全修：`v9` 把「高帧率或高刷新率时使用音频同步」拨回开（那个「关」是拿一句关于帧率的回答当成了关于屏幕的回答）、播放器着色器菜单不再拿「当前生效的链」反推片源类型、「视频同步」那一行的说明本来只有两个写手接上、两处日志说错话、`ShaderDecision` 上三个成员写了没人读（`856c215`）
- 2026-09-04 · `C:\mpv_config-2026.08.12` 的残留清干净了：真依赖只剩 `vulkan-1.dll`（libmpv 唯一一个非 Windows 的静态导入，找不到它连加载都失败），已入库；`lua51.dll` 不再装箱（解析 PE 导入表证明 libmpv 根本不加载它，省 734 KB）；csproj、`publish.ps1`、`verify-publish.ps1` 三处跟着断开，运行时那条「从 mpv.exe 目录拷 dll」的路整段删掉（`d9380c5`）
- 2026-09-04 · 「视频同步」那一行改成显示真正生效的值：病根和从前「画质预设写 scale、档位链又写一遍」同一个形状 —— 一个选项三个写手，设置页显示第一个而 mpv 收到最后一个；并成 `MpvOutputOptions.ResolveSync` 一个写手，页面读它的结论，72 种组合的契约单测把两边钉在一起（`d9380c5`）
- 2026-09-04 · 设置页每一行都写上它对应的 mpv 选项名：格式在一处组装、`Mpv(...)` 那个构造行的选项名是必填的（忘了写是编译错误，不是一行只能猜的说明），覆盖四张卡二十几行；顺带补了四条说明并把「视频同步在骗人」写明（`d9380c5`）
- 2026-09-04 · 图形接口默认换成 Vulkan（schema v8），另加一根「高帧率片源」轴：实测同一条链只换 `gpu-api`，Vulkan 45.0 fps 对 D3D11 8.7 fps，差 5.2 倍，而这道坎属于 compute pass 不属于 D3D11 本身；档位表从 48 格变 96 格，`ShaderDescriptor` 多一个 `ComputePasses` 由「描述必须和文件一致」那条单测钉着（`d9380c5`）
- 2026-09-04 · 自动 ICC 校色变成设置里一个默认关着的开关：实现是「只在打开时发 yes」，基线每次起播先发 `no`、设置压在上面，两层写同一个选项是有意的且界面不会骗人（设置页那一行是最后一个写它的人）；默认关的理由是客户端先问片源再定色调映射，桌面那份 ICC 会悄悄盖过这套判断，而开了它 HDR 直通就不再是直通（`d9380c5`）

- 2026-09-04 · 高刷新率屏幕上显示同步和插值自动回退到音频同步（阈值 120Hz，和用户自己 `mpv.conf` 里那条 `[fps-fix]` 一致）：量清了「插值贵」其实是「显示同步贵」——最后一趟渲染从每视频帧 24 次改成每次刷新 144 次，实测 24.7% → 50.1% 显卡；刷新率经 `HostWindow.RefreshHz` → `PlaybackTicket` 走到 `ResolveSync`，回退理由跟着决定一起返回并写进日志，自检多一关「屏幕刷新率读得到」，设置页那两行原来把账记错了也改了（细节在九月那份归档里）（`d9380c5`）
- 2026-09-04 · 画质预设删掉 HQ（用户 mpv.conf 里手抄的那一段）、补上 mpv 内置的 `fast`，三项预设现在全是内置 profile 名、客户端不再手写任何一条 mpv 选项；`fast` 的内容从发布件那份 libmpv 和外部 mpv.exe 两头量过，并记下「开着着色器时它只省抖动与光域、省不下那条链」。第 0 项当天按他一句「新增一个无」改名成「无」（空值），他看过之后一句「改回 default」，已按原样退回（细节在九月那份归档里）（`d9380c5`）
- 2026-09-04 · 画质档位重构第二批（架构）：每个着色器自己说它是什么（`ShaderDescriptor` / `ShaderLibrary`，每条描述由单测比着文件本身核）、档位表只剩点名而选项从着色器的前置条件合出来、链的合规规则做成纯函数（单测走四十八格、自检走发布件那八格）、切换档位那段判断搬进 `ShaderSwitch` 好让渲染层契约测试调得到真代码、`vo`/`gpu-api`/`hwdec` 纳入同一套档位但只报不改，并用外部 mpv 加合成测试片验掉「硬解出来的帧能正常进链」；同一轮按用户两句「动画换 ArtCNN」把动画三档全换成 ArtCNN，Anime4K 那六个文件跟着出箱（21 → 15 个着色器文件）（细节在九月那份归档里）（`d9380c5`）
- 2026-09-03 · 画质档位重构第一批：档位表按（放大倍数 × 真人／动画 × 显卡档）重排、老片源变成独立一轴、输出尺寸改按实际渲染目标算并跟得上窗口（双预案 + 400 毫秒防抖 + 0.05 回差）、每条链写一行可解释的判定；CfL 与放大器的先后关系用外部 mpv 的 `vo-passes` 实测掉并由单测钉住，顺带修正了 `assets/shaders/README.md` 里两条错的门控数字，以及自检「鼠标真等两秒就藏」那一关两处判错的判据（细节在九月那份归档里）（`d9380c5`）

- 2026-09-03 · 从 mpv.conf 移植的九个着色器配置组（NNEDI3、NNEDI3+、ravu-zoom、FSRCNNX、AnimeJaNai、Ani4K、AniSD、Anime4K、SSIM）按用户一句「删除这些着色器配置组」整个删掉，内置目录只剩自动规则挑的那五组；设置页下拉和播放器着色器菜单都是照这份目录生成的，所以一个文件加它的测试就够（`ba1ec03`）
- 2026-09-03 · 接着按「删掉残留着色器文件，有用再下载」：发布件不再拷整棵 shaders 树，只装配置组点名的十个 `.glsl`（585 个文件 323.4 MB → 469 个 296.3 MB），运行时那条回填的路跟着只补点名而缺的那几个；csproj 那份清单由单测钉住不许跟目录跑偏，用户自己那套 mpv 没动（`ba1ec03`）

- 2026-09-03 · 设置页那个「配置文件」分类（mpv.conf / input.conf 文本编辑器）按用户一句「删掉这个功能」整个删掉：Core 那一族四个文件、Shell 那一行连模板、两份测试、自检那一条、设置文件里那两个键一起走，只留下「粘进来的带引号路径去引号」那条判断（搬进 `Core/Configuration/TypedPath.cs`）（`ba1ec03`）

- 2026-09-03 · 用户交下来的七件里六件落地（评分来源可选平台、图片缓存上限可调、进度条与暂停键与动画时长与双击全屏、音量条摆正去边框去数字、置顶改画图钉图标、鼠标静止两秒自动隐藏 —— 最后这条由用户当天确认已好），**第三件「ASS 转 SRT」做完之后用户说不要了、已整个删掉（从未进过提交）**，细节在九月那份归档里（`4dc02e3`）

- 2026-09-02 · 图标的四个角磨圆了：形状照旧图量出来（230/256 的瓦片、四周留 13 像素、半径占瓦片边长 25.2%），比例存着让八档同形，透明度按面积八倍超采样算，1 位掩码改成按透明度生成（从前写全零，角一透明就会被画成方块）（`4feecc8`）
- 2026-09-02 · 应用图标换成用户给的那张（绿底、白色播放键、底下一层层圆弧）：照旧八档尺寸、结构一字节没变，缩放按面积平均并在线性光里算，16 像素那一档白键还看得出形状；那一版是满幅方角，随后按用户要求磨了圆（`33fd696`）
- 2026-09-02 · 卡片「更多」菜单补上十一条命令（合集、下载、封面图、字幕、刮削、刷新、扫库、从继续观看中移除、删除，加上「标记为未观看」现在和「已观看」同时出现）：哪几条出现由 `ItemMenu` 按条目类型定、`DownloadPlan` 定落点与文件名，自检多三关（搭菜单、问接口、造对话框），新增 `--show-menu` 好拍照（`33fd696`）
- 2026-09-02 · 主页大图上「最近添加」头顶那道横着的断层去掉了：横向与右沿两层暗罩不再在货架上沿收边、一路铺到带的下沿，代价是货架底下左边压到近黑（`db0b9cb`）
- 2026-09-02 · 继续观看那层玻璃压深（上沿 55%→65%、带的下沿 75%→87%），设置里主页版面那张表每行补上上下两颗箭头（拖动这台机器上验不了），自检首屏边界改成按位置问（`db0b9cb`）
- 2026-09-02 · 徽标统一挪到剧名上方、艺术图统一挪到右上角，两格之间不再互相退档，服务器没有的那一样就空着；主页轮播按用户的话不动（`db0b9cb`）
- 2026-09-02 · 详情页第一屏归剧照：富余高度从正文那张纸挪给尾部、尾部封顶 280，纸的上沿在屏上固定、窗口每高一像素多露一像素（拆掉了那道 120 的台阶）；剧页和季页也摆上「媒体源／音频／字幕」那一行（`db0b9cb`）
- 2026-09-02 · 右上角艺术图上限 260×146 → 320×180、页面窄过 1120 自己收起来；电影页／剧页／集页整页最底下多一条横幅，集页借剧集那一张（`db0b9cb`）
- 2026-09-02 · 进播放器那半秒画面两边的黑边：点播放时先按服务器给的视频宽高整一次窗口形状，mpv 的答案照旧半秒后回来做校正（`db0b9cb`）
- 2026-09-02 · 复查优化六条那一批代码并修掉两处：点「标记为已观看」后那个勾会先跳回旧值、命中缓存推时间戳不再在界面线程上等磁盘；交接文档归第二份档（`db0b9cb`）
- 2026-09-02 · 详情页那一行类型的颜色改回和副标题同一支暗墨（`Hyperlink` 自带的框架链接色盖掉了继承那一支），锁定比例那三处说法改成「不裁切轮播图」（`db0b9cb`）
- 2026-09-02 · 窗口记住自己多大、在哪块屏、是不是最大化着：那三个字段从前每次保存都写、没有一处读，是 v1 WinForms 外壳的空壳；摆回哪块屏由 `ScreenPlacement.Restore` 定（显示器拔了留尺寸丢位置），`--screen` 点过名的那一次不读也不写存档（`bdcd084`）
- 2026-09-02 · 启动时不再白问一趟媒体库列表：令牌探针那一趟的答案交给外壳，只给一次（`EmbySession.TakeRestoredViews`）；顺带给恢复登录这条路开了第一道自动化覆盖（`bdcd084`）
- 2026-09-02 · 删掉三处死代码：`GetSessionsAsync`/`GetActivityAsync` 连带五个 DTO、无人导航的 `PlaceholderPage`、工作树里的 `legacy/v1/` 11 个文件（`bdcd084`）
- 2026-09-02 · 点封面进详情页不再空等一趟往返：先用卡片那一份画一屏，完整条目回来只补差额；图不重取的判据下沉到 Core（`ItemArtwork.SamePictures`），存根导航故意不先画（`bdcd084`）
- 2026-09-02 · 图片缓存改成「最久没看过的先走」，而且不只开机清一次：命中就推时间戳（隔 6 小时才写一次），每下载 200 张再清一遍（`Emby/ImageCachePolicy.cs`，`bdcd084`）
- 2026-09-02 · 视图模型那层的失败路径补上覆盖：「哪一趟载入还算最新」和 `Failure.Describe` 搬进 Core，令牌中途过期那三档和传输层那两种「答不上来」各有测试；测试 558 → 612（`bdcd084`）
- 2026-09-02 · 详情页三处：海报按图自己的形状收窄不再被裁、艺术图挪到左上角并顶替徽标、集页把剧情说明和集列表换位（`1e42773`）
- 2026-09-02 · 媒体库也进主页：一个库一排「最近添加 · 库名」，设置里那张表按住拖排次序、勾选决定显不显示；顺带修掉一个会永久丢掉用户次序的数据损失洞（`1e42773`）
- 2026-09-01 · 主页第一屏＝一整张不裁切的 16:9 剧照占满窗口，继续观看压在它下半截上、没有板底（`1e42773`）
- 2026-09-01 · 锁定窗口比例改成 16:9，而且比例不算侧边栏；顺带修掉 `WM_GETMINMAXINFO` 里那个多算一条标题栏的旧账（`8bb1d96`）
- 2026-09-01 · 剧页上按钮和简介之间那一整段拿掉，艺术图挪到头图右下角（后来又挪到左上角，见上面 09-02 那一条）（`8bb1d96`）
- 2026-09-01 · 集页那行剧名的落点改成剧，不再是季（`8bb1d96`）
- 2026-09-01 · 集页版面重排、主页首屏锁定，加界面与设置四处优化（设置行两栏排版、「关于」卡、媒体库筛选条、详情页类型可点）（`d34275d`）

- 2026-08-31 · 播放器两处浮层间距改成量出来的：跳过按钮从「离底边 148」改成「离控制条一个间距」（控制条实测 107，从前那 41 像素的余量跟它毫无关系），统计面板不再在第二个文件里重写一遍标题栏的高度；统计读数按后端能力决定要不要一起问，三处 12×500 ms 重试合成一个；顺带被自检抓到一次「同值再赋一遍 → 指针假移动 → 鼠标不隐藏」（细节在上面「E 收尾」那一节）
- 2026-08-31 · 播放器控件对读屏软件和键盘报出名字：二十个控件各有名字（九颗全是图标的按钮从前只念得出「按钮」）、七处装饰读数退出无障碍树、OSD 按钮打开系统焦点框、换集那个转圈不再从开机转到退出（细节在上面「D 无障碍与焦点」那一节）
- 2026-08-31 · 播放器那三十五处写死的颜色搬进 Core 的一张表：梯级、透明度次序、对比度都由测试压住，自检新增一关把每支画刷读回来对账（细节在上面「C 颜色」那一节）
- 2026-08-31 · 播放中每一跳的开销：指针位置一跳只读一次（同一跳里两次读数会互相矛盾），命中测试不再每次造 transform 并逐个和 `TransformToVisual` 对账，章节刻度和统计行改成复用元素；顺带修掉「测试那一关跑的是两天前的旧程序还报全过」（细节在上面「B 每跳的开销」那一节）
- 2026-08-31 · 章节预览三条读数各自独立：服务器没抽过章节的文件也能悬停读到时间（细节在 [`2026-09 那份归档`](docs/progress-2026-09.md) 里「A 章节预览」那一节）
- 2026-08-31 · Skill 和记忆库清理一遍：项目规矩集中进新增的 [`CLAUDE.md`](CLAUDE.md)，两个自写技能搬出 Anthropic 托管目录并按项目实情重写，记忆 4 条 → 6 条（删掉过期快照、改掉一条写错的、去掉写死的坐标和测试项数），放行清单 70 条 → 31 条（去掉两条等于放行任意代码的通配项和一条会真的开始播放的）
- 2026-08-31 · 自检那两个大文件拆成 partial：3602 行 → 九个文件、1783 行 → 七个文件，正文一字节没动（`d896c3e`）
- 2026-08-31 · Shell 那层第一次有单元测试钉着：四段纯算术搬进 Core，测试 489 → 511（`de3608a`）
- 2026-08-31 · 头图罩子的渐变档位只留 Core 那一份，XAML 从它生成（`8a83b3b`）
- 2026-08-31 · 窄窗口下「媒体源／音频／字幕」排不下就换行，整排贴回左边（`734f752`）
- 2026-08-31 · 验证工具补上两个窟窿：`--scroll-half` 真的滚了、`--theme` 能把浅色主题拍下来（`4fad063`）
- 2026-08-31 · 发布件瘦掉 52 MB：拆开 Windows App SDK 那个总包（`32b76cf`）
- 2026-08-31 · 仓库瘦身、进度记录搬家、日志改成常开句柄（`a7cc3f9`）
- 2026-08-31 · 发布脚本加 `-NoArchive`：迭代时省掉那个没人看的压缩包（`272d00b`）
- 2026-08-31 · 修三处用户看得见的毛病：空封面、详情页慢、滚动卡顿（`2366f95`）
- 2026-08-31 · 整次 WinUI 3 重写入库：分支 `winui3-rewrite`、260 个文件（`bc8edbd`）
- 2026-08-31 · 头图那片暗色从满黑压到七成二，背景重新看得见
- 2026-08-31 · 详情页那道明暗分界下移到剧情说明底下，三块板去掉外圈
- 2026-08-31 · 集页和季页的头图改用剧集那一层的图
- 2026-08-31 · 窗口形状锁住，轮播那张大图不再越拉越被裁
- 2026-08-31 · 点封面不再先滑走一大段；标题栏那一条往下拉时渐变成正文的颜色
- 2026-08-30 · 一口气四条：齿轮回六齿细线、进详情页不再闪、背景改成固定的亚克力并铺进标题栏、片名头上那行眉字删掉
- 2026-08-30 · 重绘六个单元（词表、外壳、卡片和卡片带、详情页头图 + 主页轮播、设置页卡片 + 主题色板）与六套主题
- 2026-08-30 · 主页那条大图铺满窗口顶上那一整块，图源换成最近添加
- 2026-08-29 · 标题栏那一排五颗按键、账号挪到左下角、服务器和诊断进设置、音乐整片屏蔽、按家园必回主页
- 2026-08-29 · 鼠标静止两秒真的藏起来（关键是让系统重新问一次）
- 2026-08-29 · 壳层解耦、媒体库响应式与键盘／遥控器交互
- 2026-08-27～29 · 媒体库页和详情页对齐 Emby Theater（字母跳转条、视图切换、第二季卡带、推荐带、当前集高亮）
- 2026-08-25 · `PlayerPage.xaml.cs` 2717 行拆成一层视图加一个视图模型；拆掉 `ShellHost`

## 工作流的进展怎么记（2026-08-25 定的规矩）

原则：**工作流只在它能让代码更好的时候用，不为了快而用**。单纯图并行省时间的活直接自己做；能靠结构换到正确性的才值得散开 —— 一条结论交给互相独立的验证者去反驳、一个设计先要几种真的不同的思路、一次扫过比单个上下文装得下更多的材料。快只是副作用，不是理由。用了的话，编排者负责把结论写进这里，agent 自己不写。

- 起跑前先在这一节写一行「在跑什么、为什么、产物落到哪」。
- 结果落地就把**提炼过的结论**写进来，或者存到 `artifacts/` 再在这里留指针（就像上面那份 recipe）。原始记录不要往这里搬。
- 不要让并行的 agent 各自追加同一个文件 —— 九个写手交错写一份 Markdown 的结果不是进度记录。
- `resumeFromRunId` **只在同一会话内有效**。换了窗口，run id 就是死的；活下来的只有磁盘上的脚本、`journal.jsonl` 里已提交的结果，和写进本文件的东西。
- 教训是实打实的：`wf_8809d3d7-9eb`（journal 在 `.claude/projects/C--Users-89400/6e28ff25-…/subagents/workflows/wf_8809d3d7-9eb/journal.jsonl`）跑了 23 分钟、16 份记录，10 个 agent 里只有 1 个把结果提交上来 —— 就是被抢救成 `artifacts/shellhost-recipe.md` 的那份。另外 9 个全部 `started` 而没有返回，进程一换全部丢光，而当时本文件里一个字都没有。

## 未完成

- [x] 常用 mpv 参数改为下拉框、开关、滑块等专用控件 —— 设置页已用下拉框、开关和滑块承载字幕背景透明度、网络缓存等常用参数。曾经还有一个“配置文件”分类（mpv.conf / input.conf 原始文本编辑、编码与备份），2026-09-03 按用户一句「删掉这个功能」整个删掉了 —— 播放启动使用 `--no-config` / `config=no`，那两个文件本客户端从来不读，细节见九月那份归档
- [x] 服务器发现 —— 登录页与服务器管理页通过 Emby UDP 7359 广播发现局域网服务器，支持网卡定向广播、HTTP/JSON 响应解析、地址规范化、按服务器 ID 或地址去重，以及超时/取消
- [x] 生成安装包 —— `tools/publish.ps1` 可重复生成 WinUI Shell 的自包含目录与 ZIP；传 `-Msix` 且机器有 Windows SDK 时额外生成未签名 MSIX
- [x] 清理迁移期 WinForms 外壳 —— WinUI Shell 已成为唯一桌面外壳，解决方案、README、冒烟脚本与发布路径均已切换
- [x] 自定义标题栏（`InputNonClientPointerSource.SetRegionRects`）—— 浏览态保留系统标题栏和缩放边框；播放态移除 `WS_CAPTION`、保留 `WS_THICKFRAME`，由 XAML 绘制可拖动顶栏和最小化/最大化/关闭按钮，画面从窗口顶端开始

## 继续开发入口

项目目录：`C:\Users\89400\EmbyNian`

**先读本文件顶部的「在途工作」一节** —— 那里写着手上没做完的活和它的入口。**规矩和禁忌读 [`CLAUDE.md`](CLAUDE.md)**，那是唯一出处。

验证用两条命令（四道闸门的完整版在 `CLAUDE.md`）。注意 `dotnet` 不能直接用：`PATH` 上那个是 8.0.403，本项目要 .NET 10，SDK 在 `%USERPROFILE%\.dotnet\dotnet.exe`，没有进 `PATH`。

```
%USERPROFILE%\.dotnet\dotnet.exe build .\EmbyNian.sln -c Release --no-restore -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false -p:MSBuildNodeReuse=false
%USERPROFILE%\.dotnet\dotnet.exe run --project .\tests\EmbyNian.Tests\EmbyNian.Tests.csproj -c Release --no-build
```

**两条都不能少 `-c Release`**（这两行 2026-09-02 之前一直漏着）：`dotnet run` 默认去找 Debug 那份输出，配上 `--no-build` 跑的就是 `bin\Debug` 里那个陈旧程序，照样印「全部通过」、退出码照样 0 —— 08-31 撞见时那份陈的少了 160 条测试。理由的原文在 `CLAUDE.md` 的第二道闸门。

必须单节点构建：本机 Windows SDK 的多节点 workload resolver 会让并行方案构建偶发无输出失败。改过界面再跑一遍 `--self-check --dump-ui` 看页面截图。**自检默认开在副屏**（这台机器上是右边那块竖屏），所以它不会抢走你正在用的屏幕；要看着它跑就加 `--screen 1`，只有一块屏的机器上这个默认值自动失效、照旧开在主屏。

改过颜色就用 `--theme <id>` 各拍一张，别只看默认那套。逐页拍照走 `tools\shot.ps1`（`-ExeArgs "--theme midnight --show-settings"`），自检自己只留一张截图。**从前这里写的是「`--theme daylight` 是唯一的浅色，没登记进 `ThemeHost` 的画刷和系统自己画的那块标题栏只在它身上露出来」—— 那一套 2026-09-05 按他一句「删掉晴昼主题」删了**，五套主题从此全是深色，所以那类毛病现在没有东西逮得住；别再照旧话去跑 `--theme daylight`，它只回落到默认那套。

做完的活在两份归档里：[`docs/progress-2026-09.md`](docs/progress-2026-09.md)（09-01 到 09-02，外加 08-31 那两轮的尾巴）和 [`docs/progress-2026-08.md`](docs/progress-2026-08.md)（08-31 之前的全部，包括十个阶段的完成清单和 2026-08-27 那次验收复核）。
