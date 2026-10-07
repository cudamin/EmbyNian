using EmbyNian.Emby;
using EmbyNian.Playback;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

internal static class PlaybackTrackStateTests
{
    public static void Register()
    {
        Test("轨道上报：主次同时选中只报告主字幕的容器索引", () =>
        {
            var state = State();
            state.Observe([
                new(2, "audio", null, null, false, true) { FfmpegIndex = 4, MainSelection = 0 },
                new(1, "sub", null, null, false, true) { FfmpegIndex = 6, MainSelection = 0 },
                new(2, "sub", null, null, false, true) { FfmpegIndex = 7, MainSelection = 1 }
            ]);
            Assert.Equal(4, state.Selection.Audio);
            Assert.Equal(6, state.Selection.Subtitle);
        });

        Test("轨道上报：只开次字幕仍报告主字幕关闭", () =>
        {
            var state = State();
            state.Observe([new(2, "sub", null, null, false, true) { FfmpegIndex = 7, MainSelection = 1 }]);
            Assert.Equal(-1, state.Selection.Subtitle);
        });

        Test("轨道上报：外部字幕按实际文件映射，加载失败后的编号不作数", () =>
        {
            var state = State();
            state.Observe([new(3, "sub", null, null, false, true)
            { External = true, ExternalFilename = "https://fixture.invalid/second.srt", MainSelection = 0, FfmpegIndex = 0 }]);
            Assert.Equal(9, state.Selection.Subtitle);
        });

        Test("轨道上报：本地外挂和无法识别的容器轨不冒充服务器字幕", () =>
        {
            var state = State();
            state.Observe([new(3, "sub", null, null, false, true)
            { External = true, ExternalFilename = "C:/synthetic/first.srt", MainSelection = 0, FfmpegIndex = 0 }]);
            Assert.Null(state.Selection.Subtitle);
            state.Observe([new(1, "sub", null, null, false, true) { MainSelection = 0, FfmpegIndex = 91 }]);
            Assert.Null(state.Selection.Subtitle);
        });

        Test("轨道上报：旧内核缺主次角色时不猜，空列表不抹掉最后读数", () =>
        {
            var state = State();
            Assert.Equal(6, state.Selection.Subtitle);
            state.Observe([]);
            Assert.Equal(6, state.Selection.Subtitle);
            state.Observe([new(1, "sub", null, null, false, true) { FfmpegIndex = 6 }]);
            Assert.Null(state.Selection.Subtitle);
        });

        Test("轨道上报：晚到初始快照不能覆盖订阅后的新选择", () =>
        {
            var state = State();
            var revision = state.Revision;
            state.Observe([new(2, "sub", null, null, false, true) { FfmpegIndex = 7, MainSelection = 0 }]);
            state.ObserveInitial([new(1, "sub", null, null, false, true) { FfmpegIndex = 6, MainSelection = 0 }], revision);
            Assert.Equal(7, state.Selection.Subtitle);
        });

        Test("轨道上报：明确关闭与未知起播保留不同语义", () =>
        {
            var source = new MediaSource();
            var request = new PlaybackRequest { MediaUrl = new Uri("https://fixture.invalid/film"), Title = "fixture" };
            Assert.Null(new PlaybackTrackState(source, request).Selection.Subtitle);
            Assert.Equal(-1, new PlaybackTrackState(source, request with { SubtitlesDisabled = true }).Selection.Subtitle);
        });
    }

    private static PlaybackTrackState State()
    {
        var source = new MediaSource
        {
            MediaStreams = [
                new() { Index = 1, Type = "Audio" }, new() { Index = 4, Type = "Audio" },
                new() { Index = 6, Type = "Subtitle" }, new() { Index = 7, Type = "Subtitle" },
                new() { Index = 8, Type = "Subtitle", IsExternal = true, Codec = "srt" },
                new() { Index = 9, Type = "Subtitle", IsExternal = true, Codec = "srt" }
            ]
        };
        var request = new PlaybackRequest
        {
            MediaUrl = new Uri("https://fixture.invalid/film"),
            Title = "fixture",
            AudioStreamIndex = 1,
            SubtitleStreamIndex = 6,
            ExternalSubtitles = [new Uri("https://fixture.invalid/first.srt"), new Uri("https://fixture.invalid/second.srt")]
        };
        return new PlaybackTrackState(source, request);
    }
}
