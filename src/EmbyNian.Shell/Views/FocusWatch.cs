using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 「指针/焦点 整圈变强调色」里焦点那一半。胶片格的其余状态都是卡片自己的数据（看到一半、还剩几集、
/// 全看完），只有这两个是「用户此刻在哪」，而这两个当中只有焦点不在卡片身上。
/// <para>
/// 键盘焦点永远落不到 <see cref="PosterCard"/> 或 <see cref="EpisodeRow"/> 上：能接焦点的是外面那个容器
/// —— 媒体库里是 <c>ItemContainer</c>，主页和详情页里是包着卡片的 <c>Button</c>。页面各写一遍等于同一件
/// 事写三遍，所以卡片自己往上找一层：视觉树上第一个 <see cref="Control"/> 就是那个容器。
/// </para>
/// <para>
/// 往上找「第一个」而不是「第一个可 Tab 停靠的」，是因为找错的代价不对称。找窄了最多是某个模板在中间
/// 塞了别的控件、框线跟着那一层亮；找宽了 —— 比如跳过容器一路找到 <c>ItemsView</c> 或 <c>ScrollView</c>
/// —— 列表里任何一个东西拿到焦点，满屏的卡片会一起亮起来。
/// </para>
/// <para>
/// <c>GotFocus</c>/<c>LostFocus</c> 是冒泡事件，所以焦点落到容器内部（悬浮层那三个按钮）也算数，这正是
/// 要的：那时指针本来就在这张卡上。
/// </para>
/// </summary>
/// <param name="inside">卡片里的任意一个元素，从它开始往上找容器。</param>
/// <param name="paint">
/// 焦点来了传 true、走了传 false。调用方把它和指针那一半或起来 —— 两个来源各管自己的旗子，谁都不会
/// 把对方的状态擦掉。
/// </param>
internal sealed class FocusWatch(FrameworkElement inside, Action<bool> paint)
{
    private Control? _host;

    /// <summary>
    /// 挂上。<c>Loaded</c> 里调用，不能更早：容器是在卡片进树之后才在它上面的，构造函数里往上找什么都
    /// 找不到。重复调用无害。
    /// </summary>
    public void Attach()
    {
        if (_host is not null) return;

        _host = Host(inside);
        if (_host is null) return;

        _host.GotFocus += OnGot;
        _host.LostFocus += OnLost;

        // 回收回来的容器可能本来就带着焦点：它自己没有 Loaded/Unloaded，只有卡片有。所以挂上的时候
        // 先照实读一次，而不是假定「新来的都没焦点」。
        paint(_host.FocusState != FocusState.Unfocused);
    }

    /// <summary>
    /// 摘掉。<c>Unloaded</c> 里调用 —— 容器活得比卡片长，不摘就是往一个还在用的容器上挂着一个指向已经
    /// 回收掉的卡片的处理器。顺手把框线灭掉，理由和 <see cref="HoverWatch.Leave"/> 一样：容器换了个卡片
    /// 回来，不能带着上一张的状态。
    /// </summary>
    public void Detach()
    {
        if (_host is null) return;

        _host.GotFocus -= OnGot;
        _host.LostFocus -= OnLost;
        _host = null;

        paint(false);
    }

    /// <summary>自检：找到容器了没有 —— 找不到就是这条状态整个不存在，而那是看不出来的。</summary>
    public bool Probe() => _host is not null;

    private void OnGot(object sender, RoutedEventArgs e) => paint(true);

    private void OnLost(object sender, RoutedEventArgs e) => paint(false);

    private static Control? Host(DependencyObject start)
    {
        for (var node = VisualTreeHelper.GetParent(start); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is Control control) return control;

        return null;
    }
}
