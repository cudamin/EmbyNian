using EmbyNian.Configuration;
using EmbyNian.Emby;
using EmbyNian.Mpv;
using EmbyNian.Playback;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

internal static class SubtitleBehaviorTests
{
    public static void Register()
    {
        Test("字幕外语模式：兜底项不是听得懂的语言", () =>
        {
            var source = Source(Audio(1, "jpn"), Subtitle(2, "chi"));
            var settings = new PlaybackSettings
            {
                SubtitleMode = SubtitleMode.ForeignAudioOnly,
                SubtitleLanguages = ["中文", TrackLanguagePriority.Any]
            };
            Assert.Equal(2, TrackSelection.Resolve(settings, source).Subtitle.Stream?.Index);
            source.MediaStreams[0].Language = "chi";
            Assert.True(TrackSelection.Resolve(settings, source).Subtitle.Disabled);
        });

        Test("自动字幕：按最终手选音轨判断外语，两个方向都正确", () =>
        {
            var source = Source(Audio(1, "chi"), Audio(2, "jpn"), Subtitle(3, "chi"));
            source.DefaultAudioStreamIndex = 1;
            var settings = new AppSettings();
            settings.Playback.SubtitleMode = SubtitleMode.ForeignAudioOnly;
            settings.Playback.SubtitleLanguages = ["中文"];
            settings.Shaders.Enabled = false;
            var planner = new PlaybackPlanner(settings, new ShaderGroupResolver(settings.Shaders));
            var ticket = new PlaybackTicket
            {
                Item = new EmbyItem { Id = "fixture", Name = "字幕夹具" },
                Source = source,
                AudioStreamIndex = 2
            };
            var connection = new EmbyConnection(new Uri("http://192.0.2.1/emby/"), "placeholder", "u", "u", "fixture",
                DeviceIdentity.Create("fixture", "0.0.0"));
            var foreign = planner.Plan(ticket, connection);
            Assert.Equal(2, foreign.AudioStreamIndex);
            Assert.Equal(3, foreign.SubtitleStreamIndex);
            Assert.False(foreign.SubtitlesDisabled);

            source.DefaultAudioStreamIndex = 2;
            var native = planner.Plan(ticket with { AudioStreamIndex = 1 }, connection);
            Assert.True(native.SubtitlesDisabled);
            var explicitSubtitle = planner.Plan(ticket with { AudioStreamIndex = 1, SubtitleStreamIndex = 3 }, connection);
            Assert.Equal(3, explicitSubtitle.SubtitleStreamIndex);
            Assert.False(explicitSubtitle.SubtitlesDisabled);
            Assert.True(planner.Plan(ticket with { SubtitlesDisabled = true }, connection).SubtitlesDisabled);
        });

        Test("字幕下拉：自动项随显式音轨变化，手动字幕仍可选", () =>
        {
            var source = Source(Audio(1, "chi"), Audio(2, "jpn"), Subtitle(3, "chi"));
            source.DefaultAudioStreamIndex = 1;
            var settings = new PlaybackSettings { SubtitleMode = SubtitleMode.ForeignAudioOnly, SubtitleLanguages = ["中文"] };
            Assert.Contains("不显示", ItemDetail.SubtitleRows(settings, source)[0].Text);
            Assert.DoesNotContain("不显示", ItemDetail.SubtitleRows(settings, source, source.MediaStreams[1])[0].Text);
            Assert.Equal(3, ItemDetail.SubtitleRows(settings, source)[2].Stream?.Index);
        });

        Test("字幕可用性：自动跳过外挂图形轨，详情页显示不可用原因", () =>
        {
            var external = Subtitle(1, "chi");
            external.Codec = "pgs";
            external.IsExternal = true;
            var english = Subtitle(2, "eng");
            english.IsDefault = true;
            var source = Source(external, english);
            var settings = new PlaybackSettings { SubtitleLanguages = ["中文", TrackLanguagePriority.Any] };
            Assert.Equal(2, TrackSelection.Resolve(settings, source).Subtitle.Stream?.Index);
            var unavailable = ItemDetail.SubtitleRows(settings, source).Single(row => row.Stream?.Index == 1);
            Assert.False(unavailable.IsAvailable);
            Assert.Contains("无法加载", unavailable.Text);
            Assert.True(ItemDetail.SubtitleRows(settings, source).Single(row => row.Stream?.Index == 2).IsAvailable);
            Assert.True(TrackSelection.Resolve(settings, Source(external)).Subtitle.Disabled);
            external.IsExternal = false;
            Assert.Equal(1, TrackSelection.Resolve(settings, Source(external)).Subtitle.Stream?.Index,
                "内封图形字幕不受外挂接口限制");
        });

        Test("字幕回退：只使用明确默认轨，不再被标题候选替换", () =>
        {
            var source = Source(Subtitle(1, "eng"), Subtitle(2, "kor"));
            source.DefaultSubtitleStreamIndex = 1;
            source.MediaStreams[1].Title = "preferred";
            var settings = new PlaybackSettings
            {
                SubtitleLanguages = ["中文"],
                SubtitleTitleRules = [new("preferred", TitlePreference.Prefer)]
            };
            Assert.Equal(1, TrackSelection.Resolve(settings, source).Subtitle.Stream?.Index);
            settings.SubtitleFallbackToDefault = false;
            Assert.True(TrackSelection.Resolve(settings, source).Subtitle.Disabled);
            settings.SubtitleLanguages = [];
            Assert.Equal(1, TrackSelection.Resolve(settings, source).Subtitle.Stream?.Index,
                "未指定语言始终跟随默认，回退开关只管理有语言偏好的情况");
            source.DefaultSubtitleStreamIndex = null;
            Assert.True(TrackSelection.Resolve(settings, source).Subtitle.Disabled);
            settings.SubtitleLanguages = [TrackLanguagePriority.Any];
            Assert.Equal(2, TrackSelection.Resolve(settings, source).Subtitle.Stream?.Index,
                "任意轨兜底明确通过其他字幕表达，标题规则在这里有效");
        });

        Test("字幕回退：默认轨仍须可播放并服从强制字幕模式", () =>
        {
            var source = Source(Subtitle(1, "eng"), Subtitle(2, "kor"));
            source.MediaStreams[0].IsDefault = true;
            source.MediaStreams[1].IsForced = true;
            var settings = new PlaybackSettings { SubtitleLanguages = ["中文"], SubtitleMode = SubtitleMode.ForcedOnly };
            Assert.True(TrackSelection.Resolve(settings, source).Subtitle.Disabled);
            source.MediaStreams[1].IsDefault = true;
            Assert.Equal(2, TrackSelection.Resolve(settings, source).Subtitle.Stream?.Index);
        });

        Test("同窗换片：主次字幕可见性、次字幕选择和临时参数全部复位", () =>
        {
            var defaults = new Dictionary<string, string>
            {
                ["sub-visibility"] = "yes", ["secondary-sid"] = "no", ["secondary-sub-visibility"] = "yes",
                ["secondary-sub-delay"] = "0", ["secondary-sub-pos"] = "0", ["secondary-sub-ass-override"] = "strip"
            };
            var request = new PlaybackRequest { MediaUrl = new Uri("http://192.0.2.1/fixture"), Title = "fixture" };
            var options = InlineSwitch.FilmScoped(defaults, request).ToDictionary(option => option.Key, option => option.Value);
            foreach (var (name, expected) in defaults)
            {
                Assert.True(InlineSwitch.PerFilmNames.Contains(name));
                Assert.Equal(expected, options[name]);
            }
        });
    }

    private static MediaSource Source(params MediaStream[] streams) =>
        new() { Id = "fixture", Container = "mkv", MediaStreams = [.. streams] };

    private static MediaStream Audio(int index, string language) =>
        new() { Index = index, Type = "Audio", Language = language, Codec = "aac" };

    private static MediaStream Subtitle(int index, string language) =>
        new() { Index = index, Type = "Subtitle", Language = language, Codec = "srt" };
}
