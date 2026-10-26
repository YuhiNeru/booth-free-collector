namespace BoothFreeCollector;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        string? testingRoot = Environment.GetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE");
        if (!string.IsNullOrWhiteSpace(testingRoot)) Storage.Root = Path.GetFullPath(testingRoot);
        if (args.Length == 2 && args[0] is "--self-test" or "--session-self-test")
        {
            ApplicationConfiguration.Initialize();
            if (args[0] == "--self-test") SelfTest.Run(args[1]).GetAwaiter().GetResult();
            else SessionUiVerification.Run(args[1]);
            return;
        }
        try { if (testingRoot is null) Storage.Root = AppStorageLocation.ReadRoot(); }
        catch (Exception)
        {
            Environment.ExitCode = 1;
            if (args.Length == 0 || args.Contains("--tray"))
            { ApplicationConfiguration.Initialize(); MessageBox.Show("設定の保存場所を確認できません。自動取得を止めました。", "BOOTH Free Downloader"); }
            else { using var output = Console.OpenStandardOutput(); output.Write("{\"storage_error\":true}"u8); }
            return;
        }
        if (args.SequenceEqual(new[] { "--retire-extension" })) { BrowserBridge.Unregister(); return; }
        if (args.SequenceEqual(new[] { "--manager-installation-status" }))
        {
            using var output = Console.OpenStandardOutput();
            var requirement = OfficialLibraryRequirement.CheckInstalled(Storage.Load<Settings>("settings.json"));
            output.Write(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { installed_and_signed = requirement.Ready, requirement.Message }));
            return;
        }
        if (args.SequenceEqual(new[] { "--collector-status" }))
        {
            var saved = Storage.Load<CollectorState>("state.json"); var settings = Storage.Load<Settings>("settings.json"); var library = LibraryInfo.Read();
            var auth = Storage.Load<SessionStatus>("session-status.json");
            using var output = Console.OpenStandardOutput();
            output.Write(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { app_version = typeof(Program).Assembly.GetName().Version?.ToString(3),
                extension_retired = true, data_root = Storage.Root, library_folder = library.Folder, download_folder = library.Folder, library_error = library.Error, manager_registered = library.Registered.Count,
                saved.LastAttempt, next_attempt = saved.NextAttempt, saved.GeneratedAt, pending_count = saved.Queue.Count,
                manager_pending = saved.ManagerImports.Values.Count(i => !i.Confirmed), session_enabled = settings.DedicatedSessionEnabled,
                setup_completed = settings.SetupCompleted, needs_login = auth.NeedsLogin, session_checked_at = auth.CheckedAt,
                official_dependency_ready = OfficialLibraryRequirement.Check().Ready, auto_start_official = settings.AutoStartOfficialManager,
                initial_completed = saved.InitialDownloadCompleted }, Storage.Json));
            return;
        }
        if (args.SequenceEqual(new[] { "--library-status" }))
        {
            using var output = Console.OpenStandardOutput();
            output.Write(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(LibraryInfo.Read(), Storage.Json));
            return;
        }
        if (args.SequenceEqual(new[] { "--manager-trust-status" }))
        {
            using var output = Console.OpenStandardOutput();
            try
            {
                string path = ManagerIntegration.RunningExecutable();
                using var verified = OfficialExecutableTrust.OpenVerified(path);
                output.Write(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { trusted = true, path, publisher = "pixiv Inc.", revocation_checked = false }));
            }
            catch (Exception ex) { output.Write(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { trusted = false, error = ex.Message })); Environment.ExitCode = 1; }
            return;
        }
        if (args.SequenceEqual(new[] { "--manager-cache-status" }))
        {
            using var output = Console.OpenStandardOutput();
            try
            {
                using var source = ActiveLibraryDatabase.OpenForProcesses(LibraryInfo.DbPath, []);
                var info = LibraryInfo.ReadSnapshot(source);
                output.Write(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { cached = source.Cached is not null,
                    folder = info.Folder, registered_count = info.Registered.Count, error = info.Error,
                    raw_cache_present = File.Exists(Path.Combine(Storage.Root, "manager-current.db")) }));
            }
            catch (Exception ex) { output.Write(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { cached = false, error = ex.Message })); Environment.ExitCode = 1; }
            return;
        }
        if (args.Any(a => a.StartsWith("chrome-extension://", StringComparison.Ordinal)))
        {
            BrowserBridge.Write(Console.OpenStandardOutput(), "{\"desktop\":false,\"reason\":\"Extension support was retired\"}"u8.ToArray()).GetAwaiter().GetResult();
            return;
        }
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, testingRoot is null ? @"Local\BoothFreeCollector" : @"Local\" + BrowserBridge.PipeName, out bool first);
        if (!first) { if (!AppActivation.Signal()) MessageBox.Show("通知領域のアイコンから開いてください。"); return; }
        try
        {
            if (testingRoot is null) Storage.Root = AppStorageLocation.Initialize();
            Application.Run(new MainForm(args.Contains("--tray")));
        }
        catch (Exception ex) { MessageBox.Show("設定・履歴を読み取れません。自動取得を止めました。\n" + ex.Message, "BOOTH Free Downloader"); }
    }
}
