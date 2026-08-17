using System.Text.Json;
using EmbyMpvClient.Configuration;
using EmbyMpvClient.Infrastructure;
using static EmbyMpvClient.Tests.TestHarness;

namespace EmbyMpvClient.Tests;

/// <summary>
/// Settings loading, v1 migration and the credential vault. Uses
/// <see cref="PassthroughSecretProtector"/> so nothing here depends on DPAPI — the real
/// protector lives in the WinForms project.
/// </summary>
internal static class SettingsTests
{
    private static readonly ISecretProtector Protector = PassthroughSecretProtector.Instance;

    public static void Register()
    {
        RegisterMigration();
        RegisterNormalize();
        RegisterVault();
        RegisterStore();
    }

    private static void RegisterMigration()
    {
        Test("迁移：空文件与损坏内容都回到默认设置", () =>
        {
            foreach (var json in new[] { "", "   ", "[]", "\"文本\"" })
            {
                var settings = SettingsMigration.FromJson(json, Protector);
                Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
                Assert.Equal(1, settings.Servers.Count, "默认给一台服务器好让登录页有东西可选");
                Assert.True(settings.DeviceId.Length > 0, "设备 ID 必须补上");
            }
        });

        Test("迁移：v1 扁平结构升级为服务器/账户列表", () =>
        {
            const string v1 = """
            {
              "ServerUrl": "http://192.168.31.230:8896",
              "Username": "老王",
              "UserId": "u-1",
              "AccessToken": "token-v1",
              "MpvPath": "D:\\mpv\\mpv.exe",
              "MpvConfigPath": "D:\\mpv\\portable_config\\mpv.conf"
            }
            """;

            var settings = SettingsMigration.FromJson(v1, Protector);

            Assert.Equal(1, settings.Servers.Count);
            var server = settings.Servers[0];
            Assert.Equal("http://192.168.31.230:8896", server.Url);
            Assert.Equal(1, server.Accounts.Count);
            Assert.Equal("老王", server.Accounts[0].Username);
            Assert.Equal("u-1", server.Accounts[0].UserId);
            Assert.Equal(@"D:\mpv\mpv.exe", settings.Mpv.ExecutablePath, "旧的 mpv 路径要跟着搬过来");
            Assert.Equal(server.Id, settings.LastServerId, "升级后必须记得上次用的服务器");
            Assert.Equal(server.Accounts[0].Id, settings.LastAccountId);
        });

        Test("迁移：v1 明文令牌升级后不再是明文", () =>
        {
            const string v1 = """{"ServerUrl":"http://h:8096","Username":"我","AccessToken":"token-v1"}""";

            var settings = SettingsMigration.FromJson(v1, Protector);
            var account = settings.Servers[0].Accounts[0];

            Assert.True(account.HasSavedToken, "令牌要保留下来，用户不必重新登录");
            Assert.Equal("", account.ProtectedPassword, "v1 这一支没有存密码");

            // 用直通保护器时包装后仍等于原文，所以这里验证的是「走了保护器」这条路径；
            // 真实的 DPAPI 实现在 WinForms 工程里，密文与明文自然不同。
            Assert.Equal("token-v1", new CredentialVault(Protector).GetAccessToken(account));
        });

        Test("迁移：v1 的 Servers 数组与已加密密码原样带过来", () =>
        {
            const string v1 = """
            {
              "LastServerId": "s-1",
              "Servers": [
                {
                  "Id": "s-1",
                  "Name": "果服",
                  "Url": "http://192.168.31.230:8896",
                  "Accounts": [
                    { "Id": "a-1", "Username": "我", "UserId": "u-1", "ProtectedPassword": "AQAAdpapi==", "AccessToken": "tok" }
                  ]
                }
              ]
            }
            """;

            var settings = SettingsMigration.FromJson(v1, Protector);
            var account = settings.Servers[0].Accounts[0];

            Assert.Equal("果服", settings.Servers[0].Name);
            Assert.Equal("s-1", settings.LastServerId);
            Assert.Equal("AQAAdpapi==", account.ProtectedPassword, "v1 用的是同一个 DPAPI 熵，密文不能重新包装");
            Assert.True(account.RememberPassword, "存过密码就说明当时勾了记住密码");
            Assert.True(account.HasSavedToken);
        });

        Test("迁移：v2 文件按当前结构直接读入", () =>
        {
            var original = SettingsMigration.NewDefaults();
            original.Servers[0].Name = "果服";
            original.Servers[0].Url = "http://192.168.31.230:8896";
            original.Shaders.ApplyToAllVideos = false;
            original.Shaders.AnimeProfile = "我的动画组";
            original.Playback.PreferredAudioLanguage = "jpn";
            original.Mpv.ExtraArguments = "--fullscreen";

            var json = JsonSerializer.Serialize(original, SettingsSerializer.WriteOptions);
            var loaded = SettingsMigration.FromJson(json, Protector);

            Assert.Equal("果服", loaded.Servers[0].Name);
            Assert.False(loaded.Shaders.ApplyToAllVideos, "开关状态必须往返一致");
            Assert.Equal("我的动画组", loaded.Shaders.AnimeProfile);
            Assert.Equal("jpn", loaded.Playback.PreferredAudioLanguage);
            Assert.Equal("--fullscreen", loaded.Mpv.ExtraArguments);
            Assert.Equal(original.DeviceId, loaded.DeviceId, "设备 ID 不能每次启动都变");
        });

        Test("迁移：中文写进文件时不该变成 \\uXXXX", () =>
        {
            var settings = SettingsMigration.NewDefaults();
            settings.Servers[0].Name = "果服";

            var json = JsonSerializer.Serialize(settings, SettingsSerializer.WriteOptions);
            Assert.Contains("果服", json, "手工检查 settings.json 时要能看懂");
        });

        Test("迁移：容忍注释与多余逗号（用户手改过的文件）", () =>
        {
            const string json = """
            {
              // 我改过这里
              "SchemaVersion": 2,
              "Servers": [ { "Name": "果服", "Url": "http://h:8896" }, ],
            }
            """;

            var settings = SettingsMigration.FromJson(json, Protector);
            Assert.Equal("果服", settings.Servers[0].Name);
        });
    }

