using Microsoft.Web.WebView2.Core;
using System.Text;
using System.Text.Json;

namespace BoothFreeCollector;

internal static class SessionUiVerification
{
    public static void Run(string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("新しいテスト専用フォルダを指定してください");
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE", output);
        Storage.Root = Path.Combine(output, "data");
        Storage.Save("settings.json", new Settings { SetupCompleted = true });
        Storage.Save("session-import-verification.json", new SessionImportVerification { Ok = true, PagePlanVerified = true });
        bool dependencyReady = false;
        ManualResetEventSlim? dependencyGate = null;
        int browserPid = 0;
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); }
        using var form = new MainForm(false, () =>
        {
            dependencyGate?.Wait(TimeSpan.FromSeconds(10));
            return new(dependencyReady, dependencyReady ? "テスト用の公式ライブラリ確認" : "公式ライブラリ未確認（テスト）", Path.Combine(output, "library"));
        });
        form.Shown += async (_, _) =>
        {
            try
            {
                var session = form.VerificationSession;
                await Task.Delay(200);
                Check(!session.Ready && !Directory.Exists(session.ProfileFolder), "missing official Manager does not initialize a login profile or start authentication");
                await form.SetPausedForVerification(true);
                Check(!Storage.Load<Settings>("settings.json").AutoDownload, "pause button persists disabled automatic acquisition");
                await form.SetPausedForVerification(false);
                Check(Storage.Load<Settings>("settings.json").AutoDownload && !session.Ready, "resume button restores automatic acquisition without bypassing unfinished setup");
                dependencyReady = true; await form.VerifyDependencyForTest();
                for (int n = 0; n < 200 && !session.Ready; n++) await Task.Delay(100);
                Check(session.Ready, "real WebView2 initializes in an isolated app profile");
                Check(await form.ActivateForVerification(), "reopening an existing tray instance restores its window without reinitializing setup");
                Check(!session.Worker.Visible && !session.Worker.TabStop, "background retrieval view is hidden from the app's visible controls and keyboard navigation");
                Check(Path.GetFullPath(session.Worker.CoreWebView2.Environment.UserDataFolder) == session.ProfileFolder, "profile is the test-specific folder, never Chrome's profile");
                Check(!session.Worker.CoreWebView2.Settings.AreHostObjectsAllowed && !session.Worker.CoreWebView2.Settings.IsWebMessageEnabled,
                    "web content cannot call host objects or native web-message handlers");
                Check(!session.Worker.CoreWebView2.Settings.IsScriptEnabled, "background retrieval does not execute page-provided scripts");
                Check(!session.LoginView.CoreWebView2.Settings.IsPasswordAutosaveEnabled && !session.LoginView.CoreWebView2.Settings.IsGeneralAutofillEnabled,
                    "password saving and autofill are disabled in the dedicated login view");
                form.ShowLoginForVerification(); await Task.Delay(200);
                await form.CapturePresentationForVerification(output);
                using (var heldDependency = new ManualResetEventSlim(false))
                {
                    dependencyGate = heldDependency;
                    Task pendingDependency = form.VerifyDependencyForTest();
                    try
                    {
                        Check(form.MeasurePresentationForVerification(output), "time and status updates preserve the queue binding, selection and screen position");
                        await Task.Delay(20);
                        Check(!pendingDependency.IsCompleted, "page navigation and UI messages remain responsive while settings verification is still running");
                    }
                    finally { heldDependency.Set(); }
                    await pendingDependency; dependencyGate = null;
                }
                using (var image = new Bitmap(form.Width, form.Height))
                { form.DrawToBitmap(image, new Rectangle(0, 0, form.Width, form.Height)); image.Save(Path.Combine(output, "login-notice.png")); }
                var login = session.LoginView.CoreWebView2;
                login.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                login.WebResourceRequested += (_, e) => e.Response = login.Environment.CreateWebResourceResponse(
                    new MemoryStream(Encoding.UTF8.GetBytes("<!doctype html><html><body>official login entrance fixture</body></html>")),
                    e.Request.Uri == "https://booth.pm/users/sign_in" ? 200 : 404, "OK", "Content-Type: text/html; charset=utf-8");
                var loginLoaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                login.NavigationCompleted += (_, e) => loginLoaded.TrySetResult(e.IsSuccess && e.HttpStatusCode == 200);
                await form.InvokeSetupForVerification(false);
                Check(!Storage.Load<Settings>("settings.json").SetupCompleted && !Storage.Load<SessionImportVerification>("session-import-verification.json").Ok,
                    "starting login invalidates stale setup and prior-account import proof");
                Check(await loginLoaded.Task.WaitAsync(TimeSpan.FromSeconds(15)) && login.Source == "https://booth.pm/users/sign_in",
                    "login button navigates to the official sign-in route instead of the accounts root 404");
                string fixtures = Path.Combine(output, "fixtures"); Directory.CreateDirectory(fixtures);
                File.WriteAllText(Path.Combine(fixtures, "index.html"), "<!doctype html><html><body>isolated fixture</body></html>");
                var core = session.Worker.CoreWebView2;
                core.NavigationCompleted += (_, e) => File.AppendAllText(Path.Combine(output, "fixture-navigation.txt"), $"{e.IsSuccess} {e.WebErrorStatus} {e.HttpStatusCode}\n");
                int responses = 0; bool libraryChallenge = true, productChallenge = false;
                core.WebResourceRequested += (_, e) =>
                {
                    var uri = new Uri(e.Request.Uri); string? body = null; int status = 200; string headers = "Content-Type: text/html; charset=utf-8";
                    if (uri.Host == "booth.pm" && uri.AbsolutePath == "/") body = "<!doctype html><html><body>isolated fixture</body></html>";
                    if (uri.Host == "accounts.booth.pm" && uri.AbsolutePath == "/library/free_downloads") body = libraryChallenge
                        ? "<!doctype html><html><body>access confirmation required</body></html>"
                        : "<!doctype html><html><body><a href='/library'>purchases</a><a href='/library/free_downloads'>free</a></body></html>";
                    if (uri.AbsolutePath == "/ja/items/1") body = productChallenge
                        ? "<html><script src='/cdn-cgi/challenge-platform/fixture'></script></html>"
                        : "<a href=\"https://booth.pm/downloadables/10?variation_id=2\" title=\"fixture.zip\">free</a>";
                    if (uri.AbsolutePath.Contains("/deeplink"))
                    {
                        string import = "booth-library-manager://item-import?item_id=1&variation_id=2&downloadable_filename=fixture.zip&dlurl=https%3A%2F%2Fs6.booth.pm%2Ffixture";
                        if (uri.AbsolutePath.Contains("/11/")) { status = 302; headers = "Location: " + import; body = ""; }
                        else { body = JsonSerializer.Serialize(new { uri = import }); headers = "Content-Type: application/json"; }
                    }
                    if (body is null) { body = ""; status = 404; }
                    responses++; e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(body)), status, status == 200 ? "OK" : "Found", headers);
                };
                Check(!await session.CheckLoginAsync(CancellationToken.None), "a status-200 access challenge at the library URL cannot complete setup");
                libraryChallenge = false;
                Check(await session.CheckLoginAsync(CancellationToken.None), "authenticated library document is verified before setup can continue");
                var cookies = core.CookieManager;
                var fixtureCookie = cookies.CreateCookie("collector_fixture", "synthetic-only", ".booth.pm", "/"); fixtureCookie.IsSecure = true; fixtureCookie.IsHttpOnly = true;
                cookies.AddOrUpdateCookie(fixtureCookie);
                string html = await session.GetProductHtmlAsync("https://booth.pm/ja/items/1", CancellationToken.None);
                Check(SessionDownloadQueue.Plan(html, new Product { Id = "1", Variants = [new() { Id = "2" }] }).Length == 1,
                    "authenticated product fetch and free-link plan run inside real WebView2 with no external request");
                string jsonImport = await session.GetImportAsync("10", "2", CancellationToken.None);
                ManagerIntegration.ValidateBoundLink(jsonImport, "1", "10", "2", "fixture.zip");
                Check(jsonImport.StartsWith("booth-library-manager://item-import"), "JSON import capability is captured in memory and validated");
                string redirectImport = await session.GetImportAsync("11", "2", CancellationToken.None);
                Check(redirectImport == jsonImport && responses >= 3, "redirect Location import is captured without opening an external application");
                Check((await cookies.GetCookiesAsync("https://booth.pm")).Any(c => c.Name == "collector_fixture"), "synthetic session is isolated in this profile");
                productChallenge = true;
                bool accessBlocked = false;
                try { await session.GetProductHtmlAsync("https://booth.pm/ja/items/1", CancellationToken.None); }
                catch (IOException) { accessBlocked = session.NeedsLogin; }
                Check(accessBlocked, "a product access challenge pauses authenticated work instead of trying the remaining queue");
                dependencyReady = false; await form.VerifyDependencyForTest();
                Check(form.LogoutAvailableForVerification, "loss of official Manager does not disable removal of dedicated credentials");
                await form.InvokeSetupForVerification(true);
                Check(!(await cookies.GetCookiesAsync("https://booth.pm")).Any(c => c.Name == "collector_fixture"), "logout clears the dedicated authentication data");
                var savedSettings = Storage.Load<Settings>("settings.json");
                Check(!savedSettings.SetupCompleted && !savedSettings.DedicatedSessionEnabled && !Storage.Load<SessionImportVerification>("session-import-verification.json").Ok,
                    "logout invalidates setup and prior import proof before any subsequent automatic acquisition");
                string applicationJson = string.Join("", Directory.EnumerateFiles(Storage.Root, "*.json").Select(File.ReadAllText));
                Check(!applicationJson.Contains("synthetic-only") && !applicationJson.Contains("dlurl="), "session secret and import capability are absent from application JSON state");
                var persisted = cookies.CreateCookie("cold_cleanup_fixture", "synthetic-cold", ".booth.pm", "/");
                persisted.IsSecure = true; persisted.Expires = DateTime.UtcNow.AddHours(1);
                cookies.AddOrUpdateCookie(persisted);
                browserPid = (int)core.BrowserProcessId;
                savedSettings.DedicatedSessionEnabled = true; Storage.Save("settings.json", savedSettings);
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { ok = true, passed = checks }, Storage.Json));
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { ok = false, error = ex.ToString(), passed = checks }, Storage.Json));
                Environment.ExitCode = 1;
            }
            finally { form.CloseForVerification(); }
        };
        Application.Run(form);
        if (Environment.ExitCode != 0) return;
        try
        {
            // Reopen the saved fixture profile with Manager absent. A cold
            // cleanup must work without performing a login or a network fetch.
            try { using var browser = System.Diagnostics.Process.GetProcessById(browserPid); if (!browser.WaitForExit(10000)) throw new IOException("Fixture browser did not close"); }
            catch (ArgumentException) { }
            using var cleanup = new MainForm(false, () => new(false, "公式ライブラリ未確認（テスト）"));
            cleanup.BeforeForgetForVerification = async () => Check(
                (await cleanup.VerificationSession.Worker.CoreWebView2.CookieManager.GetCookiesAsync("https://booth.pm")).Any(c => c.Name == "cold_cleanup_fixture"),
                "cold cleanup starts with a fixture cookie actually persisted across browser restart");
            cleanup.Shown += async (_, _) =>
            {
                try
                {
                    await cleanup.WaitSetupIdleForVerification();
                    Check(!cleanup.VerificationSession.Ready && cleanup.LogoutAvailableForVerification,
                        "cold restart without Manager exposes credential cleanup without initializing authentication");
                    await cleanup.InvokeSetupForVerification(true);
                    Check(!(await cleanup.VerificationSession.Worker.CoreWebView2.CookieManager.GetCookiesAsync("https://booth.pm")).Any(c => c.Name == "cold_cleanup_fixture"),
                        "cold cleanup removes the persisted fixture cookie while Manager remains absent");
                    var saved = Storage.Load<Settings>("settings.json");
                    Check(!saved.DedicatedSessionEnabled && !saved.SetupCompleted,
                        "cold credential cleanup leaves acquisition disabled");
                    File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { ok = true, passed = checks }, Storage.Json));
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { ok = false, error = ex.ToString(), passed = checks }, Storage.Json));
                    Environment.ExitCode = 1;
                }
                finally { cleanup.CloseForVerification(); }
            };
            Application.Run(cleanup);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { ok = false, error = ex.ToString(), passed = checks }, Storage.Json));
            Environment.ExitCode = 1;
        }
    }
}
