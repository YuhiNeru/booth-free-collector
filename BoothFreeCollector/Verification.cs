using System.Net;
using System.Text.Json;

namespace BoothFreeCollector;

internal static class SelfTest
{
    public static async Task Run(string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("新しいテスト専用フォルダを指定してください");
        Directory.CreateDirectory(output); Storage.Root = Path.Combine(output, "data");
        Environment.SetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE", output);
        var passed = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed.Add(name); }
        try
        {
            SchedulerVerification.Run(Check);
            SessionVerification.Run(Check);
            string migrationSource = Path.Combine(output, "old-app-data"), migrationTarget = Path.Combine(output, "stable-app-data");
            Directory.CreateDirectory(Path.Combine(migrationSource, "session-profile"));
            File.WriteAllText(Path.Combine(migrationSource, "settings.json"), "{\"setup_completed\":true}");
            string profileFixture = Path.Combine(migrationSource, "session-profile", "fixture.txt"); File.WriteAllText(profileFixture, "synthetic app-owned session fixture");
            using (var held = new FileStream(profileFixture, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { AppStorageLocation.Relocate(migrationSource, migrationTarget); throw new Exception("active session moved"); }
                catch (IOException) { Check(Directory.Exists(migrationSource) && !Directory.Exists(migrationTarget), "storage relocation waits for the app's browser locks without modifying its source"); }
            }
            try { AppStorageLocation.Relocate(migrationSource, Path.Combine(migrationSource, "nested")); throw new Exception("nested migration accepted"); }
            catch (IOException) { Check(true, "storage relocation rejects nested source and target paths"); }
            string collision = Path.Combine(output, "existing-app-data"); Directory.CreateDirectory(collision);
            File.WriteAllText(Path.Combine(collision, "keep.txt"), "unchanged");
            try { AppStorageLocation.Relocate(migrationSource, collision); throw new Exception("settings overwritten"); }
            catch (IOException) { Check(File.ReadAllText(Path.Combine(collision, "keep.txt")) == "unchanged", "storage relocation cannot overwrite another existing settings directory"); }
            Check(AppStorageLocation.Relocate(migrationSource, migrationTarget) == migrationTarget && !Directory.Exists(migrationSource) &&
                File.ReadAllText(Path.Combine(migrationTarget, "session-profile", "fixture.txt")) == "synthetic app-owned session fixture",
                "same-volume relocation preserves one app-owned session instead of copying credentials to a second profile");
            Check(AppStorageLocation.ResolveDirectory(migrationTarget).Equals(migrationTarget, StringComparison.OrdinalIgnoreCase),
                "storage relocation resolves the physical app-data directory");
            string unsigned = Path.Combine(output, "unsigned-manager.exe");
            File.WriteAllText(unsigned, "unsigned test fixture");
            try { using var rejected = OfficialExecutableTrust.OpenVerified(unsigned); throw new Exception("unsigned executable accepted"); }
            catch (IOException) { passed.Add("unsigned manager is rejected before receiving an import link"); }
            var absentLibrary = new LibraryInfo(null, [], null);
            var destinationSettings = new Settings();
            Check(destinationSettings.DownloadFolder == Settings.DefaultDownloadFolder && destinationSettings.FollowLibraryFolder, "default is LocalAppData BOOTHLibraryManager with future following enabled");
            try { DownloadEngine.ResolveRoot(destinationSettings, absentLibrary); throw new Exception("fallback destination used"); } catch (IOException) { passed.Add("absent manager stops download without a fallback destination"); }
            string installedFolder = Path.Combine(output, "manager-folder");
            Check(DownloadEngine.ResolveRoot(destinationSettings, new(installedFolder, [], null)) == installedFolder, "later installation is followed without restarting");
            string movedFolder = Path.Combine(output, "manager-moved");
            Check(DownloadEngine.ResolveRoot(destinationSettings, new(movedFolder, [], null)) == movedFolder, "manager folder changes are followed");
            destinationSettings.FollowLibraryFolder = false;
            Check(DownloadEngine.ResolveRoot(destinationSettings, new(movedFolder, [], null)) == Settings.DefaultDownloadFolder, "following off preserves independent directory");
            destinationSettings.FollowLibraryFolder = true;
            Environment.SetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE", null);
            try
            {
                Check(new Settings { FollowLibraryFolder = false, DownloadFolder = "C:\\obsolete" }.ResolveFolder(new(installedFolder, [], null)) == installedFolder, "production ignores legacy manual folder and follow-off settings");
            }
            finally { Environment.SetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE", output); }
            string activeDb = Path.Combine(output, "active-settings.db"), replacementDb = Path.Combine(output, "replacement-settings.db");
            byte[] liveData = new byte[512], staleData = new byte[512];
            liveData[18] = liveData[19] = staleData[18] = staleData[19] = 1;
            liveData[100] = 42; staleData[100] = 99;
            File.WriteAllBytes(activeDb, liveData); File.WriteAllBytes(replacementDb, staleData);
            using (var writer = new FileStream(activeDb, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                Check(ActiveLibraryDatabase.TryDiskHash(activeDb) == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(liveData)),
                    "optional disk fingerprint does not reject a settings DB held by an official writer");
            Check(ActiveLibraryDatabase.TryDiskHash(Path.Combine(output, "missing-settings.db")) is null,
                "missing optional disk fingerprint cannot invalidate a separately verified live snapshot");
            using (var heldDb = new FileStream(activeDb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                File.Replace(replacementDb, activeDb, null);
                byte[] snapshot = ActiveLibraryDatabase.ReadStable(heldDb.SafeFileHandle);
                Check(snapshot[100] == 42 && File.ReadAllBytes(activeDb)[100] == 99, "active DB handle wins when a stale file replaces its pathname");
            }
            string legacyCache = Path.Combine(Storage.Root, "manager-current.db");
            CreateFixtureLibraryDb(legacyCache, installedFolder);
            string sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(legacyCache)));
            Storage.Save("manager-source.json", new ActiveLibraryDatabase.SourceFingerprint { Active = sourceHash,
                Disk = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(activeDb))) });
            using (var legacy = ActiveLibraryDatabase.OpenForProcesses(activeDb, []))
            {
                var migratedInfo = LibraryInfo.ReadSnapshot(legacy);
                Check(migratedInfo.Folder == installedFolder && migratedInfo.Registered.SetEquals(["123"]), "verified legacy snapshot migrates only folder and registered IDs");
            }
            string remembered = File.ReadAllText(Path.Combine(Storage.Root, "manager-settings.json"));
            Check(!File.Exists(legacyCache) && !remembered.Contains("must-not-persist") &&
                JsonDocument.Parse(remembered).RootElement.EnumerateObject().Count() == 3, "full manager DB cache is removed and unrelated data is not retained");
            using (var fallback = ActiveLibraryDatabase.OpenForProcesses(activeDb, []))
                Check(fallback.Cached?.Folder == installedFolder && fallback.Cached.Registered.Contains("123"), "closed manager follows verified minimal cache instead of stale disk path");
            Storage.Save("manager-settings.json", new ActiveLibraryDatabase.SettingsCache { Source = "wrong", Folder = installedFolder, Registered = ["123"] });
            try { using var wrong = ActiveLibraryDatabase.OpenForProcesses(activeDb, []); throw new Exception("unbound settings cache accepted"); }
            catch (IOException) { passed.Add("mismatched minimal cache cannot enable stale destination fallback"); }
            string orphan = Path.Combine(Storage.Root, ".manager-read-orphan-fixture.db");
            File.WriteAllText(orphan, "temporary snapshot fixture"); File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-1));
            using (var heldSnapshot = new FileStream(orphan, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                ActiveLibraryDatabase.CleanupOldSnapshots(true);
                Check(File.Exists(orphan), "snapshot cleanup does not remove an open reader");
            }
            ActiveLibraryDatabase.CleanupOldSnapshots(true);
            Check(!File.Exists(orphan), "abandoned private snapshots are cleaned after their reader closes");
            try { DownloadEngine.ResolveRoot(destinationSettings, new(null, [], "busy")); throw new Exception("silently changed folder on read error"); } catch (IOException) { passed.Add("read failure stops rather than silently switching directories"); }
            var state = new CollectorState { LastAttempt = DateTimeOffset.UtcNow };
            Check(!state.Due(state.LastAttempt.Value.AddHours(12).AddTicks(-1)) && state.Due(state.LastAttempt.Value.AddHours(12)), "12-hour boundary");
            Storage.Save("state.json", state); Check(!Storage.Load<CollectorState>("state.json").Due(DateTimeOffset.UtcNow), "schedule survives restart");
            int probes = 0, bodies = 0;
            var headerState = new CollectorState { GeneratedAt = DateTimeOffset.UtcNow.AddDays(-1).ToString("O"), LastAttempt = DateTimeOffset.UtcNow.AddHours(-13), Etag = "\"old\"" };
            using (var unchanged = new FeedClient(new Handler(request =>
            {
                if (request.Method == HttpMethod.Head) probes++; else bodies++;
                return new(HttpStatusCode.NotModified);
            })))
            {
                Check(!await unchanged.Fetch(new(), headerState, default) && probes == 1 && bodies == 0,
                    "unchanged public headers do not request a catalog body");
                var persisted = Storage.Load<CollectorState>("state.json");
                Check(persisted.LastAttempt == headerState.LastAttempt && persisted.NextHeaderCheck > DateTimeOffset.UtcNow,
                    "header probe interval survives restart without consuming the twelve-hour body lease");
                Check(!await unchanged.Fetch(new(), persisted, default) && probes == 1,
                    "early repeated header checks are suppressed");
            }
            headerState.NextHeaderCheck = null;
            using (var updated = new FeedClient(new Handler(request =>
            {
                if (request.Method != HttpMethod.Head) { bodies++; return new(HttpStatusCode.ServiceUnavailable); }
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation("X-Catalog-Generated-At", DateTimeOffset.UtcNow.ToString("O"));
                return response;
            })))
            {
                Check(!await updated.Fetch(new(), headerState, default) && headerState.CatalogReadyAfter > DateTimeOffset.UtcNow.AddSeconds(60) && bodies == 0,
                    "new publication waits for edge convergence before the body request");
                headerState.CatalogReadyAfter = DateTimeOffset.UtcNow.AddSeconds(-1);
                try { await updated.Fetch(new(), headerState, default); throw new Exception("failed body accepted"); }
                catch (HttpRequestException) { }
                var persisted = Storage.Load<CollectorState>("state.json");
                Check(bodies == 1 && persisted.LastAttempt > DateTimeOffset.UtcNow.AddMinutes(-1) && persisted.CatalogReadyAfter is null,
                    "failed catalog body reserves the durable twelve-hour lease before I/O");
                Check(!await updated.Fetch(new(), persisted, default) && bodies == 1,
                    "body failure cannot trigger an early retry");
            }
            Check(!new Settings().BrowserHandoff, "desktop authentication never opens browser Downloads by default");
            Check(!DownloadEngine.AllowedPage("https://booth.pm.attacker.invalid/") && !DownloadEngine.AllowedFile("file:///C:/test"), "URL allowlist");
            ManagerIntegration.ValidateLink("booth-library-manager://item-import?item=1");
            string Bound(string url, string suffix = "") => "booth-library-manager://item-import?item_id=1&variation_id=2&dlurl=" + Uri.EscapeDataString(url) + suffix;
            ManagerIntegration.ValidateBoundLink(Bound("https://booth.pm/downloadables/10?signature=fixture"), "1", "10", "2", "fixture.zip");
            ManagerIntegration.ValidateBoundLink(Bound("https://booth.pximg.net/fixture/fixture.zip?signature=fixture"), "1", "10", "2", "fixture.zip");
            ManagerIntegration.ValidateBoundLink(Bound("https://s6.booth.pm/opaque-object?signature=fixture", "&downloadable_filename=fixture.zip"), "1", "10", "2", "fixture.zip");
            string described = JsonSerializer.Serialize(ManagerIntegration.DescribeLink(
                Bound("https://privatecapability.booth.pm/object?token=private-value", "&privatecapability=private-value")));
            Check(!described.Contains("privatecapability") && !described.Contains("private-value"),
                "import schema diagnostics redact arbitrary field names hosts and capability values");
            passed.Add("import capability is bound to the planned product variant and official destination");
            foreach (string invalid in new[] {
                Bound("https://evil.invalid/fixture.zip"), Bound("http://booth.pm/downloadables/10"),
                Bound("https://booth.pm/downloadables/11"), Bound("https://booth.pximg.net/fixture/other.zip"),
                Bound("https://booth.pm/downloadables/10", "&item_id=999"),
                Bound("https://booth.pm/downloadables/10").Replace("variation_id=2", "variation_id=3"),
                Bound("https://booth.pm/downloadables/10", "&command=delete"),
                Bound("https://s6.booth.pm/opaque-object"),
                Bound("https://s6.booth.pm/opaque-object", "&downloadable_filename=wrong.zip"),
                Bound("https://s6.booth.pm.evil.invalid/opaque-object", "&downloadable_filename=fixture.zip"),
                Bound("https://s6.booth.pm:444/opaque-object", "&downloadable_filename=fixture.zip") })
            {
                try { ManagerIntegration.ValidateBoundLink(invalid, "1", "10", "2", "fixture.zip"); throw new Exception("unbound capability accepted"); }
                catch (IOException) { passed.Add("unbound or unexpected import capability rejected"); }
            }
            Check(ManagerIntegration.SelectExecutable(["manager.exe", "manager.exe"], []) == "manager.exe", "tray-only manager and helper process remain available");
            Check(ManagerIntegration.SelectExecutable(["old.exe", "current.exe"], ["current.exe"]) == "current.exe", "visible official installation wins over stale helper path");
            try { ManagerIntegration.SelectExecutable(["one.exe", "two.exe"], []); throw new Exception("ambiguous installation accepted"); } catch (IOException) { passed.Add("ambiguous hidden manager installations wait safely"); }
            foreach (string invalid in new[] { "booth-library-manager://delete?item_id=1", "booth-library-manager://item-import/path?item_id=1", "booth-library-manager://item-import?item_id=1#fragment", "file:///C:/bad.exe", "https://booth.pm/", "booth-library-manager://user:password@import/", "booth-library-manager://item-import?x=\n" })
            {
                try { ManagerIntegration.ValidateLink(invalid); throw new Exception("unsafe import URI accepted"); } catch (IOException) { passed.Add("official import URI validation: " + invalid.Split(':')[0]); }
            }
            int calls = 0;
            using (var failed = new FeedClient(new Handler(_ => { calls++; return new(HttpStatusCode.ServiceUnavailable); })))
            {
                state = new(); try { await failed.Fetch(new(), state, default); } catch (HttpRequestException) { }
                await failed.Fetch(new(), state, default); Check(calls == 1, "failed feed not retried before 12 hours");
            }
            var product = new Product { Id = "1", Title = "Fixture free file", Url = "https://booth.pm/ja/items/1", AgeRating = "all_ages", Categories = ["3D衣装"], Variants = [new() { Id = "2", Name = "Free" }] };
            var fixture = new Feed { Schema = 1, IntervalHours = 12, GeneratedAt = DateTimeOffset.UtcNow.ToString("O"), Items = [product] };
            Check(product.Matches(new()), "default initial and recurring filters include clothing");
            Check(new Product { AgeRating = "all_ages", Categories = ["3D髪型"] }.Matches(new()), "hair included by default");
            foreach (var category in DownloadCategories.All)
            {
                var candidate = new Product { AgeRating = "all_ages", Categories = [category.Names[0]] };
                Check(candidate.Matches(new()) == (category.Priority == "高"), "default priority: " + category.Id);
                Check(candidate.Matches(new() { DownloadCategories = [category.Id] }), "individual category: " + category.Id);
                Check(!candidate.Matches(new() { DownloadCategories = [] }), "empty selection excludes: " + category.Id);
            }
            var initial = new CollectorState(); FeedClient.Apply(new(), initial, fixture);
            Check(initial.Queue.Count == 1, "first run includes selected existing products by default");
            var transferred = new CollectorState();
            FeedClient.MergePending(new(), transferred, fixture, ["1", "1", "999"]);
            FeedClient.MergePending(new(), transferred, fixture, ["1"]);
            Check(transferred.Queue.Count == 1 && transferred.Queue[0].Id == "1", "handoff accepts current free products once and ignores unknown ids");
            transferred.Queue.Clear();
            FeedClient.MergePending(new() { Search = "absent" }, transferred, fixture, ["1"]);
            Check(transferred.Queue.Count == 0, "handoff respects Windows filters");
            Check(new Product { Title = "Ｕｎｉｔｙ 衣装", AgeRating = "all_ages", Categories = ["3D衣装"] }.Matches(new() { Search = "unity　衣装" }), "Japanese width and whitespace filters agree with extension");
            string staged = Path.Combine(output, "browser", "BOOTH-Free-Collector", "b1", "10", "fixture (1).txt");
            string browserFolder = Path.Combine(output, "browser");
            Check(DownloadEngine.MatchesBrowserFile(staged, "BOOTH-Free-Collector/b1/10/fixture.txt", browserFolder, "fixture.txt"), "Chrome uniquified staging filename accepted");
            Check(DownloadEngine.MatchesBrowserFile(Path.Combine(browserFolder, "fixture (2).txt"), "BOOTH-Free-Collector/b1/10/fixture.txt", browserFolder, "fixture.txt"), "Chrome uniquified default browser handoff accepted");
            Check(!DownloadEngine.MatchesBrowserFile(staged.Replace("b1", "b2"), "BOOTH-Free-Collector/b1/10/fixture.txt", browserFolder, "fixture.txt") &&
                !DownloadEngine.MatchesBrowserFile(staged.Replace("fixture (1)", "other"), "BOOTH-Free-Collector/b1/10/fixture.txt", browserFolder, "fixture.txt"), "unrelated file names and product directories rejected");
            string managerRoot = Path.Combine(output, "official-layout"), independentRoot = Path.Combine(output, "independent");
            Directory.CreateDirectory(Path.Combine(managerRoot, "b1", "archive"));
            File.WriteAllText(Path.Combine(managerRoot, "b1", "archive", "asset.txt"), "expanded by manager");
            File.WriteAllText(Path.Combine(managerRoot, "b1", "archive", ".gitkeep"), "");
            File.SetLastWriteTimeUtc(Path.Combine(managerRoot, "b1", "archive", ".gitkeep"), DateTime.UtcNow.AddMinutes(-1));
            File.SetLastWriteTimeUtc(Path.Combine(managerRoot, "b1", "archive", "asset.txt"), DateTime.UtcNow.AddMinutes(-1));
            Directory.SetLastWriteTimeUtc(Path.Combine(managerRoot, "b1", "archive"), DateTime.UtcNow.AddMinutes(-1));
            var existingLibrary = new LibraryInfo(managerRoot, [], null);
            Check(existingLibrary.HasLocalDownload("1", "archive.zip", independentRoot), "manager-expanded archives with legitimate empty entries are not downloaded again");
            File.WriteAllText(Path.Combine(managerRoot, "b1", "incomplete.partial"), "unfinished");
            Check(!existingLibrary.HasLocalDownload("1", "different.zip", independentRoot), "other files in manager product do not hide a new free file");
            string nested = Path.Combine(managerRoot, "b1", "archive", "unrelated.bin");
            File.WriteAllText(nested, "nested content");
            Check(existingLibrary.InspectLocalDownload("1", "unrelated.bin", independentRoot) == LocalDownloadState.Missing, "nested same-name content does not hide an independent downloadable");
            string writing = Path.Combine(managerRoot, "b1", "writing.bin");
            File.WriteAllText(writing, "in progress"); File.SetLastWriteTimeUtc(writing, DateTime.UtcNow.AddMinutes(-1));
            using (var writer = new FileStream(writing, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                Check(existingLibrary.InspectLocalDownload("1", "writing.bin", independentRoot) == LocalDownloadState.Busy, "open writer cannot produce a completion receipt even while idle");
            string truncated = Path.Combine(managerRoot, "b1", "truncated.zip");
            File.WriteAllText(truncated, "incomplete archive"); File.SetLastWriteTimeUtc(truncated, DateTime.UtcNow.AddMinutes(-1));
            Check(existingLibrary.InspectLocalDownload("1", "truncated.zip", independentRoot) == LocalDownloadState.Busy, "closed truncated ZIP does not produce a completion receipt");
            string completeArchive = Path.Combine(managerRoot, "b1", "complete.zip");
            using (var zip = System.IO.Compression.ZipFile.Open(completeArchive, System.IO.Compression.ZipArchiveMode.Create))
            using (var content = new StreamWriter(zip.CreateEntry("fixture.txt").Open())) content.Write("complete fixture");
            File.SetLastWriteTimeUtc(completeArchive, DateTime.UtcNow.AddMinutes(-1));
            Check(existingLibrary.HasLocalDownload("1", "complete.zip", independentRoot), "closed structurally complete ZIP produces a receipt");
            state = new(); FeedClient.Apply(new() { IncludeExisting = false }, state, fixture); Check(state.Queue.Count == 0 && state.Seen.Contains("1"), "initial baseline avoids mass downloads");
            Check(state.InitialDownloadCompleted, "skipped initial history cannot give future new products a twelve-hour deadline");
            product.Variants = [new() { Id = "2" }, new() { Id = "3" }]; FeedClient.Apply(new(), state, fixture); Check(state.Queue.Count == 1, "new free variant queues existing product");
            string html = "<a href='https://booth.pm/downloadables/10?variation_id=2' title='fixture.txt'>無料ダウンロード</a><a href='https://evil.invalid/downloadables/11?variation_id=2' title='bad.zip'>bad</a><a href='https://booth.pm/downloadables/12?variation_id=999' title='paid.zip'>paid</a>";
            Check(DownloadEngine.ParseLinks(html, product).Count() == 1, "only known free variant links accepted");
            var settings = new Settings { FollowLibraryFolder = false, DownloadFolder = Path.Combine(output, "downloads") };
            using (var direct = new DirectFileTransfer())
            {
                var received = new CollectorState();
                string root = Path.Combine(output, "direct-manager");
                string path = direct.Begin("ticket", "1/10", "1", "fixture.zip", root, 6);
                Check(Path.GetDirectoryName(path) == Path.Combine(root, "b1"), "direct transfer starts inside selected manager product folder");
                Check(!File.Exists(path), "incomplete transfer is not exposed as a completed file");
                direct.Append("ticket", 0, Convert.ToBase64String("abc"u8));
                try { direct.Append("ticket", 0, Convert.ToBase64String("def"u8)); throw new Exception("duplicate chunk accepted"); } catch (IOException) { passed.Add("duplicate or out-of-order transfer chunks rejected"); }
                direct.Append("ticket", 3, Convert.ToBase64String("def"u8));
                direct.Finish("ticket", 6, received);
                Check(File.ReadAllText(path) == "abcdef" && DownloadEngine.HasSaved(received, "1/10"), "direct transfer records verified file in manager folder");
                try { direct.Begin("collision", "1/10", "1", "fixture.zip", root, 6); throw new Exception("overwritten"); } catch (IOException) { passed.Add("direct transfer cannot overwrite existing file"); }
                direct.Begin("abort", "1/11", "1", "aborted.zip", root, null);
                direct.Append("abort", 0, Convert.ToBase64String("partial"u8)); direct.Abort("abort");
                Check(!Directory.EnumerateFiles(Path.Combine(root, "b1"), "*.partial").Any(), "aborted direct transfers remove only their temporary file");
                direct.Begin("changed", "1/13", "1", "changed.zip", root, 3);
                direct.Append("changed", 0, Convert.ToBase64String("new"u8));
                string changedRoot = Path.Combine(output, "manager-changed-during-transfer");
                var changed = direct.Finish("changed", 3, received, changedRoot);
                Check(changed.Path == Path.Combine(changedRoot, "b1", "changed.zip") && !File.Exists(Path.Combine(root, "b1", "changed.zip")), "manager destination change during transfer is applied before finalizing");
                try { direct.Begin("large", "1/12", "1", "large.zip", root, 2147483649L); throw new Exception("oversize accepted"); } catch (IOException) { passed.Add("direct transfer checks size before writing"); }
            }
            bool credentials = false; int files = 0, handoffs = 0;
            using (var engine = new DownloadEngine(_ => { }, new Handler(request =>
            {
                credentials |= request.Headers.Contains("Cookie") || request.Headers.Contains("Authorization");
                if (request.RequestUri!.AbsolutePath.Contains("/items/")) return new(HttpStatusCode.OK) { Content = new StringContent(html) };
                files++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent("verified fixture payload"u8.ToArray()) };
            })))
            {
                state = new(); Check(await engine.DownloadProduct(product, settings, state, default), "Windows standalone direct download completed");
                Check(File.ReadAllText(Path.Combine(settings.DownloadFolder, "b1", "fixture.txt")) == "verified fixture payload", "file stored with original name");
                await engine.DownloadProduct(product, settings, state, default); Check(files == 1, "saved hash prevents duplicate download");
                state.Files.Clear(); await engine.DownloadProduct(product, settings, state, default); Check(files == 1, "existing local file prevents duplicate download");
            }
            settings.DownloadFolder = Path.Combine(output, "handoff"); settings.BrowserHandoff = true; state = new();
            using (var engine = new DownloadEngine(_ => { }, new Handler(request => request.RequestUri!.AbsolutePath.Contains("/items/") ? new(HttpStatusCode.OK) { Content = new StringContent(html) } : new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://booth.pm/users/sign_in") } }), _ => handoffs++))
            {
                Check(!await engine.DownloadProduct(product, settings, state, default), "login-required file remains pending");
                await engine.DownloadProduct(product, settings, state, default);
                Check(handoffs == 1 && state.BrowserPending.Count == 1, "default-browser handoff occurs only once");
            }
            Check(!credentials, "no Cookie or Authorization sent by desktop");
            string source = Path.Combine(output, "browser-file.txt"); File.WriteAllText(source, "browser download fixture");
            DownloadEngine.ImportFile(settings, state, "1/10", "1", "fixture.txt", source);
            Check(!File.Exists(source) && state.Files.ContainsKey("1/10") && !state.BrowserPending.ContainsKey("1/10"), "browser file imported and pending status cleared");
            settings.DownloadFolder = Path.Combine(output, "changed-destination");
            Check(DownloadEngine.HasSaved(state, "1/10"), "destination changes keep previous file history without redownloading");
            using var buffer = new MemoryStream();
            await BrowserBridge.Write(buffer, "{\"command\":\"status\"}"u8.ToArray()); buffer.Position = 0;
            Check(System.Text.Encoding.UTF8.GetString((await BrowserBridge.Read(buffer, 1024))!) == "{\"command\":\"status\"}", "native messaging framing roundtrip");
            using var tooBig = new MemoryStream(new byte[] { 255, 255, 255, 127 });
            try { await BrowserBridge.Read(tooBig, 1024); throw new Exception("large frame accepted"); } catch (IOException) { passed.Add("oversize native message rejected"); }
            var info = LibraryInfo.Read(); Check(info.Error is not null, "isolated self-test never reads the official manager DB");
            Check(!Directory.Exists(Path.Combine(Storage.Root, "browser")), "no browser profile or authentication store created");
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { ok = true, passed }, Storage.Json));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { ok = false, error = ex.ToString(), passed }, Storage.Json)); Environment.ExitCode = 1; }
    }
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request)); }
    [System.Runtime.InteropServices.DllImport("winsqlite3", EntryPoint = "sqlite3_open_v2", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    static extern int FixtureOpen(byte[] path, out IntPtr db, int flags, IntPtr vfs);
    [System.Runtime.InteropServices.DllImport("winsqlite3", EntryPoint = "sqlite3_exec", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    static extern int FixtureExec(IntPtr db, byte[] sql, IntPtr callback, IntPtr context, out IntPtr error);
    [System.Runtime.InteropServices.DllImport("winsqlite3", EntryPoint = "sqlite3_close", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    static extern int FixtureClose(IntPtr db);
    [System.Runtime.InteropServices.DllImport("winsqlite3", EntryPoint = "sqlite3_free", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    static extern void FixtureFree(IntPtr value);
    static void CreateFixtureLibraryDb(string path, string folder)
    {
        if (File.Exists(path) || !Path.GetFullPath(path).StartsWith(Path.GetFullPath(Storage.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Fixture creation requires a new file inside the isolated test state");
        if (FixtureOpen(System.Text.Encoding.UTF8.GetBytes(path + '\0'), out var db, 6, IntPtr.Zero) != 0)
            throw new IOException("Cannot create isolated SQLite fixture");
        try
        {
            string hex = Convert.ToHexString(System.Text.Encoding.Unicode.GetBytes(folder + '\0'));
            string sql = "CREATE TABLE preferences(item_directory_path BLOB); INSERT INTO preferences VALUES(X'" + hex + "');" +
                "CREATE TABLE registered_items(booth_item_id TEXT); INSERT INTO registered_items VALUES('123');" +
                "CREATE TABLE unrelated_private_data(note TEXT); INSERT INTO unrelated_private_data VALUES('must-not-persist');";
            int result = FixtureExec(db, System.Text.Encoding.UTF8.GetBytes(sql + '\0'), IntPtr.Zero, IntPtr.Zero, out var error);
            if (error != IntPtr.Zero) FixtureFree(error);
            if (result != 0) throw new IOException("Cannot initialize isolated SQLite fixture");
        }
        finally { FixtureClose(db); }
    }
}

