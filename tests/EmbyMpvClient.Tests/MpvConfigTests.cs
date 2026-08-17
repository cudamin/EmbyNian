using EmbyMpvClient.Configuration;
using EmbyMpvClient.Mpv;
using static EmbyMpvClient.Tests.TestHarness;

namespace EmbyMpvClient.Tests;

internal static class MpvConfigTests
{
    /// <summary>The user's real config, when present: the only fixture that proves nothing is lost.</summary>
    private static readonly string[] RealConfigs =
    [
        @"C:\mpv_config-2026.08.12\portable_config\mpv.conf",
        @"C:\mpv_config-2026.08.12\portable_config\input.conf",
        @"C:\mpv_config-2026.08.12\portable_config\embympvclient.conf"
    ];

    public static void Register()
    {
        Test("配置文档：逐字节往返（合成样本）", () =>
        {
            const string text = "# 头部注释\r\nvo=gpu-next  # 行尾注释\r\n\r\n[HQ]\r\n scale=ewa_lanczossharp\r\n#profile=SSIM\r\n";
            var document = MpvConfigDocument.Parse(text);
            Assert.Equal(text, document.Serialize(), "往返必须逐字符一致");
            Assert.Equal("\r\n", document.Newline);
            Assert.True(document.EndsWithNewline, "结尾换行需要保留");
        });

        Test("配置文档：无结尾换行也要保持原样", () =>
        {
            const string text = "vo=gpu-next\nhwdec=no";
            Assert.Equal(text, MpvConfigDocument.Parse(text).Serialize());
        });

        Test("配置文档：识别行类型与行尾注释", () =>
        {
            var document = MpvConfigDocument.Parse("""
                # 纯注释
                vo=gpu-next   # 视频输出
                #hwdec=auto-safe  # 被注释掉的选项
                fullscreen
                [HQ]
                 scale=ewa_lanczossharp
                """);

            Assert.Equal(MpvLineKind.Comment, document.Lines[0].Kind);
            Assert.Equal(MpvLineKind.Option, document.Lines[1].Kind);
            Assert.Equal("vo", document.Lines[1].Key);
            Assert.Equal("gpu-next", document.Lines[1].Value);
            Assert.Equal("# 视频输出", document.Lines[1].InlineComment);
            Assert.Equal(MpvLineKind.DisabledOption, document.Lines[2].Kind);
            Assert.Equal("hwdec", document.Lines[2].Key);
            Assert.Equal(MpvLineKind.Option, document.Lines[3].Kind);
            Assert.Null(document.Lines[3].Value, "裸开关没有值");
            Assert.Equal(MpvLineKind.Section, document.Lines[4].Kind);
            Assert.Equal("HQ", document.Lines[4].Section);
        });

        Test("配置文档：引号内的 # 属于值而不是注释", () =>
        {
            var document = MpvConfigDocument.Parse("""osd-msg1="#1 播放中"  # 真注释""");
            Assert.Equal("\"#1 播放中\"", document.Lines[0].Value);
            Assert.Equal("# 真注释", document.Lines[0].InlineComment);
        });

        Test("配置文档：最后一次赋值生效（与 mpv 一致）", () =>
        {
            var document = MpvConfigDocument.Parse("deband=yes\ndeband=no\n");
            Assert.Equal("no", document.GetValue("deband"));
        });

        Test("配置文档：改值时保留缩进与行尾注释", () =>
        {
            var document = MpvConfigDocument.Parse(" scale=ewa_lanczossharp   # 放大算法\n");
            document.SetValue("scale", "spline36");
            Assert.Contains("scale=spline36", document.Serialize());
            Assert.Contains("# 放大算法", document.Serialize());
            Assert.True(document.Serialize().StartsWith(' '), "原有缩进需要保留");
        });

        Test("配置文档：启用被注释的选项而不是追加重复项", () =>
        {
            var document = MpvConfigDocument.Parse("#hwdec=no\n");
            document.SetValue("hwdec", "auto-safe");
            Assert.Equal("hwdec=auto-safe\n", document.Serialize());
        });

        Test("配置文档：禁用选项保留原文", () =>
        {
            var document = MpvConfigDocument.Parse("deband=yes  # 去色带\n");
            document.Disable("deband");
            Assert.Equal("#deband=yes  # 去色带\n", document.Serialize());
        });

        Test("配置文档：新选项写入指定配置组末尾", () =>
        {
            var document = MpvConfigDocument.Parse("[A]\n x=1\n\n[B]\n y=2\n");
            document.SetValue("z", "3", "A");
            Assert.Equal("[A]\n x=1\n z=3\n\n[B]\n y=2\n", document.Serialize());
        });

        Test("配置文档：全局选项写在第一个配置组之前", () =>
        {
            var document = MpvConfigDocument.Parse("vo=gpu-next\n\n[A]\n x=1\n");
            document.SetValue("hwdec", "no");
            Assert.Contains("vo=gpu-next\nhwdec=no", document.Serialize());
        });

        Test("着色器配置组：解析名称、说明与着色器列表", () =>
        {
            var document = MpvConfigDocument.Parse("""
                [Ani4K]
                 profile-desc=Ani4K
                 profile-desc=适用于大多数动画
                 glsl-shaders="~~/shaders/Ani4K/Ani4Kv2_ArtCNN_C4F32_i2.glsl;~~/shaders/igv/SSimDownscaler.glsl"

                [HQ]
                 scale=ewa_lanczossharp
                """);

            var profiles = ShaderProfileCatalog.Scan(document);
            Assert.Equal(1, profiles.Count, "只有含 glsl-shaders 的配置组才算着色器配置组");
            Assert.Equal("Ani4K", profiles[0].Name);
            Assert.Equal("适用于大多数动画", profiles[0].Description, "mpv 以最后一条 profile-desc 为准");
            Assert.Equal(2, profiles[0].Shaders.Count);
            Assert.Contains("SSimDownscaler.glsl", string.Join(",", profiles[0].ShaderFileNames));
        });

        Test("内置着色器包：能被自己的解析器读回来", () =>
        {
            var document = MpvConfigDocument.Parse(ShaderPack.BuildContent());
            var profiles = ShaderProfileCatalog.Scan(document, "embympvclient.conf");

            Assert.Equal(ShaderPack.ProfileNames.Count, profiles.Count, "包里的每个配置组都要能被扫出来");
            foreach (var name in ShaderPack.ProfileNames)
            {
                var profile = profiles.FirstOrDefault(candidate => candidate.Name == name);
                Assert.NotNull(profile, $"应包含 {name}");
                Assert.True(profile!.Shaders.Count > 0, $"{name} 至少要指定一个着色器");
                Assert.True(!string.IsNullOrEmpty(profile.Description), $"{name} 应带 profile-desc 说明");
            }
        });

        Test("内置着色器包：配置组名与设置默认值对得上", () =>
        {
            // The settings defaults name profiles by string; a rename in one place and not the
            // other would silently mean 「不指定」 for every video.
            var settings = new ShaderAutomationSettings();
            var names = ShaderProfileCatalog
                .Scan(MpvConfigDocument.Parse(ShaderPack.BuildContent()), "embympvclient.conf")
                .Select(profile => profile.Name)
                .ToList();

            Assert.Contains(settings.DefaultProfile, string.Join(",", names));
            Assert.Contains(settings.AnimeProfile, string.Join(",", names));
            Assert.Contains(settings.HighResProfile, string.Join(",", names));
        });

        Test("内置着色器包：写到磁盘后能原样读回", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"embympvclient-test-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(root);
                var mpvConf = Path.Combine(root, "mpv.conf");
                File.WriteAllText(mpvConf, "vo=gpu-next\n");

                var written = ShaderPack.EnsureCreated(mpvConf);
                Assert.NotNull(written, "首次调用应生成文件");
                Assert.Equal(Path.Combine(root, ShaderPack.FileName), written!);

                var profiles = ShaderProfileCatalog.Scan([mpvConf, written!]);
                Assert.Equal(ShaderPack.ProfileNames.Count, profiles.Count);
                Assert.True(
                    profiles.All(profile => profile.SourceFile == written),
                    "配置组的来源文件要指向着色器包，设置页要靠它分组显示");

                // The file is the user's to edit once it exists; a later launch must not stomp it.
                File.AppendAllText(written!, "\n# 用户自己加的一行\n");
                Assert.Equal(written, ShaderPack.EnsureCreated(mpvConf), "已存在时应直接返回原路径");
                Assert.Contains("用户自己加的一行", File.ReadAllText(written!));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        });

