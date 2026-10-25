namespace BoothFreeCollector;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        string? testingRoot = Environment.GetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE");
        if (!string.IsNullOrWhiteSpace(testingRoot)) Storage.Root = Path.GetFullPath(testingRoot);
        if (args.Any(a => a.StartsWith("chrome-extension://", StringComparison.Ordinal))) { BrowserBridge.RunHost(args).GetAwaiter().GetResult(); return; }
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] == "--self-test") { SelfTest.Run(args[1]).GetAwaiter().GetResult(); return; }
        using var mutex = new Mutex(true, testingRoot is null ? @"Local\BoothFreeCollector" : @"Local\" + BrowserBridge.PipeName, out bool first);
        if (!first) { MessageBox.Show("すでに常駐しています。通知領域のアイコンから開いてください。"); return; }
        try { Application.Run(new MainForm(args.Contains("--tray"))); }
        catch (Exception ex) { MessageBox.Show("設定・履歴を読み取れません。自動取得を止めました。\n" + ex.Message, "BOOTH Free Collector"); }
    }
}
