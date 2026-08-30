using EmbyNian.Diagnostics;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// Covers the in-memory logging the diagnostics page is built on. The page itself needs a UI thread and
/// is checked by <c>--self-check</c> instead, but everything it reads from — the ring buffer's ordering,
/// its capacity and its <see cref="RingBufferLogSink.Written"/> event — is plain synchronous code and
/// belongs here, where a failure names the reason instead of showing up as an empty log panel.
/// </summary>
internal static class DiagnosticsTests
{
    private const string Category = "测试";

    public static void Register()
    {
        RegisterRingBuffer();
        RegisterWrittenEvent();
        RegisterEntryText();
    }

    // ---- 环形缓冲 --------------------------------------------------------------

    private static void RegisterRingBuffer()
    {
        Test("内存日志：快照按写入顺序返回，最旧的在前", () =>
        {
            var sink = new RingBufferLogSink(10);
            for (var index = 1; index <= 4; index++) sink.Write(Entry($"第 {index} 条"));

            var snapshot = sink.Snapshot();
            Assert.Equal(4, snapshot.Count);
            Assert.Equal("第 1 条", snapshot[0].Message);
            Assert.Equal("第 4 条", snapshot[3].Message);
        });

        Test("内存日志：空缓冲的快照是空的，不是 null", () =>
        {
            var snapshot = new RingBufferLogSink(10).Snapshot();
            Assert.NotNull(snapshot);
            Assert.Equal(0, snapshot.Count);
        });

        Test("内存日志：写满之后丢掉最旧的，顺序不乱", () =>
        {
            // Three past capacity, so the wrap-around arithmetic in Snapshot is exercised rather than
            // just the 「exactly full」 case that happens to work with start == 0.
            var sink = new RingBufferLogSink(5);
            for (var index = 1; index <= 8; index++) sink.Write(Entry($"第 {index} 条"));

            var snapshot = sink.Snapshot();
            Assert.Equal(5, snapshot.Count);
            Assert.Equal("第 4 条", snapshot[0].Message);
            Assert.Equal("第 8 条", snapshot[4].Message);
        });

        Test("内存日志：容量对外可读，页面靠它决定自己留多少行", () =>
        {
            // The diagnostics page trims its own list to this number; if the property stopped agreeing
            // with the buffer, the page would grow without limit for as long as it stayed open.
            Assert.Equal(500, new RingBufferLogSink().Capacity, "默认容量");
            Assert.Equal(7, new RingBufferLogSink(7).Capacity);
        });

        Test("内存日志：容量不能小于 1", () =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RingBufferLogSink(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RingBufferLogSink(-1));
        });
    }

    // ---- 实时事件 --------------------------------------------------------------

    private static void RegisterWrittenEvent()
    {
        Test("实时日志：每写一条就通知一次，内容一致", () =>
        {
            var sink = new RingBufferLogSink(10);
            var seen = new List<LogEntry>();
            sink.Written += entry => seen.Add(entry);

            sink.Write(Entry("一"));
            sink.Write(Entry("二"));

            Assert.Equal(2, seen.Count);
            Assert.Equal("一", seen[0].Message);
            Assert.Equal("二", seen[1].Message);
        });

        Test("实时日志：通知时缓冲里已经有这条了", () =>
        {
            // The page appends on this event and re-snapshots on 刷新; if the event fired before the entry
            // was committed, the two would disagree and a refresh would appear to lose the newest line.
            var sink = new RingBufferLogSink(10);
            var countAtNotify = -1;
            sink.Written += _ => countAtNotify = sink.Snapshot().Count;

            sink.Write(Entry("一"));

            Assert.Equal(1, countAtNotify, "事件触发时快照里应该已经包含这条");
        });

        Test("实时日志：取消订阅之后不再收到通知", () =>
        {
            // What the page does on navigating away. A handler left attached would keep a dead page alive
            // and touch its bound collections from a background thread.
            var sink = new RingBufferLogSink(10);
            var count = 0;
            void Handler(LogEntry entry) => count++;

            sink.Written += Handler;
            sink.Write(Entry("一"));
            sink.Written -= Handler;
            sink.Write(Entry("二"));

            Assert.Equal(1, count, "退订后写入的那条不该再通知");
        });

        Test("实时日志：订阅者抛异常不会带走写入方", () =>
        {
            // Deliberate in the sink: a throwing handler must not turn logging into a crash, and must not
            // stop the entry being stored or the next handler being called.
            var sink = new RingBufferLogSink(10);
            var reached = false;
            sink.Written += _ => throw new InvalidOperationException("订阅者炸了");
            sink.Written += _ => reached = true;

            sink.Write(Entry("一"));

            Assert.Equal(1, sink.Snapshot().Count, "抛异常也要把日志存下来");
            Assert.True(reached, "前一个订阅者抛异常不应挡住后一个");
        });

        Test("实时日志：没有订阅者时写入照样正常", () =>
        {
            var sink = new RingBufferLogSink(10);
            sink.Write(Entry("一"));
            Assert.Equal(1, sink.Snapshot().Count);
        });
    }

    // ---- 单条日志的文本 --------------------------------------------------------

    private static void RegisterEntryText()
    {
        Test("日志文本：级别补齐到五格，复制出来能对齐", () =>
        {
            var text = new LogEntry(
                new DateTimeOffset(2026, 8, 23, 7, 30, 15, 250, TimeSpan.FromHours(8)),
                LogLevel.Warn,
                "playback",
                "开始播放",
                null).ToString();

            Assert.Contains("[WARN ] playback: 开始播放", text);
            Assert.DoesNotContain(Environment.NewLine, text, "没有 Detail 就不该多出一行");
        });

        Test("日志文本：有 Detail 时另起一行", () =>
        {
            var text = new LogEntry(DateTimeOffset.Now, LogLevel.Error, "mpv", "启动失败", "找不到 libmpv-2.dll")
                .ToString();

            Assert.Contains("[ERROR] mpv: 启动失败", text);
            Assert.Contains(Environment.NewLine + "找不到 libmpv-2.dll", text);
        });
    }

    private static LogEntry Entry(string message) =>
        new(DateTimeOffset.Now, LogLevel.Info, Category, message, null);
}
