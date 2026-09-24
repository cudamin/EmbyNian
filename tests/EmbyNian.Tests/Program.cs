namespace EmbyNian.Tests;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (MpvProcessTests.IsChild(args)) return MpvProcessTests.RunChildAsync(args).GetAwaiter().GetResult();
        if (args is ["selfcheck-mutex-fixture", var source, var target])
            return SelfCheckTests.RunMutexChild(source, target);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("EmbyNian Core 测试");
        Console.WriteLine();

        PlaybackTests.Register();
        MpvProcessTests.Register();
        SelfCheckTests.Register();
        SettingsTests.Register();
        ShortcutTests.Register();
        SeekKeyTests.Register();
        ServiceTests.Register();
        FontTests.Register();
        HtmlColorTests.Register();
        SubtitlePreviewTests.Register();
        EmbyTests.Register();
        SessionTests.Register();
        FailurePathTests.Register();
        ImageCacheTests.Register();
        ItemDetailTests.Register();
        ItemArtworkTests.Register();
        ItemMenuTests.Register();
        ItemActionTests.Register();
        HomeCarouselTests.Register();
        HomeFoldTests.Register();
        BackdropBlurTests.Register();
        HomeLayoutTests.Register();
        DiagnosticsTests.Register();
        ScreenTests.Register();
        CardStripTests.Register();
        CardSizeTests.Register();
        WrapLayoutTests.Register();
        StartupArgsTests.Register();
        ThemeTests.Register();
        PlayerPaletteTests.Register();
        PlayerMotionTests.Register();
        PictureRevealTests.Register();
        StatusCoalescerTests.Register();
        ResizeFreezeTests.Register();
        VolumeScaleTests.Register();
        MpvUiTests.Register();
        MediaVersionTests.Register();
        ReleaseGroupTests.Register();
        InlineSwitchTests.Register();
        WindowFormTests.Register();
        PinIndicatorTests.Register();
        AboutTests.Register();
        MoviePilotTests.Register();
        AgreementsTests.Register();

        return TestHarness.Run();
    }
}
