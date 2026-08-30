using EmbyNian.Configuration;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Services;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// The services the view models depend on. This logic is not new — it was on the shell's composition root,
/// where no test could reach it, because the console runner cannot load a WinUI project. Moving it into
/// Core is what makes the profile lookups testable at all, and they are worth testing: both of them create
/// a record when they do not find one, so a match that fails silently piles up duplicate servers with
/// duplicate copies of the password, and a duplicate account per sign-in.
/// <para>
/// <see cref="ServerCapabilities"/> is absent on purpose — it is one request to a live server and nothing
/// else. Likewise <see cref="PlaybackBackendFactory.Create"/>, which loads libmpv.
/// </para>
/// </summary>
internal static class ServiceTests
{
    private static readonly ISecretProtector Protector = PassthroughSecretProtector.Instance;

    public static void Register()
    {
        RegisterServerLookup();
        RegisterAccountLookup();
        RegisterPageSize();
        RegisterSave();
        RegisterShaderStaging();
    }

    private static void RegisterServerLookup()
    {
        Test("服务器查找：同一地址的不同写法只留一份配置", () =>
        {
            var (service, settings, root) = NewService();
            try
            {
                var before = settings.Servers.Count;

                var first = service.ResolveServer("192.168.31.230:8896");
                Assert.Equal(before + 1, settings.Servers.Count, "没见过的服务器要落到设置里");
                Assert.Equal("http://192.168.31.230:8896", first.Url, "存下来的是规范化之后的写法");

                foreach (var spelling in new[]
                {
                    "http://192.168.31.230:8896",
                    "http://192.168.31.230:8896/",
                    "http://192.168.31.230:8896/emby",
                    "http://192.168.31.230:8896/emby/"
                })
                {
                    Assert.True(
                        ReferenceEquals(first, service.ResolveServer(spelling)),
                        $"“{spelling}”和第一次登录的是同一台服务器");
                }

                Assert.Equal(before + 1, settings.Servers.Count, "认错了就会把密码存两遍");
            }
            finally
            {
                Cleanup(root);
            }
        });

        Test("服务器查找：认出老配置时顺手把地址改写成规范形式", () =>
        {
            var (service, settings, root) = NewService();
            try
            {
                // What a hand-typed address looks like once it has been saved: it works, but it is not the
                // form the sign-in page shows.
                var saved = new ServerProfile { Url = "http://192.168.31.230:8896/emby/" };
                settings.Servers.Add(saved);

                var resolved = service.ResolveServer("192.168.31.230:8896");

                Assert.True(ReferenceEquals(saved, resolved), "地址一样就是同一份配置");
                Assert.Equal("http://192.168.31.230:8896", saved.Url, "再登录一次就把写法整理好");
            }
            finally
            {
                Cleanup(root);
            }
        });

        Test("服务器查找：端口或主机不同就是两台服务器", () =>
        {
            var (service, settings, root) = NewService();
            try
            {
                var before = settings.Servers.Count;

                service.ResolveServer("http://h:8096");
                service.ResolveServer("http://h:8097");
                service.ResolveServer("http://other:8096");

                Assert.Equal(before + 3, settings.Servers.Count);
            }
            finally
            {
                Cleanup(root);
            }
        });
    }

    private static void RegisterAccountLookup()
    {
        Test("账户查找：首尾空格和大小写都算同一个人", () =>
        {
            var (service, _, root) = NewService();
            try
            {
                var server = service.ResolveServer("http://h:8096");

                var wang = service.ResolveAccount(server, "老王");
                Assert.Equal(1, server.Accounts.Count);
                Assert.Equal("老王", wang.Username);

                Assert.True(ReferenceEquals(wang, service.ResolveAccount(server, " 老王 ")), "两头的空格要修掉");
                Assert.True(ReferenceEquals(wang, service.ResolveAccount(server, "老王")));

                var emby = service.ResolveAccount(server, "Emby");
                Assert.True(ReferenceEquals(emby, service.ResolveAccount(server, "emby")), "用户名不区分大小写");
                Assert.True(ReferenceEquals(emby, service.ResolveAccount(server, "EMBY")));

                Assert.Equal(2, server.Accounts.Count, "两个人，两份账户，一份都不能多");
            }
            finally
            {
                Cleanup(root);
            }
        });

        Test("账户查找：不同服务器上的同名账户互不相干", () =>
        {
            var (service, _, root) = NewService();
            try
            {
                var first = service.ResolveServer("http://h:8096");
                var second = service.ResolveServer("http://h:8097");

                var here = service.ResolveAccount(first, "老王");
                var there = service.ResolveAccount(second, "老王");

                Assert.True(!ReferenceEquals(here, there), "两台服务器上的同名账户是两个人，令牌也是两份");
                Assert.Equal(1, first.Accounts.Count);
                Assert.Equal(1, second.Accounts.Count);
            }
            finally
            {
                Cleanup(root);
            }
        });
    }

    private static void RegisterPageSize()
    {
        Test("每页条数：夹在 20 到 500 之间", () =>
        {
            var (service, settings, root) = NewService();
            try
            {
                settings.Ui.PageSize = 0;
                Assert.Equal(20, service.PageSize, "一页 0 条会翻不动");

                settings.Ui.PageSize = -100;
                Assert.Equal(20, service.PageSize);

                settings.Ui.PageSize = 5000;
                Assert.Equal(500, service.PageSize, "一次要太多会把服务器和内存一起拖住");

                settings.Ui.PageSize = 120;
                Assert.Equal(120, service.PageSize, "范围内的值原样放过");

                settings.Ui.PageSize = 500;
                Assert.Equal(500, service.PageSize, "上限本身是允许的，设置页就让选到这里");
            }
            finally
            {
                Cleanup(root);
            }
        });
    }

    private static void RegisterSave()
    {
        Test("保存：写下去的东西读得回来", () =>
        {
            var (service, settings, root) = NewService();
            try
            {
                settings.Servers[0].Name = "果服";
                settings.Ui.PageSize = 80;
                service.Save();

                var reread = new SettingsStore(new AppPaths(root), Protector).Load();

                Assert.Equal("果服", reread.Servers[0].Name, "Save 要真的落盘，不然改完设置一重启就白改了");
                Assert.Equal(80, reread.Ui.PageSize);
            }
            finally
            {
                Cleanup(root);
            }
        });
    }

    private static void RegisterShaderStaging()
    {
        Test("着色器：配置组目录不为空", () =>
        {
            var staging = new ShaderStaging(SettingsMigration.NewDefaults());

            Assert.True(staging.Catalog.Count > 0, "设置页的着色器配置组就是这份目录，空的话那一栏没东西可选");
            Assert.True(staging.ShaderDirectory.Length > 0, "两个后端都按绝对路径加载着色器");
        });
    }

    /// <summary>
    /// A service over a settings file of its own, so a test that saves cannot disturb the next one. The
    /// settings object is handed back as well: it is the same instance the service holds, which is how the
    /// settings page edits it in place.
    /// </summary>
    private static (SettingsService Service, AppSettings Settings, string Root) NewService()
    {
        var root = TempRoot();
        var paths = new AppPaths(root);
        paths.EnsureCreated();

        var settings = SettingsMigration.NewDefaults();
        return (new SettingsService(new SettingsStore(paths, Protector), settings), settings, root);
    }

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), $"embynian-service-{Guid.NewGuid():N}");

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
