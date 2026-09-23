using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Playback;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// 字幕标题筛选那一行（「双语／特效这类词单独设 优先／默认／排除，支持自定义添加」，2026-09-22）：语言这一关选出
/// 「哪几条是中文」之后，这一关按标题里的关键词再挑一遍。一张表，一行一个词加一个三档选择（优先/默认/排除），
/// 底下一个输入框＋按钮可以自定义添加，每行末尾一颗「×」删掉。
/// <para>
/// 出厂给「双语」「特效」两个词、都设成默认（中立、不生效）。存的是 <see cref="KeywordRule"/> 列表，和选轨那头
/// （<see cref="SubtitleTitleFilter"/>）读同一份。顺序不影响打分（各词独立加减分），所以这张表不做拖拽换位 —— 比
/// 语言那张（<see cref="SettingLanguagesRow"/>）简单一档。
/// </para>
/// </summary>
public sealed partial class SettingTitleRulesRow : SettingRow
{
    private readonly Action<List<KeywordRule>> _write;
    private readonly Action _save;

    /// <summary>装表的那一下别当成用户改的 —— 不写盘。</summary>
    private bool _quiet;

    internal SettingTitleRulesRow(
        string label,
        string? note,
        Func<List<KeywordRule>> read,
        Action<List<KeywordRule>> write,
        Action save)
        : base(label, note)
    {
        _write = write;
        _save = save;

        _quiet = true;
        try
        {
            foreach (var rule in read()) Adopt(rule.Term, rule.State);
        }
        finally
        {
            _quiet = false;
        }
    }

    /// <summary>屏上那张表，一行一个关键词规则。</summary>
    public ObservableCollection<TitleRuleChoice> Rules { get; } = [];

    /// <summary>底下输入框里正在敲的新词。</summary>
    [ObservableProperty]
    public partial string NewTerm { get; set; } = string.Empty;

    /// <summary>把一条规则收进表里，接上它那两根线：态度变了要写盘，「×」按了要删掉。</summary>
    private void Adopt(string term, TitlePreference state)
    {
        var choice = new TitleRuleChoice(term, state)
        {
            Changed = Commit,
            Remove = row =>
            {
                Rules.Remove(row);
                Commit();
            }
        };
        Rules.Add(choice);
    }

    /// <summary>
    /// 「添加」：把输入框里的词加成一条新规则（默认态度＝中立），清空输入框。空词、或已经有的词（大小写不敏感）
    /// 不重复加 —— 重复只会让同一个词在打分里算两遍。
    /// </summary>
    [RelayCommand]
    private void Add()
    {
        var term = (NewTerm ?? "").Trim();
        NewTerm = string.Empty;
        if (term.Length == 0) return;
        if (Rules.Any(rule => string.Equals(rule.Term, term, StringComparison.OrdinalIgnoreCase))) return;

        Adopt(term, TitlePreference.Neutral);
        Commit();
    }

    /// <summary>态度、增删有任何变动都从这儿过：把整张表照屏上样子存回去。</summary>
    private void Commit()
    {
        if (_quiet) return;

        _write([.. Rules.Select(rule => new KeywordRule(rule.Term, rule.State))]);
        _save();
    }

