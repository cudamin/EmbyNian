using EmbyNian.Configuration;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Playback;
using EmbyNian.Services;
using EmbyNian.Shell.Platform;
using EmbyNian.Shell.Security;
using EmbyNian.Shell.ViewModels;
using EmbyNian.Shell.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;

namespace EmbyNian.Shell.Composition;

/// <summary>
/// Registers everything the app lives on, once, and hands back the container that owns it.
/// <para>
/// This replaces a composition root that did the same wiring with <c>new</c>. What is gained is not
/// brevity — the registrations below are longer than the constructor was — but two things the hand-written
/// version could not offer. A view model can now declare the one capability it needs, and get that instead
/// of a handle on everything; and <see cref="ServiceProviderOptions.ValidateOnBuild"/> turns a dependency
/// nobody registered into a startup failure that names it, where before it would surface much later as a
/// null reference on whichever page happened to touch it first.
/// </para>
/// <para>
/// The container lives in this project and not in Core, deliberately: Core takes no package references at
/// all, which is what lets the console test runner load it. The services it registers are plain classes
/// there, constructible by hand, so a test can build one without a container and the container is the only
/// thing that needs the package.
/// </para>
/// </summary>
internal static class ShellServices
{
    /// <summary>
    /// Builds the container. Takes the paths <see cref="Program"/> already resolved and migrated, rather
    /// than reaching for <see cref="AppPaths.Default"/> again: the migration from the pre-rename data
    /// directory has to have happened before anything reads settings.json, and only the entry point knows
    /// it did.
    /// </summary>
    internal static ServiceProvider Build(AppPaths paths)
    {
        paths.EnsureCreated();

        var services = new ServiceCollection();

        // ---- the ground floor: where files live and how secrets are protected -------------------------
        services.AddSingleton(paths);
        services.AddSingleton<ISecretProtector>(DpapiSecretProtector.Instance);
        services.AddSingleton<SettingsStore>();

        // The settings object is loaded once and then edited in place by the settings page, so it has to
        // be a singleton rather than something re-read per request: two instances would mean an edit on
        // one page invisible to the next. EnsureDeviceId runs here, on the way in, for the same reason it
        // used to run in the composition root — every request the session makes carries the device id.
        services.AddSingleton(provider => provider.GetRequiredService<SettingsStore>().Load().EnsureDeviceId());

        // ---- the server -------------------------------------------------------------------------------
        services.AddSingleton<CredentialVault>();
        services.AddSingleton(provider =>
            DeviceIdentity.Create(provider.GetRequiredService<AppSettings>().DeviceId, AppIdentity.Version));
        services.AddSingleton<EmbySession>();

        // The disk budget comes from the settings document rather than from a literal here: it is a row on the
        // 界面 card now, and it can change while the app runs — see EmbyImageStore.Retarget and the line in
        // App.OnLaunched that hands a changed one over.
        services.AddSingleton(provider => new EmbyImageStore(
            provider.GetRequiredService<EmbySession>(),
            paths.ImageCacheDirectory,
            ImageCachePolicy.BudgetBytes(provider.GetRequiredService<AppSettings>().Ui.ImageCacheMegabytes)));

        // ---- the player -------------------------------------------------------------------------------
        services.AddSingleton(provider =>
            new ShaderGroupResolver(provider.GetRequiredService<AppSettings>().Shaders));
        services.AddSingleton(provider => new PlaybackPlanner(
            provider.GetRequiredService<AppSettings>(),
            provider.GetRequiredService<ShaderGroupResolver>(),
            paths.ShaderCacheDirectory,
            paths.ScreenshotDirectory));
        services.AddSingleton<ShaderStaging>();
        services.AddSingleton<PlaybackBackendFactory>();

        // 音频输出设备. The concrete class rather than an interface, per the rule about not inventing one for a
        // single implementation — it has one public method and that is already the whole surface the settings
        // page should see. It is handed 「where libmpv is」 as a closure so the search stays LibMpvBackend's.
        services.AddSingleton(provider => new AudioDeviceCatalogue(
            () => LibMpvBackend.Locate(provider.GetRequiredService<AppSettings>().Mpv)));

        // The factory is handed over as a method group, which is how PlaybackService gets a new backend per
        // playback without ever naming either of them — switching 播放后端 in the settings takes effect on
        // the very next play.
        services.AddSingleton(provider => new PlaybackService(
            provider.GetRequiredService<EmbySession>(),
            provider.GetRequiredService<AppSettings>(),
            provider.GetRequiredService<PlaybackBackendFactory>().Create,
            provider.GetRequiredService<PlaybackPlanner>()));

        // ---- what the view models depend on ----------------------------------------------------------
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IServerCapabilities, ServerCapabilities>();

        // ---- the three platform capabilities a view model would otherwise reach for --------------------
        // An instance built here rather than a factory lambda, and this is the whole reason it is worth
        // saying: the queue belongs to whichever thread asks for it, so a lambda would capture the thread
        // of the first resolve — a background one on the day something resolves this off the UI thread, and
        // then every marshalled log append and every mpv status update would land on the wrong thread with
        // nothing to show for it. Build is called from App.OnLaunched and nowhere else, so capturing now
        // captures the UI thread; the throw turns 「someone added a second call site」 into a named startup
        // failure instead of a silent one, in the spirit of ValidateOnBuild below.
        services.AddSingleton<IUiDispatcher>(new UiDispatcher(
            DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("服务容器必须在界面线程上创建")));

        services.AddSingleton<IClipboard, SystemClipboard>();
        services.AddSingleton<ISystemLauncher, SystemLauncher>();

        // Singleton because the scan is per machine and slow enough to be worth doing once: the 字幕 card's
        // font picker asks for the families, and asking again on every visit to the settings page would
        // re-read every file in C:\Windows\Fonts to arrive at the same list.
        services.AddSingleton<FontLibrary>();

        // One visible shell for both the host window and every page action. App resolves ShellPage from
        // this registration instead of constructing a second instance, so IShellActions can never point
        // at a hidden visual tree.
        services.AddSingleton<ShellPage>();
        services.AddSingleton<IShellActions>(provider => provider.GetRequiredService<ShellPage>());

        // The one view model that is registered rather than newed by its page: the player's state has to
        // outlive any single navigation — the film keeps playing while the library is browsed behind it —
        // and it is the only view model with seven dependencies of its own, which is exactly the case
        // ValidateOnBuild is here to catch. The page is built by the shell's XAML and handed this in
        // AttachWindow, which is the one place a container is in scope.
        services.AddSingleton<PlayerViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            // The whole reason for the container. Everything above is a singleton with no scope, so
            // ValidateScopes has nothing to catch today and is on so that it does if a scope ever appears.
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }
}
