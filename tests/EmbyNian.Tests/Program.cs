namespace EmbyNian.Tests;

internal static class Program
{
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("EmbyNian Core 测试");
        Console.WriteLine();

        PlaybackTests.Register();
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
        MpvUiTests.Register();
        MediaVersionTests.Register();
        InlineSwitchTests.Register();
        WindowFormTests.Register();
        PinIndicatorTests.Register();
        AboutTests.Register();
        MoviePilotTests.Register();
        AgreementsTests.Register();

        return TestHarness.Run();
    }
}
