using EmbyNian.Playback;
using EmbyNian.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace EmbyNian.Shell.Views;

public sealed partial class PlayerPage
{
    private readonly AccessibilitySettings _timelineAccessibility = new();
    private readonly UISettings _timelineUiSettings = new();
    private readonly SolidColorBrush _timelineContrast = new();
    private bool? _timelineContrastActive;
    private bool? _timelineEffectsActive;

    /// <summary>
    /// 时间轴那一行底与墨的换挡：高对比度、系统「透明效果」开关各有一档。
    /// <para>
    /// 2026-09-28 晚用户令「亚克力效果时有时无，去掉亚克力效果吧」之前，这里还管着一条 250–1500ms 的
    /// 采样计时器（视频区域取样 → BoxBlur → 底图 ＋ AcrylicBrush），但取样被 seek、拖动、换片一次次作废，
    /// 亚克力跟着一次次熄灭 —— 用户看到的就是「时有时无」。整条采样链随这一条令退役（连同
    /// <c>IVideoSurface.SetBackdropCapture</c> 与 <c>CompositionVideoTarget</c> 的 capture epoch），
    /// 这一拍只剩换挡：底色在三支里选一支（高对比度＝系统底色；高级效果开＝半透明的
    /// _timelineFallback；关＝实色 _timelinePaper），五支章节图的墨与纸跟着换。
    /// </para>
    /// <para>
    /// 两套系统设置没有变化通知可挂（AccessibilitySettings 有，UISettings 的 AdvancedEffectsEnabled
    /// 没有），所以换挡靠**轮询**：调用方（OnStatusApplied、GrowTimeline、探针）在各自的节拍上跑一遍，
    /// 两个 bool 没变就走人。
    /// </para>
    /// </summary>
    private void UpdateTimelineMaterial()
    {
        if (TimelineMaterial is null) return;
        var contrast = _timelineAccessibility.HighContrast;
        var effects = _timelineUiSettings.AdvancedEffectsEnabled;
        if (_timelineContrastActive != contrast || _timelineEffectsActive != effects)
        {
            _timelineContrastActive = contrast;
            _timelineEffectsActive = effects;
            _timelineContrast.Color = _timelineUiSettings.GetColorValue(UIColorType.Background);
            TimelineMaterial.Background = contrast ? _timelineContrast : effects ? _timelineFallback : _timelinePaper;
            _timelineInk.Color = contrast ? _timelineUiSettings.GetColorValue(UIColorType.Foreground)
                : ThemeHost.ToColor(TimelineChapterMap.Foreground);
            _timelinePaper.Color = contrast ? _timelineUiSettings.GetColorValue(UIColorType.Background)
                : ThemeHost.ToColor(TimelineChapterMap.Background);
            _timelineOpening.Color = contrast ? _timelineUiSettings.UIElementColor(UIElementType.Highlight)
                : ThemeHost.ToColor(TimelineChapterMap.OpeningColor);
            _timelineEnding.Color = _timelineOpening.Color;
            _timelineAd.Color = contrast ? _timelineUiSettings.UIElementColor(UIElementType.Hotlight)
                : ThemeHost.ToColor(TimelineChapterMap.AdvertisementColor);
        }
    }
}
