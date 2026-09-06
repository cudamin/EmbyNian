using System.Collections;
using System.Reflection;
using System.Text.Json;
using EmbyNian.Configuration;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Mpv;
using EmbyNian.Playback;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// Settings loading, v1 migration and the credential vault. Uses
/// <see cref="PassthroughSecretProtector"/> so nothing here depends on DPAPI — the real
/// protector lives in the Windows shell project.
/// </summary>
internal static class SettingsTests
{
    private static readonly ISecretProtector Protector = PassthroughSecretProtector.Instance;

    public static void Register()
    {
        RegisterMigration();
        RegisterNormalize();
        RegisterReset();
        RegisterVault();
        RegisterStore();
        RegisterPaths();
    }

    /// <summary>
    /// <see cref="AppPaths.UnvirtualizeLocalAppData"/>, the one judgment in the MSIX data migration with
    /// a single right answer per input. Everything else about that migration needs a real installed
    /// package to exercise; this part does not, so it is the part that gets pinned here.
    /// </summary>
    private static void RegisterPaths()
    {
        Test("数据目录：打包之后认得出被重定向的 LocalAppData", () =>
        {
            var real = AppPaths.UnvirtualizeLocalAppData(
                @"C:\Users\someone\AppData\Local\Packages\EmbyNian_8wekyb3d8bbwe\LocalCache\Local");
            Assert.Equal(@"C:\Users\someone\AppData\Local", real);
        });

        Test("数据目录：没被重定向的路径答「不是」，而不是硬切四段", () =>
        {
            foreach (var path in new[]
            {
                @"C:\Users\someone\AppData\Local",
                @"C:\Users\someone\AppData\Local\Packages\EmbyNian_8wekyb3d8bbwe",
                @"C:\Users\someone\AppData\Local\Packages\EmbyNian_8wekyb3d8bbwe\LocalState",
                @"C:\a\b\LocalCache\Local",
            })
                Assert.True(AppPaths.UnvirtualizeLocalAppData(path) is null,
                    $"「{path}」不是包内那条路径，不该被当成重定向过的");

            Assert.True(AppPaths.UnvirtualizeLocalAppData("") is null, "空串答「不是」");
        });

        Test("数据目录：迁移候选里第一个是不打包那一份", () =>
        {
            var roots = AppPaths.PriorRoots.ToArray();
            Assert.True(roots.Length >= 2, "至少要有两个旧名字可以试");
            // Not packaged while the tests run, so the redirected candidate is absent by design and the
            // list is exactly the two old product names — in that order.
            Assert.True(roots[^2].EndsWith("EmbyGearless", StringComparison.Ordinal), "倒数第二个是 v3 那个名字");
            Assert.True(roots[^1].EndsWith("EmbyMpvClient", StringComparison.Ordinal), "最后一个是 v2 那个名字");
        });

        Test("数据目录：本目录已有 settings.json 就一个候选都不抄", () =>
        {
            var root = Directory.CreateTempSubdirectory("embynian-paths").FullName;
            try
            {
                var current = Path.Combine(root, "now");
                var prior = Path.Combine(root, "before");
                Directory.CreateDirectory(current);
                Directory.CreateDirectory(prior);
                File.WriteAllText(Path.Combine(current, "settings.json"), "{}");
                File.WriteAllText(Path.Combine(prior, "settings.json"), "{\"old\":true}");

                var migrated = new AppPaths(current).MigrateFromAny([prior]);

                Assert.True(migrated is null, "已经有自己的设置时不该迁移");
                Assert.Equal("{}", File.ReadAllText(Path.Combine(current, "settings.json")));
            }
            finally { Directory.Delete(root, recursive: true); }
        });

        Test("数据目录：按顺序抄第一个有设置的候选", () =>
        {
            var root = Directory.CreateTempSubdirectory("embynian-paths").FullName;
            try
            {
                var current = Path.Combine(root, "now");
                var empty = Path.Combine(root, "empty");
                var wanted = Path.Combine(root, "wanted");
                var later = Path.Combine(root, "later");
                foreach (var directory in new[] { empty, wanted, later }) Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(wanted, "settings.json"), "\"要的是这一份\"");
                File.WriteAllText(Path.Combine(later, "settings.json"), "\"不该是这一份\"");

                var paths = new AppPaths(current);
                var migrated = paths.MigrateFromAny([empty, wanted, later]);

                Assert.Equal(wanted, migrated);
                Assert.Equal("\"要的是这一份\"", File.ReadAllText(paths.SettingsFile));
            }
            finally { Directory.Delete(root, recursive: true); }
        });
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

        Test("迁移：文件里某一节写成 null 也照样开得起来", () =>
        {
            // 每一节都有属性初始化器，可 `"Playback": null` 会把它盖掉 —— 反序列化听 JSON 的，不听初始化器。
            // 从前这样一份文件会在 Normalize 的 settings.Playback.MarkWatchedPercent 上抛
            // NullReferenceException，而那不在 SettingsStore 认的「文件坏了」名单里：备份、隔离、退回默认三条
            // 路一条都没走，窗口根本没出来。
            const string json = """
            {
              "SchemaVersion": 10,
              "DeviceId": "d-1",
              "Mpv": null, "Playback": null, "Video": null, "Audio": null, "Shaders": null, "Ui": null
            }
            """;

            var settings = SettingsMigration.FromJson(json, Protector);

            Assert.Equal(90, settings.Playback.MarkWatchedPercent, "缺的那一节回到装机值");
            Assert.Equal("gpu-next", settings.Video.Renderer);
            Assert.Equal(100, settings.Ui.PageSize);
            Assert.Equal(100, settings.Audio.Volume);
            Assert.False(settings.Shaders.Enabled, "缺的那一节回到装机值，而着色器的装机值是关");
            Assert.Equal("d-1", settings.DeviceId, "文件里还好的东西要留着，不能整份丢掉");
        });

