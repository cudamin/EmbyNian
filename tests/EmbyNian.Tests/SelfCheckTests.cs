using System.Diagnostics;
using System.Text;
using EmbyNian.Configuration;
using EmbyNian.Infrastructure;
using EmbyNian.Playback;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

internal static class SelfCheckTests
{
    private static readonly ISecretProtector Protector = new NoSecretsProtector();
    private const string Saved = """{"SchemaVersion":22,"DeviceId":"fixture","Ui":{"WindowMaximized":true},"Servers":[{"Url":"https://selfcheck.invalid","Accounts":[{"ProtectedAccessToken":"opaque-placeholder","ProtectedPassword":"opaque-password-placeholder"}]}]}""";

    public static void Register()
    {
        Test("自检参数：原生/目录/等号/Windows别名均走独立入口", () =>
        {
            Assert.Null(StartupArgs.ValidateSelfCheck(["--self-check"]));
            Assert.Equal(@"C:\new run", StartupArgs.ValidateSelfCheck(
                ["/Self-Check", "--self-check-data=C:\\new run", "--dump-ui", "--screen", "2", "--theme=misty"]));
            Assert.Equal(@"C:\new", StartupArgs.ValidateSelfCheck(["--self-check", "--self-check-data", @"C:\new"]));
            foreach (var flag in new[] { "--self-check=true", "--self-check-data=", "/Self-Check", "-self-check-data" })
                Assert.True(StartupArgs.RequestsSelfCheck([flag]), "坏参数也不能落入迁移或激活");
            Assert.False(StartupArgs.RequestsSelfCheck(["--play"]));
        });

        Test("自检参数：所有导航/浮层/播放/探针和未知参数一律拒绝", () =>
        {
            foreach (var flag in new[] { "--play", "--probe-cursor", "--probe-player-motion", "--probe-composition",
                "--show-detail", "--show-episode", "--show-library", "--show-menu", "--show-settings", "--show-cover",
                "--show-osd", "--hide-cursor", "--scroll-end", "--scroll-half", "--maximized", "--future" })
            {
                Assert.Throws<ArgumentException>(() => StartupArgs.ValidateSelfCheck([flag, "--self-check"]));
                Assert.Throws<ArgumentException>(() => StartupArgs.ValidateSelfCheck(["--self-check", flag + "=x"]));
            }
        });

        Test("自检参数：缺值/重复/布尔等号/目录独用不能悄悄默认", () =>
        {
            foreach (var args in new string[][] {
                ["--self-check-data", @"C:\new"], ["--self-check=true"], ["--self-check", "--dump-ui=false"],
                ["--self-check", "--self-check-data"], ["--self-check", "--self-check-data="],
                ["--self-check", "--self-check-data", "--play"], ["--self-check", "--theme"],
                ["--self-check", "--screen=0"], ["--self-check", "--screen=x"],
                ["--self-check", "--screen=1", "--screen=2"], ["--self-check", "--self-check"],
                ["--self-check", "--theme=misty", "stray"] })
                Assert.Throws<ArgumentException>(() => StartupArgs.ValidateSelfCheck(args));
        });

        Test("自检：仅复制保存设置，生产设置备份缓存不变，副本非最大化", () => WithFixture((source, root) =>
        {
            var before = Snapshot(source.Root);
            var target = Path.Combine(root, "isolated");
            using (var run = SelfCheckRun.Create(source, target, Protector))
            {
                Assert.False(run.Settings.Ui.WindowMaximized);
                Assert.Equal(Saved, File.ReadAllText(run.Paths.SettingsFile));
                Assert.False(File.Exists(run.Paths.SettingsBackupFile));
                Assert.Equal(0, Directory.GetFiles(run.Paths.ImageCacheDirectory).Length);
                Assert.True(File.Exists(Path.Combine(run.Paths.LogDirectory, "selfcheck-isolation.txt")));
                new SettingsStore(run.Paths, Protector).Save(run.Settings); // emulate self-check/UI persistence
                Assert.Equal(before, Snapshot(source.Root));
            }
            Assert.True(Directory.Exists(target), "成功报告目录留下供复核");
        }));

        Test("自检：不指定目录也建立两个全新生产目录外的运行目录", () => WithFixture((source, root) =>
        {
            string first;
            using (var run = SelfCheckRun.Create(source, null, Protector))
            {
                first = run.Paths.Root;
                Assert.True(first.StartsWith(Path.Combine(root, "EmbyNian.SelfCheck") + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
                Assert.Equal(Saved, File.ReadAllText(run.Paths.SettingsFile));
            }
            using var next = SelfCheckRun.Create(source, null, Protector);
            Assert.False(first.Equals(next.Paths.Root, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(Saved, File.ReadAllText(source.SettingsFile));
        }));

        Test("自检：坏/空/非对象/旧明文设置一律红，不恢复生产备份", () => WithFixture((source, root) =>
        {
            foreach (var json in new[] { "{", "", "null", "[]", "{}", "{\"SchemaVersion\":1}", "{\"SchemaVersion\":999}" })
            {
                File.WriteAllText(source.SettingsFile, json);
                var before = Snapshot(source.Root);
                var target = Path.Combine(root, "failed");
                Assert.Throws<Exception>(() => SelfCheckRun.Create(source, target, Protector));
                Assert.False(Directory.Exists(target), "解析失败清理独立目录");
                Assert.Equal(before, Snapshot(source.Root), "不隔离/重命名源文件");
            }
            File.Delete(source.SettingsFile);
            Assert.Throws<FileNotFoundException>(() => SelfCheckRun.Create(source, Path.Combine(root, "missing"), Protector));
            Assert.False(Directory.Exists(Path.Combine(root, "missing")));
        }));

        Test("自检：复制锁定失败清理目录并释放mutex", () => WithFixture((source, root) =>
        {
            var target = Path.Combine(root, "copy-failed");
            using (var locked = new FileStream(source.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.Throws<IOException>(() => SelfCheckRun.Create(source, target, Protector));
            Assert.False(Directory.Exists(target));
            Assert.Equal(Saved, File.ReadAllText(source.SettingsFile));
            using var retry = SelfCheckRun.Create(source, target, Protector);
        }));

        Test("自检：只读源文件属性不变，坏副本仍能清理", () => WithFixture((source, root) =>
        {
            var original = File.GetAttributes(source.SettingsFile);
            try
            {
                File.SetAttributes(source.SettingsFile, original | FileAttributes.ReadOnly);
                using (var run = SelfCheckRun.Create(source, Path.Combine(root, "readonly-ok"), Protector))
                {
                    new SettingsStore(run.Paths, Protector).Save(run.Settings);
                    Assert.Equal(Saved, File.ReadAllText(source.SettingsFile));
                    Assert.True((File.GetAttributes(source.SettingsFile) & FileAttributes.ReadOnly) != 0);
                }
                File.SetAttributes(source.SettingsFile, original);
                File.WriteAllText(source.SettingsFile, "{");
                File.SetAttributes(source.SettingsFile, original | FileAttributes.ReadOnly);
                var failed = Path.Combine(root, "readonly-bad");
                Assert.Throws<Exception>(() => SelfCheckRun.Create(source, failed, Protector));
                Assert.False(Directory.Exists(failed));
                Assert.True((File.GetAttributes(source.SettingsFile) & FileAttributes.ReadOnly) != 0);
            }
            finally { File.SetAttributes(source.SettingsFile, original); }
        }));

        Test("自检：运行期间正常用户保存的新设置不会被退出还原", () => WithFixture((source, root) =>
        {
            using (var run = SelfCheckRun.Create(source, Path.Combine(root, "concurrent-save"), Protector))
            {
                File.WriteAllText(source.SettingsFile, "user saved later");
                File.WriteAllText(source.SettingsBackupFile, "user backup later");
                new SettingsStore(run.Paths, Protector).Save(run.Settings);
            }
            Assert.Equal("user saved later", File.ReadAllText(source.SettingsFile));
            Assert.Equal("user backup later", File.ReadAllText(source.SettingsBackupFile));
        }));

        Test("自检：拒绝既有目录文件、生产目录重叠、相对网络与设备路径", () => WithFixture((source, root) =>
        {
            var existing = Path.Combine(root, "existing");
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "keep"), "keep");
            foreach (var target in new[] { existing, Path.Combine(existing, "keep"), source.Root, root,
                Path.Combine(source.Root, "nested"), source.Root + ".\\nested", source.Root + " \\nested",
                "relative", @"\\server\share\new", @"\\?\C:\new", @"C:\new:stream" })
                Assert.Throws<IOException>(() => SelfCheckRun.Create(source, target, Protector));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(existing, "keep")));
            Assert.Equal(Saved, File.ReadAllText(source.SettingsFile));
        }));

        Test("自检：拒绝重解析祖先且不删除链接目标", () => WithFixture((source, root) =>
        {
            var real = Path.Combine(root, "real");
            var link = Path.Combine(root, "junction");
            Directory.CreateDirectory(real);
            var info = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode, "本地 junction 夹具必须创建成功：" + output);
            try
            {
                Assert.Throws<IOException>(() => SelfCheckRun.Create(source, Path.Combine(link, "new"), Protector));
                Assert.False(Directory.Exists(Path.Combine(real, "new")));
            }
            finally { Directory.Delete(link); }
        }));

        Test("自检：同进程及不同进程并发都拒绝，不影响正常实例mutex", () => WithFixture((source, root) =>
        {
            using var production = new Mutex(false, @"Local\EmbyNian.SingleInstance.v3");
            using (var run = SelfCheckRun.Create(source, Path.Combine(root, "first"), Protector))
            {
                Assert.Throws<InvalidOperationException>(() => SelfCheckRun.Create(source, Path.Combine(root, "second"), Protector));
                var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    info.ArgumentList.Add(typeof(SelfCheckTests).Assembly.Location);
                info.ArgumentList.Add("selfcheck-mutex-fixture");
                info.ArgumentList.Add(source.Root);
                info.ArgumentList.Add(Path.Combine(root, "child"));
                using var child = Process.Start(info)!;
                Assert.True(child.WaitForExit(15000), "并发子进程必须立即拒绝");
                Assert.Equal(13, child.ExitCode);
                Assert.False(Directory.Exists(Path.Combine(root, "child")));
            }
            using var next = SelfCheckRun.Create(source, Path.Combine(root, "second"), Protector);
        }));

        Test("自检：禁播守卫在票据、网络、backend之前执行", () =>
        {
            var called = false;
            var service = new PlaybackService(null!, null!, () => { called = true; throw new Exception(); }, null!, allowPlayback: false);
            Assert.Throws<InvalidOperationException>(() => service.PlayAsync(null!, CancellationToken.None).GetAwaiter().GetResult());
            Assert.False(called);
            Assert.False(service.IsPlaying);
        });
    }

    internal static int RunMutexChild(string source, string target)
    {
        try { using var run = SelfCheckRun.Create(new AppPaths(source), target, Protector); return 0; }
        catch (InvalidOperationException) { return 13; }
    }

    private static void WithFixture(Action<AppPaths, string> action)
    {
        // Outputs live beside this test build, never in the real app profile.
        var root = Path.Combine(AppContext.BaseDirectory, "selfcheck-fixtures", Guid.NewGuid().ToString("N"));
        var source = new AppPaths(Path.Combine(root, "production-fixture"));
        source.EnsureCreated();
        File.WriteAllText(source.SettingsFile, Saved, new UTF8Encoding(false));
        File.WriteAllText(source.SettingsBackupFile, "saved backup");
        File.WriteAllText(Path.Combine(source.ImageCacheDirectory, "keep"), "cached image");
        try { action(source, root); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class NoSecretsProtector : ISecretProtector
    {
        public string Protect(string plainText) => throw new InvalidOperationException("复制设置不应重新加密");
        public string Unprotect(string cipherText) => throw new InvalidOperationException("复制设置不应解密");
    }

    private static string Snapshot(string root) => string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + "=" + Convert.ToHexString(File.ReadAllBytes(path))));
}