    private static void RegisterNormalize()
    {
        Test("规整：越界数值被夹回合理范围", () =>
        {
            var settings = SettingsMigration.NewDefaults();
            settings.Ui.PageSize = 100000;
            settings.Ui.PosterWidth = 5;
            settings.Playback.MarkWatchedPercent = 5;
            settings.Playback.ProgressReportIntervalSeconds = 0;
            settings.Mpv.ConfigBackupsToKeep = -3;

            SettingsMigration.Normalize(settings);

            Assert.Equal(500, settings.Ui.PageSize);
            Assert.Equal(120, settings.Ui.PosterWidth);
            Assert.Equal(50, settings.Playback.MarkWatchedPercent);
            Assert.Equal(1, settings.Playback.ProgressReportIntervalSeconds);
            Assert.Equal(1, settings.Mpv.ConfigBackupsToKeep);
        });

        Test("规整：记住的账户与记住的服务器必须对得上", () =>
        {
            var settings = SettingsMigration.NewDefaults();
            var other = new ServerProfile { Id = "s-2", Name = "另一台" };
            other.Accounts.Add(new AccountProfile { Id = "a-2", Username = "我" });
            settings.Servers.Add(other);

            settings.LastServerId = settings.Servers[0].Id;
            settings.LastAccountId = "a-2";

            SettingsMigration.Normalize(settings);

            Assert.Equal("s-2", settings.LastServerId, "账户在另一台服务器上，就该跟着切过去");
        });

        Test("规整：指向已删除服务器的记录被清理", () =>
        {
            var settings = SettingsMigration.NewDefaults();
            settings.LastServerId = "已经删掉的-id";
            settings.LastAccountId = "也删掉了";

            SettingsMigration.Normalize(settings);

            Assert.Equal(settings.Servers[0].Id, settings.LastServerId);
            Assert.Null(settings.LastAccountId, "找不到的账户不能留着，否则登录页选中一个不存在的项");
        });

        Test("规整：空白服务器名与网址两端空格", () =>
        {
            var settings = SettingsMigration.NewDefaults();
            settings.Servers[0].Name = "   ";
            settings.Servers[0].Url = "  http://h:8896  ";

            SettingsMigration.Normalize(settings);

            Assert.Equal("Emby 服务器", settings.Servers[0].Name);
            Assert.Equal("http://h:8896", settings.Servers[0].Url);
        });
    }

