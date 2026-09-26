using System.Globalization;
using EmbyNian.Configuration;
using EmbyNian.Playback;

namespace EmbyNian.Mpv;

/// <summary>
/// 一次起播在 mpv 实例上钉下的东西：管线、管线的必需项、Lua UI 的装配项、方向键的步长，以及票里那份选项表
/// **去掉末尾着色器链**之后的基线。换片快路只认逐项相等的签名。
/// <para>
/// 为什么必须逐项相等：mpv 的启动选项只在 <c>mpv_initialize</c> 之前有效（<c>mpv_set_option_string</c>
/// 之后只当属性写），所以「换个片但启动配置变了」这一情形的唯一诚实答案是关窗重开 —— 签名就是那条界线。
/// 方向键那四条 <c>keybind</c> 虽然能运行期改，却是按「这次起播时的设置」发出去的（见 <c>MpvSeekKeys</c>），
/// 所以它们也算这次起播的一部分：设置改过就不该拿旧的一层接着用，否则屏上印的秒数与按下去的位移会分家。
/// </para>
/// <para>
/// 为什么把着色器链排除在外：链是运行期能改的（这个客户端本来就有在播时切换档位的功能，见
/// <see cref="ShaderSwitch"/>），换片时按新票重写一遍即可。把它算进签名会让「同一部剧里 720p 与 1080p
/// 各一集」这种常见情形白丢掉快路。
/// </para>
/// </summary>
public sealed record PlaybackLaunchSignature(
    VideoPipelineKind Pipeline,
    IReadOnlyList<KeyValuePair<string, string>> PipelineOptions,
    IReadOnlyList<KeyValuePair<string, string>> UiOptions,
    IReadOnlyList<KeyValuePair<string, string>> SeekKeyOptions,
    IReadOnlyList<KeyValuePair<string, string>> BaselineOptions);

/// <summary>
/// 同一个 mpv 实例上换片（独占模式的选集与连播不关窗口）所需要的全部**判断**，与执行分开放在这里：
/// 判断是纯的，测试在没有 mpv 的地方就能钉死；执行只有一句 <c>loadfile</c> 加几十条属性写，住在
/// <c>LibMpvHandle.SwapToAsync</c>。
/// <para>
/// 由来（2026-09-19 用户令「换集不要每次都关窗重开」）：在这之前每次换集都是
/// <c>StopAsync</c>（mpv quit＝**顶层窗口销毁**）加 <c>StartAsync</c>（新 mpv、新窗口、uosc 重装），
/// 于是全屏退出再进、控制条消失一两秒、整窗闪一下。mpv 本来就支持在同一个实例上换源
/// （<c>loadfile</c>，窗口、全屏状态与整套 Lua UI 都留着），这条快路就是把那个能力接上。
/// </para>
/// </summary>
public static class InlineSwitch
{
    /// <summary>
    /// 两次起播的签名对得上吗。对得上才允许同一个实例换片；对不上就只能停掉重开（老路）。
    /// </summary>
    public static bool SameSignature(PlaybackLaunchSignature running, PlaybackLaunchSignature next) =>
        running.Pipeline == next.Pipeline
        && Same(running.PipelineOptions, next.PipelineOptions)
        && Same(running.UiOptions, next.UiOptions)
        && Same(running.SeekKeyOptions, next.SeekKeyOptions)
        && Same(running.BaselineOptions, next.BaselineOptions);

    /// <summary>
    /// 会随影片标题变、但换片时按新票重写的选项名 —— 不进必须相等的启动基线（见
    /// <c>LibMpvBackend.Baseline</c>），换片时由 <see cref="PerFile"/> 按新票重写。名单里今天只有
    /// 截图模板一条：常规换集集名不同，模板跟着集名走，把它算进基线就把最常见的同窗换片挡死在签名。
    /// </summary>
    public static IReadOnlyList<string> PerFileSignatureNames { get; } = ["screenshot-template"];

    /// <summary>
    /// 这个实例此刻还能不能接下一票（<c>LibMpvHandle.SwapToAsync</c> 的入口闸）。交接
    /// （<c>HandOver</c>）把旧一跑的收场信号完成掉，那是叫醒监视去发「停止」上报，<b>不是</b>实例在
    /// 收场 —— 所以「已交接且旧信号已完成」恰恰是快路的正常入口，从前正是这道闸把同窗换片整个挡死。
    /// 真正要挡的只有三种：用户叫停过（quit 在路上）、实例已销毁、以及没交接过但收场信号已完成
    /// （文件真的放完或报错，实例里已经没有可接续的东西）。
    /// </summary>
    public static bool CanTakeOver(bool stopRequested, bool handedOver, bool exitCompleted, bool destroyed) =>
        !stopRequested && !destroyed && (handedOver || !exitCompleted);

