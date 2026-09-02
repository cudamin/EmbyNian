using EmbyNian.Emby;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 卡片上「更多」那颗按钮点开之后有哪几条 —— 用户按三处提的那三张单子（继续观看、电视媒体库、电影媒体库），
/// 在这里变成三条断言。
/// <para>
/// 这一族值得钉住，是因为它错起来屏上看着完全正常：一部剧上冒出「搜索和修改字幕」（剧集不是一个文件，那一条
/// 点下去只能报错）、一张媒体库卡片上冒出「删除」（那会去删整个库的目录），都得有人一条条盯着菜单才看得出来。
/// 而分隔线摆错的样子更轻 —— 两条线挨在一起，或者一条线孤零零地开头 —— 谁都不会为它开一个缺陷单，却正是
/// 「哪几条这次没出现」留下的唯一痕迹。
/// </para>
/// </summary>
internal static class ItemMenuTests
{
    public static void Register()
    {
        RegisterResume();
        RegisterSeries();
        RegisterMovie();
        RegisterShape();
        RegisterDownloadPlan();
    }

    /// <summary>继续观看那一排上的一张卡：一集看到一半。</summary>
    private static void RegisterResume()
    {
        Test("更多菜单：继续观看那张卡上，用户要的十一条都在", () =>
        {
            var labels = Labels(Resuming());

            foreach (var wanted in (string[])[
                "添加到合集…",
                "添加到收藏",
                "下载到设备",
                "编辑元数据信息…",
                "修改媒体封面图…",
                "搜索和修改字幕…",
                "标记为未观看",
                "刷新元数据信息",
                "重新扫描媒体库",
                "从继续观看中移除",
                "删除"])
                Assert.Contains(wanted, labels);
        });

        Test("更多菜单：看到一半的条目「已观看」和「未观看」两条同时给", () =>
        {
            // 一条都能说：看完了，或者当没看过（那一下把进度清掉）。从前这里只有一条、按 Played 翻面，于是
            // 继续观看那一排上永远只有「标记为已观看」—— 用户要的「标记为未播放」在那儿根本没有。
            var labels = Labels(Resuming());
            Assert.Contains("标记为已观看", labels);
            Assert.Contains("标记为未观看", labels);
        });

        Test("更多菜单：没看过、也没进度的条目不给「未观看」，也不给「从继续观看中移除」", () =>
        {
            var fresh = new EmbyItem { Id = "m2", Name = "新片", Type = EmbyItemType.Movie };
            var labels = Labels(fresh);

            Assert.Contains("标记为已观看", labels);
            Assert.DoesNotContain("标记为未观看", labels);
            Assert.DoesNotContain("从继续观看中移除", labels);
        });

        Test("更多菜单：看过的条目不给「已观看」", () =>
        {
            var watched = new EmbyItem
            {
                Id = "m3",
                Name = "看过的",
                Type = EmbyItemType.Movie,
                UserData = new EmbyUserData { Played = true }
            };

            Assert.DoesNotContain("标记为已观看", Labels(watched));
            Assert.Contains("标记为未观看", Labels(watched));
        });

        Test("更多菜单：继续观看那张卡的第一条写着接着播的位置", () =>
        {
            var menu = ItemMenu.For(Resuming());

            Assert.Equal(ItemCommand.Play, menu[0].Command);
            Assert.Contains("继续播放", menu[0].Label);
        });
    }

    /// <summary>电视媒体库里的一张卡：一部剧。</summary>
    private static void RegisterSeries()
    {
        Test("更多菜单：电视媒体库那张卡上，用户要的十条都在", () =>
        {
            var labels = Labels(new EmbyItem { Id = "s1", Name = "某剧", Type = EmbyItemType.Series });

            foreach (var wanted in (string[])[
                "添加到合集…",
                "添加到收藏",
                "下载到设备",
                "编辑元数据信息…",
                "修改媒体封面图…",
                "刮削元数据信息",
                "标记为已观看",
                "刷新元数据信息",
                "重新扫描媒体库",
                "删除"])
                Assert.Contains(wanted, labels);
        });

        Test("更多菜单：剧集上没有「搜索和修改字幕」", () =>
        {
            // 字幕挂在文件上，而一部剧是一叠文件 —— 服务器上没有一个「这部剧的字幕」可放。用户那张单子里
            // 电视媒体库也正好没有这一条。
            Assert.DoesNotContain("搜索和修改字幕", Labels(new EmbyItem { Id = "s1", Type = EmbyItemType.Series }));
        });

        Test("更多菜单：剧集给的是「打开」，不是「立即播放」加「详细信息」", () =>
        {
            var menu = ItemMenu.For(new EmbyItem { Id = "s1", Type = EmbyItemType.Series });

            Assert.Equal(ItemCommand.Open, menu[0].Command);
            Assert.Equal("打开", menu[0].Label);
            Assert.DoesNotContain("立即播放", Labels(new EmbyItem { Id = "s1", Type = EmbyItemType.Series }));
        });

        Test("更多菜单：一季也能下载，一个合集不能", () =>
        {
            Assert.Contains("下载到设备", Labels(new EmbyItem { Id = "n1", Type = EmbyItemType.Season }));

            // 合集是用户自己攒的一篮子东西，可以横跨几个库、几种类型 ——「下载这个合集」不是一句说得清的话。
            Assert.DoesNotContain("下载到设备", Labels(new EmbyItem { Id = "b1", Type = EmbyItemType.BoxSet }));
        });
    }

