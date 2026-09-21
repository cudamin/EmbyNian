using System.Buffers.Binary;
using System.Text;
using EmbyNian.Configuration;
using EmbyNian.Infrastructure;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// The font catalogue: the <c>name</c>-table parser, the merge into families, and the search.
/// <para>
/// The fonts are built here byte by byte rather than copied from <c>C:\Windows\Fonts</c>: a test that
/// reads a real font can only assert what that machine happens to have, and cannot make a font say the
/// awkward things — a name in one script only, a length that runs off the end of the table, four fonts
/// in one file. One test at the end does read the real directory, and that one is about the machine
/// rather than about the parser.
/// </para>
/// </summary>
internal static class FontTests
{
    private const int Windows = 3;
    private const int Macintosh = 1;
    private const ushort EnUs = 0x0409;

    /// <summary>One record in a name table: who wrote it, in what language, and what it says.</summary>
    private sealed record Name(int NameId, int Platform, int Language, string Text);

    public static void Register()
    {
        RegisterParser();
        RegisterCatalogue();
        RegisterSearch();
        RegisterWeight();
        RegisterMachine();
    }

    private static void RegisterWeight()
    {
        // ResolveWeighted 是「字重」那三档唯一的落点：mpv 没有字重选项，所以字重变成「发哪个族名 +
        // 要不要 sub-bold」。这条钉住三档各自发什么，尤其是 细 靠给族名接一个「 Light」——雅黑上恰好命中
        // Microsoft YaHei Light，别的字体上 libass 找不到就退回本体（no-op，不是豆腐块）。
        Test("字重：常规发本体、不加粗", () =>
        {
            var (font, bold) = FontFamilies.ResolveWeighted("Microsoft YaHei", PlaybackSettings.RegularSubtitleWeight);
            Assert.Equal("Microsoft YaHei", font);
            Assert.False(bold);
        });

        Test("字重：粗发本体 + sub-bold", () =>
        {
            var (font, bold) = FontFamilies.ResolveWeighted("Microsoft YaHei", PlaybackSettings.BoldSubtitleWeight);
            Assert.Equal("Microsoft YaHei", font, "粗不是换族名，是让本体开 sub-bold（有真粗体就用真的）");
            Assert.True(bold);
        });

        Test("字重：细给族名接一个「 Light」", () =>
        {
            var (font, bold) = FontFamilies.ResolveWeighted("Microsoft YaHei", PlaybackSettings.LightSubtitleWeight);
            Assert.Equal("Microsoft YaHei Light", font, "雅黑上恰好命中系统自带的 Light 那一支");
            Assert.False(bold);
        });

        Test("字重：细不会把「 Light」叠两遍", () =>
        {
            var (font, _) = FontFamilies.ResolveWeighted("Microsoft YaHei Light", PlaybackSettings.LightSubtitleWeight);
            Assert.Equal("Microsoft YaHei Light", font, "本来就带 Light 的族名，细这一档不再接一个 Light");
        });

        Test("字重：空族名先兜底成默认，再按字重发", () =>
        {
            var (light, _) = FontFamilies.ResolveWeighted("", PlaybackSettings.LightSubtitleWeight);
            Assert.Equal(FontFamilies.Default + " Light", light, "空值走兜底族名（雅黑），细再接 Light");

            var (bold, isBold) = FontFamilies.ResolveWeighted(null, PlaybackSettings.BoldSubtitleWeight);
            Assert.Equal(FontFamilies.Default, bold);
            Assert.True(isBold);
        });

        Test("字重：越界的字重先吸附到最近一档", () =>
        {
            var (font, bold) = FontFamilies.ResolveWeighted("SimHei", 999);
            Assert.Equal("SimHei", font, "999 吸附到粗：发本体 + sub-bold");
            Assert.True(bold);
        });
    }

