using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Playback;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// 字幕语言优先级那一行（「参考上图改成复选下拉」，2026-09-22）：一颗下拉按钮显示当前挑好的语言，点开是一张
/// 带勾选、按住能拖、也能用箭头换次序的语言表 —— 换掉了原先那个手打逗号分隔的输入框（<see cref="SettingTextRow"/>
/// 那条 <see cref="SettingsViewModel.Languages"/>）。
/// <para>
/// 这一行和主页版面那张表（<see cref="SettingHomeLayoutRow"/>）是同一套骨架，理由一样：勾选决定「进不进优先级
/// 列表」，次序决定「谁先匹配」，而这台机器上注不进鼠标事件，拖那一下没法自动做一遍，所以除了拖还留着两颗
/// 上下箭头 —— 那既是不使指针的人的路，也是这件事唯一验得到的形式（<see cref="Probe"/>）。两处不同：表放在
/// 下拉的浮层里而不是摊在页面上，存的是「勾了的那些名字，按屏上次序」而不是「全部加一个开关」。
/// </para>
/// <para>
/// 表里的每一项从 <see cref="TrackLanguagePriority.OrderedChoices"/> 来：已选的排在前头（照存档的次序），后面接上
/// 目录里还没选的语言，末了是「其他字幕」这个兜底项。存盘时把勾了的那些名字照屏上次序滤出来 —— 那正是 mpv 的
/// slang 想要的优先级次序。
/// </para>
/// </summary>
public sealed partial class SettingLanguagesRow : SettingRow
{
    private readonly Action<List<string>> _write;
    private readonly Action _save;
    private readonly string _placeholder;

    /// <summary>装表的那一下别当成用户改的 —— 既不写盘，也不重算下拉上那句话。</summary>
    private bool _quiet;

    internal SettingLanguagesRow(
        string label,
        string? note,
        string placeholder,
        Func<List<string>> read,
        Action<List<string>> write,
        Action save,
        IReadOnlyCollection<string>? exclude = null)
        : base(label, note)
    {
        _write = write;
        _save = save;
        _placeholder = placeholder;

        _quiet = true;
        try
        {
            // exclude：这张表压根不列的语言（字幕表传 普通话/粤语），offered 和 stored 都不出现。
            foreach (var (name, chosen) in TrackLanguagePriority.OrderedChoices(read(), exclude)) Adopt(name, chosen);
        }
        finally
        {
            _quiet = false;
        }

        Choices.CollectionChanged += (_, args) =>
        {
            MarkEnds();

            // 勾选那条路自己会挪位再写盘（Regroup ＋ Commit），挪位那一下的集合变动不能再写第二遍 ——
            // 否则一次勾选存两遍盘。
            if (_reordering) return;

            // 拖一次是两下：ListView 换位走的是 Remove ＋ Add（不是 Move），摘掉那一下表里少一项，写出去
            // 就照少一项存一遍，插回来那一下才是最终次序 —— 同 SettingHomeLayoutRow 的做法，只在 Add 上写盘。
            // 箭头走的是 Move，一下就是一下，落在下面这句上。
            if (args.Action is NotifyCollectionChangedAction.Remove) return;

            Commit();
        };

        MarkEnds();
        UpdateSummary();
    }

    /// <summary>Regroup 挪位那一下的集合变动别再写第二遍盘 —— 勾选那条路自己收尾。</summary>
    private bool _reordering;

    /// <summary>下拉里那张表，屏上的次序就是优先级的次序。勾了的永远聚在顶上，照优先级排；没勾的排在底下。</summary>
    public ObservableCollection<LanguageChoice> Choices { get; } = [];

    /// <summary>下拉按钮上那句话：勾了的语言照优先级次序连起来，一个都没勾时是占位文字。</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    /// <summary>把一项收进表里，接上它那两根线：勾变了要归位＋写盘，箭头按了要换位。</summary>
    private void Adopt(string name, bool chosen)
    {
        var choice = new LanguageChoice(name, chosen)
        {
            Changed = OnToggled,
            Shift = Shift
        };
        Choices.Add(choice);
    }