    /// <summary>电影媒体库里的一张卡。</summary>
    private static void RegisterMovie()
    {
        Test("更多菜单：电影媒体库那张卡上，用户要的十一条都在", () =>
        {
            var labels = Labels(new EmbyItem { Id = "m1", Name = "某片", Type = EmbyItemType.Movie });

            foreach (var wanted in (string[])[
                "添加到合集…",
                "添加到收藏",
                "下载到设备",
                "编辑元数据信息…",
                "修改媒体封面图…",
                "搜索和修改字幕…",
                "刮削元数据信息",
                "标记为已观看",
                "刷新元数据信息",
                "重新扫描媒体库",
                "删除"])
                Assert.Contains(wanted, labels);
        });

        Test("更多菜单：媒体库自己那张卡上没有删除、也没有刮削", () =>
        {
            // DELETE /Items/{Id} 连磁盘上的文件一起删 —— 落在一张媒体库卡片上就是去删整个库的目录。
            var labels = Labels(new EmbyItem { Id = "lib1", Type = EmbyItemType.CollectionFolder });

            Assert.DoesNotContain("删除", labels);
            Assert.DoesNotContain("刮削", labels);
            Assert.DoesNotContain("添加到合集", labels);
            Assert.Contains("打开", labels);
            Assert.Contains("编辑元数据信息…", labels);
            Assert.Contains("重新扫描媒体库", labels);
        });

        Test("更多菜单：演职人员那张卡上只有打开和编辑", () =>
        {
            // 那一排卡片是存根（只有 id、名字和类型），照它去刮削、改封面或者扫库是拿一个人名当影片使。
            var labels = Labels(new EmbyItem { Id = "p1", Type = EmbyItemType.Person });

            Assert.DoesNotContain("删除", labels);
            Assert.DoesNotContain("重新扫描媒体库", labels);
            Assert.DoesNotContain("修改媒体封面图", labels);
            Assert.DoesNotContain("添加到收藏", labels);
        });

        Test("更多菜单：单集上多一条「打开所属剧集」，电影上没有", () =>
        {
            var episode = new EmbyItem { Id = "e1", Type = EmbyItemType.Episode, SeriesId = "s1" };
            Assert.Contains("打开所属剧集", Labels(episode));

            // 服务器没给剧集 id 的那一集（搜索结果里有过）不给这一条 —— 点下去无处可去。
            var orphan = new EmbyItem { Id = "e2", Type = EmbyItemType.Episode };
            Assert.DoesNotContain("打开所属剧集", Labels(orphan));

            Assert.DoesNotContain("打开所属剧集", Labels(new EmbyItem { Id = "m1", Type = EmbyItemType.Movie }));
        });
    }

    /// <summary>菜单的形状：分隔线摆在段与段之间，一条都不多。</summary>
    private static void RegisterShape()
    {
        Test("更多菜单：没有两条挨着的分隔线，也不以分隔线开头或结尾", () =>
        {
            foreach (var item in (EmbyItem[])[
                Resuming(),
                new() { Id = "s1", Type = EmbyItemType.Series },
                new() { Id = "m1", Type = EmbyItemType.Movie },
                new() { Id = "lib1", Type = EmbyItemType.CollectionFolder },
                new() { Id = "p1", Type = EmbyItemType.Person },
                new() { Id = "b1", Type = EmbyItemType.BoxSet }])
            {
                var menu = ItemMenu.For(item);

                Assert.True(menu.Count > 0, $"{item.Type} 的菜单是空的");
                Assert.False(menu[0].IsRule, $"{item.Type} 的菜单以分隔线开头");
                Assert.False(menu[^1].IsRule, $"{item.Type} 的菜单以分隔线结尾");

                for (var index = 1; index < menu.Count; index++)
                    Assert.False(menu[index].IsRule && menu[index - 1].IsRule,
                        $"{item.Type} 的菜单里有两条挨着的分隔线");
            }
        });

        Test("更多菜单：每一条命令只出现一次，每一行都有字", () =>
        {
            var menu = ItemMenu.For(Resuming());
            var commands = menu.Where(row => !row.IsRule).Select(row => row.Command).ToList();

            Assert.Equal(commands.Count, commands.Distinct().Count(), "同一条命令出现了两次");
            Assert.True(menu.Where(row => !row.IsRule).All(row => row.Label.Length > 0), "有一行没有字");
            Assert.True(menu.Where(row => row.IsRule).All(row => row.Label.Length == 0));
        });

        Test("更多菜单：删除永远是最后一条", () =>
        {
            // 单独一段摆在末尾，前面还隔着一条分隔线 —— 它连磁盘上的文件一起删，不该和「刷新元数据」贴在
            // 一起被误点。
            foreach (var item in (EmbyItem[])[
                Resuming(),
                new() { Id = "s1", Type = EmbyItemType.Series },
                new() { Id = "m1", Type = EmbyItemType.Movie }])
            {
                var menu = ItemMenu.For(item);

                Assert.Equal(ItemCommand.Delete, menu[^1].Command);
                Assert.True(menu[^2].IsRule, "删除前面没有分隔线");
            }
        });
    }