        Test("编码检测：BOM、UTF-8、GBK", () =>
        {
            Assert.Equal("UTF-8 (BOM)", TextFileEncoding.Detect([0xEF, 0xBB, 0xBF, 0x61]).Description);
            Assert.Equal("UTF-8", TextFileEncoding.Detect([0xE4, 0xB8, 0xAD, 0xE6, 0x96, 0x87]).Description);
            // 0xD6 0xD0 0xCE 0xC4 = 中文 in GBK, invalid as UTF-8.
            Assert.Equal("GBK", TextFileEncoding.Detect([0xD6, 0xD0, 0xCE, 0xC4]).Description);
            Assert.Equal("UTF-8", TextFileEncoding.Detect([]).Description, "空文件按 UTF-8 处理");
        });

        Test("编码检测：GBK 与 UTF-8 往返都不损坏中文", () =>
        {
            const string text = "# 中文注释\nvo=gpu-next\n";
            foreach (var encoding in new[] { TextFileEncoding.Utf8NoBom, TextFileEncoding.Utf8Bom, TextFileEncoding.Gbk })
            {
                var bytes = encoding.GetBytes(text);
                Assert.Equal(text, TextFileEncoding.Detect(bytes).GetString(bytes), encoding.Description);
            }
        });

        RegisterRealConfigTests();
    }

    private static void RegisterRealConfigTests()
    {
        foreach (var path in RealConfigs)
        {
            var name = Path.GetFileName(path);
            if (!File.Exists(path))
            {
                Skip($"真实配置往返：{name}", "本机不存在该文件");
                continue;
            }

            Test($"真实配置往返：{name}", () =>
            {
                var original = File.ReadAllBytes(path);
                var document = MpvConfigDocument.Load(path);
                Assert.Equal(original.Length, document.SerializeToBytes().Length, "字节数必须一致");
                Assert.True(original.AsSpan().SequenceEqual(document.SerializeToBytes()), "未修改的文件必须逐字节一致");
            });
        }

        var mpvConf = RealConfigs[0];
        if (!File.Exists(mpvConf))
        {
            Skip("真实配置：着色器配置组扫描", "本机不存在 mpv.conf");
            return;
        }

        Test("真实配置：能从 mpv.conf 扫出着色器配置组", () =>
        {
            var profiles = ShaderProfileCatalog.Scan([mpvConf]);
            Assert.True(profiles.Count >= 8, $"应能扫出用户配置里的多个配置组，实际 {profiles.Count}");
            Assert.NotNull(profiles.FirstOrDefault(profile => profile.Name == "Anime4K"), "应包含 Anime4K");
            Assert.True(profiles.All(profile => profile.Shaders.Count > 0), "每个配置组都应含至少一个着色器");
        });
    }
}
