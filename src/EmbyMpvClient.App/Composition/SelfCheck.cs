using System.Text;
using EmbyMpvClient.App.Views;
using EmbyMpvClient.Diagnostics;
using EmbyMpvClient.Emby;
using EmbyMpvClient.Infrastructure;
using EmbyMpvClient.Playback;

namespace EmbyMpvClient.App.Composition;

/// <summary>
/// The <c>--self-check</c> mode: builds the composition root, then builds, lays out and paints every
/// page once, and writes a report to <c>logs\selfcheck.txt</c>.
/// <para>
/// It exists because the two failure modes this client actually has cannot be caught by the compiler
/// or by the unit tests: a WinForms control that throws from its constructor or its paint handler
/// (nothing references it until the user opens that page), and a configured path or shader group name
/// that no longer matches the machine. The window is never shown, so a full check costs about a
/// second and can run before a release or after editing mpv.conf.
/// </para>
/// </summary>
internal static class SelfCheck
{
    private const string Category = "自检";

    public static int Run(AppPaths paths, RingBufferLogSink logBuffer, bool dumpUi)
    {
        var report = new Report(paths);
        var dump = dumpUi ? Path.Combine(paths.LogDirectory, "ui") : null;

        try
        {
            // Deliberately never disposed: AppHost.Dispose saves settings, and a save rotates
            // settings.backup.json. A diagnostic must not rewrite what it is inspecting.
            var host = AppHost.Create();

            report.Note($"{AppInfo.TitleWithVersion} 自检 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.Note($"Windows {Environment.OSVersion.Version} · 数据目录 {paths.Root}");

            CheckEnvironment(report, host);
            CheckPages(report, host, logBuffer, dump);
        }
        catch (Exception error)
        {
            report.Probe("自检本身能跑起来", () => throw error);
        }

        return report.Finish();
    }

    /// <summary>Paths and names that live outside the build and can rot without anyone noticing.</summary>
    private static void CheckEnvironment(Report report, AppHost host)
    {
        var mpv = host.Settings.Mpv;
        var shaders = host.Settings.Shaders;

        report.Probe("mpv 可执行文件", () => host.Playback.Validate());
        report.Probe("mpv.conf", () => File.Exists(host.MpvConfigPath) ? null : $"找不到 {host.MpvConfigPath}");
        report.Probe("input.conf", () => File.Exists(host.MpvInputConfigPath) ? null : $"找不到 {host.MpvInputConfigPath}");
        report.Probe("着色器包", () => File.Exists(host.ShaderPackPath) ? null : $"找不到 {host.ShaderPackPath}");

        report.Probe("日志目录可写", () =>
        {
            var probe = Path.Combine(host.Paths.LogDirectory, "selfcheck.probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return null;
        });

        report.Probe("着色器配置组", () => host.ShaderCatalog.Count > 0 ? null : "一个也没扫到");
        report.Note($"       共 {host.ShaderCatalog.Count} 个：{string.Join("、", host.ShaderCatalog.Select(profile => profile.Name))}");

        // A profile named in settings but missing from the config files means mpv is asked for a
        // group it does not have, and mpv silently ignores it — no shaders, no error, no clue.
        foreach (var (label, name) in new[]
                 {
                     ("默认", shaders.DefaultProfile),
                     ("动画", shaders.AnimeProfile),
                     ("高分辨率", shaders.HighResProfile)
                 })
        {
            report.Probe($"设置里的{label}配置组", () =>
                string.IsNullOrWhiteSpace(name) || host.ShaderCatalog.Any(profile => profile.Name == name)
                    ? null
                    : $"设置指向 [{name}]，但配置文件里没有这个配置组");
        }

        report.Note($"       全部视频套用={shaders.ApplyToAllVideos} 动画自动切换={shaders.AutoAnimeProfile} 高分辨率阈值={shaders.HighResThresholdHeight}p");
        report.Probe("服务器地址", () =>
            host.Settings.Servers.Count == 0 ? "还没有配置服务器（首次运行属于正常）" : null);
        report.Note($"       已记住 {host.Settings.Servers.Count} 个服务器、{host.Settings.Servers.Sum(server => server.Accounts.Count)} 个账号");
    }

    /// <summary>
    /// Builds every page against the real host, lays it out and paints it. The paint is the point:
    /// <c>OnPaint</c> is where this UI does its work, and a page nobody has opened yet has never had
    /// its paint code run. With <c>--dump-ui</c> the window bitmaps are kept, which is the only way to
    /// look at a page's layout without clicking through the app.
    /// <para>
    /// The window is shown far off-screen rather than left unshown: an unshown form never creates its
    /// children's handles, so every screenshot would come out as an empty background. Off to the left
    /// and not also upwards, because <see cref="CheckPlayerBar"/> follows: the bar's menus size
    /// themselves to the room above their anchor, and an anchor parked above every monitor leaves room
    /// for exactly one row, so they would be painted at a height the user never sees.
    /// </para>
    /// </summary>
    private static void CheckPages(Report report, AppHost host, RingBufferLogSink logBuffer, string? dumpDirectory)
    {
        using var window = new MainForm(host, logBuffer, selfCheck: true);

        report.Probe("主窗口", () =>
        {
            window.StartPosition = FormStartPosition.Manual;
            window.WindowState = FormWindowState.Normal;
            window.ShowInTaskbar = false;
            window.Size = new Size(1440, 900);
            window.Location = new Point(-4000, 0);
            window.Show();
            return window.IsHandleCreated ? null : "窗口句柄创建失败";
        });

        report.Probe("页面 login", () => Render(window, window.BuildLogin(), dumpDirectory, "login"));

        window.ShowChromeForSelfCheck("自检");
        foreach (var key in MainForm.PageKeys)
            report.Probe($"页面 {key}", () => Render(window, window.BuildPage(key), dumpDirectory, key));

        CheckPlayerBar(report, window, host, dumpDirectory);

        if (dumpDirectory is not null) report.Note($"       页面截图已写入 {dumpDirectory}");
    }

    private static string? Render(MainForm window, AppView page, string? dumpDirectory, string key)
    {
        // One visible page at a time: two visible Dock.Fill siblings would fight over the same
        // rectangle and the second would be laid out into nothing.
        page.Visible = true;
        try
        {
            page.BringToFront();
            window.PerformLayout();
            page.PerformLayout();

            // Lets the pending WM_PAINT run, so a paint handler that throws is caught here rather
            // than on the user's first visit to the page.
            Application.DoEvents();

            if (page.Width <= 0 || page.Height <= 0) return $"页面尺寸异常（{page.Width}×{page.Height}）";

            using var bitmap = new Bitmap(window.Width, window.Height);
            window.DrawToBitmap(bitmap, new Rectangle(0, 0, window.Width, window.Height));

            if (dumpDirectory is not null)
            {
                Directory.CreateDirectory(dumpDirectory);
                bitmap.Save(Path.Combine(dumpDirectory, $"{key}.png"), System.Drawing.Imaging.ImageFormat.Png);
            }

            return null;
        }
        finally
        {
            page.Visible = false;
        }
    }

    /// <summary>
    /// Paints the transport bar and every panel it can open. They are top-level windows owned by the
    /// shell, so <see cref="CheckPages"/> never reaches them, and nothing else runs their paint code
    /// without a real playback session — the one part of this UI a user only ever sees over video.
    /// <para>
    /// The sample episodes carry chapters with no image tag on purpose: the chapter ticks and the
    /// hover bubble both get painted, while the bubble asks the server for no still, so the check
    /// stays offline.
    /// </para>
    /// </summary>
    private static void CheckPlayerBar(Report report, MainForm window, AppHost host, string? dumpDirectory)
    {
        IReadOnlyList<EmbyItem> episodes =
        [
            SampleEpisode(1, "自检 · 第一集"),
            SampleEpisode(2, "自检 · 第二集"),
            SampleEpisode(3, "自检 · 第三集")
        ];

        var bar = window.ShowPlayerBarForSelfCheck();

        report.Probe("播放控制栏", () =>
        {
            bar.Begin(episodes[1], episodes, host.ShaderCatalog, host.ShaderCatalog.FirstOrDefault());
            bar.SetTracks(SampleTracks);
            bar.SetFullscreen(false);
            bar.Update(new PlayerStatus
            {
                Loaded = true,
                Position = 723,
                Duration = 1500,
                CacheEnd = 870,
                Volume = 70
            });

            return Snapshot(bar, dumpDirectory, "player-bar");
        });

        foreach (var (key, open) in bar.SelfCheckPanels())
            report.Probe($"控制栏面板 {key}", () => Snapshot(open(), dumpDirectory, $"player-{key}"));

        bar.End();
    }

    private static EmbyItem SampleEpisode(int number, string name) => new()
    {
        Id = $"selfcheck-{number}",
        Name = name,
        Type = EmbyItemType.Episode,
        SeriesName = "自检剧集",
        ParentIndexNumber = 1,
        IndexNumber = number,
        RunTimeTicks = TimeSpan.FromMinutes(25).Ticks,
        Chapters =
        [
            new ChapterInfo { StartPositionTicks = 0, Name = "片头" },
            new ChapterInfo { StartPositionTicks = TimeSpan.FromSeconds(90).Ticks, Name = "正片" },
            new ChapterInfo { StartPositionTicks = TimeSpan.FromMinutes(23).Ticks, Name = "片尾" }
        ]
    };

    private static readonly IReadOnlyList<MpvTrack> SampleTracks =
    [
        new(1, "audio", "日语", "Japanese", true, true),
        new(2, "audio", "国语", "Mandarin", false, false),
        new(1, "sub", "简体中文", null, true, true),
        new(2, "sub", "English", null, false, false)
    ];

    /// <summary>
    /// Paints one of the floating player windows. Unlike <see cref="Render"/> this deliberately does
    /// not pump the message loop: a pump lets the video surface's reveal poll run, and with nothing
    /// playing it hides the bar again — taking every panel with it. <c>DrawToBitmap</c> paints
    /// through the window procedure on the spot, so there is nothing to wait for.
    /// </summary>
    private static string? Snapshot(Form floating, string? dumpDirectory, string key)
    {
        if (!floating.Visible) return "窗口没有显示出来";
        if (floating.Width <= 0 || floating.Height <= 0) return $"窗口尺寸异常（{floating.Width}×{floating.Height}）";

        using var bitmap = new Bitmap(floating.Width, floating.Height);
        floating.DrawToBitmap(bitmap, new Rectangle(0, 0, floating.Width, floating.Height));

        if (dumpDirectory is not null)
        {
            Directory.CreateDirectory(dumpDirectory);
            bitmap.Save(Path.Combine(dumpDirectory, $"{key}.png"), System.Drawing.Imaging.ImageFormat.Png);
        }

        return null;
    }

    private sealed class Report(AppPaths paths)
    {
        private readonly StringBuilder _text = new();
        private int _failures;

        public void Note(string line)
        {
            _text.AppendLine(line);
            Log.Info(Category, line);
        }

        public void Probe(string name, Func<string?> check)
        {
            try
            {
                if (check() is { } problem) Fail(name, problem);
                else Note($"[通过] {name}");
            }
            catch (Exception error)
            {
                Fail(name, $"{error.GetType().Name}：{error.Message}");
                Log.Error(Category, $"{name} 抛出异常", error);
            }
        }

        public int Finish()
        {
            var summary = _failures == 0 ? "自检全部通过" : $"自检发现 {_failures} 个问题";
            _text.AppendLine(summary);
            Log.Info(Category, summary);

            var file = Path.Combine(paths.LogDirectory, "selfcheck.txt");
            try
            {
                Directory.CreateDirectory(paths.LogDirectory);
                File.WriteAllText(file, _text.ToString(), new UTF8Encoding(false));
            }
            catch (Exception error)
            {
                // The console is not attached in a WinExe, so the file is the only report there is;
                // losing it is worth a log line of its own.
                Log.Warn(Category, $"写自检报告 {file} 失败", error);
            }

            return _failures == 0 ? 0 : 1;
        }

        private void Fail(string name, string problem)
        {
            _failures++;
            _text.AppendLine($"[失败] {name} —— {problem}");
            Log.Error(Category, $"{name} 失败：{problem}");
        }
    }
}
