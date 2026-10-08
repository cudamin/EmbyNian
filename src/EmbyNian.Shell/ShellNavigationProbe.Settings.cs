using EmbyNian.Configuration;
using EmbyNian.Infrastructure;
using EmbyNian.MoviePilot;
using EmbyNian.Playback;
using EmbyNian.Services;
using EmbyNian.Shell.ViewModels;
using EmbyNian.Shell.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EmbyNian.Shell;

internal sealed partial class ShellNavigationProbe
{
    private async Task SettingsEntranceAsync()
    {
        using var fixture = CreateFixture();
        using var moviePilot = new MoviePilotClient(new RejectNetwork());
        var paths = new AppPaths(Path.Combine(_options.Paths.Root, "settings-motion"));
        paths.EnsureCreated();
        // 本例测动效，不扫描机器上的数千字体；给现有缓存放入空目录，字体扫描另有专项探针。
        var fonts = new FontLibrary();
        Field(fonts, "_scan", Task.FromResult(FontCatalogue.Empty));
        var evidence = new List<string> { $"系统动画：{HomeMotion.AnimationsEnabled}" };
        var page = new SettingsPage();
        page.ViewModel.Attach(fixture.SettingsService, new ShaderStaging(fixture.Settings), fonts, paths,
            fixture.Launcher, new AudioDeviceCatalogue(() => null), () => Task.CompletedTask, _ => Task.CompletedTask,
            new MoviePilotProbe(moviePilot), new MoviePilotCredentials(PassthroughSecretProtector.Instance));
        await page.ViewModel.ReloadAsync();
        var sections = page.ViewModel.Sections.ToArray();
        _root.Children.Add(page);
        try
        {
            await LayoutAsync(page);
            foreach (var width in new[] { 1100, 760 })
            {
                ResizeInspect(width, 900);
                await LayoutAsync(page);
                var content = Get<Grid>(page, "SettingsContent");
                var began = false;
                var startReading = "未观察到动画开始";
                // 布局可能占满 UI 线程直到动画结束。紧盯父层从隐藏恢复的那一刻，
                // 不把定时器恢复得晚误判成没有动画；原来的淡入、位移断言保持不变。
                var watch = content.RegisterPropertyChangedCallback(UIElement.OpacityProperty, (_, _) =>
                {
                    if (content.Opacity != 1) return;
                    var starting = Get<List<FrameworkElement>>(page, "_enteringSections");
                    if (starting.Count == 0) return;
                    began |= starting.Any(section => section.Opacity < 1 && section.RenderTransform is TranslateTransform { Y: > 0 });
                    startReading = $"开始时分组 {starting.Count}，透明度 "
                        + string.Join(", ", starting.Select(section => section.Opacity.ToString("0.000")));
                });
                try
                {
                    page.SelectedCategory = "关于";
                    page.SelectedCategory = "字幕";
                    page.SelectedCategory = "视频输出";
                    await UntilAsync(() => content.Opacity == 1);
                }
                finally { content.UnregisterPropertyChangedCallback(UIElement.OpacityProperty, watch); }
                var entering = Get<List<FrameworkElement>>(page, "_enteringSections").ToArray();
                evidence.Add($"宽度 {width}：{startReading}；采样时分组 {entering.Length}");
                if (HomeMotion.AnimationsEnabled)
                {
                    Require(entering.Length > 0 && began, "设置分组没有实际淡入和上浮：" + startReading);
                    Require(entering.All(section => section.RenderTransform is TranslateTransform), "入场没有使用模板根位移");
                    Require(Get<Grid>(page, "SettingsLayout").Opacity == 1
                        && Get<Grid>(page, "SettingsContent").RenderTransform is not TranslateTransform,
                        "父层仍有叠加动画");
                }
                evidence.Add($"宽度 {width}：入场分组 {entering.Length}，透明度 "
                    + string.Join(", ", entering.Select(section => section.Opacity.ToString("0.000"))));
                await Task.Delay(430);
                AssertSettingsSettled(page);
                Require(page.SelectedCategory == "视频输出" && page.ViewModel.Sections.SequenceEqual(sections),
                    "快速切换回到了旧分类或重建了设置行");
                await SaveReturnFrameAsync(page, $"settings-motion-{width}.png");

                // 长页滚到底后切短页，不能留下空内容；只检查视口真正画到的分组。
                var scroll = Descendants(page).OfType<ScrollView>().Single();
                scroll.ScrollTo(0, scroll.ScrollableHeight,
                    new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
                await Task.Delay(100);
                page.SelectedCategory = "关于";
                await Task.Delay(430);
                AssertSettingsSettled(page);
                var visible = Descendants(page).OfType<FrameworkElement>()
                    .Where(element => element.Name == "SettingsSection" && element.Visibility == Visibility.Visible).ToArray();
                Require(visible.Length == 1 && visible[0].ActualHeight > 0, "长页切短页没有实现目标分组");
                Require(scroll.VerticalOffset <= scroll.ScrollableHeight + 1, "切短页后滚动位置超出内容");

                // 窗口隐藏调用的释放入口，在排队或播放中都必须作废回调；之后仍能重新入场。
                page.SelectedCategory = "播放器";
                page.ReleaseHosted();
                await Task.Delay(80);
                AssertSettingsSettled(page);
                Require(Get<List<FrameworkElement>>(page, "_enteringSections").Count == 0, "隐藏后迟到回调重新播放动画");
                Call(page, "QueueSettingsEntrance");
                await UntilAsync(() => Get<Grid>(page, "SettingsContent").Opacity == 1);
                page.ReleaseHosted();
                AssertSettingsSettled(page);
            }

            // 真实托管 Frame 的父层不参与动画；无需构造播放服务或访问真实服务器。
            Field(page, "_request", new SettingsRequest(fixture.Services));
            page.SelectedCategory = "服务器";
            await LayoutAsync(page);
            Require(page.CurrentPage is ServersPage && Get<Grid>(page, "SettingsContent").Opacity == 1
                && Get<List<FrameworkElement>>(page, "_enteringSections").Count == 0, "托管页叠加了父层动画");
            page.ReleaseHosted();
            if (page.CurrentPage is Page { Content: FrameworkElement hosted })
                Require(hosted.Opacity == 1, "隐藏后托管页仍半透明");
        }
        finally
        {
            page.Release();
            _root.Children.Remove(page);
            ResizeInspect(1420, 980);
            File.WriteAllLines(Path.Combine(_options.Paths.LogDirectory, "settings-motion.txt"), evidence);
        }
    }

    private static void AssertSettingsSettled(SettingsPage page)
    {
        Require(Get<Grid>(page, "SettingsContent").Opacity == 1, "设置内容停留在透明状态");
        foreach (var section in Descendants(page).OfType<FrameworkElement>().Where(element => element.Name == "SettingsSection"))
            Require(section.Opacity == 1 && (section.RenderTransform is not TranslateTransform drift || drift.Y == 0),
                "设置分组未归位");
    }
}