    /// <summary>
    /// 「这一集改过就不该带去下一集」的属性名。来源是应用自己那两份运行期名字清单 —— 画面菜单碰得到的
    /// 属性（<see cref="PlayerMenuCatalog"/> 的每一行）与任何着色器链会碰的名字
    /// （<see cref="ShaderGroupCatalog.NeutralOptions"/>），再加上不在菜单里却会跟着一集走的几只
    /// （字幕/音频延迟、倍速、音量、静音）。从菜单表推出来而不是手抄一份：菜单加一行，这里自动跟上。
    /// <para>
    /// 不在这里的名字就是**故意**的：<c>chapter</c>（章节位置）与 <c>screenshot</c>、<c>frame-step</c>、
    /// <c>sub-seek</c> 这些都不是设置项，换文件时 mpv 自己就把位置拨回去了。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> PerFilmNames { get; } = BuildPerFilmNames();

    /// <summary>
    /// 换片前把这一集的样子拨回去：名单上的每个名字，取新票里的值（同一个名字出现多次取最后一次，
    /// 与 mpv 自己解析重复选项的规矩一致），新票没说到这个名字就取 <paramref name="defaults"/> ——
    /// 那是句柄起播时问 mpv 要的 <c>option-info/&lt;名字&gt;/default-value</c>，也就是「这个播放器在没
    /// 被任何选项碰过时的值」。用 mpv 自己的默认而不是「上一集起播时的值」，是为了让设置在两集之间被改
    /// 过（比如关掉了去色带）这件事在下一集生效。
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> FilmScoped(
        IReadOnlyDictionary<string, string> defaults,
        PlaybackRequest next,
        string? profilesJson = null)
    {
        var expanded = MpvProfiles.Expand(next.PlayerOptions, profilesJson);
        var plan = new List<KeyValuePair<string, string>>(PerFilmNames.Count);

        foreach (var name in PerFilmNames)
        {
            var value = LastValue(expanded, name)
                ?? (defaults.TryGetValue(name, out var fallback) ? fallback : null)
                ?? Neutral(name);

            plan.Add(new(name, value));
        }

        return plan;
    }

    /// <summary>
    /// 票里没说、mpv 也没报出厂值的那些名字的兜底。实测（2026-09-19）：<c>vf</c>、<c>af</c>、
    /// <c>glsl-shaders</c>、<c>cscale</c> 没有 <c>default-value</c> —— 它们都是列表/空值选项，而「空」
    /// 就是出厂状态：没有滤镜、没有链、cscale 跟随 scale（<see cref="ShaderGroupCatalog.NeutralOptions"/>
    /// 里记的就是空串）。漏了这一手，上一集的视频滤镜与着色器链会跟着下一集走。
    /// </summary>
    private static string Neutral(string name) =>
        ShaderGroupCatalog.NeutralOptions
            .FirstOrDefault(option => string.Equals(option.Key, name, StringComparison.Ordinal)).Value ?? "";

