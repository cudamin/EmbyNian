namespace EmbyMpvClient.Tests;

internal static class Program
{
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("EmbyMpvClient Core 测试");
        Console.WriteLine();

        MpvConfigTests.Register();
        PlaybackTests.Register();
        SettingsTests.Register();
        EmbyTests.Register();

        return TestHarness.Run();
    }
}
