using System.Net;
using System.Text.Json;

namespace BoothFreeCollector;

internal static class SelfTest
{
    public static async Task Run(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output); Storage.Root = Path.Combine(output, "data");
        var passed = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed.Add(name); }
        try
        {
            var absentLibrary = new LibraryInfo(null, [], null);
            var destinationSettings = new Settings();
            Check(destinationSettings.DownloadFolder == Settings.DefaultDownloadFolder && destinationSettings.FollowLibraryFolder, "default is LocalAppData BOOTHLibraryManager with future following enabled");
            Check(DownloadEngine.ResolveRoot(destinationSettings, absentLibrary) == Settings.DefaultDownloadFolder, "standalone without Library Manager uses default directory");
            string installedFolder = Path.Combine(output, "manager-folder");
            Check(DownloadEngine.ResolveRoot(destinationSettings, new(installedFolder, [], null)) == installedFolder, "later installation is followed without restarting");
            string movedFolder = Path.Combine(output, "manager-moved");
            Check(DownloadEngine.ResolveRoot(destinationSettings, new(movedFolder, [], null)) == movedFolder, "manager folder changes are followed");
            destinationSettings.FollowLibraryFolder = false;
            Check(DownloadEngine.ResolveRoot(destinationSettings, new(movedFolder, [], null)) == Settings.DefaultDownloadFolder, "following off preserves independent directory");
            destinationSettings.FollowLibraryFolder = true;
            try { DownloadEngine.ResolveRoot(destinationSettings, new(null, [], "busy")); throw new Exception("silently changed folder on read error"); } catch (IOException) { passed.Add("read failure stops rather than silently switching directories"); }
            var state = new CollectorState { LastAttempt = DateTimeOffset.UtcNow };
            Check(!state.Due(state.LastAttempt.Value.AddHours(12).AddTicks(-1)) && state.Due(state.LastAttempt.Value.AddHours(12)), "12-hour boundary");
            Storage.Save("state.json", state); Check(!Storage.Load<CollectorState>("state.json").Due(DateTimeOffset.UtcNow), "schedule survives restart");
            Check(!DownloadEngine.AllowedPage("https://booth.pm.attacker.invalid/") && !DownloadEngine.AllowedFile("file:///C:/test"), "URL allowlist");
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
            var existingLibrary = new LibraryInfo(managerRoot, [], null);
            Check(existingLibrary.HasLocalDownload("1", "archive.zip", independentRoot), "manager-expanded archives are not downloaded again into independent destination");
            File.WriteAllText(Path.Combine(managerRoot, "b1", "incomplete.partial"), "unfinished");
            Check(!existingLibrary.HasLocalDownload("1", "different.zip", independentRoot), "other files in manager product do not hide a new free file");
            state = new(); FeedClient.Apply(new() { IncludeExisting = false }, state, fixture); Check(state.Queue.Count == 0 && state.Seen.Contains("1"), "initial baseline avoids mass downloads");
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
            settings.DownloadFolder = Path.Combine(output, "handoff"); state = new();
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
            var info = LibraryInfo.Read(); Check(info.Error is null, "official Library Manager read-only preference access");
            Check(!Directory.Exists(Path.Combine(Storage.Root, "browser")), "no browser profile or authentication store created");
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { ok = true, passed }, Storage.Json));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { ok = false, error = ex.ToString(), passed }, Storage.Json)); Environment.ExitCode = 1; }
    }
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request)); }
}
