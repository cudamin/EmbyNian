using CommunityToolkit.WinUI.Controls;
using EmbyNian.Shell.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace EmbyNian.Shell;

internal sealed partial class ShellNavigationProbe
{
    private async Task SearchHeaderLayoutAsync()
    {
        using var fixture = CreateFixture();
        await fixture.SelectAsync();
        fixture.Settings.MoviePilot.Enabled = true;
        var frame = new Frame();
        _root.Children.Add(frame);
        var evidence = new List<string>();
        try
        {
            frame.Navigate(typeof(LibraryPage), LibraryRequest.Search(fixture.Services));
            var page = (LibraryPage)frame.Content;
            await LayoutAsync(page);
            var header = Get<Grid>(page, "HeaderLayout");
            var search = Get<Border>(page, "SearchSurface");
            var tools = Get<Grid>(page, "ToolbarSurface");
            var tabs = Get<Segmented>(page, "SourceTabs");
            var toolbar = Get<WrapRow>(page, "Toolbar");
            var sawInline = false;
            var sawStacked = false;
            var sawButtonWrap = false;
            foreach (var width in new[] { 1600, 1040, 760, 520, 360, 1600 })
            {
                var scale = _root.XamlRoot.RasterizationScale;
                ResizeInspect((int)(width * scale), (int)(800 * scale));
                await LayoutAsync(page);
                await Task.Delay(100);
                await SaveReturnFrameAsync(page, $"search-layout-{width}.png");
                AssertCentered();
                var searchBox = Bounds(search, header);
                var toolBox = Bounds(tools, header);
                Require(!Intersects(searchBox, toolBox), "搜索区和工具栏重叠");
                Require(toolBox.Left >= 0 && toolBox.Right <= header.ActualWidth + 1, "工具栏超出内容区");
                var buttons = toolbar.Children.OfType<FrameworkElement>()
                    .Where(button => button.Visibility == Visibility.Visible).ToArray();
                foreach (var button in buttons)
                {
                    var box = Bounds(button, toolbar);
                    Require(box.Left >= 0 && box.Right <= toolbar.ActualWidth + 1
                        && box.Top >= 0 && box.Bottom <= toolbar.ActualHeight + 1
                        && box.Height >= 30, $"工具栏按钮被裁切：{button.Name} {box} / {toolbar.ActualWidth}×{toolbar.ActualHeight}");
                }
                sawInline |= Math.Abs(toolBox.Top - searchBox.Top) < 1;
                sawStacked |= toolBox.Top >= searchBox.Bottom;
                sawButtonWrap |= buttons.Any(button => Bounds(button, toolbar).Top > 1);
                evidence.Add($"内容宽 {header.ActualWidth:0}：搜索中心 {searchBox.Left + searchBox.Width / 2:0.0}，工具栏 {toolBox}，按钮区高 {toolbar.ActualHeight:0}");
            }
            Require(sawInline && sawStacked && sawButtonWrap, "没有覆盖同排、整组换行和按钮换行三种布局");
            tabs.SelectedIndex = 1;
            await LayoutAsync(page);
            AssertCentered();
            Require(tools.Visibility == Visibility.Collapsed, "切换 MoviePilot 留下空工具栏背景");
            tabs.SelectedIndex = 0;
            await page.ViewModel.SearchAsync("这是一条用于检查长搜索标题不会挤动居中搜索框的搜索词");
            await LayoutAsync(page);
            AssertCentered();
            Require(tools.Visibility == Visibility.Visible, "返回媒体库没有恢复工具栏");
            var titleBox = Bounds(Get<TextBlock>(page, "LibraryTitle"), header);
            Require(!Intersects(titleBox, Bounds(search, header)), "长搜索标题和搜索区重叠");
            await Task.Delay(300);
            await SaveReturnFrameAsync(page, "search-layout-long-title.png");

            page.Release();
            fixture.Settings.MoviePilot.Enabled = false;
            frame.Navigate(typeof(LibraryPage), LibraryRequest.Search(fixture.Services));
            page = (LibraryPage)frame.Content;
            await LayoutAsync(page);
            await Task.Delay(300);
            header = Get<Grid>(page, "HeaderLayout");
            search = Get<Border>(page, "SearchSurface");
            AssertCentered();
            Require(Get<Segmented>(page, "SourceTabs").Visibility == Visibility.Collapsed, "未启用 MoviePilot 仍显示来源切换");
            await SaveReturnFrameAsync(page, "search-layout-emby-only.png");

            page.Release();
            frame.Navigate(typeof(LibraryPage), new LibraryRequest { Services = fixture.Services, Title = "离线媒体库", ParentId = "library" });
            page = (LibraryPage)frame.Content;
            await LayoutAsync(page);
            await Task.Delay(300);
            Require(Get<Border>(page, "SearchSurface").Visibility == Visibility.Collapsed
                && Get<Grid>(page, "ToolbarSurface").Visibility == Visibility.Visible, "普通媒体库页头显隐错误");
            await SaveReturnFrameAsync(page, "search-layout-library.png");

            void AssertCentered()
            {
                var box = Bounds(search, header);
                Require(Math.Abs(box.Left + box.Width / 2 - header.ActualWidth / 2) <= 1,
                    $"搜索没有相对内容区居中：{box} / {header.ActualWidth}");
                Require(box.Left >= -1 && box.Right <= header.ActualWidth + 1, "搜索区超出内容边界");
            }
        }
        finally
        {
            File.WriteAllLines(Path.Combine(_options.Paths.LogDirectory, "search-layout.txt"), evidence);
            (frame.Content as IShellContent)?.Release();
            _root.Children.Remove(frame);
            ResizeInspect(1420, 980);
        }

        static Rect Bounds(FrameworkElement element, UIElement relative)
        {
            var origin = element.TransformToVisual(relative).TransformPoint(default);
            return new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
        }

        static bool Intersects(Rect first, Rect second) => first.Left < second.Right && first.Right > second.Left
            && first.Top < second.Bottom && first.Bottom > second.Top;
    }
}
