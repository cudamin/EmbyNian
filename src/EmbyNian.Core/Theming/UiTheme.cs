namespace EmbyNian.Theming;

/// <summary>
/// 一套主题里全部的颜色角色。控件从来不点名颜色，只点名角色 —— 这个类型就是角色表。
/// <para>
/// 二十个角色里只有五个是一套主题真正要自己定的（见 <see cref="UiThemes"/> 的 <c>Make</c>），其余全部
/// 推出来。这不是为了少打字：手写二十个色值乘五套主题，等于一百个各自可以写错的数，而且没有任何东西
/// 保证「悬停比静止亮一点」这类关系在每一套里都成立。推导反过来，把关系写成一份代码，几套主题共用。
/// </para>
/// </summary>
public sealed record UiPalette
{
    /// <summary>窗口底色，最暗（浅色主题里最亮）的一层。</summary>
    public required ThemeColor Window { get; init; }

    /// <summary>侧边栏、卡片、面板那一层。</summary>
    public required ThemeColor Surface { get; init; }

    /// <summary>压在 <see cref="Surface"/> 上还要再分出来的一层，比如输入框、海报底。</summary>
    public required ThemeColor SurfaceAlt { get; init; }

    /// <summary>浮起来的一层：飞出菜单、对话框。</summary>
    public required ThemeColor SurfaceElevated { get; init; }

    /// <summary>指针底下的那一层。</summary>
    public required ThemeColor SurfaceHover { get; init; }

    public required ThemeColor Border { get; init; }

    public required ThemeColor BorderStrong { get; init; }

    public required ThemeColor Text { get; init; }

    public required ThemeColor TextDim { get; init; }

    public required ThemeColor TextFaint { get; init; }

    /// <summary>压在 <see cref="Accent"/> 上的字，按对比度选出来的深墨或浅纸。</summary>
    public required ThemeColor TextOnAccent { get; init; }

    public required ThemeColor Accent { get; init; }

    public required ThemeColor AccentHover { get; init; }

    public required ThemeColor AccentPressed { get; init; }

    /// <summary>强调色兑进窗口底色，不透明。信息条、选中行的底。</summary>
    public required ThemeColor AccentSoft { get; init; }

    /// <summary>半透明的强调色，给侧边栏选中那颗药丸 —— 压在 Mica 上要透得过去。</summary>
    public required ThemeColor AccentMuted { get; init; }

    public required ThemeColor Danger { get; init; }

    public required ThemeColor Warning { get; init; }

    public required ThemeColor Info { get; init; }

    /// <summary>对话框背后那层遮罩，半透明。</summary>
    public required ThemeColor Scrim { get; init; }
}

/// <summary>
/// 一套主题：一个存进 settings.json 的 id、一个给下拉框看的名字、深浅，和整张角色表。
/// </summary>
/// <param name="Id">存进设置文件的值。改了它等于把用户选过的主题弄丢，所以只增不改。</param>
/// <param name="Name">设置里那个下拉框显示的名字。</param>
/// <param name="Note">下拉框那行说明，一句话。</param>
/// <param name="IsDark">
/// 深色还是浅色。不只是给人看的：框架自己那几百个没被覆盖的刷子跟着元素树的
/// <c>ElementTheme</c> 走，外壳就是按这个字段把树翻过去的。
/// <para>
/// 眼下每一套都是深色（唯一那套浅色「晴昼」2026-09-05 删了），但这个字段和它背后那条推导都留着 —— 理由写在
/// <see cref="UiThemes"/> 的类注释里，别顺手清。
/// </para>
/// </param>
public sealed record UiTheme(string Id, string Name, string Note, bool IsDark, UiPalette Colors);
