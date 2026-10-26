namespace BoothFreeCollector;

internal static class SessionVerification
{
    public static void Run(Action<bool, string> check)
    {
        check(!OfficialLibraryRequirement.Evaluate(new(null, [], null), false, false).Ready, "missing running Manager blocks imports");
        check(!OfficialLibraryRequirement.Evaluate(new("C:\\fixture", [], null), true, false).Ready, "unsigned Manager cannot unlock setup");
        check(!OfficialLibraryRequirement.Evaluate(new(null, [], null), true, true).Ready, "unconfigured official destination blocks imports");
        check(!OfficialLibraryRequirement.Evaluate(new("C:\\fixture", [], "busy"), true, true).Ready, "unreadable official settings cannot unlock imports");
        check(OfficialLibraryRequirement.Evaluate(new("C:\\fixture", [], null), true, true).Ready, "signed running Manager with configured local storage unlocks login");
        check(OfficialManagerLauncher.ExecutableFromCommand("\"C:\\Apps\\BOOTH Library Manager\\booth_library_manager_client.exe\" \"%1\"") == "C:\\Apps\\BOOTH Library Manager\\booth_library_manager_client.exe", "launcher extracts only the executable from a quoted protocol registration");
        foreach (var bad in new[] { "cmd.exe /c malicious", "\"C:\\Apps\\other.exe\" %1", "\\\\server\\booth_library_manager_client.exe", "C:\\Apps\\booth_library_manager_client.exe\nextra" })
            check(OfficialManagerLauncher.ExecutableFromCommand(bad) is null, "launcher rejects commands and unsafe registered executable paths");
        Storage.Save("manager-launch-state.json", new OfficialManagerLauncher.LaunchStamp { At = DateTimeOffset.UtcNow, ExpectedFolder = "E:\\fixture", AwaitingConfirmation = true });
        using (var launcher = new OfficialManagerLauncher())
            check(!launcher.Confirm(new(true, "fixture", "C:\\unexpected")).Ready, "unexpected destination after automatic Manager launch cannot unlock acquisition");
        using (var restarted = new OfficialManagerLauncher())
        {
            check(!restarted.Confirm(new(true, "fixture", "C:\\unexpected")).Ready, "restart cannot forget an unresolved automatic-launch destination mismatch");
            check(restarted.Confirm(new(true, "fixture", "E:\\fixture")).Ready && !Storage.Load<OfficialManagerLauncher.LaunchStamp>("manager-launch-state.json").AwaitingConfirmation,
                "expected official destination clears the launch guard only after verification");
        }
        var launchNow = DateTimeOffset.UtcNow;
        check(!OfficialManagerLauncher.WaitingForStart(new() { At = launchNow, AwaitingConfirmation = false }, launchNow),
            "a previously confirmed Manager can restart immediately after it closes");
        check(OfficialManagerLauncher.WaitingForStart(new() { At = launchNow, AwaitingConfirmation = true }, launchNow),
            "an unconfirmed launch has a short duplicate-start guard");
        check(!OfficialManagerLauncher.WaitingForStart(new() { At = launchNow.AddSeconds(-6), AwaitingConfirmation = true }, launchNow),
            "an unsuccessful launch can retry without waiting a full minute");
        const string installedExe = @"C:\Apps\booth_library_manager_client.exe";
        check(OfficialLibraryRequirement.EvaluateInstallation(installedExe, true).Ready,
            "signed installed Manager unlocks initial setup without a running process or configured destination");
        check(!OfficialLibraryRequirement.EvaluateInstallation(null, false).Ready,
            "missing installation cannot unlock dedicated login");
        check(!OfficialLibraryRequirement.EvaluateInstallation(installedExe, false).Ready,
            "unsigned installed file cannot unlock dedicated login");
        var idleSettings = new Settings { Configured = true, SetupCompleted = true, DedicatedSessionEnabled = true };
        var idleState = new CollectorState();
        check(!OfficialLibraryRequirement.NeedsRunningManager(idleSettings, idleState), "idle polling does not require starting Manager");
        idleState.Queue.Add(new Product { Id = "1", AgeRating = "all_ages", Categories = ["3D衣装"] });
        check(OfficialLibraryRequirement.NeedsRunningManager(idleSettings, idleState), "actual selected downloads require active Manager verification");
        idleSettings.AutoDownload = false;
        check(!OfficialLibraryRequirement.NeedsRunningManager(idleSettings, idleState), "paused acquisition cannot automatically open Manager");
        idleSettings.AutoDownload = true; idleSettings.SetupCompleted = false;
        check(!OfficialLibraryRequirement.NeedsRunningManager(idleSettings, idleState), "unfinished initial setup cannot start Manager for pending files");
        check(EmbeddedSession.AllowedNavigation("https://accounts.booth.pm/") && EmbeddedSession.AllowedNavigation("https://accounts.pixiv.net/login"), "dedicated session permits official login origins");
        foreach (var bad in new[] { "http://booth.pm/", "https://booth.pm.attacker.invalid/", "file:///C:/private", "https://booth.pm:444/", "https://user@booth.pm/", "javascript:alert(1)", "booth-library-manager://item-import?x=1" })
            check(!EmbeddedSession.AllowedNavigation(bad), "dedicated session blocks unsafe top-level navigation");
        string entrance = "https://booth.pm/downloadables/10/deeplink?client=booth-library-manager&variation_id=2";
        check(EmbeddedSession.AllowedRequest("https://booth.pm/ja/items/1") && EmbeddedSession.ValidEntrance(entrance), "session fetch is limited to product pages and bound import entrances");
        foreach (var bad in new[] { "https://accounts.booth.pm/library", "https://booth.pm/ja/items/1?extra=1", entrance + "&variation_id=3", entrance + "&command=delete", entrance.Replace("10/deeplink", "10/other") })
            check(!EmbeddedSession.AllowedRequest(bad), "session fetch rejects unrelated routes and ambiguous parameters");
        var product = new Product { Id = "1", Url = "https://booth.pm/ja/items/1", Variants = [new() { Id = "2" }] };
        const string link = "<a href=\"https://booth.pm/downloadables/10?variation_id=2\" title=\"fixture.zip\">free</a>";
        check(SessionDownloadQueue.Plan(link + link, product).Length == 1, "identical product anchors do not duplicate the plan");
        var sharedProduct = new Product { Id = "1", Variants = [new() { Id = "2" }, new() { Id = "3" }] };
        var shared = SessionDownloadQueue.Plan(link.Replace("variation_id=2", "variation_id=3") + link, sharedProduct);
        check(shared.Length == 1 && shared[0].VariantId == "2", "one file shared by multiple free variants is planned once in catalog order");
        try { SessionDownloadQueue.Plan(link + link.Replace("fixture.zip", "other.zip"), product); throw new Exception("conflicting file identity accepted"); }
        catch (IOException) { check(true, "one file ID with conflicting names remains rejected"); }
        try { SessionDownloadQueue.Plan(link + link.Replace("/10?", "/11?"), product); throw new Exception("ambiguous filename accepted"); }
        catch (IOException) { check(true, "ambiguous same-name files cannot bind an opaque import capability"); }
        check(!new Settings().DedicatedSessionEnabled, "new profile requires the login notice before enabling authentication");
        const string local = @"C:\Users\fixture\AppData\Local";
        const string normal = @"C:\Users\fixture\AppData\Roaming\pm.booth.library-manager\data.db";
        string packaged = local + @"\Packages\Example.App_abc123\LocalCache\Roaming\pm.booth.library-manager\data.db";
        check(ActiveLibraryDatabase.IsSettingsPath(@"\\?\" + normal, normal, local), "official normal settings file is recognized from its live handle");
        check(ActiveLibraryDatabase.IsSettingsPath(@"\\?\" + packaged, normal, local), "official process settings redirected by a packaged launcher are recognized");
        foreach (string bad in new[] { packaged.Replace("fixture", "another-user"), packaged.Replace("LocalCache", "OtherCache"), packaged.Replace("data.db", "cookies.db"),
            local + @"\Packages\..\LocalCache\Roaming\pm.booth.library-manager\data.db" })
            check(!ActiveLibraryDatabase.IsSettingsPath(bad, normal, local), "live settings recognition rejects other users, paths and filenames");
        check(OfficialManagerLauncher.ExpectedFolderForLaunch(new() { AwaitingConfirmation = true, ExpectedFolder = @"E:\fixture" }, @"C:\unexpected") == @"E:\fixture",
            "a repeated launch cannot replace an unresolved destination guard with a newly cached wrong folder");
    }
}