    /// <summary>
    /// 自检：加一条真进表、换态度真写盘、按「×」真删掉、存回去的和屏上一致 —— 就地造一张假表拨一遍，不碰设置
    /// 文件、也不碰屏上那一页（同 <see cref="SettingLanguagesRow.Probe"/> 的做法）。这台机器上注不进鼠标事件，点
    /// 下拉、按按钮没法自动做一遍，假表是这件事唯一验得到的形式。
    /// </summary>
    internal static (bool Ok, string Detail) Probe()
    {
        List<KeywordRule> stored = [new("双语", TitlePreference.Neutral)];
        var saves = 0;

        var row = new SettingTitleRulesRow(
            "探针", null, () => stored, value => stored = [.. value], () => saves++);

        static string Dump(IEnumerable<KeywordRule> rules) =>
            string.Join(",", rules.Select(rule => $"{rule.Term}:{rule.State}"));

        // 装值那一下不算用户改的：不写盘，屏上先有那一条。
        var seeded = saves == 0 && row.Rules.Count == 1 && row.Rules[0] is { Term: "双语", State: TitlePreference.Neutral };

        // 把「双语」改成排除：写盘一次，存档跟着变。Dump 打的是枚举名（Exclude/Neutral），不是下拉上那三个中文字。
        row.Rules[0].State = TitlePreference.Exclude;
        var changed = saves == 1 && Dump(stored) == "双语:Exclude";

        // 添加「特效」：进表、默认中立、写盘一次。
        row.NewTerm = "特效";
        row.AddCommand.Execute(null);
        var added = saves == 2 && row.Rules.Count == 2 && Dump(stored) == "双语:Exclude,特效:Neutral";

        // 重复添加「特效」：不进表、不写盘。
        row.NewTerm = "特效";
        row.AddCommand.Execute(null);
        var noDupe = saves == 2 && row.Rules.Count == 2;

        // 删掉「双语」：写盘一次，只剩特效。
        row.Rules[0].RemoveSelfCommand.Execute(null);
        var removed = saves == 3 && row.Rules.Count == 1 && Dump(stored) == "特效:Neutral";

        return (seeded && changed && added && noDupe && removed,
            $"假表：装值{(seeded ? "不写盘且有「双语」" : "写了盘或没那条")}、"
                + $"改排除{(changed ? "写盘一次并存下" : "没写对")}、"
                + $"加「特效」{(added ? "进表中立并写盘" : "没进表或没写对")}、"
                + $"再加一遍{(noDupe ? "不重复不写盘" : "重复了或写了盘")}、"
                + $"删「双语」{(removed ? "只剩特效并写盘" : "没删对")}"
                + $"；末了 {Dump(stored)}、写盘 {saves} 次");
    }
}

/// <summary>
/// 标题筛选表里的一项：一个关键词加它的三档态度。态度变了就地喊回去（<see cref="Changed"/>）、「×」把整项交回给
/// 表去删（<see cref="Remove"/>）—— 同 <see cref="HomeRowChoice"/> 的分工。三档用一个下拉呈现，
/// <see cref="StateIndex"/> 是下拉的选中项、映到 <see cref="State"/>。
/// </summary>
public sealed partial class TitleRuleChoice : ObservableObject
{
    /// <summary>下拉里那三档的顺序，和 <see cref="StateLabels"/> 一一对应。</summary>
    private static readonly TitlePreference[] Order =
        [TitlePreference.Prefer, TitlePreference.Neutral, TitlePreference.Exclude];

    internal TitleRuleChoice(string term, TitlePreference state)
    {
        Term = term;

        // 直接写属性：这一刻 Changed 还是空的（造完才由那张表挂上），OnStateIndexChanged 里那声喊是空转。
        State = state;
    }

    /// <summary>要在字幕轨标题里找的那个词。</summary>
    public string Term { get; }

    /// <summary>下拉的三个选项：优先 / 默认 / 候补。实例属性（不是 static），x:Bind 才够得着它。和 <see cref="Order"/> 一一对应。</summary>
    public IReadOnlyList<string> StateLabels { get; } = ["优先", "默认", "候补"];

    /// <summary>态度变了的时候喊一声；由 <see cref="SettingTitleRulesRow"/> 挂上。</summary>
    internal Action? Changed { get; set; }

    /// <summary>按了「×」把自己交回去删；同上，由那张表挂上。</summary>
    internal Action<TitleRuleChoice>? Remove { get; set; }

    /// <summary>下拉当前选中的档位（<see cref="Order"/> 的下标）。</summary>
    [ObservableProperty]
    public partial int StateIndex { get; set; }

    /// <summary>这一项此刻的态度。set 只是挪下拉的选中项（<see cref="StateIndex"/>），显示与存储都从那儿走。</summary>
    public TitlePreference State
    {
        get => Order[Math.Clamp(StateIndex, 0, Order.Length - 1)];
        set => StateIndex = Math.Max(0, Array.IndexOf(Order, value));
    }

    partial void OnStateIndexChanged(int value) => Changed?.Invoke();

    [RelayCommand]
    private void RemoveSelf() => Remove?.Invoke(this);

    public override string ToString() => Term;
}
