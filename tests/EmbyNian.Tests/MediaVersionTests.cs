using EmbyNian.Emby;
using EmbyNian.Playback;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 播放中换版本那几条判据（<see cref="MediaVersionSwitch"/>）。
/// <para>
/// 这个功能的每一条规矩都错得起、且错了不报错：位置从零开始（观众看到的地方白丢）、把别的版本认成正在放的
/// 那一版（菜单勾错一行）、点了自己那一版还是重开一次（平白断一次片）、1 起算的序号差一位（换到旁边那一版）。
/// 四种都在屏上长得像「没坏」，所以钉在这里 —— 与 <see cref="PlaybackTests"/> 里候选版本回退那几条是同族的。
/// </para>
/// </summary>
internal static class MediaVersionTests
{
    public static void Register()
    {
        // 顺序就是菜单里的次序，且行必须持有原对象 —— 换版要的正是那个对象（宿主按它去发起播）。
        Test("版本：条目上有哪几版，按服务器给的顺序", () =>
        {
            var item = new EmbyItem
            {
                MediaSources =
                [
                    new MediaSource { Id = "a", Name = "4K HDR" },
                    new MediaSource { Id = "b", Name = "1080p" }
                ]
            };

            var versions = MediaVersionSwitch.Versions(item);
            Assert.Equal(2, versions.Count);
            Assert.Equal("4K HDR", versions[0].Name);
            Assert.True(ReferenceEquals(versions[0], item.MediaSources[0]), "换版要用这个对象本身");

            Assert.Equal(0, MediaVersionSwitch.Versions(null).Count);
            Assert.Equal(0, MediaVersionSwitch.Versions(new EmbyItem()).Count);
        });

        // 同一个对象算同一版（不问 id）；另取一次得到的对象按 id 算；空 id 不算 ——
        // Emby 对一部分直连文件不返回源 Id，那时几个空 id 会互相「相等」。
        Test("版本：同一版先认对象、再认 id，空 id 不算", () =>
        {
            var first = new MediaSource { Id = "", Name = "第一版" };
            var second = new MediaSource { Id = "", Name = "第二版" };

            Assert.True(MediaVersionSwitch.Same(first, first), "同一个对象就是同一版");
            Assert.False(MediaVersionSwitch.Same(first, second), "两个空 id 不是同一版");
            Assert.False(MediaVersionSwitch.Same(first, null));
            Assert.False(MediaVersionSwitch.Same(null, first));

            Assert.True(MediaVersionSwitch.Same(new MediaSource { Id = "x" }, new MediaSource { Id = "x" }),
                "另取一次的同 id 源是同一版");
            Assert.False(MediaVersionSwitch.Same(new MediaSource { Id = "x" }, new MediaSource { Id = "y" }));
        });

        // 点了自己正在看的那一版什么都不该发生：换版是一次实打实的重开。
        Test("版本：点自己正在看的那一版不换", () =>
        {
            var item = new EmbyItem
            {
                MediaSources =
                [
                    new MediaSource { Id = "a", Name = "4K HDR" },
                    new MediaSource { Id = "b", Name = "1080p" }
                ]
            };

            var playing = item.MediaSources[0];

            Assert.False(MediaVersionSwitch.ShouldSwitch(item, playing, playing), "自己这一版不换");
            Assert.False(
                MediaVersionSwitch.ShouldSwitch(item, new MediaSource { Id = "a" }, playing),
                "同 id 的另一份对象也是自己这一版");
            Assert.True(MediaVersionSwitch.ShouldSwitch(item, item.MediaSources[1], playing), "另一版要换");
            Assert.True(MediaVersionSwitch.ShouldSwitch(item, item.MediaSources[1], null), "不知道在放哪一版时按换处理");

            Assert.False(MediaVersionSwitch.ShouldSwitch(item, null, playing), "没有目标就不换");
            Assert.False(MediaVersionSwitch.ShouldSwitch(null, item.MediaSources[1], playing), "没有条目就不换");

            // 别的条目上的一版：那是换片子，不是换版本，别从这里走。
            var other = new MediaSource { Id = "c", Name = "别的片子的一版" };
            Assert.False(MediaVersionSwitch.ShouldSwitch(item, other, playing), "不在这个条目的版本表里就不换");
        });

        // 视频窗那条消息给的是 1 起算的序号（与推送菜单时的次序对齐）；越界与垃圾不是「第一版」。
        Test("版本：1 起算的序号，越界是 null", () =>
        {
            var item = new EmbyItem
            {
                MediaSources = [new MediaSource { Name = "4K HDR" }, new MediaSource { Name = "1080p" }]
            };

            Assert.Equal("4K HDR", MediaVersionSwitch.At(item, 1)?.Name);
            Assert.Equal("1080p", MediaVersionSwitch.At(item, 2)?.Name);

            Assert.Null(MediaVersionSwitch.At(item, 0));
            Assert.Null(MediaVersionSwitch.At(item, -1));
            Assert.Null(MediaVersionSwitch.At(item, 3));
            Assert.Null(MediaVersionSwitch.At(null, 1));
        });

        // 「看到哪儿」与版本无关：换版从手里这一刻接着放，不是从头。
        Test("版本：换版接着当前这一刻放，问不出来才退回条目的断点", () =>
        {
            const long hour = 3600 * 10_000_000L;

            Assert.Equal(90 * 10_000_000L, MediaVersionSwitch.StartTicks(90.0, 0));
            Assert.Equal(12_345_000L, MediaVersionSwitch.StartTicks(1.2345, 0));

            // mpv 还没报位置（null）时退回条目自己的续播点 —— 起播用的就是它。
            Assert.Equal(hour, MediaVersionSwitch.StartTicks(null, hour));

            // mpv 报的<b>正好是 0</b> 也退回断点：那是「这一跑刚打开、还没跳到断点」那一刻的读数，
            // 信它就是把看到一小时的人扔回片头。反过来的代价（从头开始看的人被送回断点）不存在 ——
            // 那种条目的断点本来就接近 0。
            Assert.Equal(hour, MediaVersionSwitch.StartTicks(0, hour));

            // 负数（有些后端用 -1 表示「还不知道」）同样退回断点，且断点本身也为负时归零。
            Assert.Equal(hour, MediaVersionSwitch.StartTicks(-1, hour));
            Assert.Equal(0L, MediaVersionSwitch.StartTicks(-1, 0));
        });

        // 菜单勾哪一行：「条目版本表里的那一行」，两种认法都用上。
        Test("版本：在放的那一行按对象与 id 认出来", () =>
        {
            var item = new EmbyItem
            {
                MediaSources =
                [
                    new MediaSource { Id = "a", Name = "4K HDR" },
                    new MediaSource { Id = "b", Name = "1080p" }
                ]
            };

            Assert.True(ReferenceEquals(item.MediaSources[1], MediaVersionSwitch.Playing(item, item.MediaSources[1])),
                "认出来的是表里那一行，不是在播那个对象");
            Assert.Equal("4K HDR", MediaVersionSwitch.Playing(item, new MediaSource { Id = "a" })?.Name);

            Assert.Null(MediaVersionSwitch.Playing(item, new MediaSource { Id = "zzz" }));
            Assert.Null(MediaVersionSwitch.Playing(item, null));
            Assert.Null(MediaVersionSwitch.Playing(null, item.MediaSources[0]));
        });

        // 默认播哪一版：按视频文件名筛选规则打分（「参考标题筛选，新增视频文件名筛选」）。
        Test("版本：默认版本按文件名规则挑，单版本/没规则一律第一版", () =>
        {
            var item = new EmbyItem
            {
                MediaSources =
                [
                    new MediaSource { Id = "a", Path = @"X:\Movies\电影.2160p.WEB-DL.mp4" },
                    new MediaSource { Id = "b", Name = "REMUX", Path = @"X:\Movies\电影.1080p.BluRay.REMUX.mkv" },
                    new MediaSource { Id = "c", Path = @"X:\Movies\电影.枪版.mp4" }
                ]
            };

            // 没有规则：还是服务器第一版（行为不变）。
            Assert.True(ReferenceEquals(item.MediaSources[0], MediaVersionSwitch.Preferred(item, [])));
            Assert.True(ReferenceEquals(item.MediaSources[0], MediaVersionSwitch.Preferred(item, null)));

            // 优先 REMUX：命中版本名/文件名里的 REMUX，抬到最前。
            Assert.Equal("b", MediaVersionSwitch.Preferred(item, [new("REMUX", TitlePreference.Prefer)])?.Id);

            // 候补 枪版：把枪版压到最后；同分（其余两版都 0 分）时保留服务器次序，取第一版。
            Assert.Equal("a", MediaVersionSwitch.Preferred(item, [new("枪版", TitlePreference.Exclude)])?.Id);

            // 优先 2160p、同时候补 枪版：2160p 那版 +1 胜出。
            Assert.Equal("a", MediaVersionSwitch.Preferred(item,
                [new("2160p", TitlePreference.Prefer), new("枪版", TitlePreference.Exclude)])?.Id);

            // 全被压低时软兜底：只有枪版一版且它被候补，仍然给它。
            var onlyCam = new EmbyItem { MediaSources = [new MediaSource { Id = "c", Path = @"X:\电影.枪版.mp4" }] };
            Assert.Equal("c", MediaVersionSwitch.Preferred(onlyCam, [new("枪版", TitlePreference.Exclude)])?.Id, "只剩它时还是给它");

            Assert.Null(MediaVersionSwitch.Preferred(null, [new("REMUX", TitlePreference.Prefer)]));
            Assert.Null(MediaVersionSwitch.Preferred(new EmbyItem(), [new("REMUX", TitlePreference.Prefer)]));
        });
    }
}