    private static void RegisterParser()
    {
        Test("字体名：英文族名优先于本地化族名", () =>
        {
            var font = OneFont(
                new Name(1, Windows, 0x0804, "微软雅黑"),
                new Name(1, Windows, EnUs, "Microsoft YaHei"));

            var names = Read(font);
            Assert.Equal(1, names.Count);
            Assert.Equal("Microsoft YaHei", names[0].Family);
            Assert.True(names[0].AlsoCalled.Contains("微软雅黑"), "本地化名要留着好让搜索能用中文找");
        });

        Test("字体名：没有英文名时用它自己说的第一个", () =>
        {
            var names = Read(OneFont(new Name(1, Windows, 0x0804, "方正兰亭黑")));
            Assert.Equal(1, names.Count);
            Assert.Equal("方正兰亭黑", names[0].Family);
        });

        Test("字体名：nameID 1 是族名，16 只当别名", () =>
        {
            // A family whose typographic name is the one without the weight. Preferring 16 would merge
            // this into 「Yu Gothic」 and the picker would lose the Light face entirely.
            var names = Read(OneFont(
                new Name(16, Windows, EnUs, "Yu Gothic"),
                new Name(1, Windows, EnUs, "Yu Gothic Light")));

            Assert.Equal("Yu Gothic Light", names[0].Family);
            Assert.True(names[0].AlsoCalled.Contains("Yu Gothic"), "排版族名仍要能搜到");
        });

        Test("字体名：MacRoman 记录只在纯 ASCII 时采用", () =>
        {
            var ascii = Read(OneFont(new Name(1, Macintosh, 0, "Consolas")));
            Assert.Equal("Consolas", ascii[0].Family);

            // A Macintosh record holding high bytes is a legacy encoding we do not carry a table for,
            // and reading it as ASCII would put mojibake in the list.
            var mangled = Read(OneFont(new Name(1, Macintosh, 0, "Ma\u00A5s")));
            Assert.Equal(0, mangled.Count, "认不出编码就当这个字体没说过名字");
        });

        Test("字体名：不是字体的文件、截断的文件都读不出东西", () =>
        {
            Assert.Equal(0, Read([]).Count);
            Assert.Equal(0, Read(Encoding.ASCII.GetBytes("这不是字体")).Count);

            var font = OneFont(new Name(1, Windows, EnUs, "Arial"));
            Assert.Equal(0, Read(font[..(font.Length / 2)]).Count, "半个文件不能读出半个名字");

            for (var cut = 1; cut < font.Length; cut += 3)
            {
                // Every truncation, not just the tidy half: the parser must return rather than throw
                // wherever the file happens to stop.
                Assert.Equal(0, Read(font[..cut]).Count);
            }
        });

        Test("字体名：记录长度越界时跳过这一条", () =>
        {
            var font = OneFont(new Name(1, Windows, EnUs, "Arial"));

            // The first record's length field, made longer than the whole table. A file like this is
            // either damaged or lying; either way it must not read past what it handed us.
            var table = font.Length - 1;
            var lengthField = FindNameTable(font) + 6 + 8;
            BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(lengthField), (ushort)table);

            Assert.Equal(0, Read(font).Count);
        });