    /// <summary>
    /// 这一票自己的那几条，与上一集无关、每次都重设一遍：起播位置、轨道选择、语言优先、外挂字幕、
    /// 请求头、标题，最后是暂停。
    /// <para>
    /// <c>aid</c>/<c>sid</c> 一定要显式写：票里没指定时它们该是 <c>auto</c>，而「不写」留在这台实例上的
    /// 是上一集的值 —— 上一集选了第 3 条音轨，下一集就会莫名其妙也选第 3 条。<c>sub-files</c> 同理，用
    /// 替换语义（不是 <c>-append</c>）：上一集的外挂字幕不能跟过来。
    /// </para>
    /// <para>
    /// <c>pause</c> 收尾：上一集是暂停着被换掉的（用户先暂停再选集），这一集该照常开始播 —— 新实例天生
    /// 是未暂停的，快路得自己把这一位写回去。
    /// </para>
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> PerFile(PlaybackRequest next)
    {
        var plan = new List<KeyValuePair<string, string>>(8)
        {
            new("start", Math.Max(0, next.StartSeconds).ToString("0.###", CultureInfo.InvariantCulture)),
            new("aid", next.AudioId?.ToString(CultureInfo.InvariantCulture) ?? "auto"),
            new("sid", next.SubtitlesDisabled
                ? "no"
                : next.SubtitleId?.ToString(CultureInfo.InvariantCulture) ?? "auto"),
            new("alang", next.AudioLanguage ?? ""),
            new("slang", next.SubtitleLanguage ?? ""),
            new("sub-files", next.ExternalSubtitles.Count == 0
                ? ""
                // sub-files 是 file-list（分号分隔、反斜杠转义），与 glsl-shaders 同类，不是逗号列表 —— 对随包
                // libmpv 实测确认（逗号不切分、分号才切成多条），换片走的属性接口分隔符与启动选项一致。所以用
                // MpvListValue.JoinFiles，别用逗号那家（Escape）；从前误按逗号列表拼，两条以上外挂字幕会被并成一条坏路径。
                : MpvListValue.JoinFiles(next.ExternalSubtitles.Select(uri => uri.AbsoluteUri))),
            new("http-header-fields", next.HttpHeaders.Count == 0
                ? ""
                : string.Join(",",
                    next.HttpHeaders.Select(header => $"{header.Key}: {MpvListValue.Escape(header.Value)}"))),
            new("force-media-title", next.Title),
            new("pause", "no"),
        };

        // 截图模板随片名走（每部片一个模板）：起播时它已经随 PlayerOptions 应用过一次，这里在换片时
        // 按新票再写一遍 —— planner 放的那条就在新票自己的选项表里。它因此不进启动基线
        // （<see cref="PerFileSignatureNames"/>），否则集名不同的常规换集在签名那一步就丢了快路。
        // 新票没有这条（截图功能整个没开）就不写：那时 directory/format 也不在，签名本来就不等。
        if (LastValue(next.PlayerOptions, "screenshot-template") is { } template)
            plan.Add(new("screenshot-template", template));

        return plan;
    }

    /// <summary>同一个名字在选项表里的最后一次出现 —— mpv 对重复选项就是这么解析的。</summary>
    private static string? LastValue(IReadOnlyList<KeyValuePair<string, string>> options, string name)
    {
        string? found = null;
        foreach (var (key, value) in options)
        {
            if (string.Equals(key, name, StringComparison.Ordinal)) found = value;
        }

        return found;
    }

    private static bool Same(
        IReadOnlyList<KeyValuePair<string, string>> left,
        IReadOnlyList<KeyValuePair<string, string>> right)
    {
        if (left.Count != right.Count) return false;

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].Key, right[index].Key, StringComparison.Ordinal)) return false;
            if (!string.Equals(left[index].Value, right[index].Value, StringComparison.Ordinal)) return false;
        }

        return true;
    }

    private static IReadOnlyList<string> BuildPerFilmNames()
    {
        var names = new List<string>();

        void Add(string name)
        {
            if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name);
        }

        // 画面菜单：命令的第 2 个参数就是它动的那个属性，vf/af 的属性就是命令名本身。
        foreach (var node in PlayerMenuCatalog.Flatten(PlayerMenuCatalog.Root))
        {
            foreach (var command in node.Commands)
            {
                if (command.Count == 0) continue;

                // ab-loop 只带命令自己（没有属性参数）：它动的就是那两个循环点。
                if (string.Equals(command[0], "ab-loop", StringComparison.Ordinal))
                {
                    Add("ab-loop-a");
                    Add("ab-loop-b");
                    continue;
                }

                if (command.Count < 2) continue;

                switch (command[0])
                {
                    case "set" or "cycle" or "cycle-values" or "add" or "multiply" or "toggle":
                        // chapter 是位置不是设置：换文件时 mpv 自己把它拨回去，写它反而把新片子拖到旧章上。
                        if (!string.Equals(command[1], "chapter", StringComparison.Ordinal)) Add(command[1]);
                        break;

                    case "vf" or "af":
                        Add(command[0]);
                        break;
                }
            }
        }

        // 任何链会碰的名字（含 glsl-shaders、deband、scale 那几家）。
        foreach (var (name, _) in ShaderGroupCatalog.NeutralOptions) Add(name);

        // 不在菜单里、却同样会跟着一集走的：两处延迟走快捷键与 ⚙ 菜单，倍速走控制条，音量/静音走壳。
        foreach (var name in new[] { "sub-delay", "audio-delay", "speed", "volume", "mute" }) Add(name);

        return names;
    }
}
