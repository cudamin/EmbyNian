using System.Text.Json;
using EmbyNian.Configuration;
using EmbyNian.Mpv;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

internal static class MpvConfigTests
{
    public static void Register()
    {
        Test("mpv 配置：CRLF 与结尾换行逐字节往返", () =>
        {
            const string text = "# 头部注释\r\nvo=gpu-next  # 行尾注释\r\n\r\n[HQ]\r\n scale=ewa_lanczossharp\r\n";
            var document = MpvConfigDocument.Parse(text);

            Assert.Equal(text, document.Serialize());
            Assert.Equal("\r\n", document.Newline);
            Assert.True(document.EndsWithNewline);
        });

        Test("mpv 配置：行尾注释不吃掉引号内的井号", () =>
        {
            var document = MpvConfigDocument.Parse("osd-msg1=\"#1 播放中\"  # 真注释");

            Assert.Equal("\"#1 播放中\"", document.Lines[0].Value);
            Assert.Equal("# 真注释", document.Lines[0].InlineComment);
        });

        Test("mpv 配置：启用注释项并保留缩进与说明", () =>
        {
            var document = MpvConfigDocument.Parse("  #scale=ewa_lanczossharp  # 放大算法\n");
            document.SetValue("scale", "spline36");

            Assert.Equal("  scale=spline36  # 放大算法\n", document.Serialize());
        });

        Test("mpv 配置：新选项插入指定段落末尾", () =>
        {
            var document = MpvConfigDocument.Parse("[A]\n x=1\n\n[B]\n y=2\n");
            document.SetValue("z", "3", "A");

            Assert.Equal("[A]\n x=1\n z=3\n\n[B]\n y=2\n", document.Serialize());
        });

        Test("input.conf：绑定、禁用绑定与普通说明分开解析", () =>
        {
            var document = MpvConfigDocument.Parse(
                "CTRL+s show-text \"a b\"\n#F1 script-message menu\n普通 中文说明\n",
                MpvConfigKind.Input);

            Assert.Equal(MpvLineKind.Option, document.Lines[0].Kind);
            Assert.Equal("CTRL+s", document.Lines[0].Key);
            Assert.Equal("show-text \"a b\"", document.Lines[0].Value);
            Assert.Equal(MpvLineKind.DisabledOption, document.Lines[1].Kind);
            Assert.Equal("F1", document.Lines[1].Key);
            Assert.Equal(MpvLineKind.Comment, document.Lines[2].Kind, "普通中文说明不能被当成按键绑定");
        });

        Test("input.conf：修改绑定时保留行尾说明", () =>
        {
            var document = MpvConfigDocument.Parse("CTRL+s show-text old  # 提示\n", MpvConfigKind.Input);
            document.SetValue("CTRL+s", "show-text new");

            Assert.Equal("CTRL+s show-text new  # 提示\n", document.Serialize());
        });

        Test("配置编码：UTF-8 BOM、UTF-16 与 GBK 中文往返", () =>
        {
            const string text = "# 中文注释\r\nvo=gpu-next\r\n";
            var encodings = new[]
            {
                TextFileEncoding.Utf8NoBom,
                TextFileEncoding.Utf8Bom,
                new TextFileEncoding(new System.Text.UnicodeEncoding(false, true), true, "UTF-16 LE"),
                new TextFileEncoding(new System.Text.UnicodeEncoding(true, true), true, "UTF-16 BE"),
                TextFileEncoding.Gbk
            };

            foreach (var encoding in encodings)
            {
                var bytes = encoding.GetBytes(text);
                var detected = TextFileEncoding.Detect(bytes);
                Assert.Equal(text, detected.GetString(bytes), encoding.Description);
                Assert.True(bytes.AsSpan().SequenceEqual(MpvConfigDocument.Parse(text, detected).SerializeToBytes()), encoding.Description);
            }
        });

        Test("配置文件：保存前备份、原子写入并可恢复", () =>
        {
            var root = NewTempDirectory();
            try
            {
                var path = Path.Combine(root, "mpv.conf");
                File.WriteAllText(path, "vo=gpu-next\n");
                var file = new MpvConfigFile(path, backupsToKeep: 2);

                var changed = MpvConfigDocument.Parse("vo=libmpv\n");
                var backup = file.Save(changed);
                Assert.NotNull(backup);
                Assert.Equal("vo=gpu-next\n", File.ReadAllText(backup!));
                Assert.Equal("vo=libmpv\n", File.ReadAllText(path));
                Assert.False(File.Exists(path + ".tmp"), "原子写入的临时文件必须清理");

                file.RestoreFrom(backup!);
                Assert.Equal("vo=gpu-next\n", File.ReadAllText(path));
                Assert.True(file.ListBackups().Count <= 2, "备份数量需要按上限清理");

                var customInput = Path.Combine(root, "my-bindings.txt");
                var inputFile = new MpvConfigFile(customInput, MpvConfigKind.Input);
                inputFile.Save(MpvConfigDocument.Parse("CTRL+s show-text ok\n", MpvConfigKind.Input));
                Assert.Equal(MpvConfigKind.Input, inputFile.Kind);
                Assert.Equal("CTRL+s", inputFile.Load().Lines[0].Key);
            }
            finally
            {
                DeleteTempDirectory(root);
            }
        });

        Test("配置路径：用户覆盖值持久化，默认值不污染旧迁移结果", () =>
        {
            var defaults = SettingsMigration.NewDefaults();
            var defaultJson = JsonSerializer.Serialize(defaults, SettingsSerializer.WriteOptions);
            Assert.DoesNotContain("ConfigPath", defaultJson);
            Assert.DoesNotContain("InputConfigPath", defaultJson);

            defaults.Mpv.ConfigPath = @"D:\mpv\portable_config\mpv.conf";
            defaults.Mpv.InputConfigPath = @"D:\mpv\portable_config\input.conf";
            var json = JsonSerializer.Serialize(defaults, SettingsSerializer.WriteOptions);
            var loaded = SettingsMigration.FromJson(json, PassthroughSecretProtector.Instance);

            Assert.Equal(defaults.Mpv.ConfigPath, loaded.Mpv.ConfigPath);
            Assert.Equal(defaults.Mpv.InputConfigPath, loaded.Mpv.InputConfigPath);
        });
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"embynian-mpv-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