        Test("字体名：一个 ttc 里的每个字体都读出来", () =>
        {
            var collection = Collection(
                [new Name(1, Windows, EnUs, "Microsoft YaHei")],
                [new Name(1, Windows, EnUs, "Microsoft YaHei Light")],
                [new Name(1, Windows, EnUs, "Microsoft YaHei UI")]);

            var names = Read(collection);
            Assert.Equal(3, names.Count);
            Assert.Equal("Microsoft YaHei Light", names[1].Family);
        });
    }

    private static void RegisterCatalogue()
    {
        Test("字体表：一个族的多个文件并成一条", () =>
        {
            using var folder = new TempFolder();
            folder.Write("arial.ttf", OneFont(new Name(1, Windows, EnUs, "Arial")));
            folder.Write("arialbd.ttf", OneFont(new Name(1, Windows, EnUs, "Arial")));
            folder.Write("msyh.ttc", Collection([
                new Name(1, Windows, EnUs, "Microsoft YaHei"),
                new Name(1, Windows, 0x0804, "微软雅黑")]));

            var catalogue = FontCatalogue.Scan([folder.Path]);

            Assert.Equal(2, catalogue.Families.Count, "常规和粗体是同一个族");
            Assert.Equal(3, catalogue.FileCount);
            Assert.Equal("Arial", catalogue.Families[0].Name, "按名字排序，不按文件顺序");
            Assert.Equal("微软雅黑", catalogue.Families[1].Localized);
        });

        Test("字体表：不是字体的文件不算，缺失的目录不算", () =>
        {
            using var folder = new TempFolder();
            folder.Write("说明.txt", Encoding.UTF8.GetBytes("放在字体目录里的说明"));
            folder.Write("空的.ttf", []);
            folder.Write("real.otf", OneFont(new Name(1, Windows, EnUs, "Cascadia Code")));

            var catalogue = FontCatalogue.Scan([folder.Path, Path.Combine(folder.Path, "没有这个目录")]);

            Assert.Equal(1, catalogue.Families.Count);
            Assert.Equal(1, catalogue.FileCount, "读不出名字的文件不算读到了");
        });

        Test("字体表：设置文件里的字体没装也要在列表里", () =>
        {
            using var folder = new TempFolder();
            folder.Write("arial.ttf", OneFont(new Name(1, Windows, EnUs, "Arial")));

            var catalogue = FontCatalogue.Scan([folder.Path]);
            var kept = catalogue.Including("Some Font I Uninstalled");

            Assert.Equal(2, kept.Families.Count);
            Assert.Equal("Arial", kept.Families[0].Name, "补进去的那个也照名字排序");

            // Already there, in another casing, or nothing at all: all three leave the list alone.
            Assert.Equal(2, kept.Including("some font i uninstalled").Families.Count);
            Assert.Equal(1, catalogue.Including("arial").Families.Count);
            Assert.Equal(1, catalogue.Including("   ").Families.Count);
        });
    }

    private static void RegisterSearch()
    {
        Test("字体搜索：分词、忽略大小写、能用本地化名找", () =>
        {
            var catalogue = Catalogue(
                ["Microsoft YaHei", "微软雅黑"],
                ["Microsoft YaHei Light", "微软雅黑 Light"],
                ["SimSun", "宋体"],
                ["Consolas"]);

            Assert.Equal(4, catalogue.Search("").Count, "没输字就是全部");
            Assert.Equal(4, catalogue.Search(null).Count);
            Assert.Equal(2, catalogue.Search("yahei").Count, "大小写不算");
            Assert.Equal(2, catalogue.Search("雅黑").Count, "中文名也能找");
            Assert.Equal(1, catalogue.Search("yahei light").Count, "两个词都得中");
            Assert.Equal(1, catalogue.Search("light yahei").Count, "词序不算");
            Assert.Equal(0, catalogue.Search("宋体 yahei").Count);
            Assert.Equal(1, catalogue.Search("宋").Count);
            Assert.Equal(1, catalogue.Search("  consolas  ").Count, "两头的空白不算一个词");
        });
    }

    private static void RegisterMachine()
    {
        // The one test about this machine rather than about the code: 「列出 C:\Windows\Fonts 里所有的
        // 字体」 is the request, and the only way to know a real font directory reads is to read one.
        var fonts = FontCatalogue.Directories.FirstOrDefault(directory => Directory.Exists(directory));
        if (fonts is null)
        {
            Skip("字体表：读这台机器装的字体", "没有找到 Windows 字体目录");
            return;
        }

        Test("字体表：读这台机器装的字体", () =>
        {
            var catalogue = FontCatalogue.Scan([fonts]);

            Assert.True(catalogue.FileCount > 20, $"{fonts} 里只读出 {catalogue.FileCount} 个字体文件");
            Assert.True(catalogue.Families.Count > 20, $"只认出 {catalogue.Families.Count} 个字体族");
            Assert.True(
                catalogue.Families.Any(entry => string.Equals(entry.Name, "Arial", StringComparison.Ordinal)),
                "每台 Windows 都有 Arial");
        });

        // The one subtitle font still bundled (assets/fonts, handed to mpv as sub-fonts-dir) is
        // 方正中等线简体 — it held the 装机默认 in v12 and stays selectable. **The default is no longer
        // bundled**: v18 (「不要放字体进去，默认就用雅黑」, 2026-09-21) put Microsoft YaHei — a system
        // font — back as the default, and dropped the thin Noto VF v17 had shipped. So the thing to assert
        // is just that the shipped 方正 file parses to a family the catalogue can name under both its
        // spellings; the default's rendering rides on the system font, not on this folder.
        var fontsDirectory = FindRepositoryRoot() is { } repo
            ? Path.Combine(repo, "assets", "fonts")
            : null;
        if (fontsDirectory is null || !Directory.Exists(fontsDirectory))
        {
            Skip("字体表：程序自带的字幕字体解析出的族名可以被选中", "找不到仓库里的 assets/fonts");
            return;
        }

        Test("字体表：程序自带的字幕字体解析出的族名可以被选中", () =>
        {
            var catalogue = FontCatalogue.Scan([fontsDirectory]);

            Assert.True(catalogue.FileCount >= 1,
                $"自带目录里至少应有方正一个文件，只读出 {catalogue.FileCount} 个");

            foreach (var entry in catalogue.Families)
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.Name), "自带字体必须报出一个族名");
            }

            // 方正中等线简体：中文名与英文名是同一款文件的两个名字，存哪一边都要挑得到。
            Assert.True(catalogue.Families.Any(entry => entry.AnswersTo("方正中等线简体")),
                "自带目录里的方正没被认出来，存中文名的那份设置就挑不回这一行");
        });
    }

    private static string? FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "EmbyNian.sln"))) return directory.FullName;
        }

        return null;
    }

    private static IReadOnlyList<SfntFontNames> Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return SfntNames.Read(stream);
    }

    private static FontCatalogue Catalogue(params string[][] families)
    {
        using var folder = new TempFolder();
        for (var index = 0; index < families.Length; index++)
        {
            var names = families[index]
                .Select((text, order) => new Name(1, Windows, order == 0 ? EnUs : 0x0804, text))
                .ToArray();

            folder.Write($"font{index}.ttf", OneFont(names));
        }

        return FontCatalogue.Scan([folder.Path]);
    }

    /// <summary>A font file holding nothing but a <c>name</c> table — every other table is unread.</summary>
    private static byte[] OneFont(params Name[] names)
    {
        var table = NameTable(names);
        var bytes = new byte[12 + 16 + table.Length];

        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 1);

        WriteTableRecord(bytes.AsSpan(12), 28, table.Length);
        table.CopyTo(bytes.AsSpan(28));
        return bytes;
    }

    /// <summary>A <c>ttcf</c> collection: a header of offsets, then one whole font per entry.</summary>
    private static byte[] Collection(params Name[][] fonts)
    {
        var bodies = fonts.Select(NameTable).ToList();
        var header = 12 + (4 * fonts.Length);
        var bytes = new byte[header + bodies.Sum(body => 12 + 16 + body.Length)];

        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), 0x74746366);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), (uint)fonts.Length);

        var at = header;
        for (var index = 0; index < bodies.Count; index++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12 + (4 * index)), (uint)at);

            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), 0x00010000);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(at + 4), 1);
            WriteTableRecord(bytes.AsSpan(at + 12), at + 28, bodies[index].Length);
            bodies[index].CopyTo(bytes.AsSpan(at + 28));

            at += 12 + 16 + bodies[index].Length;
        }

        return bytes;
    }

    private static void WriteTableRecord(Span<byte> record, int offset, int length)
    {
        BinaryPrimitives.WriteUInt32BigEndian(record, 0x6E616D65);
        BinaryPrimitives.WriteUInt32BigEndian(record[4..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)offset);
        BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)length);
    }

    private static byte[] NameTable(Name[] names)
    {
        var texts = names.Select(name => Encode(name)).ToList();
        var heap = 6 + (12 * names.Length);
        var bytes = new byte[heap + texts.Sum(text => text.Length)];

        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0), 0);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), (ushort)names.Length);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), (ushort)heap);

        var at = 0;
        for (var index = 0; index < names.Length; index++)
        {
            var record = bytes.AsSpan(6 + (12 * index));
            BinaryPrimitives.WriteUInt16BigEndian(record, (ushort)names[index].Platform);
            BinaryPrimitives.WriteUInt16BigEndian(record[2..], (ushort)(names[index].Platform == Windows ? 1 : 0));
            BinaryPrimitives.WriteUInt16BigEndian(record[4..], (ushort)names[index].Language);
            BinaryPrimitives.WriteUInt16BigEndian(record[6..], (ushort)names[index].NameId);
            BinaryPrimitives.WriteUInt16BigEndian(record[8..], (ushort)texts[index].Length);
            BinaryPrimitives.WriteUInt16BigEndian(record[10..], (ushort)at);

            texts[index].CopyTo(bytes.AsSpan(heap + at));
            at += texts[index].Length;
        }

        return bytes;
    }

    private static byte[] Encode(Name name) => name.Platform == Macintosh
        ? Encoding.Latin1.GetBytes(name.Text)
        : Encoding.BigEndianUnicode.GetBytes(name.Text);

    /// <summary>Where the name table starts in a file built by <see cref="OneFont"/>.</summary>
    private static int FindNameTable(byte[] font) => (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(12 + 8));

    /// <summary>A directory of its own per test, deleted with what is in it.</summary>
    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EmbyNianFonts-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Write(string name, byte[] bytes) => File.WriteAllBytes(System.IO.Path.Combine(Path, name), bytes);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
