using EmbyNian.Configuration;
using EmbyNian.Mpv;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// The editing side of the mpv config files: where they are, what a typed-in path means, and what happens to
/// text that has been through a <c>TextBox</c>.
/// <para>
/// These exist because that last part produced a cluster of wrong answers that no test could see while the
/// logic lived in the settings page's code-behind. WinUI's <c>TextBox.Text</c> hands back a bare CR for every
/// line break regardless of what was assigned to it, so a CRLF file came back from the editor different from
/// how it went in — which made a freshly-loaded file report unsaved changes, collapsed its line count to 1,
/// and made saving rewrite every line ending in the file. Moving the logic into Core is what makes it
/// checkable, so the checks are here.
/// </para>
/// </summary>
internal static class MpvWorkspaceTests
{
    /// <summary>What WinUI hands back from a <c>TextBox</c>: every line break is a bare CR.</summary>
    private static string AsTextBoxWouldReturn(string text) => MpvConfigText.ToNewline(text, "\r");

    public static void Register()
    {
        Test("换行归一：编辑框回传的裸 CR 能还原为文件自己的换行", () =>
        {
            const string crlf = "# 头部\r\nvo=gpu-next\r\n\r\n[HQ]\r\n scale=spline36\r\n";
            var fromEditor = AsTextBoxWouldReturn(crlf);

            Assert.DoesNotContain("\n", fromEditor, "先确认编辑框确实把 CRLF 变成了裸 CR");
            Assert.Equal(crlf, MpvConfigText.ToNewline(fromEditor, "\r\n"));
            Assert.Equal(crlf.Replace("\r\n", "\n"), MpvConfigText.ToNewline(fromEditor, "\n"));
        });

        Test("换行归一：CR、LF、CRLF 混排统一成一种", () =>
        {
            Assert.Equal("a\nb\nc\nd\n", MpvConfigText.ToNewline("a\r\nb\rc\nd\r\n", "\n"));
            Assert.Equal("a\r\nb\r\nc\r\n", MpvConfigText.ToNewline("a\nb\r\nc\r", "\r\n"));
            Assert.Equal("", MpvConfigText.ToNewline("", "\r\n"));
            Assert.Equal("没有换行", MpvConfigText.ToNewline("没有换行", "\r\n"));
        });

        Test("换行归一：换页符和 Unicode 行分隔符不算换行", () =>
        {
            // 这是不用 string.ReplaceLineEndings 的原因：它会把 FF、NEL、LS、PS 也当成换行，
            // 于是配置文件里出现这些字符时，走一趟编辑框就会多出原本没有的断行。
            const string exotic = "a\fb\u0085c\u2028d\u2029e";
            Assert.Equal(exotic, MpvConfigText.ToNewline(exotic, "\r\n"));
            Assert.Equal(1, MpvConfigText.CountLines(exotic));
        });

        Test("行数统计：与解析器对同一段文本的看法一致", () =>
        {
            // 断言的是「两者相等」而不是写死的数字：状态栏上的行数和文档的行数是同一个东西，
            // 二者只要开始各说各话，编辑一下文本行数就会莫名跳一格。
            string[] samples =
            [
                "", "a", "a\n", "a\nb", "a\nb\n", "\n", "\n\n",
                "a\r\n", "a\r\nb", "a\r\nb\r\n", "\r\n",
                "# 注释\r\nvo=gpu-next\r\n\r\n[HQ]\r\n scale=spline36\r\n"
            ];

            foreach (var sample in samples)
            {
                var expected = MpvConfigDocument.Parse(sample).Lines.Count;
                Assert.Equal(expected, MpvConfigText.CountLines(sample), $"文本 <{sample.Replace("\r", "\\r").Replace("\n", "\\n")}>");
            }
        });

        Test("配置位置：留空与填入推断值都表示「跟着 mpv.exe 走」", () =>
        {
            var settings = new MpvSettings { ExecutablePath = @"D:\mpv\mpv.exe" };
            var location = new MpvConfigLocation(settings);

            var inferred = location.Infer(MpvConfigKind.Mpv);
            Assert.Equal(Path.Combine(@"D:\mpv", "portable_config", "mpv.conf"), inferred);
            Assert.Equal(Path.Combine(@"D:\mpv", "portable_config", "input.conf"), location.Infer(MpvConfigKind.Input));
            Assert.Equal(inferred, location.Resolve(MpvConfigKind.Mpv), "未配置时用推断值");

            // 填入的正好是推断值：存下来就把今天的推断结果冻住了，所以存 null。
            Assert.Equal(inferred, location.Store(MpvConfigKind.Mpv, inferred));
            Assert.Null(settings.ConfigPath);

            Assert.Equal(inferred, location.Store(MpvConfigKind.Mpv, "   "));
            Assert.Null(settings.ConfigPath, "留空也是「跟着走」");

            // 真正换了个位置才存，且回显的是去掉空白后的值。
            Assert.Equal(@"E:\conf\mpv.conf", location.Store(MpvConfigKind.Mpv, "  E:\\conf\\mpv.conf  "));
            Assert.Equal(@"E:\conf\mpv.conf", settings.ConfigPath);
            Assert.Equal(@"E:\conf\mpv.conf", location.Resolve(MpvConfigKind.Mpv));
        });

        Test("配置位置：改了 mpv.exe 之后推断路径跟着搬家", () =>
        {
            var settings = new MpvSettings { ExecutablePath = @"D:\mpv\mpv.exe" };
            var location = new MpvConfigLocation(settings);

            location.Store(MpvConfigKind.Mpv, location.Infer(MpvConfigKind.Mpv));
            settings.ExecutablePath = @"F:\另一个 mpv\mpv.exe";

            Assert.Equal(Path.Combine(@"F:\另一个 mpv", "portable_config", "mpv.conf"), location.Resolve(MpvConfigKind.Mpv));
        });

        Test("配置位置：mpv.exe 路径为空或非法时推断不抛异常", () =>
        {
            foreach (var executable in new[] { "", "   ", "<>|?*", "\"带引号\"" })
            {
                var location = new MpvConfigLocation(new MpvSettings { ExecutablePath = executable });
                var path = location.Infer(MpvConfigKind.Mpv);
                Assert.True(path.Length > 0, $"路径 <{executable}> 不能推断出空字符串");
                Assert.Contains("mpv.conf", path);
            }
        });

        Test("配置草稿：CRLF 文件刚打开时不算有未保存修改", () =>
        {
            var root = NewTempDirectory();
            try
            {
                var path = Path.Combine(root, "mpv.conf");
                const string text = "# 头部\r\nvo=gpu-next\r\n\r\n[HQ]\r\n scale=spline36\r\n";
                File.WriteAllText(path, text);

                var draft = MpvConfigDraft.Open(path, MpvConfigKind.Mpv);
                Assert.Equal(MpvConfigDraftState.Clean, draft.State);
                Assert.False(draft.IsDirty);
                Assert.Equal(5, draft.LineCount);
                Assert.Contains("行", draft.Status);

                // 页面把草稿文本塞进 TextBox，再原样读回来 —— 这一趟不能凭空变成「有修改」。
                draft.Edit(AsTextBoxWouldReturn(draft.DraftText));
                Assert.False(draft.IsDirty, "编辑框回传的裸 CR 不是一次真实修改");
                Assert.Equal(MpvConfigDraftState.Clean, draft.State);
                Assert.Equal(5, draft.LineCount, "行数不能因为走了一趟编辑框就塌成 1");
            }
            finally
            {
                DeleteTempDirectory(root);
            }
        });

        Test("配置草稿：改一个字符只改状态，不改行数", () =>
        {
            var root = NewTempDirectory();
            try
            {
                var path = Path.Combine(root, "mpv.conf");
                File.WriteAllText(path, "a=1\nb=2\n");

                var draft = MpvConfigDraft.Open(path, MpvConfigKind.Mpv);
                Assert.Equal(2, draft.LineCount);

                draft.Edit(AsTextBoxWouldReturn("a=9\nb=2\n"));
                Assert.True(draft.IsDirty);
                Assert.Equal(MpvConfigDraftState.Dirty, draft.State);
                Assert.Contains("有未保存修改", draft.Status);
                Assert.Equal(2, draft.LineCount, "末尾换行不该多算一行，否则一编辑行数就跳一格");

                draft.Restore();
                Assert.False(draft.IsDirty);
                Assert.Equal("a=1\nb=2\n", draft.DraftText);
                Assert.Contains("已撤销未保存修改", draft.Status);
            }
            finally
            {
                DeleteTempDirectory(root);
            }
        });

        Test("配置草稿：保存后磁盘上仍是文件原来的换行和编码", () =>
        {
            var root = NewTempDirectory();
            try
            {
                var path = Path.Combine(root, "mpv.conf");
                const string original = "vo=gpu-next\r\nhwdec=auto\r\n";
                File.WriteAllText(path, original);

                var draft = MpvConfigDraft.Open(path, MpvConfigKind.Mpv);
                draft.Edit(AsTextBoxWouldReturn("vo=gpu-next\r\nhwdec=no\r\n"));

                var backup = draft.Save();
                Assert.NotNull(backup);
                Assert.Equal(original, File.ReadAllText(backup!), "备份是保存前的内容");

                var written = File.ReadAllText(path);
                Assert.Equal("vo=gpu-next\r\nhwdec=no\r\n", written);
                Assert.DoesNotContain("\r\r", written);
                Assert.Equal(2, written.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length, "整份文件不能被改写成裸 CR");

                Assert.False(draft.IsDirty);
                Assert.Equal(MpvConfigDraftState.Saved, draft.State);
                Assert.Equal("\r\n", draft.Document.Newline);
            }
            finally
            {
                DeleteTempDirectory(root);
            }
        });

        Test("配置草稿：文件不存在时先给提示，保存时以 UTF-8 新建", () =>
        {
            var root = NewTempDirectory();
            try
            {
                var path = Path.Combine(root, "portable_config", "input.conf");

                var draft = MpvConfigDraft.Open(path, MpvConfigKind.Input);
                Assert.Equal(MpvConfigDraftState.Missing, draft.State);
                Assert.Contains("不存在", draft.Status);
                Assert.Contains("UTF-8", draft.Status);
                Assert.Equal("", draft.DraftText);
                Assert.False(draft.IsDirty);

                draft.Edit(AsTextBoxWouldReturn("CTRL+s show-text 已保存\n"));
                Assert.True(draft.IsDirty);

                Assert.Null(draft.Save(), "原本没有文件，就没有可备份的内容");
                Assert.True(File.Exists(path));
                Assert.Equal("CTRL+s show-text 已保存\n", File.ReadAllText(path));
                Assert.Equal(MpvConfigKind.Input, draft.Document.Kind);
            }
            finally
            {
                DeleteTempDirectory(root);
            }
        });

        Test("配置位置：从资源管理器粘进来的带引号路径能用", () =>
        {
            // 「复制为路径」给出的就是带引号的路径，而 Windows 路径里不可能出现双引号，
            // 所以带着引号的值只可能是这么来的。留着引号，后面每一次 GetFullPath 都会抛，
            // 这个框就永远找不到它的文件了。
            var settings = new MpvSettings { ExecutablePath = "\"D:\\mpv\\mpv.exe\"" };
            var location = new MpvConfigLocation(settings);

            Assert.Equal(Path.Combine(@"D:\mpv", "portable_config", "mpv.conf"), location.Infer(MpvConfigKind.Mpv),
                "推断路径要认得带引号的 mpv.exe");

            Assert.Equal(@"E:\conf\mpv.conf", location.Store(MpvConfigKind.Mpv, "  \"E:\\conf\\mpv.conf\"  "));
            Assert.Equal(@"E:\conf\mpv.conf", settings.ConfigPath, "存进设置里的不能带引号");

            // 之前的版本会把引号原样存下来；读的时候也清一遍，这种值第一次被读到就自愈。
            settings.InputConfigPath = "\"E:\\conf\\input.conf\"";
            Assert.Equal(@"E:\conf\input.conf", location.Resolve(MpvConfigKind.Input));

            // 带引号的推断值仍然是推断值，不该被冻住。
            Assert.Equal(location.Infer(MpvConfigKind.Input), location.Store(MpvConfigKind.Input, $"\"{location.Infer(MpvConfigKind.Input)}\""));
            Assert.Null(settings.InputConfigPath);
        });

        Test("配置位置：同一个文件的不同写法算同一个，畸形路径不抛异常", () =>
        {
            Assert.True(MpvConfigLocation.SamePath(@"D:\mpv\portable_config\..\mpv.conf", @"D:\MPV\MPV.CONF"));
            Assert.False(MpvConfigLocation.SamePath(@"D:\mpv\mpv.conf", @"D:\mpv\input.conf"));

            // 用户有权往框里打任何东西，抱怨该由「打开这个文件」的人来提，不该由「比较两个名字」的人来抛。
            // GetFullPath 真正拒绝的（空串、内嵌 NUL）原样返回；它不拒绝的（<>|?* 这类，如今只当相对路径
            // 展开）也照样有个结果。两种都不能抛，也都不能变成空串之外的意外值。
            Assert.Equal("", MpvConfigLocation.Normalise("   "));
            Assert.Equal("mpv\0.conf", MpvConfigLocation.Normalise("mpv\0.conf"));
            Assert.True(MpvConfigLocation.Normalise("<>|?*").Length > 0);
            Assert.True(MpvConfigLocation.SamePath("mpv\0.conf", "mpv\0.conf"), "认不出来的路径至少要等于它自己");
        });

        Test("配置草稿：路径指到大文件时拒绝打开，也不会拿空内容覆盖它", () =>
        {
            var root = NewTempDirectory();
            try
            {
                // 把路径框指到同目录下那个 38 MB 的 libmpv-2.dll 是最容易发生的一种误操作。
                var path = Path.Combine(root, "libmpv-2.dll");
                using (var stream = File.Create(path)) stream.SetLength(MpvConfigDraft.SizeLimit + 1);

                var draft = MpvConfigDraft.Open(path, MpvConfigKind.Mpv);
                Assert.Equal(MpvConfigDraftState.Failed, draft.State);
                Assert.False(draft.CanSave);
                Assert.Contains("不像是配置文件", draft.Status);

                draft.Edit("vo=gpu-next\n");
                Assert.Equal("", draft.DraftText, "拒绝打开的文件不接受编辑");
                draft.Restore();
                Assert.Equal("", draft.DraftText);

                Assert.Throws<InvalidOperationException>(() => draft.Save(), "没读进来的文件不能覆盖");
                Assert.Equal(MpvConfigDraft.SizeLimit + 1, new FileInfo(path).Length, "文件必须原封不动");
            }
            finally
            {
                DeleteTempDirectory(root);
            }
        });

        Test("配置草稿：编辑期间别的程序改了文件，会说明，且对方的内容进了备份", () =>
        {
            var root = NewTempDirectory();
            try
            {
                var path = Path.Combine(root, "mpv.conf");
                File.WriteAllText(path, "vo=gpu-next\r\n");

                var draft = MpvConfigDraft.Open(path, MpvConfigKind.Mpv);
                draft.Edit(AsTextBoxWouldReturn("vo=gpu-next\r\nhwdec=no\r\n"));

                // 用户在别的编辑器里改了同一个文件。长度和时间都变，避免踩到时间戳精度。
                const string outside = "vo=gpu\r\nhwdec=auto-safe\r\nprofile=high-quality\r\n";
                File.WriteAllText(path, outside);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

                var backup = draft.Save();

                Assert.NotNull(backup);
                Assert.Equal(outside, File.ReadAllText(backup!), "备份里必须是对方写的那一版，而不是我们打开时看到的那一版");
                Assert.Equal("vo=gpu-next\r\nhwdec=no\r\n", File.ReadAllText(path), "保存就是保存，用户要的是把自己这份写下去");
                Assert.Contains("被其他程序改过", draft.Status);
                Assert.Equal(MpvConfigDraftState.Saved, draft.State);
            }
            finally
            {
                DeleteTempDirectory(root);
            }
        });

        Test("配置草稿：备份没做成时说清楚，而不是报「已创建」", () =>
        {
            var root = NewTempDirectory();
            try
            {
                var path = Path.Combine(root, "mpv.conf");
                File.WriteAllText(path, "vo=gpu-next\n");

                // 备份目录的位置上放一个同名文件，CreateDirectory 就会失败 —— 这是「备份做不成」最好复现的一种。
                File.WriteAllText(Path.Combine(root, "EmbyNian-backups"), "占位");

                var draft = MpvConfigDraft.Open(path, MpvConfigKind.Mpv);
                draft.Edit(AsTextBoxWouldReturn("vo=gpu\n"));

                Assert.Null(draft.Save());
                Assert.Equal("vo=gpu\n", File.ReadAllText(path), "备份失败不拦着保存");
                Assert.Equal(MpvConfigDraftState.SavedWithoutBackup, draft.State, "得是警告色，不能和成功保存一个颜色");
                Assert.Contains("备份没做成", draft.Status);
                Assert.DoesNotContain("已创建", draft.Status, "文件本来就在，说「已创建」是反过来的");
                Assert.DoesNotContain("被其他程序改过", draft.Status);
            }
            finally
            {
                DeleteTempDirectory(root);
            }
        });

        Test("配置草稿：打开之后才出现的 GBK 文件，不会被改写成 UTF-8", () =>
        {
            TextFileEncoding.EnsureCodePagesRegistered();
            var gbk = System.Text.Encoding.GetEncoding(936);

            var root = NewTempDirectory();
            try
            {
                var path = Path.Combine(root, "mpv.conf");

                // 打开时还没有这个文件，草稿按 UTF-8 起手。
                var draft = MpvConfigDraft.Open(path, MpvConfigKind.Mpv);
                Assert.Equal(MpvConfigDraftState.Missing, draft.State);

                // 用户这时候从别处拷了一份带中文注释的 GBK 配置进来。
                File.WriteAllBytes(path, gbk.GetBytes("# 旧的中文注释\nvo=gpu\n"));

                draft.Edit(AsTextBoxWouldReturn("# 旧的中文注释\nvo=gpu-next\n"));
                draft.Save();

                var written = File.ReadAllBytes(path);
                Assert.False(TextFileEncoding.IsValidUtf8(written), "写回去的必须还是 GBK，否则中文注释就成乱码了");
                Assert.Equal("# 旧的中文注释\nvo=gpu-next\n", gbk.GetString(written));
                Assert.Equal("GBK", draft.Document.Encoding.Description);
                Assert.Contains("被其他程序改过", draft.Status);
            }
            finally
            {
                DeleteTempDirectory(root);
            }
        });
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"embynian-mpv-workspace-{Guid.NewGuid():N}");
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