    /// <summary>下载到设备落在哪儿、叫什么名字。</summary>
    private static void RegisterDownloadPlan()
    {
        Test("下载：文件名用条目自己那套说法，后缀跟着服务器上的文件走", () =>
        {
            var episode = new EmbyItem
            {
                Id = "e1",
                Name = "崛起",
                Type = EmbyItemType.Episode,
                SeriesName = "攻壳机动队",
                ParentIndexNumber = 1,
                IndexNumber = 2
            };

            var source = new MediaSource { Path = "/media/tv/Show/Show.S01E02.1080p.WEB-DL.x265.mkv", Container = "mkv" };
            Assert.Equal("攻壳机动队 S01E02 崛起.mkv", DownloadPlan.FileName(episode, source));
        });

        Test("下载：没有路径时后缀取容器的头一个", () =>
        {
            var film = new EmbyItem { Id = "m1", Name = "某片", Type = EmbyItemType.Movie };

            Assert.Equal("某片.mkv", DownloadPlan.FileName(film, new MediaSource { Container = "mkv,mka,webm" }));

            // 一样都问不出来就没有后缀 —— 猜一个错的（把 mkv 存成 mp4）比没有更糟。
            Assert.Equal("某片", DownloadPlan.FileName(film, new MediaSource()));
            Assert.Equal("某片", DownloadPlan.FileName(film, null));
        });

        Test("下载：名字里 Windows 不认的字符换掉，连着的空白并成一个", () =>
        {
            var film = new EmbyItem { Id = "m1", Name = "谁?: 那个/人 <上>", Type = EmbyItemType.Movie };

            var name = DownloadPlan.FileName(film, new MediaSource { Container = "mp4" });
            Assert.Equal("谁 那个 人 上.mp4", name);
            foreach (var illegal in (char[])['\\', '/', ':', '*', '?', '"', '<', '>', '|'])
                Assert.DoesNotContain(illegal.ToString(), name);
        });

        Test("下载：名字全是不认的字符时退到条目 id", () =>
        {
            var film = new EmbyItem { Id = "abc123", Name = "??", Type = EmbyItemType.Movie };
            Assert.Equal("item-abc123", DownloadPlan.FileName(film, null));
        });

        Test("下载：末尾的点和空格去掉，长名字掐短", () =>
        {
            Assert.Equal("某片", DownloadPlan.Safe("某片... "));
            Assert.True(DownloadPlan.Safe(new string('长', 300)).Length <= 96);
        });

        Test("下载：一部剧和一季各落进自己的文件夹，单个文件落在根上", () =>
        {
            var root = Path.Combine("D:", "视频", "EmbyNian");

            var series = new EmbyItem { Id = "s1", Name = "攻壳机动队", Type = EmbyItemType.Series };
            Assert.Equal(Path.Combine(root, "攻壳机动队"), DownloadPlan.Folder(root, series));

            // 季用「剧名 第 1 季」：光一个「第 1 季」摆在视频目录里谁都认不出是哪部剧的。
            var season = new EmbyItem
            {
                Id = "n1",
                Name = "第 1 季",
                Type = EmbyItemType.Season,
                SeriesName = "攻壳机动队"
            };
            Assert.Equal(Path.Combine(root, "攻壳机动队 第 1 季"), DownloadPlan.Folder(root, season));

            var film = new EmbyItem { Id = "m1", Name = "某片", Type = EmbyItemType.Movie };
            Assert.Equal(root, DownloadPlan.Folder(root, film));
        });

        Test("下载：根目录就是视频文件夹底下那一个", () =>
        {
            Assert.Equal(Path.Combine("D:", "视频", "EmbyNian"), DownloadPlan.Root(Path.Combine("D:", "视频")));

            // 这台机器上问出来的那一个必须是绝对路径：相对路径会让影片落在程序自己的目录里。
            Assert.True(Path.IsPathFullyQualified(DownloadPlan.DefaultRoot), DownloadPlan.DefaultRoot);
        });
    }

    /// <summary>继续观看那一排上的一张卡：一集看了三成，没看完。</summary>
    private static EmbyItem Resuming() => new()
    {
        Id = "e1",
        Name = "第二集",
        Type = EmbyItemType.Episode,
        SeriesId = "s1",
        SeriesName = "某剧",
        RunTimeTicks = 6000_000_000,
        UserData = new EmbyUserData { PlaybackPositionTicks = 1800_000_000, PlayedPercentage = 30 }
    };

    private static string Labels(EmbyItem item) =>
        string.Join(" | ", ItemMenu.For(item).Select(row => row.IsRule ? "──" : row.Label));
}
