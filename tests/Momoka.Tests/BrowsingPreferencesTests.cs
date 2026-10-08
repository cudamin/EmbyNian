using Momoka.Configuration;
using Momoka.Emby;
using Momoka.Infrastructure;
using static Momoka.Tests.TestHarness;

namespace Momoka.Tests;

internal static class BrowsingPreferencesTests
{
    public static void Register()
    {
        Test("浏览偏好：同编号的库按服务器和账号分开", () =>
        {
            var a = Connection("https://a.invalid/media/", "user");
            Assert.False(BrowsingPreferences.LibraryKey(a, "same") == BrowsingPreferences.LibraryKey(Connection("https://b.invalid/media/", "user"), "same"));
            Assert.False(BrowsingPreferences.LibraryKey(a, "same") == BrowsingPreferences.LibraryKey(Connection("https://a.invalid/media/", "other"), "same"));
            Assert.False(BrowsingPreferences.LibraryKey(a, "same") == BrowsingPreferences.LibraryKey(Connection("https://a.invalid/Media/", "user"), "same"));
            Assert.Equal(BrowsingPreferences.IdentityKey(a), BrowsingPreferences.IdentityKey(a with { AccessToken = "different" }));
            Assert.DoesNotContain("synthetic-secret", BrowsingPreferences.IdentityKey(a));
        });
        Test("浏览偏好：切服返回保留首页隐藏和顺序且不共享列表", () =>
        {
            var ui = new UiSettings { HomeRows = [new() { Key = "library:same", Title = "A", Visible = false }] };
            var a = Connection("https://a.invalid/media/", "user");
            var b = Connection("https://b.invalid/media/", "user");
            BrowsingPreferences.SelectIdentity(ui, a);
            BrowsingPreferences.SelectIdentity(ui, b);
            Assert.Equal(0, ui.HomeRows.Count);
            ui.HomeRows.Add(new() { Key = "library:same", Title = "B", Visible = true });
            BrowsingPreferences.SelectIdentity(ui, a);
            Assert.Equal("A", ui.HomeRows.Single().Title);
            Assert.False(ui.HomeRows.Single().Visible);
            ui.HomeRows.Single().Title = "A2";
            BrowsingPreferences.SelectIdentity(ui, b);
            Assert.Equal("B", ui.HomeRows.Single().Title);
        });
        Test("浏览偏好：旧裸库键仅由首次身份认领一次", () =>
        {
            var ui = new UiSettings();
            ui.Sort["same"] = new() { By = EmbySortBy.Name, Descending = true };
            ui.Views["same"] = LibraryView.List;
            ui.Filters["same"] = new();
            var a = Connection("https://a.invalid/", "a");
            var b = Connection("https://b.invalid/", "b");
            BrowsingPreferences.SelectIdentity(ui, a);
            BrowsingPreferences.SelectIdentity(ui, b);
            Assert.True(ui.Sort.ContainsKey(BrowsingPreferences.LibraryKey(a, "same")));
            Assert.False(ui.Sort.ContainsKey(BrowsingPreferences.LibraryKey(b, "same")));
            Assert.False(ui.Views.ContainsKey("same"));
        });
        Test("浏览偏好：恢复默认不替换身份历史，导出不带身份键", () =>
        {
            var settings = new AppSettings();
            var a = Connection("https://a.invalid/", "a");
            var b = Connection("https://b.invalid/", "b");
            BrowsingPreferences.SelectIdentity(settings.Ui, a);
            settings.Ui.HomeRows.Add(new() { Key = "library:a", Visible = false });
            BrowsingPreferences.SelectIdentity(settings.Ui, b);
            var history = settings.Ui.HomeRowsByIdentity;
            var identity = settings.Ui.HomeRowsIdentity;
            SettingsPreferences.Apply(settings, new AppSettings());
            Assert.Equal(identity, settings.Ui.HomeRowsIdentity);
            Assert.True(ReferenceEquals(history, settings.Ui.HomeRowsByIdentity));
            var backup = SettingsPreferences.ToBackupDocument(settings);
            Assert.Equal("", backup.Ui.HomeRowsIdentity);
            Assert.Equal(0, backup.Ui.HomeRowsByIdentity.Count);
        });
        Test("图片地址：背景图第二张显式携带序号而非仅标签", () =>
        {
            var url = EmbyUrl.Image(new Uri("https://example.invalid/emby/"), "item", "Backdrop", "b", 320, 1);
            Assert.Equal("/emby/Items/item/Images/Backdrop/1", url.AbsolutePath);
            Assert.True(url.Query.Contains("tag=b", StringComparison.Ordinal));
        });
        Test("Shell探针：合法参数与非法组合不会落入普通启动", () =>
        {
            foreach (var args in new[] { new[] { "--probe-shell" }, new[] { "--probe-shell", "inspect", "--screen", "1" }, new[] { "/probe-shell=inspect", "--theme=midnight" } })
            {
                Assert.True(StartupArgs.RequestsShellProbe(args));
                StartupArgs.ValidateShellProbe(args);
            }
            foreach (var args in new[] { new[] { "--probe-shell=" }, new[] { "--probe-shell", "--play" },
                new[] { "--probe-shell", "--self-check" }, new[] { "--probe-shell", "--show-settings" },
                new[] { "--probe-shell", "--probe-subtitles" }, new[] { "--probe-shell", "--screen", "0" } })
            {
                Assert.True(StartupArgs.RequestsShellProbe(args));
                Assert.Throws<ArgumentException>(() => StartupArgs.ValidateShellProbe(args));
            }
        });
    }

    private static EmbyConnection Connection(string address, string user) =>
        new(new Uri(address), "synthetic-secret", user, user, "fixture", DeviceIdentity.Create("fixture", "test"));
}
