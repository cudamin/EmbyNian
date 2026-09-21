using EmbyNian.Configuration;
using EmbyNian.Mpv;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 独占模式那四颗方向键的步长（<see cref="MpvSeekKeys"/>）。这些是屏上看不出对错、编译也看不出的判断：
/// 错一个数字就是「按 ↑ 跳 60 秒」，而这台机器上没人会拿秒表去数。
/// <para>
/// <b>值从哪来也一起钉住。</b> 这四个数必须是设置页上那四行，而不是写死的 5 / 30 —— 用户改了跨度、独占
/// 模式却照旧跳老数字，是「界面在骗人」里最安静的一种。实测见 <c>work/probe-keybind.txt</c>：这四条命令
/// 发出去之后 mpv 那层确实按新数字走。
/// </para>
/// </summary>
internal static class SeekKeyTests
{
    public static void Register()
    {
        Test("独占模式方向键：四颗键各绑到设置里那两对跨度上，方向也对", () =>
        {
            var playback = new PlaybackSettings
            {
                SeekBackwardSeconds = 5,
                SeekForwardSeconds = 5,
                SeekBackwardLongSeconds = 30,
                SeekForwardLongSeconds = 30
            };

            var bindings = MpvSeekKeys.Bindings(playback).ToDictionary(pair => pair.Key, pair => pair.Value);

            Assert.Equal(4, bindings.Count, "四颗方向键，一条不多一条不少");
            Assert.Equal("seek -5", bindings["LEFT"], "← 是回退，负号丢了这个键就往反方向跳");
            Assert.Equal("seek 5", bindings["RIGHT"]);
            Assert.Equal("seek -30", bindings["DOWN"], "↓ 是回退 30 秒");
            Assert.Equal("seek 30", bindings["UP"], "↑ 是快进 30 秒");
        });

        Test("独占模式方向键：数字跟着设置走，不是写死的 5 和 30", () =>
        {
            var playback = new PlaybackSettings
            {
                SeekBackwardSeconds = 7,
                SeekForwardSeconds = 11,
                SeekBackwardLongSeconds = 45,
                SeekForwardLongSeconds = 90
            };

            var bindings = MpvSeekKeys.Bindings(playback).ToDictionary(pair => pair.Key, pair => pair.Value);

            Assert.Equal("seek -7", bindings["LEFT"]);
            Assert.Equal("seek 11", bindings["RIGHT"]);
            Assert.Equal("seek -45", bindings["DOWN"]);
            Assert.Equal("seek 90", bindings["UP"]);
        });
    }
}
