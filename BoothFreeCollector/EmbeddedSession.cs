using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BoothFreeCollector;

// Authentication is kept in this app's WebView2 profile, never copied from Chrome.
// No host objects, web messages, cookie export, or arbitrary fetch endpoint is exposed.
public sealed class EmbeddedSession : IDisposable
{
    public const string ArticleUrl = "https://zenn.dev/yuhineru/articles/booth-free-collector-design";
    public const string LoginWarning = "サブアカウントでお願いします";
    public WebView2 LoginView { get; } = new() { Dock = DockStyle.Fill };
    public WebView2 Worker { get; } = new() { Visible = false, Size = new Size(1000, 700), TabStop = false };
    readonly SemaphoreSlim gate = new(1, 1);
    CoreWebView2Environment? environment;
    string? pendingEndpoint;
    TaskCompletionSource<string>? pendingImport;
    bool suspended, forgetting;
    public bool Ready { get; private set; }
    public bool NeedsLogin { get; private set; } = true;
    public event Action? Changed;
    public string ProfileFolder => Path.Combine(Storage.Root, "session-profile");
    static readonly HashSet<string> LoginHosts = ["booth.pm", "accounts.booth.pm", "accounts.pixiv.net", "oauth.secure.pixiv.net", "www.pixiv.net"];
    public static bool AllowedNavigation(string value) => value == "about:blank" ||
        Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme == "https" && u.UserInfo.Length == 0 && u.IsDefaultPort && LoginHosts.Contains(u.Host);
    public static bool AllowedRequest(string value) => Uri.TryCreate(value, UriKind.Absolute, out var u) &&
        u.Scheme == "https" && u.Host == "booth.pm" && u.UserInfo.Length == 0 && u.IsDefaultPort && u.Fragment.Length == 0 &&
        (Regex.IsMatch(u.AbsolutePath, @"^/ja/items/[1-9]\d{0,19}$") && u.Query.Length == 0 || ValidEntrance(value));
    public static bool ValidEntrance(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Host != "booth.pm" ||
            !u.IsDefaultPort || u.UserInfo.Length != 0 || u.Fragment.Length != 0 || !Regex.IsMatch(u.AbsolutePath, @"^/downloadables/[1-9]\d{0,19}/deeplink$")) return false;
        var parts = u.Query.TrimStart('?').Split('&');
        return parts.Length == 2 && parts.Count(p => p == "client=booth-library-manager") == 1 &&
            parts.Count(p => Regex.IsMatch(p, @"^variation_id=[1-9]\d{0,19}$")) == 1;
    }
    public async Task InitializeAsync()
    {
        DownloadEngine.EnsureSafeDirectory(ProfileFolder);
        environment ??= await CoreWebView2Environment.CreateAsync(null, ProfileFolder,
            new CoreWebView2EnvironmentOptions { ExclusiveUserDataFolderAccess = true });
        if (!Path.GetFullPath(environment.UserDataFolder).Equals(Path.GetFullPath(ProfileFolder), StringComparison.OrdinalIgnoreCase))
            throw new IOException("専用のログイン保存領域を確認できません");
        foreach (var view in new[] { LoginView, Worker })
        {
            _ = view.Handle;
            await view.EnsureCoreWebView2Async(environment);
            var core = view.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.SavesInProfile = false; };
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.NewWindowRequested += (_, e) => { e.Handled = true; if (AllowedNavigation(e.Uri)) core.Navigate(e.Uri); };
            core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (suspended || forgetting) e.Response = environment.CreateWebResourceResponse(Stream.Null, 204, "No Content", "");
            };
            core.NavigationStarting += (_, e) =>
            {
                if ((suspended || forgetting) && e.Uri != "about:blank") { e.Cancel = true; return; }
                if (e.Uri.StartsWith("booth-library-manager:", StringComparison.OrdinalIgnoreCase))
                { e.Cancel = true; if (view == Worker && pendingEndpoint is not null) CaptureImport(e.Uri); return; }
                if (!AllowedNavigation(e.Uri)) e.Cancel = true;
            };
            core.FrameNavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "about")) e.Cancel = true;
            };
            if (view == Worker)
            {
                core.Settings.IsScriptEnabled = false;
                await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
                await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\":true}");
                await core.CallDevToolsProtocolMethodAsync("Network.setBypassServiceWorker", "{\"bypass\":true}");
                core.WebResourceResponseReceived += (_, e) =>
                {
                    if (pendingEndpoint is null || e.Request.Uri != pendingEndpoint) return;
                    if (e.Response.Headers.Contains("Location")) CaptureImport(e.Response.Headers.GetHeader("Location"));
                };
                core.WebResourceRequested += (_, e) =>
                {
                    if (suspended || forgetting) return;
                    if (AllowedRequest(e.Request.Uri))
                    { e.Request.Headers.SetHeader("Cache-Control", "no-store"); e.Request.Headers.SetHeader("Pragma", "no-cache"); }
                    if (e.ResourceContext is CoreWebView2WebResourceContext.Image or CoreWebView2WebResourceContext.Media or CoreWebView2WebResourceContext.Font)
                        e.Response = environment.CreateWebResourceResponse(Stream.Null, 204, "No Content", "");
                };
            }
        }
        Ready = true;
    }
    void CaptureImport(string value)
    {
        if (!value.StartsWith("booth-library-manager:", StringComparison.OrdinalIgnoreCase)) return;
        try { ManagerIntegration.ValidateLink(value); pendingImport?.TrySetResult(value); }
        catch (IOException) { pendingImport?.TrySetException(new IOException("公式取り込み情報の形式を確認できません")); }
    }
    public void OpenLogin()
    {
        if (!Ready) throw new IOException("ログイン画面の準備を待ってください");
        LoginView.CoreWebView2.Navigate("https://booth.pm/users/sign_in");
    }
    async Task Navigate(WebView2 view, string url, CancellationToken ct)
    {
        if (!AllowedNavigation(url)) throw new IOException("許可されていない移動先です");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        void Starting(object? sender, CoreWebView2NavigationStartingEventArgs e) { if (navigationId is null && e.Uri == url) navigationId = e.NavigationId; }
        void Finished(object? sender, CoreWebView2NavigationCompletedEventArgs e) { if (e.NavigationId == navigationId) completion.TrySetResult(e.IsSuccess); }
        view.CoreWebView2.NavigationStarting += Starting;
        view.CoreWebView2.NavigationCompleted += Finished;
        try
        {
            view.CoreWebView2.Navigate(url);
            if (!await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), ct)) throw new IOException("BOOTHの画面を読み込めません。ログイン画面を確認してください");
        }
        finally { view.CoreWebView2.NavigationStarting -= Starting; view.CoreWebView2.NavigationCompleted -= Finished; }
    }
    public async Task<bool> CheckLoginAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            NeedsLogin = true;
            await Navigate(Worker, "https://accounts.booth.pm/library/free_downloads?page=1", ct);
            var source = new Uri(Worker.CoreWebView2.Source);
            NeedsLogin = source.Scheme != "https" || source.Host != "accounts.booth.pm" || source.AbsolutePath != "/library/free_downloads";
            if (!NeedsLogin)
            {
                // A challenge/error page can return 200 at the same URL. Check
                // the library's own navigation without reading account details.
                const string expression = "(() => { const paths = new Set(Array.from(document.querySelectorAll('a[href]')).map(a => { try { const u = new URL(a.getAttribute('href'), location.href); return u.origin === location.origin ? u.pathname : ''; } catch { return ''; } }).filter(p => p === '/library' || p.startsWith('/library/'))); return paths.has('/library/free_downloads') && paths.size >= 2; })()";
                string answer = await Worker.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression, returnByValue = true }));
                using var parsed = JsonDocument.Parse(answer);
                NeedsLogin = !parsed.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.True;
            }
            if (!NeedsLogin) await Navigate(Worker, "https://booth.pm/", ct);
            RememberStatus(); return !NeedsLogin;
        }
        catch (OperationCanceledException) { NeedsLogin = true; RememberStatus(); throw; }
        catch (Exception)
        { NeedsLogin = true; RememberStatus(); return false; }
        finally { gate.Release(); }
    }
    void RememberStatus()
    {
        Storage.Save("session-status.json", new { needs_login = NeedsLogin, checked_at = DateTimeOffset.UtcNow,
            runtime = environment?.BrowserVersionString });
        Changed?.Invoke();
    }
    static bool AtBoothOrigin(CoreWebView2 core) => Uri.TryCreate(core.Source, UriKind.Absolute, out var u) &&
        u.Scheme == "https" && u.Host == "booth.pm" && u.IsDefaultPort && u.UserInfo.Length == 0;
    async Task<JsonElement> Fetch(string url, CancellationToken ct)
    {
        if (!AllowedRequest(url)) throw new IOException("取得対象のURL形式が不正です");
        if (!AtBoothOrigin(Worker.CoreWebView2)) await Navigate(Worker, "https://booth.pm/", ct);
        if (!AtBoothOrigin(Worker.CoreWebView2)) throw new IOException("BOOTHの通信元を確認できません");
        string script = $$$"""
        (async () => {
          if(location.origin !== 'https://booth.pm') return {error:true};
          try {
            const r=await fetch({{{JsonSerializer.Serialize(url)}}},{credentials:'include',redirect:'manual',cache:'no-store',signal:AbortSignal.timeout(25000)});
            if(!r.body)return {status:r.status,text:''};
            const reader=r.body.getReader(),parts=[];let size=0;
            try{for(;;){const x=await reader.read();if(x.done)break;size+=x.value.length;if(size>2097152)return {error:true};parts.push(x.value);}}
            finally{await reader.cancel();}
            const bytes=new Uint8Array(size);let pos=0;for(const p of parts){bytes.set(p,pos);pos+=p.length;}
            return {status:r.status,text:new TextDecoder('utf-8',{fatal:true}).decode(bytes)};
          } catch { return {error:true}; }
        })()
        """;
        var parameters = JsonSerializer.Serialize(new { expression = script, awaitPromise = true, returnByValue = true, timeout = 30000 });
        string result = await Worker.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", parameters).WaitAsync(TimeSpan.FromSeconds(35), ct);
        // Never surface parser/CDP exceptions containing authenticated response text.
        try
        {
            using var doc = JsonDocument.Parse(result);
            if (doc.RootElement.TryGetProperty("exceptionDetails", out _) || !AtBoothOrigin(Worker.CoreWebView2)) throw new IOException("BOOTHへの通信を確認できません");
            var value = doc.RootElement.GetProperty("result").GetProperty("value");
            if (value.TryGetProperty("error", out _)) throw new IOException("BOOTHへの通信を確認できません。ログイン状態を確認してください");
            return value.Clone();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new IOException("BOOTHの応答形式を確認できません"); }
    }
    public async Task<string> GetProductHtmlAsync(string url, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var response = await Fetch(url, ct); int status = response.GetProperty("status").GetInt32();
            if (status is 0 or 301 or 302 or 303 or 307 or 308 or 401 or 403) { NeedsLogin = true; RememberStatus(); throw new IOException("アプリ内のBOOTHログインを確認してください"); }
            if (status != 200) throw new IOException("商品ページを取得できません");
            string html = response.GetProperty("text").GetString() ?? "";
            if (Regex.IsMatch(html, @"<script\b[^>]*\bsrc\s*=\s*['""][^'""]*/cdn-cgi/challenge-platform/", RegexOptions.IgnoreCase))
            {
                NeedsLogin = true; RememberStatus();
                throw new IOException("BOOTHのアクセス確認が必要です。アプリ内のログイン画面で確認してください");
            }
            return html;
        }
        finally { gate.Release(); }
    }
    public async Task<string> GetImportAsync(string fileId, string variantId, CancellationToken ct)
    {
        if (!Feed.SafeId(fileId) || !Feed.SafeId(variantId)) throw new IOException("取得対象のIDが不正です");
        await gate.WaitAsync(ct);
        try
        {
            pendingEndpoint = $"https://booth.pm/downloadables/{fileId}/deeplink?client=booth-library-manager&variation_id={variantId}";
            pendingImport = new(TaskCreationOptions.RunContinuationsAsynchronously);
            JsonElement? response = null;
            try { response = await Fetch(pendingEndpoint, ct); } catch (IOException) { }
            if (pendingImport.Task.IsCompleted) return await pendingImport.Task;
            if (response is JsonElement body && body.GetProperty("status").GetInt32() == 200)
            {
                string? candidate = FindImport(body.GetProperty("text").GetString() ?? "");
                if (candidate is not null) { ManagerIntegration.ValidateLink(candidate); return candidate; }
            }
            // Navigate only our dedicated hidden control, not the user's browser.
            Worker.CoreWebView2.Navigate(pendingEndpoint);
            try { return await pendingImport.Task.WaitAsync(TimeSpan.FromSeconds(25), ct); }
            catch (TimeoutException) { NeedsLogin = true; RememberStatus(); throw new IOException("取り込み情報を取得できません。アプリ内でログイン・アクセス確認を行ってください"); }
        }
        finally { pendingEndpoint = null; pendingImport = null; gate.Release(); }
    }
    static string? FindImport(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json); var found = new HashSet<string>(); int visited = 0;
            void Walk(JsonElement value, int depth)
            {
                if (++visited > 200 || depth > 6) return;
                if (value.ValueKind == JsonValueKind.String && value.GetString() is string s && s.StartsWith("booth-library-manager:", StringComparison.Ordinal)) found.Add(s);
                else if (value.ValueKind == JsonValueKind.Object) foreach (var p in value.EnumerateObject()) Walk(p.Value, depth + 1);
                else if (value.ValueKind == JsonValueKind.Array) foreach (var p in value.EnumerateArray()) Walk(p, depth + 1);
            }
            Walk(doc.RootElement, 0); return found.Count == 1 ? found.Single() : null;
        }
        catch (JsonException) { return null; }
    }
    public async Task ForgetAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            forgetting = true;
            pendingEndpoint = null; pendingImport = null;
            Worker.CoreWebView2.Stop(); LoginView.CoreWebView2.Stop();
            await Navigate(Worker, "about:blank", ct); await Navigate(LoginView, "about:blank", ct);
            await Worker.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            NeedsLogin = true; RememberStatus();
        }
        finally { forgetting = false; gate.Release(); }
    }
    public void Suspend()
    {
        suspended = true;
        if (!Ready) return;
        LoginView.Enabled = false;
        foreach (var view in new[] { Worker, LoginView })
            view.CoreWebView2.Stop();
    }
    public void Resume() { suspended = false; LoginView.Enabled = true; }
    public void Dispose() { LoginView.Dispose(); Worker.Dispose(); gate.Dispose(); }
}
