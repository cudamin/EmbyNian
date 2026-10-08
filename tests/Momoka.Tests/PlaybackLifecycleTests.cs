using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Momoka.Configuration;
using Momoka.Emby;
using Momoka.Infrastructure;
using Momoka.Mpv;
using Momoka.Playback;
using static Momoka.Tests.TestHarness;

namespace Momoka.Tests;

internal static class PlaybackLifecycleTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    public static void Register()
    {
        Case("播放所有权：预取消的新请求不能停止正在播放的内容", async () =>
        {
            using var fixture = new Fixture();
            var first = new Handle();
            fixture.Backend.Next = (_, _) => Task.FromResult<IPlaybackHandle>(first);
            var playing = fixture.Service.PlayAsync(Ticket(), None);
            await first.Started.Task;
            try
            {
                await Cancelled(fixture.Service.PlayAsync(Ticket("other"), new CancellationToken(true)));
                Assert.Equal(0, first.StopCount);
                Assert.True(fixture.Service.IsPlaying);
            }
            finally { first.End(); await playing; }
        });

        Case("播放所有权：启动中停止撤销后端令牌，迟到句柄不宣布开播", async () =>
        {
            using var fixture = new Fixture();
            var entered = Signal();
            var release = Signal();
            var handle = new Handle();
            CancellationToken startToken = default;
            fixture.Backend.Next = async (_, token) =>
            {
                startToken = token;
                entered.TrySetResult();
                await release.Task;
                return handle;
            };
            var playing = fixture.Service.PlayAsync(Ticket(), None);
            await entered.Task;
            var stopping = fixture.Service.StopAsync();
            var cancelled = startToken.IsCancellationRequested;
            release.TrySetResult();
            handle.End();
            await Observe(playing);
            await stopping;
            Assert.True(cancelled, "已有启动操作也必须接到停止，不只撤销闸门里的票号");
            Assert.Equal(0, fixture.Transport.Requests.Count(request => request.Path.EndsWith("/Sessions/Playing", StringComparison.Ordinal)));
            Assert.Equal(1, handle.DisposeCount);
        });

        Case("播放所有权：新请求撤销尚未建立的旧后端", async () =>
        {
            using var fixture = new Fixture();
            var entered = Signal();
            var release = Signal();
            var first = new Handle();
            var second = new Handle();
            CancellationToken firstToken = default;
            fixture.Backend.Next = async (request, token) =>
            {
                if (request.ItemId != "first") return second;
                firstToken = token;
                entered.TrySetResult();
                await release.Task;
                return first;
            };
            var old = fixture.Service.PlayAsync(Ticket("first"), None);
            await entered.Task;
            var latest = fixture.Service.PlayAsync(Ticket("second"), None);
            var cancelled = firstToken.IsCancellationRequested;
            release.TrySetResult();
            first.End();
            second.End();
            await Observe(old);
            await latest;
            Assert.True(cancelled);
            Assert.Equal(1, first.DisposeCount);
            Assert.Equal(1, second.DisposeCount);
            Assert.False(fixture.Transport.Requests.Any(request => request.IsStart && request.ItemId == "first"));
        });

        Case("播放所有权：停止等待最终上报完成而不是只等句柄", async () =>
        {
            using var fixture = new Fixture();
            var entered = Signal();
            var release = Signal();
            fixture.Transport.Reply = async (request, _) =>
            {
                if (request.IsStop) { entered.TrySetResult(); await release.Task; }
                return Json("{}");
            };
            var handle = new Handle();
            fixture.Backend.Next = (_, _) => Task.FromResult<IPlaybackHandle>(handle);
            var playing = fixture.Service.PlayAsync(Ticket(), None);
            await handle.Started.Task;
            var stopping = fixture.Service.StopAsync();
            await entered.Task;
            var premature = stopping.IsCompleted;
            release.TrySetResult();
            await Task.WhenAll(stopping, playing);
            Assert.False(premature, "session只有在停止任务完成后才可以释放");
            Assert.Equal(1, handle.DisposeCount);
        });

        Case("播放上报：到阈值停止在飞时结束不再发第二个停止", async () =>
        {
            using var fixture = new Fixture();
            fixture.Settings.Playback.StopReportEnabled = true;
            var entered = Signal();
            var release = Signal();
            fixture.Transport.Reply = async (request, _) =>
            {
                if (request.IsStop && fixture.Transport.Requests.Count(value => value.IsStop) == 1)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
                return Json("{}");
            };
            var handle = new Handle();
            fixture.Backend.Next = (_, _) => Task.FromResult<IPlaybackHandle>(handle);
            var playing = fixture.Service.PlayAsync(Ticket(), None);
            await handle.Listening.Task;
            handle.Publish(95);
            await entered.Task;
            handle.End(95);
            await Task.WhenAny(playing, Task.Delay(100));
            release.TrySetResult();
            await playing;
            Assert.Equal(1, fixture.Transport.Requests.Count(request => request.IsStop));
        });

        Case("播放上报：暂停读数迟到不能在停止通知后重新点亮会话", async () =>
        {
            using var fixture = new Fixture();
            fixture.Settings.Playback.StopReportEnabled = true;
            var position = new TaskCompletionSource<double?>();
            var entered = Signal();
            var handle = new Handle
            {
                ReadPosition = _ => { entered.TrySetResult(); return position.Task; }
            };
            fixture.Backend.Next = (_, _) => Task.FromResult<IPlaybackHandle>(handle);
            var playing = fixture.Service.PlayAsync(Ticket(), None);
            await handle.Listening.Task;
            handle.Pause(true);
            await entered.Task;
            handle.Publish(95);
            position.SetResult(5);
            handle.End(95);
            await playing;
            var requests = fixture.Transport.Requests.ToArray();
            var stopped = Array.FindIndex(requests, request => request.IsStop);
            Assert.True(stopped >= 0);
            Assert.False(requests.Skip(stopped + 1).Any(request => request.IsProgress));
        });

        Case("播放上报：未知暂停位置不能当作零上报", async () =>
        {
            using var fixture = new Fixture();
            var handle = new Handle { ReadPosition = _ => Task.FromResult<double?>(null) };
            fixture.Backend.Next = (_, _) => Task.FromResult<IPlaybackHandle>(handle);
            var playing = fixture.Service.PlayAsync(Ticket(), None);
            await handle.Listening.Task;
            handle.Pause(true);
            handle.End();
            await playing;
            Assert.Equal(0, fixture.Transport.Requests.Count(request => request.IsProgress));
        });

        Case("播放上报：注销取消已发出的暂停请求", async () =>
        {
            using var fixture = new Fixture();
            var entered = Signal();
            var release = Signal();
            CancellationToken reportToken = default;
            fixture.Transport.Reply = async (request, token) =>
            {
                if (request.IsProgress)
                {
                    reportToken = token;
                    entered.TrySetResult();
                    await release.Task;
                }
                return Json("{}");
            };
            var handle = new Handle { Position = 10 };
            fixture.Backend.Next = (_, _) => Task.FromResult<IPlaybackHandle>(handle);
            var playing = fixture.Service.PlayAsync(Ticket(), None);
            await handle.Listening.Task;
            handle.Pause(true);
            await entered.Task;
            fixture.Session.SignOut();
            var cancelled = reportToken.IsCancellationRequested;
            release.TrySetResult();
            handle.End();
            await playing;
            Assert.True(cancelled, "scope的取消令牌必须传到HTTP调用");
        });

        Case("播放进度：从续播点拖回零后停止保存零", async () =>
        {
            using var fixture = new Fixture();
            var handle = new Handle();
            fixture.Backend.Next = (_, _) => Task.FromResult<IPlaybackHandle>(handle);
            var playing = fixture.Service.PlayAsync(Ticket() with { StartTicks = TimeSpan.TicksPerSecond * 50 }, None);
            await handle.Started.Task;
            handle.End(0);
            var result = await playing;
            Assert.Equal(0L, result.PositionTicks);
            Assert.Equal(0L, fixture.Transport.Requests.Single(request => request.IsStop).PositionTicks);
        });

        Case("播放进度：开始上报使用规划后的快退位置", async () =>
        {
            using var fixture = new Fixture();
            fixture.Settings.Playback.ResumeRewindSeconds = 5;
            var handle = new Handle();
            fixture.Backend.Next = (_, _) => Task.FromResult<IPlaybackHandle>(handle);
            var playing = fixture.Service.PlayAsync(Ticket() with { StartTicks = TimeSpan.TicksPerSecond * 20 }, None);
            await handle.Started.Task;
            handle.End(15);
            await playing;
            Assert.Equal(TimeSpan.TicksPerSecond * 15, fixture.Transport.Requests.Single(request => request.IsStart).PositionTicks);
        });

        Case("播放进度：标记已看失败不能向界面宣称已标记", async () =>
        {
            using var fixture = new Fixture();
            fixture.Transport.Reply = (request, _) => Task.FromResult(request.Path.Contains("/PlayedItems/", StringComparison.Ordinal)
                ? Json("{}", HttpStatusCode.InternalServerError) : Json("{}"));
            var handle = new Handle();
            fixture.Backend.Next = (_, _) => Task.FromResult<IPlaybackHandle>(handle);
            var playing = fixture.Service.PlayAsync(Ticket(), None);
            await handle.Started.Task;
            handle.End(100, PlaybackEndReason.EndOfFile);
            var result = await playing;
            Assert.False(result.MarkedWatched);
            Assert.DoesNotContain("已标记", result.ToChinese());
        });

        Test("播放候选：无编号的不同源保留，其余编号也只尝试一次", () =>
        {
            var first = Source(null);
            var second = Source(null);
            var known = Source("known");
            var repeated = Source("known");
            var item = new EmbyItem { Id = "item", MediaSources = [first, second, known, repeated, second] };
            var candidates = PlaybackService.CandidateSources(new PlaybackTicket { Item = item, Source = first });
            Assert.Equal(3, candidates.Count);
            Assert.True(ReferenceEquals(first, candidates[0]));
            Assert.True(ReferenceEquals(second, candidates[1]));
            Assert.True(ReferenceEquals(known, candidates[2]));
        });

        Case("播放所有权：交接等待被取消仍释放遗留实例", async () =>
        {
            using var fixture = new Fixture();
            var entered = Signal();
            var release = Signal();
            fixture.Transport.Reply = async (request, _) =>
            {
                if (request.IsStop) { entered.TrySetResult(); await release.Task; }
                return Json("{}");
            };
            var handle = new Handle { Swappable = true };
            fixture.Backend.Next = (_, _) => Task.FromResult<IPlaybackHandle>(handle);
            var first = fixture.Service.PlayAsync(Ticket(), None);
            await handle.Started.Task;
            using var cancellation = new CancellationTokenSource();
            var next = fixture.Service.PlayAsync(Ticket("next"), cancellation.Token);
            await entered.Task;
            cancellation.Cancel();
            release.TrySetResult();
            await first;
            await Cancelled(next);
            Assert.False(fixture.Service.IsPlaying);
            Assert.Equal(1, handle.DisposeCount);
            Assert.Equal(0, handle.SwapCount);
        });

        Case("播放事件：退订前已捕获的旧状态委托不得进入下一场", async () =>
        {
            using var fixture = new Fixture();
            var first = new Handle();
            var second = new Handle();
            fixture.Backend.Next = (request, _) => Task.FromResult<IPlaybackHandle>(request.ItemId == "first" ? first : second);
            var old = fixture.Service.PlayAsync(Ticket("first"), None);
            await first.Listening.Task;
            var delayed = first.CaptureStatus();
            first.End();
            await old;
            var latest = fixture.Service.PlayAsync(Ticket("second"), None);
            await second.Started.Task;
            var received = 0;
            fixture.Service.StatusChanged += _ => received++;
            delayed(new PlayerStatus { Position = 99 });
            second.End();
            await latest;
            Assert.Equal(0, received);
        });

        Case("播放上报：正在发送的进度完成后才发最终停止", async () =>
        {
            using var fixture = new Fixture();
            var entered = Signal();
            var release = Signal();
            fixture.Transport.Reply = async (request, _) =>
            {
                if (request.IsProgress) { entered.TrySetResult(); await release.Task; }
                return Json("{}");
            };
            var handle = new Handle { Position = 10 };
            fixture.Backend.Next = (_, _) => Task.FromResult<IPlaybackHandle>(handle);
            var playing = fixture.Service.PlayAsync(Ticket(), None);
            await handle.Listening.Task;
            handle.Pause(true);
            await entered.Task;
            handle.End(10);
            await Task.Delay(50);
            var stopsBeforeProgressFinished = fixture.Transport.Requests.Count(request => request.IsStop);
            release.TrySetResult();
            await playing;
            Assert.Equal(0, stopsBeforeProgressFinished);
            Assert.Equal(1, fixture.Transport.Requests.Count(request => request.IsStop));
        });

        Case("播放所有权：监视任务异常也停止句柄并完成最终上报", async () =>
        {
            using var fixture = new Fixture();
            var handle = new Handle();
            fixture.Backend.Next = (_, _) => Task.FromResult<IPlaybackHandle>(handle);
            var playing = fixture.Service.PlayAsync(Ticket(), None);
            await handle.Started.Task;
            handle.FailExit();
            var result = await playing;
            Assert.Equal(PlaybackEndReason.Error, result.Exit.Reason);
            Assert.Equal(1, handle.StopCount);
            Assert.Equal(1, handle.DisposeCount);
            Assert.Equal(1, fixture.Transport.Requests.Count(request => request.IsStop));
        });

        Case("播放所有权：同句柄成功交接后只有新播放仍能发布状态", async () =>
        {
            using var fixture = new Fixture();
            var handle = new Handle { Swappable = true };
            fixture.Backend.Next = (_, _) => Task.FromResult<IPlaybackHandle>(handle);
            var old = fixture.Service.PlayAsync(Ticket("old"), None);
            await handle.Listening.Task;
            var delayed = handle.CaptureStatus();
            var next = fixture.Service.PlayAsync(Ticket("next"), None);
            await old;
            await handle.Swapped.Task;
            await Until(() => fixture.Transport.Requests.Count(request => request.IsStart) == 2);
            var events = new List<long>();
            fixture.Service.StatusUpdated += update => events.Add(update.Generation);
            delayed(new PlayerStatus { Position = 70 });
            handle.Publish(3);
            Assert.Equal(1, events.Count);
            Assert.Equal(fixture.Service.Generation, events[0]);
            Assert.Equal(1, fixture.Backend.Starts.Count);
            handle.End(3);
            await next;
            Assert.Equal(1, handle.DisposeCount);
        });

        Case("播放所有权：后端改为外部时不接受内置句柄接管", async () =>
        {
            using var fixture = new Fixture();
            var first = new Handle { Swappable = true };
            var second = new Handle();
            fixture.Backend.Next = (request, _) => Task.FromResult<IPlaybackHandle>(request.ItemId == "old" ? first : second);
            var old = fixture.Service.PlayAsync(Ticket("old"), None);
            await first.Started.Task;
            fixture.Settings.Mpv.Backend = MpvBackendKind.ExternalMpv;
            var next = fixture.Service.PlayAsync(Ticket("next"), None);
            await second.Started.Task;
            Assert.Equal(0, first.SwapCount);
            Assert.Equal(MpvBackendKind.ExternalMpv, fixture.Service.PlayingBackend);
            Assert.Equal(2, fixture.Backend.Starts.Count);
            second.End();
            await Task.WhenAll(old, next);
        });

        Case("播放候选：已经观看后失败不从原续播点重放其他版本", async () =>
        {
            using var fixture = new Fixture();
            var first = new Handle { Position = 20 };
            var second = new Handle();
            fixture.Backend.Next = (request, _) => Task.FromResult<IPlaybackHandle>(request.MediaSourceId == "first" ? first : second);
            var ticket = Ticket();
            ticket.Item.MediaSources.Add(Source("second"));
            var playing = fixture.Service.PlayAsync(ticket, None);
            await first.Started.Task;
            first.Publish(20);
            second.End(20);
            first.End(20, PlaybackEndReason.Error);
            var result = await playing;
            Assert.Equal(1, fixture.Backend.Starts.Count);
            Assert.Equal(PlaybackEndReason.Error, result.Exit.Reason);
        });
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var n = 0; n < 500; n++)
        {
            if (condition()) return;
            await Task.Delay(5);
        }
        throw new TimeoutException("等待播放交接");
    }

    private static void Case(string name, Func<Task> run) =>
        Test(name, () => run().WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult());

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Cancelled(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { return; }
        throw new AssertionException("期望取消，而不是成功起播");
    }

    private static async Task Observe(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    internal static PlaybackTicket Ticket(string id = "item")
    {
        var source = Source("first");
        var item = new EmbyItem { Id = id, Name = id, Type = "Movie", MediaSources = [source] };
        return new PlaybackTicket { Item = item, Source = source };
    }

    private static MediaSource Source(string? id) => new()
    {
        Id = id ?? "",
        Container = "mkv",
        RunTimeTicks = TimeSpan.TicksPerSecond * 100
    };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    internal sealed class Fixture : IDisposable
    {
        public AppSettings Settings { get; } = new();
        public Transport Transport { get; } = new();
        public Backend Backend { get; } = new();
        public EmbySession Session { get; }
        public PlaybackService Service { get; }

        public Fixture()
        {
            Settings.Playback.ReportProgressToServer = true;
            Settings.Playback.ProgressReportIntervalSeconds = 60;
            Settings.Playback.ResumeRewindSeconds = 0;
            Settings.Playback.SubtitleAssOverride = "";
            Session = new EmbySession(Settings,
                new SettingsStore(new AppPaths(Path.Combine(Path.GetTempPath(), "stage4-" + Guid.NewGuid().ToString("N"))),
                    PassthroughSecretProtector.Instance),
                new CredentialVault(PassthroughSecretProtector.Instance), DeviceIdentity.Create("offline", "1"), Transport);
            var account = new AccountProfile { UserId = "a", Username = "a", ProtectedAccessToken = "offline-a" };
            Assert.True(Session.TryRestoreAsync(new ServerProfile { Url = "https://a.invalid" }, account, None).GetAwaiter().GetResult());
            Service = new PlaybackService(Session, Settings, () => Backend,
                new PlaybackPlanner(Settings, new ShaderGroupResolver(Settings.Shaders)));
        }

        public void Dispose() => Session.Dispose();
    }

    internal sealed record Sent(string Path, string Body)
    {
        public bool IsStart => Path.EndsWith("/Sessions/Playing", StringComparison.Ordinal);
        public bool IsStop => Path.EndsWith("/Sessions/Playing/Stopped", StringComparison.Ordinal);
        public bool IsProgress => Path.EndsWith("/Sessions/Playing/Progress", StringComparison.Ordinal);
        public string ItemId => Field("ItemId").GetString() ?? "";
        public long PositionTicks => Field("PositionTicks").GetInt64();
        private JsonElement Field(string name)
        {
            using var document = JsonDocument.Parse(Body);
            return document.RootElement.GetProperty(name).Clone();
        }
    }

    internal sealed class Transport : HttpMessageHandler
    {
        public ConcurrentQueue<Sent> Requests { get; } = new();
        public Func<Sent, CancellationToken, Task<HttpResponseMessage>> Reply { get; set; } =
            (_, _) => Task.FromResult(Json("{}"));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal)) return Json("{\"Items\":[]}");
            if (request.RequestUri.AbsolutePath.EndsWith("/System/Info/Public", StringComparison.Ordinal)) return Json("{}");
            var sent = new Sent(request.RequestUri.AbsolutePath,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Enqueue(sent);
            return await Reply(sent, cancellationToken);
        }
    }

    internal sealed class Backend : IPlaybackBackend
    {
        public string DisplayName => "离线会话";
        public Func<PlaybackRequest, CancellationToken, Task<IPlaybackHandle>> Next { get; set; } =
            (_, _) => throw new InvalidOperationException("未安排假句柄");
        public ConcurrentQueue<PlaybackRequest> Starts { get; } = new();
        public string? Validate() => null;
        public Task<IPlaybackHandle> StartAsync(PlaybackRequest request, CancellationToken cancellationToken)
        {
            Starts.Enqueue(request);
            return Next(request, cancellationToken);
        }
    }

    internal sealed class Handle : IPlaybackHandle, IPlayerControl
    {
        private TaskCompletionSource<PlaybackExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<PlayerStatus>? _statusChanged;
        public TaskCompletionSource Started { get; } = Signal();
        public TaskCompletionSource Listening { get; } = Signal();
        public TaskCompletionSource Swapped { get; } = Signal();
        public bool HasControlChannel => true;
        public bool IsPaused { get; private set; }
        public bool Swappable { get; init; }
        public bool WasHandedOver { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int SwapCount { get; private set; }
        public double? Position { get; set; } = 0;
        public Func<CancellationToken, Task<double?>>? ReadPosition { get; init; }
        public PlayerStatus Status { get; private set; } = new();
        public event Action<bool>? PauseChanged;
        public event Action<IReadOnlyList<MpvTrack>>? TracksChanged { add { } remove { } }
        public event Action<PlayerStatus>? StatusChanged
        {
            add { _statusChanged += value; if (Started.Task.IsCompleted) Listening.TrySetResult(); }
            remove { _statusChanged -= value; }
        }

        public Action<PlayerStatus> CaptureStatus() => _statusChanged ?? (_ => { });
        public void Publish(double position)
        {
            Status = new PlayerStatus { Position = position, Duration = 100, Loaded = true, PictureStarted = true };
            _statusChanged?.Invoke(Status);
        }
        public void Pause(bool paused) { IsPaused = paused; PauseChanged?.Invoke(paused); }
        public void FailExit() => _exit.TrySetException(new IOException("synthetic monitor failure"));
        public void End(double? position = null, PlaybackEndReason reason = PlaybackEndReason.Stopped) =>
            _exit.TrySetResult(new PlaybackExit(reason, position, 0, reason == PlaybackEndReason.Error ? "synthetic" : null));
        public Task<PlaybackExit> WaitForExitAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return _exit.Task.WaitAsync(cancellationToken);
        }
        public Task StopAsync() { StopCount++; End(Position); return Task.CompletedTask; }
        public bool CanSwapTo(PlaybackRequest request) => Swappable;
        public void HandOver() { WasHandedOver = true; End(Position); }
        public Task<bool> SwapToAsync(PlaybackRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SwapCount++;
            WasHandedOver = false;
            _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Swapped.TrySetResult();
            return Task.FromResult(true);
        }
        public Task<double?> GetPositionAsync(CancellationToken cancellationToken) =>
            ReadPosition?.Invoke(cancellationToken) ?? Task.FromResult(Position);
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
        public Task ShowMessageAsync(string text) => Task.CompletedTask;
        public Task SetPropertyAsync(string name, object? value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<MpvTrack>> GetTracksAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MpvTrack>>([]);
        public Task<double?> GetNumberAsync(string name, CancellationToken cancellationToken) => Task.FromResult<double?>(null);
        public Task<string?> GetTextAsync(string name, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<bool> CommandAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