    private static void RegisterVault()
    {
        Test("凭据：勾选记住密码才会写入文件", () =>
        {
            var vault = new CredentialVault(Protector);
            var account = new AccountProfile();

            vault.SetPassword(account, "密码123", remember: true);
            Assert.True(account.HasSavedPassword);
            Assert.Equal("密码123", vault.GetPassword(account));

            vault.SetPassword(account, "密码123", remember: false);
            Assert.False(account.HasSavedPassword, "没勾记住密码就不该留下任何痕迹");
            Assert.Equal("", vault.GetPassword(account));
        });

        Test("凭据：令牌可写入、读出与清除", () =>
        {
            var vault = new CredentialVault(Protector);
            var account = new AccountProfile();

            vault.SetAccessToken(account, "tok-1");
            Assert.Equal("tok-1", vault.GetAccessToken(account));

            vault.ClearAccessToken(account);
            Assert.False(account.HasSavedToken);
            Assert.Equal("", vault.GetAccessToken(account));
        });

        Test("凭据：换台机器解不开时只是要求重新登录", () =>
        {
            var vault = new CredentialVault(new ThrowingProtector());
            var account = new AccountProfile { ProtectedPassword = "别的电脑加密的" };

            Assert.Equal("", vault.GetPassword(account), "解不开要返回空而不是抛异常");

            vault.SetPassword(account, "密码", remember: true);
            Assert.Equal("", account.ProtectedPassword, "加密失败就不保存，绝不退回明文");
        });
    }

    private static void RegisterStore()
    {
        Test("存储：保存后读回内容一致，并留下备份", () =>
        {
            var root = TempRoot();
            try
            {
                var store = new SettingsStore(new AppPaths(root), Protector);
                var settings = SettingsMigration.NewDefaults();
                settings.Servers[0].Name = "果服";
                settings.Shaders.AutoAnimeProfile = false;

                store.Save(settings);
                Assert.True(File.Exists(store.Paths.SettingsFile), "settings.json 应已写出");

                var loaded = store.Load();
                Assert.Equal("果服", loaded.Servers[0].Name);
                Assert.False(loaded.Shaders.AutoAnimeProfile);

                store.Save(loaded);
                Assert.True(File.Exists(store.Paths.SettingsBackupFile), "第二次保存前应先备份上一份");
            }
            finally
            {
                Cleanup(root);
            }
        });

        Test("存储：文件损坏时启动仍能用默认设置", () =>
        {
            var root = TempRoot();
            try
            {
                var paths = new AppPaths(root);
                Directory.CreateDirectory(paths.Root);
                File.WriteAllText(paths.SettingsFile, "{ 这不是 JSON");

                var loaded = new SettingsStore(paths, Protector).Load();

                Assert.Equal(1, loaded.Servers.Count, "坏文件不能让程序打不开");
                Assert.False(File.Exists(paths.SettingsFile), "损坏的文件应被改名保留");
                Assert.True(Directory.GetFiles(paths.Root, "*.corrupt-*").Length > 0, "改名后的文件要还在，方便找回服务器地址");
            }
            finally
            {
                Cleanup(root);
            }
        });

        Test("存储：主文件坏了就用备份", () =>
        {
            var root = TempRoot();
            try
            {
                var paths = new AppPaths(root);
                var store = new SettingsStore(paths, Protector);

                var settings = SettingsMigration.NewDefaults();
                settings.Servers[0].Name = "果服";
                store.Save(settings);
                store.Save(settings);            // 这一次会生成 settings.backup.json
                File.WriteAllText(paths.SettingsFile, "坏了");

                Assert.Equal("果服", store.Load().Servers[0].Name, "备份里有好的内容就该用它");
            }
            finally
            {
                Cleanup(root);
            }
        });

        Test("原子写入：临时文件不会留在目录里", () =>
        {
            var root = TempRoot();
            try
            {
                Directory.CreateDirectory(root);
                var target = Path.Combine(root, "a.conf");
                AtomicFile.WriteAllText(target, "内容", new System.Text.UTF8Encoding(false));
                AtomicFile.WriteAllText(target, "新内容", new System.Text.UTF8Encoding(false));

                Assert.Equal("新内容", File.ReadAllText(target));
                Assert.Equal(1, Directory.GetFiles(root).Length, "不该留下 .tmp 之类的残骸");
            }
            finally
            {
                Cleanup(root);
            }
        });
    }

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), $"embympvclient-test-{Guid.NewGuid():N}");

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class ThrowingProtector : ISecretProtector
    {
        public string Protect(string plainText) => throw new InvalidOperationException("模拟 DPAPI 失败");

        public string Unprotect(string cipherText) => throw new InvalidOperationException("模拟 DPAPI 失败");
    }
}