    /// <summary>
    /// 勾了或取消了一项：先把它归位 —— 勾了的聚到顶上那一批的末尾，取消的落到没勾那一批的开头 —— 再写一次盘。
    /// <para>
    /// 归位是这张表能用箭头／拖动排优先级的前提：勾了的项要是散落在目录次序的原位上（简体中文在第 1、英语在第
    /// 6），两个已选项中间隔着四个没勾的，「把英语往上挪一格」就只是跟没勾的粤语换了个位，优先级一点没动 ——
    /// 自检第一版就是这么红的。聚到一起之后，相邻的两个已选项一步就换得动。
    /// </para>
    /// </summary>
    private void OnToggled(LanguageChoice choice)
    {
        if (_quiet) return;

        Regroup(choice);
        Commit();
    }

    /// <summary>
    /// 把刚变过勾选的那一项挪到已选／未选的边界上：勾了的挪到已选那一批的末尾（新加的排在优先级最后），取消的挪到
    /// 未选那一批的开头（紧跟在还勾着的后头）。挪位走 <c>Move</c>，那一下的集合变动由 <see cref="_reordering"/> 挡住
    /// 不重复写盘。
    /// </summary>
    private void Regroup(LanguageChoice choice)
    {
        var from = Choices.IndexOf(choice);
        if (from < 0) return;

        var chosenCount = Choices.Count(item => item.Checked);
        var to = choice.Checked ? chosenCount - 1 : chosenCount;
        if (to < 0) to = 0;
        if (to == from) return;

        _reordering = true;
        try { Choices.Move(from, to); }
        finally { _reordering = false; }
    }

    /// <summary>勾选、次序有任何变动都从这儿过：把勾了的名字照屏上次序滤出来存盘，再重算下拉上那句话。</summary>
    private void Commit()
    {
        if (_quiet) return;

        _write([.. Chosen()]);
        _save();
        UpdateSummary();
    }

    private IEnumerable<string> Chosen() => Choices.Where(choice => choice.Checked).Select(choice => choice.Name);

    private void UpdateSummary()
    {
        var chosen = Chosen().ToList();
        Summary = chosen.Count > 0 ? string.Join("、", chosen) : _placeholder;
    }

    /// <summary>
    /// 把一项往上或往下挪一格 —— 那两颗箭头按的就是这个。走 <c>Move</c> 而不是自己摘了再插：一次集合变动、一次
    /// 写盘。到顶或到底时什么都不做（那时按钮本来就是灰的，这一句防的是键盘和读屏那条路）。同
    /// <see cref="SettingHomeLayoutRow.Shift"/>。
    /// </summary>
    private void Shift(LanguageChoice choice, int delta)
    {
        var from = Choices.IndexOf(choice);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Choices.Count) return;

