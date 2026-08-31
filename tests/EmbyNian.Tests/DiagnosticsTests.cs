using EmbyNian.Diagnostics;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// Covers the in-memory logging the diagnostics page is built on. The page itself needs a UI thread and
/// is checked by <c>--self-check</c> instead, but everything it reads from — the ring buffer's ordering,
/// its capacity and its <see cref="RingBufferLogSink.Written"/> event — is plain synchronous code and
/// belongs here, where a failure names the reason instead of showing up as an empty log panel.
/// <para>
/// The file sink is here for a different reason: it holds a handle open for the life of the process, and
/// the two things that buys — the tail readable before the writer closes, a new file after midnight —
/// are invisible until the day someone needs the log of a crash.
/// </para>
/// </summary>
internal static class DiagnosticsTests
{
    private const string Category = "测试";

    public static void Register()
    {
        RegisterRingBuffer();
        RegisterWrittenEvent();
        RegisterEntryText();
        RegisterFileSink();
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

    // ---- 落盘的日志 ------------------------------------------------------------

    private static void RegisterFileSink()
    {
        Test("磁盘日志：写进当天那个文件，一行一条", () => InTempDirectory(directory =>
        {
            var now = DateTimeOffset.Now;
            using (var sink = new FileLogSink(directory))
            {
                sink.Write(Entry("第一条", LogLevel.Info, now));
                sink.Write(Entry("第二条", LogLevel.Warn, now));
            }

            var lines = File.ReadAllLines(LogPath(directory, now));
            Assert.Equal(2, lines.Length);
            Assert.Contains("第一条", lines[0]);
            Assert.Contains("第二条", lines[1]);
        }));

        Test("磁盘日志：句柄还开着的时候就已经读得到", () => InTempDirectory(directory =>
        {
            var now = DateTimeOffset.Now;
            using var sink = new FileLogSink(directory);
            sink.Write(Entry("崩之前最后一句", LogLevel.Error, now));

            // 这一条钉的是这次改动的要害：句柄常开省掉了每行一次的开关文件，但日志的用处正在于进程被
            // 它自己在查的那个毛病带走时最后那几行还在盘上。所以必须是「写完立刻可读」，不能等关闭。
            Assert.Contains("崩之前最后一句", ReadWhileOpen(LogPath(directory, now)));
        }));

        Test("磁盘日志：低于门槛的那些不落盘", () => InTempDirectory(directory =>
        {
            var now = DateTimeOffset.Now;
            using (var sink = new FileLogSink(directory, LogLevel.Warn))
            {
                sink.Write(Entry("啰嗦话", LogLevel.Debug, now));
                sink.Write(Entry("平常话", LogLevel.Info, now));
                sink.Write(Entry("要紧话", LogLevel.Warn, now));
            }

            var text = File.ReadAllText(LogPath(directory, now));
            Assert.Contains("要紧话", text);
            Assert.DoesNotContain("啰嗦话", text);
            Assert.DoesNotContain("平常话", text);
        }));

        Test("磁盘日志：跨过午夜就换一个文件", () => InTempDirectory(directory =>
        {
            var now = DateTimeOffset.Now;
            var yesterday = now.AddDays(-1);
            using (var sink = new FileLogSink(directory))
            {
                sink.Write(Entry("昨天的", LogLevel.Info, yesterday));
                sink.Write(Entry("今天的", LogLevel.Info, now));
            }

            // 一直开着一个句柄的代价就是这个：换天时要真的换文件。写串了的话，通宵跑的那一遍会把第二天
            // 全部记进前一天的文件里。
            Assert.Contains("昨天的", File.ReadAllText(LogPath(directory, yesterday)));
            Assert.Contains("今天的", File.ReadAllText(LogPath(directory, now)));
        }));

        Test("磁盘日志：过期的旧日志开机时清掉", () => InTempDirectory(directory =>
        {
            Directory.CreateDirectory(directory);
            var stale = Path.Combine(directory, "app-20250101.log");
            var fresh = Path.Combine(directory, "app-20260830.log");
            File.WriteAllText(stale, "很久以前");
            File.WriteAllText(fresh, "前几天");
            File.SetLastWriteTime(stale, DateTime.Now.AddDays(-30));
            File.SetLastWriteTime(fresh, DateTime.Now.AddDays(-2));

            using var sink = new FileLogSink(directory, LogLevel.Debug, retainDays: 14);

            Assert.False(File.Exists(stale), "过了保留期的该删");
            Assert.True(File.Exists(fresh), "保留期内的不能连坐");
        }));
    }

    private static LogEntry Entry(string message) =>
        new(DateTimeOffset.Now, LogLevel.Info, Category, message, null);

    private static LogEntry Entry(string message, LogLevel level, DateTimeOffset when) =>
        new(when, level, Category, message, null);

    /// <summary>
    /// Runs a case against a directory of its own and takes it away afterwards. Every sink inside has to
    /// be disposed before the body returns, or the directory cannot be removed on Windows.
    /// </summary>
    private static void InTempDirectory(Action<string> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), "EmbyNian-日志-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            body(directory);
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string LogPath(string directory, DateTimeOffset when) =>
        Path.Combine(directory, $"app-{when.LocalDateTime:yyyyMMdd}.log");

    /// <summary>
    /// Reads a file the sink still has open. <see cref="FileShare.ReadWrite"/> is not optional here: a
    /// plain <see cref="File.ReadAllText"/> asks that nobody else be writing, and the writer inside is,
    /// so it would fail with a sharing violation. Anything that tails this log needs the same.
    /// </summary>
    private static string ReadWhileOpen(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
