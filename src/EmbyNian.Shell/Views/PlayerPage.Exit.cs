using EmbyNian.Diagnostics;
using EmbyNian.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 退出播放那一趟：最后一块面、最后一帧的压暗，以及跟着它们一起收摊的东西。
/// <para>
/// <b>这块面的来历（2026-09-20 用户截图）。</b>退场那一趟里，屏上同时有：缩在原位置的最后一帧、一圈
/// 近黑的窗口底色、底边上一条通亮的细线、以及一个停在原地的光标。等淡出走完本页一收，浏览页才「啪」
/// 地换上来 —— 用户看到的就是那一瞬间的半成品。
/// </para>
/// <para>
/// <b>2026-09-20 晚重新设计（用户令「重新设计集成模式下退出播放界面返回主页的动画」）。</b>当初这块
/// 面之所以存在，是因为「整页淡出」淡不动画面 —— 画面是挂在 <c>VideoHost</c> 上的 SpriteVisual，
/// 不吃页面那层不透明度。于是当时的办法是拿一块不透明的面盖住它，再把那块面淡掉。用户看到的因此是
/// 「一次沉黑之后主页出现」；而且点下返回的那一帧，画面会骤然从全亮掉到 15%（一支旋钮一步拧到 0.85）。
/// </para>
/// <para>
/// 现在画面自己会淡了：<c>CompositionVideoTarget.RetainDim</c> 被<b>逐拍</b>拧过去
/// （<see cref="DrivePlayerExit"/>），从全亮一路溶解到零；同时那一帧在退场期间是<b>铺满</b>的
/// （<c>RetainFill</c>），于是窗口从播放几何跳成浏览几何时它不缩、不跳，自始至终是「同一幅满屏的
/// 画」。最终观感是<b>这一帧溶解进浏览页</b>，而不是「沉黑一下再换页」。
/// </para>
/// <para>
/// 这块面因此退居二线：它仍旧在第 0 拍立起（<see cref="BeginPlayerExit"/>），但它在画面<b>之下</b>，
/// 只在「窗口刚变形的那些拍、而画面还没把新宿主盖住」时才被看见 —— 它是那道窗口跳变的保险，不是
/// 淡出的主角。色号取自应用级字典的 <c>EgWindowBrush</c>，与浏览页同一个色号。
/// </para>
/// <para>
/// <b>这里管三件成对的事。</b><see cref="BeginPlayerExit"/> 在第 0 拍立起面、把留帧交给「铺满」；
/// <see cref="EndPlayerExit"/> 在落定拍把三样都收回去。成对是刻意的：一个只开不收的标记（面没收掉、
/// 旋钮停在 1、铺满旗还开着）会在下一趟进场时以「一页黑的、被压暗的、被裁过的播放页」出现，
/// 而那时候没有人会想到来这个文件里找。
/// </para>
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>
    /// 退场的第 0 拍：立起那块保险面、把留帧交给「铺满」，<b>但不压暗它</b>。调用点只有
    /// <c>LeavePlayer</c> 一处 —— 写在标记里那块面默认是收起的，所以「没走这个函数就直接退场」不会
    /// 露出它。
    /// <para>
    /// 从前这里是一步拧到 0.85：点下返回的那一帧，屏上的画面骤然掉到 15%，之后才是整页淡出 —— 那就是
    /// 一次硬切。压暗现在归 <see cref="DrivePlayerExit"/> 逐拍拧，起点是全亮。
    /// </para>
    /// </summary>
    private void BeginPlayerExit()
    {
        ExitBackdrop.Visibility = Visibility.Visible;
        _videoTarget.RetainFill = true;
        _videoTarget.RetainDim = 0;
    }

    /// <summary>
    /// 退场每一拍：把留帧的压暗推到 <see cref="PlayerMotion.ExitDimAt"/> 说的那一档。
    /// <para>
    /// 演出者由 <c>TransitionPage</c> 那同一颗 16ms 计时器兼任（<c>LeavePlayer</c> 把它当
    /// <c>progress</c> 传进去），所以画面的溶解与页面的姿势同拍、同一条轨道 —— 不另起一颗表。
    /// </para>
    /// </summary>
    private void DrivePlayerExit(double progress) =>
        _videoTarget.RetainDim = PlayerMotion.ExitDimAt(progress);

    /// <summary>
    /// 落定拍收回去，与 <c>Stage.Visibility = Collapsed</c> 同一处（<c>CompletePlayerExit</c>）。
    /// <b>不把这支旋钮交给 <c>RetainLastFrame</c> 的 setter 一起管</b>：那条路在换集的每一趟也会走一遍
    /// （<c>ShowCoverPlate</c> 把留帧关掉再打开），在那里顺手复位会变成「换集时把压暗系数摸一遍」——
    /// 换集并不退场，这个数就不该动。
    /// </summary>
    private void EndPlayerExit()
    {
        ExitBackdrop.Visibility = Visibility.Collapsed;
        _videoTarget.RetainDim = 0;
        _videoTarget.RetainFill = false;
    }

    /// <summary>
    /// 给退场底上色，取的是<b>应用</b>当前主题的窗口色，而不是在本页的主题作用域里求值的一支框架画刷。
    /// <para>
    /// 本页是 <c>RequestedTheme="Dark"</c>，所以 <c>{ThemeResource LayerFillColorDefaultBrush}</c> 在这一页
    /// 里永远解析到 Dark 那一份 —— 一套浅色主题（晴昼）上退出播放，用那一支会露出一块深色的面盖住浅色的
    /// 浏览页。绕开这件事还有另一半原因：<c>PlayerPalette.Brushes</c> 里每一支都是
    /// <c>ThemeHost.ToColor(…) + StaticResource</c> 那套「先画上去、换主题时再画一遍」的，退场底跟着走才能
    /// 在中途换主题时和浏览页一起翻过去。
    /// </para>
    /// <para>
    /// 色号来源是 <c>EgWindowBrush</c> 在<b>应用级</b>字典里的那一支 —— 和 <c>ShellPage.ContentHost</c> 的
    /// 底、和 <c>HostWindow.BaseBrush</c>、和 <c>LayerFillColorDefaultBrush</c> 是同一个色号（Dark 下都是
    /// <c>#FF0C1116</c>）。
    /// </para>
    /// </summary>
    private void PaintExit()
    {
        // 解析不到就保持上一支：这个色号在六套主题里都是一个「窗口色的近黑」，画错的代价远小于不画 ——
        // 不画的话退场底是全透明的，等于回到了用户截图里那个观感。
        if (Application.Current.Resources["EgWindowBrush"] is not SolidColorBrush reference) return;

        ((SolidColorBrush)Resources["PlayerExitBrush"]).Color = reference.Color;
    }

    /// <summary>
    /// 换主题时重画一次退场底。挂在与 <c>PaintPalette</c> 同一处（构造函数的同一行序列里），因为两件事
    /// 的理由是同一个：这几支颜色是画上去的，不是 <c>ThemeResource</c> 求出来的。
    /// </summary>
    private void WireExitTheme() => ThemeHost.Changed += _ => PaintExit();

    /// <summary>
    /// 退场底此刻的颜色，自检读它。<b>故意它不在 <see cref="ProbePalette"/> 那张表里</b>：那张表比的是
    /// 「本页画刷 vs PlayerPalette 那张 Core 的表」，而这一支的色号来自主题、不是来自播放器调色板 ——
    /// 混进去会让那张表出现一个它管不了、也断言不了的条目。
    /// </summary>
    internal Color ExitBackdropColor =>
        (Resources["PlayerExitBrush"] as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.Transparent;
}