        Choices.Move(from, to);
    }

    /// <summary>头一项的「上移」和末一项的「下移」置灰 —— 按下去也不动的按钮不该是亮的。</summary>
    private void MarkEnds()
    {
        for (var index = 0; index < Choices.Count; index++)
        {
            Choices[index].CanUp = index > 0;
            Choices[index].CanDown = index < Choices.Count - 1;
        }
    }

    /// <summary>
    /// 自检：勾一下真进列表、次序真换得动、下拉上那句话真跟着变 —— 就地造一张假表拨一遍，不碰设置文件、也不碰
    /// 屏上那一页（同 <see cref="SettingHomeLayoutRow.Probe"/> 的做法）。
    /// <para>
    /// 这一条非有不可，理由和主页那张表一样：单元测试进不到外壳这个程序集，而这台机器上注不进鼠标事件，勾一下、
    /// 拖一下没法自动做一遍。一张勾不动、换不了次序的下拉在屏上和好用的长得一模一样，行数、勾选那些读数也一个都
    /// 不会差。数写盘次数也是这一条的一半：勾一下、挪一格都只能写一遍盘。
    /// </para>
    /// </summary>
    internal static (bool Ok, string Detail) Probe()
    {
        List<string> stored = ["简体中文"];
        var saves = 0;

        var row = new SettingLanguagesRow(
            "探针", null, "占位",
            () => stored, value => stored = [.. value], () => saves++);

        LanguageChoice? Find(string name) => row.Choices.FirstOrDefault(choice => choice.Name == name);

        // 装表那一下不算用户改的：不写盘，下拉上先显示已选的那一个。
        var seeded = saves == 0
            && row.Choices.Count > 1
            && row.Choices[0] is { Name: "简体中文", Checked: true }
            && row.Summary == "简体中文";

        // 勾上「英语」：进列表，排在已选的后头（照它在目录里的位置），下拉那句话跟着长出来。
        if (Find("英语") is { } english) english.Checked = true;
        var ticked = string.Join(",", stored) == "简体中文,英语"
            && saves == 1
            && row.Summary == "简体中文、英语";

        // 把「英语」往上挪一格，越过「简体中文」：优先级次序真换了。
        Find("英语")?.UpCommand.Execute(null);
        var moved = string.Join(",", stored) == "英语,简体中文"
            && saves == 2
            && row.Summary == "英语、简体中文";

        // 取消勾选「简体中文」：退出列表，占位文字在全空时才回来（这里还剩英语，所以不回）。
        if (Find("简体中文") is { } simplified) simplified.Checked = false;
        var unticked = string.Join(",", stored) == "英语"
            && saves == 3
            && row.Summary == "英语";

        return (seeded && ticked && moved && unticked,
            $"假表：装值时{(seeded ? "不写盘且显示「简体中文」" : "写了盘或没显示当前值")}、"
                + $"勾「英语」后{(ticked ? "进表排在后头、写盘一次、下拉变「简体中文、英语」" : "没进表或没写对")}、"
                + $"上移一格后{(moved ? "换成「英语、简体中文」" : "没换动")}、"
                + $"取消「简体中文」后{(unticked ? "只剩「英语」" : "没退出或没写对")}"
                + $"；末了存 {string.Join(",", stored)}、写盘 {saves} 次");
    }
}

/// <summary>
/// 字幕语言表里的一项：屏上那个语言名、勾了没有，加上换次序那两颗箭头。和主页版面表里的
/// <see cref="HomeRowChoice"/> 是同一种东西 —— 勾变了就地喊回去（<see cref="Changed"/>），因为
/// <c>CheckBox</c> 改的是这一项而不是那张表，集合自己的 <c>CollectionChanged</c> 听不见；箭头反过来要动整张表
/// 的次序，那一下交回给表去做（<see cref="Shift"/>）。
/// </summary>
public sealed partial class LanguageChoice : ObservableObject
{
    internal LanguageChoice(string name, bool chosen)
    {
        Name = name;

        // 直接写属性：这一刻 Changed 还是空的（造完才由那一行挂上），所以 OnCheckedChanged 里那声喊是空转，
        // 不会把「装表」当成「用户点了勾」。同 HomeRowChoice 的做法。
        Checked = chosen;
    }

    /// <summary>存进设置文件、送给 mpv 的 slang 的那个语言名，也是屏上显示的字。</summary>
    public string Name { get; }

    /// <summary>勾变了的时候喊一声，把自己交回去；由 <see cref="SettingLanguagesRow"/> 挂上（它要知道是哪一项好归位）。</summary>
    internal Action<LanguageChoice>? Changed { get; set; }

    /// <summary>按了箭头往哪边挪一格；同上，由那张表挂上。</summary>
    internal Action<LanguageChoice, int>? Shift { get; set; }

    [ObservableProperty]
    public partial bool Checked { get; set; }

    /// <summary>那两颗箭头亮不亮：头一项不能再往上，末一项不能再往下。由那张表在次序变动后重算。</summary>
    [ObservableProperty]
    public partial bool CanUp { get; set; }

    /// <inheritdoc cref="CanUp"/>
    [ObservableProperty]
    public partial bool CanDown { get; set; }

    partial void OnCheckedChanged(bool value) => Changed?.Invoke(this);

    [RelayCommand]
    private void Up() => Shift?.Invoke(this, -1);

    [RelayCommand]
    private void Down() => Shift?.Invoke(this, 1);

    /// <summary>读屏的人听到的那一句，也是拖动时那块浮起来的东西的名字。</summary>
    public override string ToString() => Name;
}
