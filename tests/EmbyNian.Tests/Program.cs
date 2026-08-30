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
        ServiceTests.Register();
        MpvConfigTests.Register();
        MpvWorkspaceTests.Register();
        FontTests.Register();
        EmbyTests.Register();
        ImageCacheTests.Register();
        ItemDetailTests.Register();
        ItemArtworkTests.Register();
        HomeCarouselTests.Register();
        DiagnosticsTests.Register();
        ScreenTests.Register();
        ThemeTests.Register();

        return TestHarness.Run();
    }
}
