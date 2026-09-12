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
        PinIndicatorTests.Register();
        AboutTests.Register();

        return TestHarness.Run();
    }
}