        Test("迁移：列表、字典和它们里头的 null 都要挡住", () =>
        {
            const string json = """
            {
              "SchemaVersion": 10,
              "LastServerId": "s-1",
              "Servers": [ null, { "Id": "s-1", "Name": "果服", "Url": "http://h:8096", "Accounts": [ null ] } ],
              "Playback": { "AudioLanguages": null, "SubtitleLanguages": null },
              "Audio": { "PassthroughCodecs": null },
              "Shaders": { "AnimeKeywords": null },
              "Ui": { "HomeRows": [ null ], "Sort": { "lib-1": null }, "Filters": { "lib-1": null } }
            }
            """;

            var settings = SettingsMigration.FromJson(json, Protector);

            Assert.Equal(1, settings.Servers.Count, "空洞那一项丢掉，真的那台留着");
            Assert.Equal("果服", settings.Servers[0].Name);
            Assert.Equal("s-1", settings.LastServerId);
            Assert.Equal(0, settings.Servers[0].Accounts.Count, "账号列表里的空洞同样丢掉");

            // 列表写成 null 读出来是空列表，而不是那一项的装机默认值：`null` 和 `[]` 在文件里分不出来，而
            // 「一个都不选」本来就是这几行合法的答案（字幕语言留空就是交给 mpv 自己挑）。
            Assert.Equal(0, settings.Playback.SubtitleLanguages.Count);
            Assert.Equal(0, settings.Playback.AudioLanguages.Count);
            Assert.Equal(0, settings.Audio.PassthroughCodecs.Count);
            Assert.Equal(0, settings.Shaders.AnimeKeywords.Count);

            Assert.Equal(0, settings.Ui.HomeRows.Count);
            Assert.Equal(0, settings.Ui.Sort.Count, "指向 null 的库不算一条记档");
            Assert.Equal(0, settings.Ui.Filters.Count);
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
            // 真实的 DPAPI 实现在 Windows shell 工程里，密文与明文自然不同。
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
            original.Shaders.Enabled = false;
            original.Shaders.Gpu = EmbyNian.Mpv.GpuTier.High;
            original.Shaders.ManualGroup = "anime-sweet";
            original.Shaders.RestoreVintageSources = false;
            original.Playback.AudioLanguages = ["日语", "粤语"];
            original.Playback.SubtitleLanguages = ["繁体中文", "中文"];
            original.Mpv.ExecutablePath = @"D:\mpv\mpv.exe";

            var json = JsonSerializer.Serialize(original, SettingsSerializer.WriteOptions);
            var loaded = SettingsMigration.FromJson(json, Protector);

            Assert.Equal("果服", loaded.Servers[0].Name);
            Assert.False(loaded.Shaders.Enabled, "开关状态必须往返一致");
            Assert.Equal(EmbyNian.Mpv.GpuTier.High, loaded.Shaders.Gpu);
            Assert.Equal("anime-sweet", loaded.Shaders.ManualGroup);
            Assert.False(loaded.Shaders.RestoreVintageSources, "老片源修复关掉了就得记住，不然每次开机又打开");
            Assert.Equal("日语,粤语", string.Join(",", loaded.Playback.AudioLanguages), "音轨优先级的顺序同样不能被打乱");
            Assert.Equal("繁体中文,中文", string.Join(",", loaded.Playback.SubtitleLanguages), "字幕优先级的顺序不能被读写打乱");
            Assert.Equal(@"D:\mpv\mpv.exe", loaded.Mpv.ExecutablePath);
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

        Test("迁移：v2 的字幕/音轨优先级字符串都升级成多选", () =>
        {
            const string json = """
            {
              "SchemaVersion": 2,
              "Servers": [ { "Name": "果服", "Url": "http://h:8896" } ],
              "Playback": {
                "SubtitleLanguagePriority": "Simplified Chinese>中文>繁體中文",
                "AudioLanguagePriority": "日语, 英语",
                "PreferForcedSubtitles": false
              }
            }
            """;

            var settings = SettingsMigration.FromJson(json, Protector);

            Assert.Equal("简体中文,中文,繁体中文", string.Join(",", settings.Playback.SubtitleLanguages),
                "顺序就是优先级，而且要落到目录里的规范名字上");

            // v3 到 v9 之间音轨只放得下一种语言，所以这里从前是「只留第一种」。v10 把它变回列表，v2 文件里那一整
            // 串于是完整活下来了 —— 它本来的意思就是一串。
            Assert.Equal("日语,英语", string.Join(",", settings.Playback.AudioLanguages));
            Assert.Equal(SubtitleMode.Always, settings.Playback.SubtitleMode);
            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        });

        Test("迁移：v9 的单个音轨语言变成一元列表，而被关掉的那一档不许复活", () =>
        {
            // AudioTrack 1 是「按语言挑」，0 是「跟随服务器默认」。一个文件完全可能存着 AudioTrack=0 加一个
            // 用不上的 AudioLanguage —— 那个语言当时是被忽略的，在这儿把它捡起来就是悄悄换掉播哪条音轨。
            const string used = """
            {
              "SchemaVersion": 9,
              "Servers": [ { "Name": "果服", "Url": "http://h:8896" } ],
              "Playback": { "AudioTrack": 1, "AudioLanguage": "粤语" }
            }
            """;

            Assert.Equal("粤语", string.Join(",", SettingsMigration.FromJson(used, Protector).Playback.AudioLanguages),
                "在用的那一个要变成一元列表");

            const string ignored = """
            {
              "SchemaVersion": 9,
              "Servers": [ { "Name": "果服", "Url": "http://h:8896" } ],
              "Playback": { "AudioTrack": 0, "AudioLanguage": "粤语" }
            }
            """;

            Assert.Equal(0, SettingsMigration.FromJson(ignored, Protector).Playback.AudioLanguages.Count,
                "当时被忽略的语言不许在迁移里复活");
        });

        Test("迁移：v2 的「优先强制字幕」变成只显示强制字幕", () =>
        {
            const string json = """
            {
              "SchemaVersion": 2,
              "Servers": [ { "Name": "果服", "Url": "http://h:8896" } ],
              "Playback": { "PreferForcedSubtitles": true }
            }
            """;

            var settings = SettingsMigration.FromJson(json, Protector);
            Assert.Equal(SubtitleMode.ForcedOnly, settings.Playback.SubtitleMode);
        });

        // v3 之前这些字段留空是「让 mpv.conf 说话」。现在不读那个文件了，留空就等于 mpv 的裸默认值，
        // 画面会当场变样，所以 v4 要把原来 mpv.conf 提供的那一套补进去。
        Test("迁移：v3 里留空的画面与字幕项，v4 补成原先 mpv.conf 给的值", () =>
        {
            const string v3 = """
            {
              "SchemaVersion": 3,
              "Servers": [ { "Name": "果服", "Url": "http://h:8896" } ],
              "Video": { "Renderer": "", "GpuApi": "", "Dither": "", "Deband": "", "HdrMode": "", "OutputLevels": "" },
              "Playback": {
                "SubtitleFontFamily": "",
                "SubtitleFontPath": "C:\\Windows\\Fonts\\simhei.ttf",
                "SubtitleCodepage": "",
                "SubtitleColor": "",
                "SubtitleBorderSize": "",
                "SubtitleBorderColor": "",
                "SubtitleShadowOffset": "",
                "SubtitleFontSize": 0
              }
            }
            """;

            var settings = SettingsMigration.FromJson(v3, Protector);

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Equal("gpu-next", settings.Video.Renderer);
            Assert.Equal("vulkan", settings.Video.GpuApi,
                "v4 先补成 d3d11，v8 再把它挪到 vulkan —— 没人主动选过 d3d11，那只是当年装机带的值");
            Assert.Equal("fruit", settings.Video.Dither);
            Assert.Equal("auto", settings.Video.Deband);
            Assert.Equal("tonemap", settings.Video.HdrMode);
            Assert.Equal("full", settings.Video.OutputLevels, "v5 起色彩范围默认 PC(0-255)，留空不能再等于「跟随片源标记」");
            Assert.Equal("default", settings.Video.QualityPreset, "v4 的文件没有画质预设这一项，升级后必须落在 default 上");

            Assert.Equal("", settings.Playback.SubtitleCodepage,
                "字幕编码是 v4 唯一不再补的一项：v11 判定 gb18030 那个值本身是错的，补了也要被清掉");
            Assert.Equal("#FFFFFF", settings.Playback.SubtitleColor);
            Assert.Equal("0.5", settings.Playback.SubtitleBorderSize);
            Assert.Equal("#000000", settings.Playback.SubtitleBorderColor);
            Assert.Equal("0.5", settings.Playback.SubtitleShadowOffset);
            Assert.Equal(50, settings.Playback.SubtitleFontSize);
            Assert.False(settings.Playback.SubtitleBold,
                "v4 按当年的 mpv.conf 把粗体补回来，v13 又按用户 2026-09-06 定的默认外观关掉 —— 新指令压过旧配置");
            Assert.Equal("#000000", settings.Playback.SubtitleBackColor, "文件没存过底板颜色，v13 带上新的出厂黑");
            Assert.Equal("SimHei", settings.Playback.SubtitleFontFamily, "旧的字体文件路径要换成 mpv 认的字体族名");
        });

        Test("迁移：v3 里自己选过的画面与字幕项，v4 一个都不改", () =>
        {
            const string v3 = """
            {
              "SchemaVersion": 3,
              "Servers": [ { "Name": "果服", "Url": "http://h:8896" } ],
              "Video": { "Renderer": "gpu", "GpuApi": "vulkan", "Dither": "no", "Deband": "no", "HdrMode": "passthrough" },
              "Playback": {
                "SubtitleFontFamily": "思源黑体 CN",
                "SubtitleCodepage": "big5",
                "SubtitleColor": "#FFF200",
                "SubtitleBorderSize": "0",
                "SubtitleBorderColor": "#FFFFFF",
                "SubtitleShadowOffset": "0",
                "SubtitleFontSize": 36,
                "SubtitleBold": false
              }
            }
            """;

            var settings = SettingsMigration.FromJson(v3, Protector);

            Assert.Equal("gpu", settings.Video.Renderer);
            Assert.Equal("vulkan", settings.Video.GpuApi);
            Assert.Equal("no", settings.Video.Dither, "明确选了「关闭」不是「没选」");
            Assert.Equal("no", settings.Video.Deband);
            Assert.Equal("passthrough", settings.Video.HdrMode);

            Assert.Equal("思源黑体 CN", settings.Playback.SubtitleFontFamily);
            Assert.Equal("big5", settings.Playback.SubtitleCodepage);
            Assert.Equal("#FFF200", settings.Playback.SubtitleColor);
            Assert.Equal("0", settings.Playback.SubtitleBorderSize, "「无描边」也是一种选择");
            Assert.Equal("#FFFFFF", settings.Playback.SubtitleBorderColor);
            Assert.Equal("0", settings.Playback.SubtitleShadowOffset);
            Assert.Equal(36, settings.Playback.SubtitleFontSize);
            Assert.False(settings.Playback.SubtitleBold, "v3 的默认值就是粗体，写了 false 就是特意关掉的");
        });

        // 「色彩范围默认使用 PC(0-255)」。v4 之前留空的意思是「跟随片源标记」，绝大多数片源标的是 TV 范围，
        // 接电脑显示器时就会灰蒙蒙一片。v5 把留空的补成 full，但自己挑过的一个都不动。
        Test("迁移：v4 里留空的色彩范围，v5 补成 PC(0-255)", () =>
        {
            const string v4 = """
            {
              "SchemaVersion": 4,
              "Servers": [ { "Name": "果服", "Url": "http://h:8896" } ],
              "Video": { "OutputLevels": "" },
              "Mpv": {
                "ExecutablePath": "D:\\mpv\\mpv.exe",
                "LibMpvPath": "D:\\mpv\\libmpv-2.dll",
                "ExtraArguments": "--fullscreen --volume=80"
              }
            }
            """;

            var settings = SettingsMigration.FromJson(v4, Protector);
            var json = JsonSerializer.Serialize(settings, SettingsSerializer.WriteOptions);

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Equal("full", settings.Video.OutputLevels);
            Assert.Equal(@"D:\mpv\mpv.exe", settings.Mpv.ExecutablePath, "mpv 本体的路径还是要留着");
            Assert.DoesNotContain("LibMpvPath", json, "libmpv-2.dll 的路径已删除，写回去只会让人以为它还有用");
            Assert.DoesNotContain("ExtraArguments", json);
            Assert.DoesNotContain("--fullscreen", json, "旧的附加参数不能悄悄留在文件里等着某天又被读回来");
        });

        Test("迁移：v4 里自己选过的色彩范围，v5 不改", () =>
        {
            const string v4 = """
            {
              "SchemaVersion": 4,
              "Servers": [ { "Name": "果服", "Url": "http://h:8896" } ],
              "Video": { "OutputLevels": "limited" }
            }
            """;

            var settings = SettingsMigration.FromJson(v4, Protector);
            Assert.Equal("limited", settings.Video.OutputLevels, "接电视特意选了 16-235 的人不该被改回来");
        });

        // v6 是第一个真的把窗口尺寸读回来的版本。v5 之前那三个字段每次保存都写、谁也不读 —— v1 的 WinForms
        // 外壳会还原它们，WinUI 外壳从来没有 —— 所以 v5 文件里那个数要么来自一个已经不存在的外壳，要么就是
        // v5 自己那个没有任何窗口是这个尺寸的默认值 1360×860。
        Test("迁移：v5 里那份没人读过的窗口尺寸，v6 清掉", () =>
        {
            const string v5 = """
            {
              "SchemaVersion": 5,
              "Servers": [ { "Name": "果服", "Url": "http://h:8896" } ],
              "Ui": { "WindowWidth": 1360, "WindowHeight": 860, "WindowMaximized": true }
            }
            """;

            var settings = SettingsMigration.FromJson(v5, Protector);

            Assert.Equal(0, settings.Ui.WindowWidth, "0 就是「还没记过」，下次照外壳算出来的尺寸开");
            Assert.Equal(0, settings.Ui.WindowHeight);
            Assert.Equal(0, settings.Ui.WindowLeft);
            Assert.Equal(0, settings.Ui.WindowTop);
            Assert.False(settings.Ui.WindowMaximized, "连最大化那一位也不算数：它也从来没被读过");
        });

        Test("迁移：v6 记下来的窗口尺寸原样留着", () =>
        {
            const string v6 = """
            {
              "SchemaVersion": 6,
              "Servers": [ { "Name": "果服", "Url": "http://h:8896" } ],
              "Ui": { "WindowLeft": 300, "WindowTop": 200, "WindowWidth": 1471, "WindowHeight": 839 }
            }
            """;

            var settings = SettingsMigration.FromJson(v6, Protector);

            Assert.Equal(300, settings.Ui.WindowLeft);
            Assert.Equal(200, settings.Ui.WindowTop);
            Assert.Equal(1471, settings.Ui.WindowWidth);
            Assert.Equal(839, settings.Ui.WindowHeight);
        });

        Test("校正：没记过的窗口尺寸不许被夹成 400×300", () =>
        {
            // 夹一下就等于替用户宣布他拉过一个 400 宽的窗口，而下次开窗就真的是那个尺寸。
            var fresh = SettingsMigration.Normalize(new AppSettings());

            Assert.Equal(0, fresh.Ui.WindowWidth);
            Assert.Equal(0, fresh.Ui.WindowHeight);
        });

        Test("校正：手改出来的荒唐窗口尺寸还是要夹", () =>
        {
            var settings = new AppSettings();
            settings.Ui.WindowWidth = 60;
            settings.Ui.WindowHeight = 99999;

            SettingsMigration.Normalize(settings);

            Assert.Equal(400, settings.Ui.WindowWidth);
            Assert.Equal(8000, settings.Ui.WindowHeight);
        });

        Test("迁移：旧文件里 mpv.conf / input.conf 的路径直接丢掉", () =>
        {
            const string v1 = """
            {
              "ServerUrl": "http://h:8096",
              "MpvPath": "D:\\mpv\\mpv.exe",
              "MpvConfigPath": "D:\\mpv\\portable_config\\mpv.conf",
              "InputConfigPath": "D:\\mpv\\portable_config\\input.conf"
            }
            """;

            var settings = SettingsMigration.FromJson(v1, Protector);
            var json = JsonSerializer.Serialize(settings, SettingsSerializer.WriteOptions);

            Assert.Equal(@"D:\mpv\mpv.exe", settings.Mpv.ExecutablePath, "mpv 本体的路径还是要留着");
            Assert.DoesNotContain("mpv.conf", json, "客户端已经不读配置文件了，留着路径只会让人以为它还有用");
            Assert.DoesNotContain("input.conf", json);
        });

        // 「删掉这个功能」（2026-09-03）：设置页那张「配置文件」卡片连它那两个路径框一起删掉了，于是 v2 之后
        // 那两个键也跟 v1 的一样直接丢掉。这一条钉的是「丢掉」而不是「读得出来」—— 反序列化器碰到没处放的键
        // 本来就不出声，写回去时它悄悄留在文件里才是问题。
        Test("迁移：新版文件里 mpv.conf / input.conf 的路径也丢掉", () =>
        {
            const string v6 = """
            {
              "SchemaVersion": 6,
              "Servers": [ { "Name": "果服", "Url": "http://h:8896" } ],
              "Mpv": {
                "ExecutablePath": "D:\\mpv\\mpv.exe",
                "ConfigPath": "D:\\mpv\\portable_config\\mpv.conf",
                "InputConfigPath": "D:\\mpv\\portable_config\\input.conf"
              }
            }
            """;

            var settings = SettingsMigration.FromJson(v6, Protector);
            var json = JsonSerializer.Serialize(settings, SettingsSerializer.WriteOptions);

            Assert.Equal(@"D:\mpv\mpv.exe", settings.Mpv.ExecutablePath, "mpv 本体的路径还是要留着");
            Assert.DoesNotContain("ConfigPath", json);
            Assert.DoesNotContain("mpv.conf", json);
            Assert.DoesNotContain("input.conf", json);
        });

        // 设置页每个路径框都走 TypedPath.Clean。资源管理器的「复制为路径」给出的就是带引号的路径，而 Windows
        // 路径里不可能出现双引号，所以带着引号的值只可能是这么来的；留着引号，后面每一次 GetFullPath 都会抛，
        // 那个框就永远找不到它的文件了。之前的版本真这么存过。
        Test("路径框：粘进来的带引号路径去掉引号，别的一个字不动", () =>
        {
            Assert.Equal(@"D:\mpv\mpv.exe", TypedPath.Clean("  \"D:\\mpv\\mpv.exe\"  "));
            Assert.Equal(@"D:\mpv\mpv.exe", TypedPath.Clean(@"  D:\mpv\mpv.exe  "));
            Assert.Equal("", TypedPath.Clean(null));
            Assert.Equal("", TypedPath.Clean("   "));
            Assert.Equal("\"", TypedPath.Clean("\""), "只有一个引号不成对，不能把它当成一对剥掉");
            Assert.Equal(@"D:\一半""引号", TypedPath.Clean(@"D:\一半""引号"), "只有一头带引号的不动");
        });
    }

    private static void RegisterNormalize()
    {
        Test("规整：越界数值被夹回合理范围", () =>
        {
            var settings = SettingsMigration.NewDefaults();
            settings.Ui.PageSize = 100000;
            settings.Playback.MarkWatchedPercent = 5;
            settings.Playback.ProgressReportIntervalSeconds = 0;
            settings.Ui.ImageCacheMegabytes = 999999;

            SettingsMigration.Normalize(settings);

            Assert.Equal(500, settings.Ui.PageSize);
            Assert.Equal(50, settings.Playback.MarkWatchedPercent);
            Assert.Equal(1, settings.Playback.ProgressReportIntervalSeconds);
            Assert.Equal(EmbyNian.Emby.ImageCachePolicy.MaxMegabytes, settings.Ui.ImageCacheMegabytes,
                "图片缓存上限的范围必须和设置页那一行是同一对数");
        });

        Test("规整：设置文件里没有图片缓存上限那一键时读成装机默认值", () =>
        {
            // 从旧版本升上来就是这一档：缺键反序列化出来是 0，而 0 不能读成「一张都不缓存」。
            var settings = SettingsMigration.NewDefaults();
            settings.Ui.ImageCacheMegabytes = 0;

            SettingsMigration.Normalize(settings);

            Assert.Equal(EmbyNian.Emby.ImageCachePolicy.DefaultMegabytes, settings.Ui.ImageCacheMegabytes);
        });

        Test("规整：认不出来的主题 id 被写回默认那套", () =>
        {
            var settings = SettingsMigration.NewDefaults();
            Assert.Equal(EmbyNian.Theming.UiThemes.DefaultId, settings.Ui.Theme);

            settings.Ui.Theme = "以前那套已经删掉的主题";
            SettingsMigration.Normalize(settings);
            Assert.Equal(EmbyNian.Theming.UiThemes.DefaultId, settings.Ui.Theme);

            // 真发生过的那一次：「晴昼」（daylight）2026-09-05 按用户一句「删掉晴昼主题」删了，而用过它的人设置
            // 文件里还存着这个 id。他们下次开机该落到默认那套上，而不是卡在一个不存在的主题上 —— 这三句就是那
            // 条升级路，也是「删一套主题」这件事在代码里唯一需要额外照顾的地方。
            settings.Ui.Theme = "daylight";
            SettingsMigration.Normalize(settings);
            Assert.Equal(EmbyNian.Theming.UiThemes.DefaultId, settings.Ui.Theme);

            // 认得出来的就别动 —— 这条才是这段代码存在的风险所在。
            settings.Ui.Theme = "midnight";
            SettingsMigration.Normalize(settings);
            Assert.Equal("midnight", settings.Ui.Theme);
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

        Test("规整：手改进来的 mpv 选项值只认设置页给出的那些", () =>
        {
            var settings = SettingsMigration.NewDefaults();
            settings.Video.Renderer = "gpu-next";
            settings.Video.GpuApi = "我随手写的";
            settings.Video.HardwareDecoding = "d3d11va";
            settings.Audio.Channels = "7.1.4";
            settings.Audio.PassthroughCodecs = ["truehd", "不存在的编码", "ac3"];
            settings.Playback.SubtitleBackColor = "rgb(1,2,3)";

            SettingsMigration.Normalize(settings);

            Assert.Equal("gpu-next", settings.Video.Renderer);
            Assert.Equal("", settings.Video.GpuApi, "mpv 遇到不认识的选项值会直接退出，不是无视它");
            Assert.Equal("d3d11va", settings.Video.HardwareDecoding);
            Assert.Equal("", settings.Audio.Channels);
            Assert.Equal("ac3,truehd", string.Join(",", settings.Audio.PassthroughCodecs));
            Assert.Equal("", settings.Playback.SubtitleBackColor);
        });

        Test("规整：认不出来的着色器档位 id 退回「自动」，认不出来的显卡档退回低档", () =>
        {
            var settings = SettingsMigration.NewDefaults();
            settings.Shaders.ManualGroup = "我自己起的名字";
            settings.Shaders.Gpu = (EmbyNian.Mpv.GpuTier)99;

            SettingsMigration.Normalize(settings);

            Assert.Equal("", settings.Shaders.ManualGroup, "退回「自动」而不是退回「不上着色器」——后者屏上很难看出来");
            Assert.Equal(EmbyNian.Mpv.GpuTier.Low, settings.Shaders.Gpu, "认不出来的数字就是「没人选过」，那就用最省的那一列");

            settings.Shaders.ManualGroup = "anime-large";
            SettingsMigration.Normalize(settings);
            Assert.Equal("anime-large", settings.Shaders.ManualGroup, "表里有的 id 要原样留着");
        });

        Test("迁移：v6 那四个配置组名和两个阈值不再写回文件，而「所有视频默认启用」跟着走", () =>
        {
            // 属性没了，反序列化器碰到没处放的键本来就不出声 —— 这一条钉的是「真的不出声」，
            // 以及那一个有后继的开关（所有视频默认启用 → 启用着色器）在升级时不会被悄悄打开。
            const string v6 = """
            {
              "SchemaVersion": 6,
              "Shaders": {
                "ApplyToAllVideos": false,
                "DefaultProfile": "2K-iGPU",
                "AnimeProfile": "2K-iGPU-Anime",
                "HighResProfile": "2K-iGPU-Light",
                "HighResThresholdHeight": 1600,
                "LowResProfile": "2K-iGPU",
                "LowResThresholdHeight": 720
              }
            }
            """;

            var loaded = SettingsMigration.FromJson(v6, Protector);
            Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.False(loaded.Shaders.Enabled, "他关掉过着色器，升级不能替他打开");
            Assert.Equal("", loaded.Shaders.ManualGroup, "旧文件里没有手动指定这一项");
            Assert.Equal(EmbyNian.Mpv.GpuTier.Low, loaded.Shaders.Gpu);

            var written = JsonSerializer.Serialize(loaded, SettingsSerializer.WriteOptions);
            foreach (var gone in new[]
            {
                "\"ApplyToAllVideos\"", "\"DefaultProfile\"", "\"AnimeProfile\"",
                "\"HighResProfile\"", "\"LowResProfile\"", "ThresholdHeight"
            })
            {
                Assert.DoesNotContain(gone, written, $"{gone} 已经没有属性了，不该再出现在写回去的文件里");
            }
        });

        Test("迁移：v6 里没关过着色器的文件升上来照旧是开着的", () =>
        {
            // 装机默认 2026-09-05 从「开」改成了「关」，所以这一条从「什么都不用做」变成了真有一手要做：
            // v6 那个开关是开的就得照抄成开，不然升级会替他关掉一件他当年打开的东西。
            var loaded = SettingsMigration.FromJson("""{ "SchemaVersion": 6, "Shaders": { "ApplyToAllVideos": true } }""", Protector);
            Assert.True(loaded.Shaders.Enabled);

            // 而 v6 之后的文件里这个键本来就存着，所以照文件说的算，不受新默认影响。
            var stored = SettingsMigration.FromJson("""{ "SchemaVersion": 7, "Shaders": { "Enabled": true } }""", Protector);
            Assert.True(stored.Shaders.Enabled, "存过「开」的人不该被新的装机默认关掉");
        });

        Test("迁移：文件里存着删掉的画质预设 HQ，读回来退成 default", () =>
        {
            // HQ 2026-09-04 删掉了（它是用户 mpv.conf 里手抄的段名，mpv 自己并不认识这个 profile）。存着它的文件
            // 不能坏，也不能把这个名字留在文件里 —— 校验退不回出厂值的话，下一次启动就是 profile=HQ 交给 mpv，
            // 而 mpv 碰到不认识的 profile 名会直接退出、一个字节都不播。
            var loaded = SettingsMigration.FromJson("""{ "SchemaVersion": 7, "Video": { "QualityPreset": "HQ" } }""", Protector);

            Assert.Equal("default", loaded.Video.QualityPreset, "认不出来的预设名要退回出厂那一项");
            Assert.DoesNotContain("\"HQ\"", JsonSerializer.Serialize(loaded, SettingsSerializer.WriteOptions),
                "退回去之后写回文件里也不该再留着这个名字");
        });

        Test("迁移：v7 之前的图形接口挪到 vulkan，opengl 那种自己选过的不动", () =>
        {
            // 2026-09-04 实测：同一条 ArtCNN_C4F16 + CfL 的链，1080p 放到 2560×1440，只换图形接口 —— vulkan
            // 45 fps、d3d11 8.7 fps，而 24fps 的片子要 24。「自动挑选」在 Windows 上就是 d3d11，所以两个都要挪；
            // 这两个值都不是有人特意选的，它们是当年装机带的。
            Assert.Equal("vulkan",
                SettingsMigration.FromJson("""{ "SchemaVersion": 7, "Video": { "GpuApi": "d3d11" } }""", Protector).Video.GpuApi);
            Assert.Equal("vulkan",
                SettingsMigration.FromJson("""{ "SchemaVersion": 7, "Video": { "GpuApi": "" } }""", Protector).Video.GpuApi,
                "「自动挑选」在 Windows 上落的就是 d3d11");
            Assert.Equal("vulkan",
                SettingsMigration.FromJson("""{ "SchemaVersion": 7 }""", Protector).Video.GpuApi,
                "整个 Video 段都没有的文件，读出来是装机默认，也该是 vulkan");

            Assert.Equal("opengl",
                SettingsMigration.FromJson("""{ "SchemaVersion": 7, "Video": { "GpuApi": "opengl" } }""", Protector).Video.GpuApi,
                "没人会因为装机默认落到 opengl —— 写着它就是有人选的");

            Assert.Equal("vulkan", new AppSettings().Video.GpuApi, "装机默认");
            Assert.Equal("vulkan", Mpv.MpvRenderCheck.PreferredApi, "迁移和装机默认读的是同一个常量");
        });

        Test("迁移：v9 把「高帧率或高刷新率时使用音频同步」拨回开，v9 之后关掉的不动", () =>
        {
            // 这个开关到 v8 只管「高帧率片源」，2026-09-04 长出了第二半 ——「屏幕超过 120Hz」。所以 v9 以前存下来的
            // 一个「关」，是拿一句关于帧率的回答当成了关于屏幕的回答。这台机器上它正好把唯一那条实测能把 144Hz
            // 屏上显卡占用砍一半的规则（24.7% 对 50.1%）挡在门外，而且一个字都不会说 —— 日志只在规则真出手时才写。
            // 和上面 v8 挪图形接口是同一个判断：一个值不是有人为它现在的含义选的，那就不是偏好。
            Assert.True(
                SettingsMigration.FromJson(
                    """{ "SchemaVersion": 8, "Video": { "HighFrameRateAudioSync": false } }""", Protector)
                    .Video.HighFrameRateAudioSync,
                "v8 的文件里那个「关」是关于帧率的，不能当成关于屏幕的");

            Assert.True(
                SettingsMigration.FromJson("""{ "SchemaVersion": 6 }""", Protector).Video.HighFrameRateAudioSync,
                "更老的文件同样，而且它本来就是开着的");

            // 只搬一次。v9 起设置页那一行的名字和说明把两半都写出来了，所以从这里往后关掉它是真的选择。
            Assert.False(
                SettingsMigration.FromJson(
                    $$"""{ "SchemaVersion": {{AppSettings.CurrentSchemaVersion}}, "Video": { "HighFrameRateAudioSync": false } }""",
                    Protector)
                    .Video.HighFrameRateAudioSync,
                "v9 之后关掉的是他自己的决定，迁移不许再碰");

            Assert.True(new AppSettings().Video.HighFrameRateAudioSync, "装机默认是开着的");
        });

        Test("规整：字幕外观的数值范围", () =>
        {
            var settings = SettingsMigration.NewDefaults();
            settings.Playback.SubtitleBackOpacity = 400;
            settings.Playback.SubtitleFontSize = 5;
            settings.Playback.SubtitleScalePercent = 9000;

            SettingsMigration.Normalize(settings);

            Assert.Equal(100, settings.Playback.SubtitleBackOpacity);
            Assert.Equal(16, settings.Playback.SubtitleFontSize);
            Assert.Equal(PlaybackSettings.MaximumSubtitleScale, settings.Playback.SubtitleScalePercent);

            settings.Playback.SubtitleFontSize = 0;
            SettingsMigration.Normalize(settings);
            Assert.Equal(0, settings.Playback.SubtitleFontSize, "0 是「用 mpv 自己的默认字号」，不能被夹成 16");

            // 设置页那一行和这里必须是同一条规则：以前只有这儿夹，于是屏上填 5 这一次播放就是 5，
            // 下次启动才变成 16 —— 用户看见的那个值既没留下也没被拒绝。
            Assert.Equal(16, PlaybackSettings.ClampFontSize(5), "行里输的数走的是同一条规则");
            Assert.Equal(0, PlaybackSettings.ClampFontSize(0));
            Assert.Equal(0, PlaybackSettings.ClampFontSize(-8), "负数也当「不指定」");
            Assert.Equal(PlaybackSettings.MaximumSubtitleFontSize, PlaybackSettings.ClampFontSize(9999));
            Assert.Equal(72, PlaybackSettings.ClampFontSize(72));
        });

        Test("迁移：v11 把写死的 gb18030 字幕编码改回自动识别", () =>
        {
            // 那个值是装机默认，不是谁挑的，而它把 mpv 的自动识别关掉了：Big5 的繁体字幕会被按 GB18030
            // 读成乱码（2026-09-05 渲图对比过，简体 GBK 那一半两种读法逐字节相同）。所以这一步跟 v8 的
            // 图形接口、v9 的音频同步同一个道理 —— 没人选过的值不算偏好。
            var upgraded = SettingsMigration.FromJson(
                """{"SchemaVersion":10,"Playback":{"SubtitleCodepage":"gb18030"}}""", Protector);
            Assert.Equal("", upgraded.Playback.SubtitleCodepage);

            // 自己挑的编码不许动 —— 自动识别也会认错，那一档就是给这种情况留的。
            var chosen = SettingsMigration.FromJson(
                """{"SchemaVersion":10,"Playback":{"SubtitleCodepage":"big5"}}""", Protector);
            Assert.Equal("big5", chosen.Playback.SubtitleCodepage);

            // v11 之后再填回 gb18030 就是他的决定，迁移不许再碰。
            var deliberate = SettingsMigration.FromJson(
                "{\"SchemaVersion\":" + AppSettings.CurrentSchemaVersion
                    + ",\"Playback\":{\"SubtitleCodepage\":\"gb18030\"}}",
                Protector);
            Assert.Equal("gb18030", deliberate.Playback.SubtitleCodepage);

            Assert.Equal("", new PlaybackSettings().SubtitleCodepage, "装机默认是自动识别");
        });

        Test("迁移：旧文件里的「无背景」不再画出底板", () =>
        {
            // 「无背景」以前是一个全透明的黑色，靠把阴影也一起弄没来表达「没有底板」。它不是颜色，Rgb
            // 不认，于是 v13 的「不是颜色就换出厂黑」把它一起接了过去；底板样式那一行照样关着 ——
            // 屏上还是没有底板，变的只是阴影的颜色从此钉在黑色上（mpv 自己的阴影本来就是黑的）。
            var carried = SettingsMigration.FromJson(
                """{"SchemaVersion":10,"Playback":{"SubtitleBackColor":"none"}}""", Protector);

            Assert.Equal("#000000", carried.Playback.SubtitleBackColor);
            Assert.Equal("", carried.Playback.SubtitleBackStyle);
        });

        Test("迁移：v14 把字幕默认字体换回 Microsoft YaHei", () =>
        {
            // 「默认字体改为Microsoft YaHei」（2026-09-06）。v12 把装机默认挪到程序自带的方正中等线
            // 简体时写过一批文件，v14 把它们一并跟到新默认 —— 存着旧默认就是没挑过字体，v12 怎么搬
            // 方正中等线简体，v14 就怎么搬回来。自带字体的英文族名 FZZhongDengXian-Z07S 是同一个家族，
            // 一样要跟；自己挑的字体（思源黑体）不动。
            var v12default = SettingsMigration.FromJson(
                """{"SchemaVersion":13,"Playback":{"SubtitleFontFamily":"方正中等线简体"}}""", Protector);
            Assert.Equal("Microsoft YaHei", v12default.Playback.SubtitleFontFamily);

            var alias = SettingsMigration.FromJson(
                """{"SchemaVersion":13,"Playback":{"SubtitleFontFamily":"FZZhongDengXian-Z07S"}}""", Protector);
            Assert.Equal("Microsoft YaHei", alias.Playback.SubtitleFontFamily, "英文族名是同一家族，一样跟到新默认");

            // v11 的文件先过 v12 再过 v14：Microsoft YaHei → 方正中等线简体 → Microsoft YaHei，终点
            // 还是新默认；.Heiti J 那个从来画不出来的名字也一样。
            var v11 = SettingsMigration.FromJson(
                """{"SchemaVersion":11,"Playback":{"SubtitleFontFamily":"Microsoft YaHei"}}""", Protector);
            Assert.Equal("Microsoft YaHei", v11.Playback.SubtitleFontFamily);

            var macish = SettingsMigration.FromJson(
                """{"SchemaVersion":11,"Playback":{"SubtitleFontFamily":".Heiti J"}}""", Protector);
            Assert.Equal("Microsoft YaHei", macish.Playback.SubtitleFontFamily);

            var picked = SettingsMigration.FromJson(
                """{"SchemaVersion":13,"Playback":{"SubtitleFontFamily":"思源黑体 CN"}}""", Protector);
            Assert.Equal("思源黑体 CN", picked.Playback.SubtitleFontFamily, "自己挑的字体不是装机默认，不许动");

            var empty = SettingsMigration.FromJson(
                """{"SchemaVersion":13,"Playback":{"SubtitleFontFamily":""}}""", Protector);
            Assert.Equal("Microsoft YaHei", empty.Playback.SubtitleFontFamily, "空值走兜底族名，兜底族名就是新默认");

            // v14 起再存方正中等线简体就是他的决定 —— 字体照样自带、照样可选，迁移不许再碰。
            var deliberate = SettingsMigration.FromJson(
                "{\"SchemaVersion\":" + AppSettings.CurrentSchemaVersion
                    + ",\"Playback\":{\"SubtitleFontFamily\":\"方正中等线简体\"}}",
                Protector);
            Assert.Equal("方正中等线简体", deliberate.Playback.SubtitleFontFamily);

            Assert.Equal("Microsoft YaHei", new PlaybackSettings().SubtitleFontFamily, "装机默认是每台 Windows 都有的雅黑");
            Assert.Equal("Microsoft YaHei", FontFamilies.Default, "兜底族名也是它：Windows 上一定找得到");
        });

        Test("描边大小、阴影：从固定几档改成自由数字输入", () =>
        {
            // 「这个不用弄成固定的选项，改成输入数字」（2026-09-06）。不在旧档位里的数字现在是合法值；
            // 逗号当小数点；范围外的拉回 0–10（mpv.exe 拿到范围外的选项值是拒启动，上限不能松，跟字号
            // 那行「填更小的会被抬上来」一条规矩）；不是数的退回「不设置」，和颜色那三行同一条规矩。
            // 这些走的是 Normalize，每次加载都过一遍，不用版本号。
            var free = SettingsMigration.FromJson(
                """{"SchemaVersion":14,"Playback":{"SubtitleBorderSize":"0.75","SubtitleShadowOffset":"2"}}""", Protector);
            Assert.Equal("0.75", free.Playback.SubtitleBorderSize, "不在旧档位里的数字现在是合法值");
            Assert.Equal("2", free.Playback.SubtitleShadowOffset);

            var comma = SettingsMigration.FromJson(
                """{"SchemaVersion":14,"Playback":{"SubtitleBorderSize":"1,5"}}""", Protector);
            Assert.Equal("1.5", comma.Playback.SubtitleBorderSize, "逗号当小数点收下");

            var clamped = SettingsMigration.FromJson(
                """{"SchemaVersion":14,"Playback":{"SubtitleBorderSize":"-1","SubtitleShadowOffset":"11"}}""", Protector);
            Assert.Equal("0", clamped.Playback.SubtitleBorderSize, "负的拉回下限，正好是「无描边」");
            Assert.Equal("10", clamped.Playback.SubtitleShadowOffset, "超出上限的拉回 10 —— mpv.exe 拿到范围外的值是拒启动");

            var junk = SettingsMigration.FromJson(
                """{"SchemaVersion":14,"Playback":{"SubtitleBorderSize":"abc"}}""", Protector);
            Assert.Equal("", junk.Playback.SubtitleBorderSize, "不是数的退回「不设置」，mpv 自己的 1.65 接手");
        });

        Test("迁移：v13 把出厂字幕外观换成用户定过的那套（不粗体、黑底板）", () =>
        {
            // 「把默认字幕样式设置为…」（2026-09-06）。他给的八项里六项当时就是出厂值，要带过旧文件的
            // 是两处：加粗（旧出厂是开，存着它就是没挑过）和底板颜色（旧出厂是「不设置」，从来没人挑过
            // 颜色）。自己挑过的一律不动 —— v13 之后再存回旧默认就是他的决定，迁移不许再碰。
            var upgraded = SettingsMigration.FromJson(
                """{"SchemaVersion":12,"Playback":{"SubtitleBold":true,"SubtitleBackColor":""}}""", Protector);
            Assert.False(upgraded.Playback.SubtitleBold, "存着旧出厂的「开」就是没挑过，换成新出厂的「关」");
            Assert.Equal("#000000", upgraded.Playback.SubtitleBackColor, "存着「不设置」就是没挑过颜色，带上新出厂的黑");

            var untouched = SettingsMigration.FromJson(
                """{"SchemaVersion":12,"Playback":{"SubtitleBold":false,"SubtitleBackColor":"#ff0000"}}""", Protector);
            Assert.False(untouched.Playback.SubtitleBold, "自己关掉的不动");
            Assert.Equal("#FF0000", untouched.Playback.SubtitleBackColor, "自己挑的颜色不动，只统一大小写");

            var deliberate = SettingsMigration.FromJson(
                "{\"SchemaVersion\":" + AppSettings.CurrentSchemaVersion
                    + ",\"Playback\":{\"SubtitleBold\":true,\"SubtitleBackColor\":\"\"}}",
                Protector);
            Assert.True(deliberate.Playback.SubtitleBold, "v13 之后再开粗体是他的决定，迁移不许再碰");
            Assert.Equal("", deliberate.Playback.SubtitleBackColor, "v13 之后清回「不设置」也是他的决定");

            Assert.False(new PlaybackSettings().SubtitleBold, "装机默认不加粗");
            Assert.Equal("#000000", new PlaybackSettings().SubtitleBackColor, "装机底板颜色是黑色");
        });

        Test("迁移：三行字幕颜色认一切合法的 HTML 颜色代码，不再限于旧色板", () =>
        {
            // 从前颜色是六个预设里选一个，迁移把不在色板上的值全数扔掉。拾色器进了门之后那一半就是
            // 错的：用户挑的任何 #RRGGBB 都得原样过迁移，只有真的不是颜色的才退回「不设置」。
            var carried = SettingsMigration.FromJson(
                """
                {"SchemaVersion":11,"Playback":{
                  "SubtitleColor":"#ac5d5d",
                  "SubtitleBorderColor":"#123ABC",
                  "SubtitleBackColor":"#ffffff"}}
                """, Protector);

            Assert.Equal("#AC5D5D", carried.Playback.SubtitleColor, "写出去统一大写，文件里是哪一种写法无关紧要");
            Assert.Equal("#123ABC", carried.Playback.SubtitleBorderColor);
            Assert.Equal("#FFFFFF", carried.Playback.SubtitleBackColor);

            var junk = SettingsMigration.FromJson(
                """{"SchemaVersion":11,"Playback":{"SubtitleColor":"黄色","SubtitleBorderColor":"#12345"}}""", Protector);
            Assert.Equal("", junk.Playback.SubtitleColor, "不是颜色的值退到「不设置」");
            Assert.Equal("", junk.Playback.SubtitleBorderColor, "位数不够的也一样");
        });

        Test("规整：跨度与音频延迟被夹回范围", () =>
        {
            var settings = SettingsMigration.NewDefaults();
            settings.Playback.SeekForwardSeconds = 0;
            settings.Playback.SeekBackwardSeconds = 9000;
            settings.Playback.ResumeRewindSeconds = -5;
            settings.Audio.DelayMilliseconds = 99999;
            settings.Video.NetworkCacheMegabytes = -1;

            SettingsMigration.Normalize(settings);

            Assert.Equal(1, settings.Playback.SeekForwardSeconds);
            Assert.Equal(600, settings.Playback.SeekBackwardSeconds);
            Assert.Equal(0, settings.Playback.ResumeRewindSeconds);
            Assert.Equal(5000, settings.Audio.DelayMilliseconds);
            Assert.Equal(0, settings.Video.NetworkCacheMegabytes);
        });

        Test("规整：音量默认 100，越界夹回 0–130", () =>
        {
            Assert.Equal(100, SettingsMigration.NewDefaults().Audio.Volume, "没人调过音量就是满的");

            // 天花板是 AudioSettings.MaxVolume（130，mpv 自己的 volume-max 默认值），不是这里写死的一个数 ——
            // 这一条正是「六处必须一致」里的一处。
            var loud = SettingsMigration.NewDefaults();
            loud.Audio.Volume = 400;
            SettingsMigration.Normalize(loud);
            Assert.Equal(AudioSettings.MaxVolume, loud.Audio.Volume);

            var boosted = SettingsMigration.NewDefaults();
            boosted.Audio.Volume = 130;
            SettingsMigration.Normalize(boosted);
            Assert.Equal(130, boosted.Audio.Volume, "130 在范围之内，不许被夹回 100");

            var negative = SettingsMigration.NewDefaults();
            negative.Audio.Volume = -20;
            SettingsMigration.Normalize(negative);
            Assert.Equal(0, negative.Audio.Volume);
        });

        Test("规整：音量均衡只收目录里那三档", () =>
        {
            // 这个值会原样变成一条 af 滤镜串交给 mpv，而 mpv 碰到认不出的滤镜会直接退出、一个字节都不播。
            var typo = SettingsMigration.NewDefaults();
            typo.Audio.VolumeNormalize = "lavfi=[definitely-not-a-filter]";
            SettingsMigration.Normalize(typo);
            Assert.Equal("", typo.Audio.VolumeNormalize, "认不出来的滤镜串要退回「不启用」");

            var kept = SettingsMigration.NewDefaults();
            kept.Audio.VolumeNormalize = MpvOutputOptions.LoudNorm;
            SettingsMigration.Normalize(kept);
            Assert.Equal(MpvOutputOptions.LoudNorm, kept.Audio.VolumeNormalize);
        });

        Test("规整：设置文件里存着的 auto 读成空串", () =>
        {
            // 0.0.1 那个版本的音频输出设备下拉里，中文的「跟随系统默认设备」下面还并排放着 mpv 自己那一项英文的
            // 「Autoselect device」，点了它存下来的是 auto。那一项现在不在下拉里了，所以留着这个值的话，这一行会
            // 走 Options 的「设置文件中的值」兜底分支，显示成「auto（设置文件中的值，这台机器上没找到）」——
            // 一句不实的话：auto 恰恰是永远找得到的那一个。
            var stored = SettingsMigration.NewDefaults();
            stored.Audio.Device = "auto";
            SettingsMigration.Normalize(stored);
            Assert.Equal("", stored.Audio.Device, "auto 就是「跟随系统默认设备」，两者只留一个写法");

            var upper = SettingsMigration.NewDefaults();
            upper.Audio.Device = "AUTO";
            SettingsMigration.Normalize(upper);
            Assert.Equal("", upper.Audio.Device);

            // 真设备名一个字都不许动 —— 它是从 mpv 那儿读来的、没人抄得对的一串东西。
            var real = SettingsMigration.NewDefaults();
            real.Audio.Device = "wasapi/{0.0.0.00000000}.{9c3d1b2e}";
            SettingsMigration.Normalize(real);
            Assert.Equal("wasapi/{0.0.0.00000000}.{9c3d1b2e}", real.Audio.Device);
        });

        Test("规整：档位表按放大倍数挑，装机默认不会把任何一档挑成摆设", () =>
        {
            // 从前这一条钉的是「低清阈值必须低于高清阈值」：两个框的范围在 720–1080 上重叠、Resolve 先判高清，
            // 于是低清阈值一顶到高清阈值上，低清配置组就成了摆设 —— 设了、看着像设了、永远不生效。两个阈值
            // 2026-09-03 连同那四个组名一起删掉了，接替它们的是放大倍数，而这一条守的还是同一件事：八个档位
            // 每一个都真的挑得到，没有一个是永远轮不到的摆设。
            var shaders = SettingsMigration.Normalize(SettingsMigration.NewDefaults()).Shaders;

            // 开关明写成开：装机默认 2026-09-05 改成了关（那一条由「着色器档位：设置里的装机默认值」钉着），
            // 而这一条问的是「开着的时候八个档位是不是都挑得到」—— 别的几项照旧是装机值，那才是这一条的意思。
            shaders.Enabled = true;

            (int SourceWidth, int SourceHeight, int OutWidth, int OutHeight, bool Animated, string Id)[] reachable =
            [
                (3840, 2160, 2560, 1440, false, "live-shrink"),
                (1920, 1080, 2560, 1440, false, "live-slight"),
                (1280, 720, 2560, 1440, false, "live-sweet"),
                (854, 480, 2560, 1440, false, "live-large"),
                (3840, 2160, 2560, 1440, true, "anime-shrink"),
                (1920, 1080, 2560, 1440, true, "anime-slight"),
                (1280, 720, 2560, 1440, true, "anime-sweet"),
                (854, 480, 2560, 1440, true, "anime-large")
            ];

            foreach (var row in reachable)
            {
                Assert.Equal(
                    row.Id,
                    shaders.Resolve(row.Animated, row.SourceWidth, row.SourceHeight, row.OutWidth, row.OutHeight).Group?.Id,
                    $"{row.SourceHeight}p 上 {row.OutHeight}p 该挑到 {row.Id}");
            }

            Assert.Equal(8, reachable.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count(), "八个档位一个不落");
        });

        Test("语言列表：mpv 的「>」写法和全角分隔符都算分隔符", () =>
        {
            // 设置页那个框以前只认半角和全角的逗号、分号。而 mpv 自己、以及本客户端 v2 的设置文件，
            // 都是用「>」写这个列表的 —— 于是「简体中文 > 中文」被当成一整个语言名存了下来，匹配不到
            // 任何轨道，框里还照样把它显示回来。
            Assert.Equal("简体中文, 中文, 繁体中文", Joined("简体中文 > 中文 > 繁体中文"));
            Assert.Equal("简体中文, 中文", Joined("简体中文＞中文"));
            Assert.Equal("日语, 英语", Joined("日语、英语"));
            Assert.Equal("日语, 英语", Joined("日语；英语"));
            Assert.Equal("日语, 英语", Joined("日语 → 英语"));

            // 写成 mpv 代码的归到目录里的名字上，重复的只留第一次出现的位置，空白项直接消失。
            Assert.Equal("简体中文, 英语", Joined("chs, eng, zh-Hans, , 英语"));

            // 目录里没有的原样留着：它会作为原始 mpv 语言代码传下去，这是指定目录外语言的唯一办法。
            Assert.Equal("hu, pl", Joined("hu, pl"));

            // 中英写法是同一门语言：出厂默认列出的四种写法归并成两个名字，别名不占优先级的位置。
            Assert.Equal("简体中文, 中文", Joined("Chinese Simplified, 简体中文, Chinese, 中文"));

            // 出厂默认（2026-09-05 定版）：简体中文在前，中文兜底。中文是 zh 家族的统称项，
            // 繁体不用单独列 —— 没有简体时它就是中文会命中的那一条。
            Assert.Equal("简体中文, 中文", string.Join(", ", SettingsMigration.NewDefaults().Playback.SubtitleLanguages));

            Assert.Equal(0, TrackLanguagePriority.ParseList("   ").Count);
            Assert.Equal(0, TrackLanguagePriority.ParseList(null).Count);

            // 设置文件里存的是已经拆好的列表，规整时走的是同一套清理。**音轨那一份也走这一套** —— 它 v10 才变成
            // 列表，而字幕那边正是在「简体中文 > 中文 被当成一个语言名」上栽过的，所以两边必须是同一段代码。
            var settings = SettingsMigration.NewDefaults();
            settings.Playback.SubtitleLanguages = ["  简体中文  ", "chs", "", "eng"];
            settings.Playback.AudioLanguages = ["日语", "jpn", "  ", "粤语"];
            SettingsMigration.Normalize(settings);
            Assert.Equal("简体中文, 英语", string.Join(", ", settings.Playback.SubtitleLanguages));
            Assert.Equal("日语, 粤语", string.Join(", ", settings.Playback.AudioLanguages),
                "音轨列表同样去重、去空白、归到目录里的名字上");

            Assert.Equal("日语, 粤语, 英语", Joined("日语 > 粤语 > 英语"),
                "音轨那一行现在也吃这种写法，而它从前一整串会被当成一个语言名");

            static string Joined(string? typed) => string.Join(", ", TrackLanguagePriority.ParseList(typed));
        });
    }

    /// <summary>
    /// 恢复默认设置。四条钉的是同一件事的四个角：该回默认的一项不漏、不该动的一项不碰、身份那一摊一个字不动，
    /// 以及子对象还是原来那几个。
    /// <para>
    /// 头一条用反射逐个属性扫，理由和 <see cref="SettingsReset"/> 里用反射同一个：以后往设置类上加一项，这一条
    /// 自己就会把它算进来。手写一份「该回默认的属性名单」的话，漏掉新加那一项的测试和漏掉它的实现会一起漏。
    /// </para>
    /// </summary>
    private static void RegisterReset()
    {
        Test("恢复默认：设置页管的每一项都回到装机值", () =>
        {
            var settings = SettingsMigration.NewDefaults();

            // 每一组挨个属性改掉，再看恢复之后是不是一项不差地回到了一个全新对象的样子。「音频」也在里面 ——
            // 它是唯一一组用「列出要留的」写法的，也就是以后新加的字段一律会被清掉，所以更要有人逐项看着；
            // 它那一项例外（音量留着）由下面单独对付。
            foreach (var group in new object[]
                { settings.Mpv, settings.Playback, settings.Video, settings.Audio, settings.Shaders })
            {
                var before = Snapshot(group);
                var changed = MutateAll(group);

                Assert.True(changed > 0, $"{group.GetType().Name} 一项都没改动，这一条会变成空话");
                Assert.True(Snapshot(group) != before, $"{group.GetType().Name} 改完和改前一模一样");
            }

            // 音量是上面那一趟顺带改掉的，而它本来就该原样留着（播放器上次被留在哪儿，设置页上没有这一行），
            // 所以「装机的样子」对这一组来说是「除音量之外全默认」。留不留由下一条单独钉，这里只是别错报。
            var keptVolume = settings.Audio.Volume;

            // 「界面」那一组混着两类，这里只改设置页上有的那几行；记下来的那几项由下一条管。
            var ui = settings.Ui;
            ui.Theme = "midnight";
            ui.PageSize = 37;
            ui.ShowWatchedIndicators = false;
            ui.ShowHomeBanner = false;
            ui.ImageCacheMegabytes = ImageCachePolicy.MaxMegabytes;
            ui.ScoreSource = ScoreSource.Critic;
            ui.HomeRows = [new HomeRowSetting { Key = "library:1", Title = "改过", Visible = false }];

            SettingsReset.Restore(settings);

            AssertDefaults(settings.Mpv, new MpvSettings());
            AssertDefaults(settings.Playback, new PlaybackSettings());
            AssertDefaults(settings.Video, new VideoSettings());
            AssertDefaults(settings.Audio, new AudioSettings { Volume = keptVolume });
            AssertDefaults(settings.Shaders, new ShaderAutomationSettings());
            Assert.False(settings.Shaders.Enabled, "「恢复默认」之后着色器要是关的 —— 用户 2026-09-05 定的");

            var fresh = new UiSettings();
            Assert.Equal(fresh.Theme, ui.Theme, "主题回默认那一套");
            Assert.Equal(fresh.PageSize, ui.PageSize);
            Assert.Equal(fresh.ShowWatchedIndicators, ui.ShowWatchedIndicators);
            Assert.Equal(fresh.ShowHomeBanner, ui.ShowHomeBanner, "「恢复默认」之后主页轮播大图要是开的");
            Assert.Equal(fresh.ImageCacheMegabytes, ui.ImageCacheMegabytes);
            Assert.Equal(fresh.ScoreSource, ui.ScoreSource);
            Assert.Equal(0, ui.HomeRows.Count, "主页版面回到空，也就是「照默认版面排」");
        });

        RegisterResetKeeps();
    }

    /// <summary>恢复默认不许动的那几摊：身份、记下来的位置、子对象本身。</summary>
    private static void RegisterResetKeeps()
    {
        Test("恢复默认：服务器、账号和令牌一个字都不动", () =>
        {
            var settings = SettingsMigration.NewDefaults();
            var vault = new CredentialVault(Protector);

            var server = settings.Servers[0];
            server.Name = "客厅那台";
            server.Url = "http://192.168.31.230:8896";

            var account = new AccountProfile { Username = "老王", UserId = "u-1" };
            vault.SetPassword(account, "密码123", remember: true);
            vault.SetAccessToken(account, "token-abc");
            server.Accounts.Add(account);
            settings.Remember(server, account);

            var deviceId = settings.DeviceId;

            SettingsReset.Restore(settings);

            Assert.Equal(1, settings.Servers.Count, "服务器一台都不许少");
            Assert.True(ReferenceEquals(server, settings.Servers[0]), "还是原来那一台，不是重建的");
            Assert.Equal("客厅那台", server.Name);
            Assert.Equal("http://192.168.31.230:8896", server.Url);
            Assert.Equal(1, server.Accounts.Count);
            Assert.Equal("老王", server.Accounts[0].Username);
            Assert.Equal("密码123", vault.GetPassword(account), "密码还在，恢复默认不等于退出登录");
            Assert.Equal("token-abc", vault.GetAccessToken(account), "令牌还在");
            Assert.Equal(deviceId, settings.DeviceId, "设备 id 换掉就等于让 Emby 把这台机器当成新设备");
            Assert.Equal(server.Id, settings.LastServerId);
            Assert.Equal(account.Id, settings.LastAccountId);
        });

        Test("恢复默认：记下来的位置不算设置", () =>
        {
            var settings = SettingsMigration.NewDefaults();
            var ui = settings.Ui;

            ui.WindowLeft = 120;
            ui.WindowTop = 60;
            ui.WindowWidth = 1600;
            ui.WindowHeight = 900;
            ui.WindowMaximized = true;
            ui.LastLibraryId = "lib-7";
            ui.Sort["lib-7"] = new LibrarySort { By = "DateCreated", Descending = true };
            ui.Filters["lib-7"] = new ItemFilters();
            ui.Views["lib-7"] = LibraryView.List;
            settings.Audio.Volume = 118;

            SettingsReset.Restore(settings);

            Assert.Equal(120, ui.WindowLeft, "窗口位置不是设置页上的一行");
            Assert.Equal(60, ui.WindowTop);
            Assert.Equal(1600, ui.WindowWidth, "清成 0 就等于替用户宣布他从没拉过这个窗口");
            Assert.Equal(900, ui.WindowHeight);
            Assert.True(ui.WindowMaximized);
            Assert.Equal("lib-7", ui.LastLibraryId);
            Assert.Equal("DateCreated", ui.Sort["lib-7"].By, "每个库各自的排序是 Emby 也按库记的东西");
            Assert.True(ui.Filters.ContainsKey("lib-7"));
            Assert.Equal(LibraryView.List, ui.Views["lib-7"]);
            Assert.Equal(118, settings.Audio.Volume, "音量是播放器上次被留在哪儿，设置页上没有这一行");
        });

        Test("恢复默认：几个子对象还是原来那几个", () =>
        {
            // 这一条钉的是那个「三处读数全对、行为照旧」的坑：容器把 AppSettings.Shaders 直接交给了单例
            // ShaderGroupResolver，AudioDeviceCatalogue 闭包着 AppSettings.Mpv，设置页每一行捕获的也是子对象
            // 本身。换成新对象的话，屏上每一行都显示成默认值，而真去放片子时一个都没变。
            var settings = SettingsMigration.NewDefaults();
            var (mpv, playback, video, audio, shaders, ui) =
                (settings.Mpv, settings.Playback, settings.Video, settings.Audio, settings.Shaders, settings.Ui);

            settings.Video.Renderer = "gpu";
            SettingsReset.Restore(settings);

            Assert.True(ReferenceEquals(mpv, settings.Mpv), "Mpv 必须是就地改的");
            Assert.True(ReferenceEquals(playback, settings.Playback), "Playback 必须是就地改的");
            Assert.True(ReferenceEquals(video, settings.Video), "Video 必须是就地改的");
            Assert.True(ReferenceEquals(audio, settings.Audio), "Audio 必须是就地改的");
            Assert.True(ReferenceEquals(shaders, settings.Shaders), "Shaders 必须是就地改的");
            Assert.True(ReferenceEquals(ui, settings.Ui), "Ui 必须是就地改的");
            Assert.Equal(new VideoSettings().Renderer, video.Renderer, "而且那个原来的对象里真的换成了默认值");
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

        Test("存储：每个库记住的排序与筛选都能读回来", () =>
        {
            var root = TempRoot();
            try
            {
                var store = new SettingsStore(new AppPaths(root), Protector);
                var settings = SettingsMigration.NewDefaults();

                settings.Ui.Sort["lib-1"] = new LibrarySort { By = "DateCreated,SortName", Descending = true };
                settings.Ui.Filters["lib-1"] = new ItemFilters
                {
                    Toggles = ["unplayed", "hd"],
                    Genres = ["动画"],
                    Years = ["2020"]
                };

                store.Save(settings);
                var loaded = store.Load();

                Assert.True(loaded.Ui.Sort["lib-1"].Descending);
                var filters = loaded.Ui.Filters["lib-1"];

                // 4 项：徽标上的数字要和存盘前一样
                Assert.Equal(4, filters.Count);
                Assert.True(filters.Has("hd"));
                Assert.Equal("动画", filters.Genres[0]);
                Assert.Equal("2020", filters.Years[0]);
                Assert.Equal(0, filters.Tags.Count, "没选过的分组读回来是空的，不是 null");

                Assert.Equal(0, loaded.Ui.Filters.Count(entry => entry.Key == "lib-2"), "没存过的库不该凭空出现");
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

        Test("存储：结构对不上的设置文件也走「坏文件」那条路，不是拖着程序一起死", () =>
        {
            var root = TempRoot();
            try
            {
                var paths = new AppPaths(root);
                Directory.CreateDirectory(paths.Root);

                // 一台服务器的 Url 写成 null。**整节缺席和整份列表缺席由迁移自己补齐**（见上面那两条），而一个
                // 字段里的 null 刻意不补 —— 那要把每个设置类的每个字符串属性都列一遍，而那份名单必然跟不上以后
                // 新加的字段。所以这一档由这里兜住：Normalize 会在 server.Url.Trim() 上抛，而 Load 必须照旧交回
                // 一份能用的设置，把认不出来的那份改名留在磁盘上。
                File.WriteAllText(paths.SettingsFile, """{"SchemaVersion":10,"Servers":[{"Url":null}]}""");

                var loaded = new SettingsStore(paths, Protector).Load();

                Assert.Equal(1, loaded.Servers.Count, "回到默认设置，而不是抛出去让窗口开不出来");
                Assert.False(File.Exists(paths.SettingsFile), "认不出来的文件要改名保留");
                Assert.True(Directory.GetFiles(paths.Root, "*.corrupt-*").Length > 0, "改名后的那份要还在，方便找回服务器地址");
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
        Path.Combine(Path.GetTempPath(), $"embynian-test-{Guid.NewGuid():N}");

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

    // ── 恢复默认那几条用的反射小工具 ────────────────────────────────────────────────────────────
    //
    // 为什么这几条测试也用反射：这样一来，以后往设置类上加一项，测试自己就会把它算进来。手写一份「该回默认的
    // 属性名单」的话，漏掉新加那一项的实现和漏掉它的测试会一起漏 —— 那正是这一条要防的事。

    /// <summary>一个对象上每一个可读可写的公开实例属性，和 <see cref="SettingsReset"/> 扫的是同一批。</summary>
    private static IEnumerable<PropertyInfo> Fields(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property is { CanRead: true, CanWrite: true });

    /// <summary>把一个对象的每一项值写成一串，好拿来比「改过没有」。</summary>
    private static string Snapshot(object target) =>
        string.Join('\n', Fields(target.GetType())
            .Select(property => $"{property.Name}={Describe(property.GetValue(target))}"));

    /// <summary>逐项比对：<paramref name="actual"/> 上每一项都该等于一个全新对象上的那一项。</summary>
    private static void AssertDefaults(object actual, object fresh)
    {
        foreach (var property in Fields(actual.GetType()))
        {
            Assert.Equal(
                Describe(property.GetValue(fresh)),
                Describe(property.GetValue(actual)),
                $"{actual.GetType().Name}.{property.Name} 没有回到装机值");
        }
    }

    /// <summary>把每一项都改成和现在不一样的值，交回改动了几项。字典跳过（调用处自己按名字改）。</summary>
    private static int MutateAll(object target)
    {
        var changed = 0;
        foreach (var property in Fields(target.GetType()))
        {
            if (Mutate(target, property)) changed++;
        }

        return changed;
    }

    private static bool Mutate(object target, PropertyInfo property)
    {
        var current = property.GetValue(target);
        var type = property.PropertyType;

        if (type == typeof(bool))
        {
            property.SetValue(target, !(bool)current!);
            return true;
        }

        if (type == typeof(int))
        {
            property.SetValue(target, (int)current! + 1);
            return true;
        }

        if (type == typeof(string))
        {
            property.SetValue(target, (current as string ?? "") + "改过");
            return true;
        }

        if (type.IsEnum)
        {
            foreach (var value in Enum.GetValues(type))
            {
                if (Equals(value, current)) continue;

                property.SetValue(target, value);
                return true;
            }

            return false;
        }

        if (type == typeof(List<string>))
        {
            property.SetValue(target, new List<string> { "改过" });
            return true;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (IList)Activator.CreateInstance(type)!;
            list.Add(Activator.CreateInstance(type.GetGenericArguments()[0]));
            property.SetValue(target, list);
            return true;
        }

        return false;
    }

    /// <summary>一项值写成一句话。列表和字典按内容展开 —— 不然两个不同的列表比出来永远「相等」。</summary>
    private static string Describe(object? value) => value switch
    {
        null => "null",
        string text => text,
        IDictionary map => string.Join('|', map.Keys.Cast<object>()
            .Select(key => $"{key}={Describe(map[key])}")),
        IEnumerable items => string.Join('|', items.Cast<object?>().Select(Describe)),
        _ => value.ToString() ?? "?"
    };
}
