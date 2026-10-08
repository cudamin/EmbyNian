using Momoka.Mpv;
using Momoka.Playback;

namespace Momoka.Tests;

internal static class PlayerInteractionTests
{
    internal static void Register()
    {
        TestHarness.Test("独占快捷键：默认动作完整且清除旧键与新增键同时下发", () =>
        {
            var defaults = VideoWindowShortcuts.Bindings(new Dictionary<string, string>());
            Assert.Equal(ShortcutCatalog.Actions.Count, defaults.Count);
            Assert.True(ShortcutCatalog.Actions.All(action => defaults.Values.Contains(action.Id)));
            var changed = VideoWindowShortcuts.Bindings(new Dictionary<string, string>
            {
                ["toggle-pause"] = "Ctrl+F9",
                ["toggle-fullscreen"] = ""
            });
            Assert.Equal("", changed["SPACE"]);
            Assert.Equal("", changed["f"]);
            Assert.Equal("toggle-pause", changed["Ctrl+F9"]);
            Assert.False(changed.Keys.Any(key => key.Contains("ESC", StringComparison.Ordinal) || key.Contains("ENTER", StringComparison.Ordinal)));
        });
        TestHarness.Test("独占快捷键：大小写修饰符及标点只走受支持的键名", () =>
        {
            Assert.Equal("Shift+z", VideoWindowShortcuts.Key(new KeyStroke("Z", false, false, true)));
            Assert.Equal("Ctrl+Alt+F12", VideoWindowShortcuts.Key(new KeyStroke("F12", true, true, false)));
            Assert.Equal("[", VideoWindowShortcuts.Key(new KeyStroke("BracketLeft", false, false, false)));
            Assert.Null(VideoWindowShortcuts.Key(new KeyStroke("F13", false, false, false)));
            Assert.Null(VideoWindowShortcuts.Key(new KeyStroke("x;quit", false, false, false)));
        });

        TestHarness.Test("独占快捷键：宿主只接受已知动作不接受任意命令", () =>
        {
            foreach (var action in ShortcutCatalog.Actions)
                Assert.NotNull(VideoWindowContract.Parse([VideoWindowContract.Shortcut, action.Id]));
            Assert.Null(VideoWindowContract.Parse([VideoWindowContract.Shortcut, "quit"]));
            Assert.Null(VideoWindowContract.Parse([VideoWindowContract.Shortcut, "seek 20"]));
        });

        TestHarness.Test("播放器菜单：同一行号不得跨菜单或重开复用", () =>
        {
            var first = new VideoMenuSnapshot<string>(["A1", "A2"]);
            var second = new VideoMenuSnapshot<string>(["B1", "B2"]);
            var value = first.ValueAt(1);
            Assert.True(first.TryResolve(value, out var selected));
            Assert.Equal("A2", selected);
            Assert.False(second.TryResolve(value, out _));
            Assert.False(first.TryResolve("2", out _));
        });

        TestHarness.Test("播放器菜单：列表原地替换不改旧菜单的条目快照", () =>
        {
            var items = new List<string> { "A1", "A2" };
            var menu = new VideoMenuSnapshot<string>(items);
            var value = menu.ValueAt(1);
            items[1] = "B2";
            Assert.True(menu.TryResolve(value, out var selected));
            Assert.Equal("A2", selected);
            Assert.False(menu.TryResolve(value[..33] + "3", out _));
            Assert.False(menu.TryResolve(value[..33] + "0", out _));
            Assert.False(VideoMenuSnapshot<string>.IsSelection(value + ";quit"));
        });

        TestHarness.Test("播放器菜单：契约接受带快照的选择且拒绝损坏的值", () =>
        {
            var menu = new VideoMenuSnapshot<string>(["A"]);
            var value = menu.ValueAt(0);
            foreach (var key in new[] { VideoWindowContract.EpisodeIndex, VideoWindowContract.VersionIndex, VideoWindowContract.MenuIndex })
            {
                Assert.NotNull(VideoWindowContract.Parse([key, value]));
                Assert.Null(VideoWindowContract.Parse([key, "not-a-snapshot:1"]));
                Assert.Null(VideoWindowContract.Parse([key, value + " extra"]));
            }
        });

        TestHarness.Test("播放器输入：静止位置重报不延后首次隐藏", () =>
        {
            var chrome = new ChromeReveal();
            chrome.Pointer(500, 1000, ChromePart.None, -1, 1000);
            for (var now = 1100; now <= 2000; now += 100)
                chrome.Pointer(500, 1000, ChromePart.None, -1, now, moved: false);
            Assert.True(chrome.CursorHidden);
            Assert.Equal(1000L, chrome.IdleAgo(2000));
        });

        TestHarness.Test("播放器输入：静止重排不撤掉键盘反馈宽限", () =>
        {
            var chrome = new ChromeReveal();
            chrome.Pointer(500, 1000, ChromePart.None, -1, 1000);
            chrome.WakeFully(2000);
            chrome.Pointer(400, 800, ChromePart.None, -1, 2100, moved: false);
            Assert.Equal(new ChromeState(true, true, true), chrome.State);
            chrome.Tick(2000 + ChromeReveal.GraceMilliseconds);
            Assert.Equal(new ChromeState(false, false, false), chrome.State);
        });

        TestHarness.Test("播放器输入：真移动仍取消键盘反馈并唤醒光标", () =>
        {
            var chrome = new ChromeReveal();
            chrome.Pointer(500, 1000, ChromePart.None, -1, 1000);
            chrome.Tick(2000);
            Assert.True(chrome.CursorHidden);
            chrome.WakeFully(2100);
            chrome.Pointer(500, 1000, ChromePart.None, -1, 2200, moved: true);
            Assert.False(chrome.CursorHidden);
            Assert.Equal(new ChromeState(false, false, false), chrome.State);
        });

        TestHarness.Test("播放器输入：控件本体保留光标但唤出带不保留", () =>
        {
            var chrome = new ChromeReveal();
            chrome.Pointer(950, 1000, ChromePart.Bar, -1, 1000);
            chrome.Pointer(950, 1000, ChromePart.Bar, -1, 3000, moved: false);
            Assert.False(chrome.CursorHidden);
            Assert.True(chrome.State.Bar);
            chrome.Pointer(850, 1000, ChromePart.None, -1, 3001, moved: false);
            Assert.True(chrome.CursorHidden);
            Assert.True(chrome.State.Bar);
        });
    }
}
