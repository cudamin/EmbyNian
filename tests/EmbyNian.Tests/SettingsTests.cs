using System.Text.Json;
using EmbyNian.Configuration;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
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
            original.Playback.AudioTrack = AudioTrackMode.Language;
            original.Playback.AudioLanguage = "日语";
            original.Playback.SubtitleLanguages = ["繁体中文", "中文"];
            original.Mpv.ExecutablePath = @"D:\mpv\mpv.exe";

            var json = JsonSerializer.Serialize(original, SettingsSerializer.WriteOptions);
            var loaded = SettingsMigration.FromJson(json, Protector);

            Assert.Equal("果服", loaded.Servers[0].Name);
            Assert.False(loaded.Shaders.Enabled, "开关状态必须往返一致");
            Assert.Equal(EmbyNian.Mpv.GpuTier.High, loaded.Shaders.Gpu);
            Assert.Equal("anime-sweet", loaded.Shaders.ManualGroup);
            Assert.False(loaded.Shaders.RestoreVintageSources, "老片源修复关掉了就得记住，不然每次开机又打开");
            Assert.Equal("日语", loaded.Playback.AudioLanguage);
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

        Test("迁移：v2 的字幕/音轨优先级字符串升级成多选与单选", () =>
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
            Assert.Equal(AudioTrackMode.Language, settings.Playback.AudioTrack);
            Assert.Equal("日语", settings.Playback.AudioLanguage, "音轨只留第一种语言，它已经没有优先级了");
            Assert.Equal(SubtitleMode.Always, settings.Playback.SubtitleMode);
            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
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

            Assert.Equal("gb18030", settings.Playback.SubtitleCodepage);
            Assert.Equal("#FFFFFF", settings.Playback.SubtitleColor);
            Assert.Equal("0.5", settings.Playback.SubtitleBorderSize);
            Assert.Equal("#000000", settings.Playback.SubtitleBorderColor);
            Assert.Equal("0.5", settings.Playback.SubtitleShadowOffset);
            Assert.Equal(50, settings.Playback.SubtitleFontSize);
            Assert.True(settings.Playback.SubtitleBold, "旧配置里的字幕是粗体，升级后不能忽然变细");
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
            settings.Ui.PosterWidth = 5;
            settings.Playback.MarkWatchedPercent = 5;
            settings.Playback.ProgressReportIntervalSeconds = 0;
            settings.Ui.ImageCacheMegabytes = 999999;

            SettingsMigration.Normalize(settings);

            Assert.Equal(500, settings.Ui.PageSize);
            Assert.Equal(120, settings.Ui.PosterWidth);
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

            // 认得出来的就别动 —— 这条才是这段代码存在的风险所在。
            settings.Ui.Theme = "daylight";
            SettingsMigration.Normalize(settings);
            Assert.Equal("daylight", settings.Ui.Theme);
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
            var loaded = SettingsMigration.FromJson("""{ "SchemaVersion": 6, "Shaders": { "ApplyToAllVideos": true } }""", Protector);
            Assert.True(loaded.Shaders.Enabled);
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

        Test("规整：字幕外观的数值范围", () =>
        {
            var settings = SettingsMigration.NewDefaults();
            settings.Playback.SubtitleBackOpacity = 400;
            settings.Playback.SubtitleFontSize = 5;

            SettingsMigration.Normalize(settings);

            Assert.Equal(100, settings.Playback.SubtitleBackOpacity);
            Assert.Equal(16, settings.Playback.SubtitleFontSize);

            settings.Playback.SubtitleFontSize = 0;
            SettingsMigration.Normalize(settings);
            Assert.Equal(0, settings.Playback.SubtitleFontSize, "0 是「用 mpv 自己的默认字号」，不能被夹成 16");
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

        Test("规整：音量默认 100，越界夹回 0–100", () =>
        {
            Assert.Equal(100, SettingsMigration.NewDefaults().Audio.Volume, "没人调过音量就是满的");

            var loud = SettingsMigration.NewDefaults();
            loud.Audio.Volume = 400;
            SettingsMigration.Normalize(loud);
            Assert.Equal(100, loud.Audio.Volume);

            var negative = SettingsMigration.NewDefaults();
            negative.Audio.Volume = -20;
            SettingsMigration.Normalize(negative);
            Assert.Equal(0, negative.Audio.Volume);
        });

        Test("规整：档位表按放大倍数挑，装机默认不会把任何一档挑成摆设", () =>
        {
            // 从前这一条钉的是「低清阈值必须低于高清阈值」：两个框的范围在 720–1080 上重叠、Resolve 先判高清，
            // 于是低清阈值一顶到高清阈值上，低清配置组就成了摆设 —— 设了、看着像设了、永远不生效。两个阈值
            // 2026-09-03 连同那四个组名一起删掉了，接替它们的是放大倍数，而这一条守的还是同一件事：八个档位
            // 每一个都真的挑得到，没有一个是永远轮不到的摆设。
            var shaders = SettingsMigration.Normalize(SettingsMigration.NewDefaults()).Shaders;

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

            Assert.Equal(0, TrackLanguagePriority.ParseList("   ").Count);
            Assert.Equal(0, TrackLanguagePriority.ParseList(null).Count);

            // 设置文件里存的是已经拆好的列表，规整时走的是同一套清理。
            var settings = SettingsMigration.NewDefaults();
            settings.Playback.SubtitleLanguages = ["  简体中文  ", "chs", "", "eng"];
            SettingsMigration.Normalize(settings);
            Assert.Equal("简体中文, 英语", string.Join(", ", settings.Playback.SubtitleLanguages));

            static string Joined(string? typed) => string.Join(", ", TrackLanguagePriority.ParseList(typed));
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
}
