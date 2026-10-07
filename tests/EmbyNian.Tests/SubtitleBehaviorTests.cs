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
        Test("字幕颜色：所有字节精确下发，不经过三位小数舍入", () =>
        {
            for (var channel = 0; channel <= 255; channel++)
            {
                var rgb = $"#{channel:X2}{(255 - channel):X2}56";
                Assert.Equal("#FF" + rgb[1..], MpvOutputOptions.ToMpvColor(rgb, 100));
            }
            Assert.Equal("#00123456", MpvOutputOptions.ToMpvColor("#123456", -1));
            Assert.Equal("#66AC5D5D", MpvOutputOptions.ToMpvColor("#AC5D5D", 40));
            Assert.Equal("#FFFE0102", MpvOutputOptions.ToMpvColor("#FE0102", 101));
            Assert.Null(MpvOutputOptions.ToMpvColor("", 100));
        });

        Test("字幕预览：百分比缩放同步放大字号、描边与阴影", () =>
        {
            foreach (var percent in new[] { 50, 100, 200, 300 })
            {
                var settings = new PlaybackSettings { SubtitleScalePercent = percent };
                var plan = SubtitlePreviewPlan.Plan(settings, 1.3, "两行\n字幕");
                Assert.Equal(65.0 * percent / 100, plan.FontSize);
                Assert.True(Math.Abs(plan.Layers[0].X - 0.65 * percent / 100) < 0.001);
            }
        });

        Test("字幕底板：未指定颜色仍应用透明度，预览与 mpv 参数一致", () =>
        {
            foreach (var opacity in new[] { 0, 40, 100 })
            {
                var settings = new PlaybackSettings
                {
                    SubtitleBackColor = "",
                    SubtitleBackOpacity = opacity,
                    SubtitleBackStyle = "background-box"
                };
                var plan = SubtitlePreviewPlan.Plan(settings, 1, "两行\n字幕");
                Assert.Equal(opacity / 100.0, plan.PlateOpacity);
                Assert.Equal("#000000", plan.PlateColor);
                var options = MpvOutputOptions.SubtitleAppearance(settings).ToDictionary(pair => pair.Key, pair => pair.Value);
                Assert.Equal($"#{(int)Math.Round(opacity * 255.0 / 100):X2}000000", options["sub-back-color"]);
            }
        });

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

        Test("字幕元数据未知：外语模式仍关闭已知母语的字幕，未知音轨仍交给内核", () =>
        {
            var settings = new PlaybackSettings { SubtitleMode = SubtitleMode.ForeignAudioOnly, SubtitleLanguages = ["中文"] };
            Assert.True(TrackSelection.Resolve(settings, Source(Audio(1, "chi"))).Subtitle.Disabled);
            Assert.False(TrackSelection.Resolve(settings, Source(Audio(1, "jpn"))).Subtitle.Disabled);
            Assert.False(TrackSelection.Resolve(settings, Source()).Subtitle.Disabled);
        });

        Test("同窗换片：完整字幕外观按新票重发，不继承尚未恢复的旧值", () =>
        {
            var appearance = MpvOutputOptions.SubtitleAppearance(new PlaybackSettings
            {
                SubtitleAssOverride = "force",
                SubtitleColor = "#FFFFFF",
                SubtitleBorderColor = "#123456"
            });
            var defaults = MpvOutputOptions.SubtitleStyleOptions.ToDictionary(name => name, _ => "default");
            var request = new PlaybackRequest { MediaUrl = new Uri("http://192.0.2.1/fixture"), Title = "fixture", PlayerOptions = appearance };
            var options = InlineSwitch.FilmScoped(defaults, request).ToDictionary(option => option.Key, option => option.Value);
            foreach (var name in MpvOutputOptions.SubtitleStyleOptions)
            {
                Assert.True(options.ContainsKey(name), name);
                Assert.Equal(appearance.LastOrDefault(option => option.Key == name).Value ?? "default", options[name], name);
            }
        });

        Test("同窗换片：主次字幕可见性、次字幕选择和临时参数全部复位", () =>
        {
            var defaults = new Dictionary<string, string>
            {
                ["sub-visibility"] = "yes",
                ["secondary-sid"] = "no",
                ["secondary-sub-visibility"] = "yes",
                ["secondary-sub-delay"] = "0",
                ["secondary-sub-pos"] = "0",
                ["secondary-sub-ass-override"] = "strip"
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
