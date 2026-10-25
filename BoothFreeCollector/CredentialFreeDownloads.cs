using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace BoothFreeCollector;

// No browser profile, cookie container, or login UI. Authentication stays in the user's browser.
public sealed class DownloadEngine(Action<string> log, HttpMessageHandler? handler = null, Action<string>? handoff = null) : IDisposable
{
    readonly HttpClient http = new(handler ?? new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
    public bool Ready => true;
    public bool NeedsLogin => false;
    public Task Initialize() => Task.CompletedTask;
    public void Stop() { }
    public void Resume() { }
    public static bool AllowedPage(string value) => Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme == "https" && u.UserInfo.Length == 0 &&
        (u.Host == "booth.pm" || u.Host.EndsWith(".booth.pm", StringComparison.OrdinalIgnoreCase));
    public static bool AllowedFile(string value) => AllowedPage(value) || Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme == "https" && u.UserInfo.Length == 0 && u.Host == "booth.pximg.net";
    public static IEnumerable<FreeLink> ParseLinks(string html, Product product)
    {
        var ids = product.Variants.Select(v => v.Id).ToHashSet();
        foreach (Match anchor in Regex.Matches(html, @"<a\b[^>]*>", RegexOptions.IgnoreCase))
        {
            string Attr(string name)
            {
                var m = Regex.Match(anchor.Value, "\\b" + name + "\\s*=\\s*([\"'])(.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                return WebUtility.HtmlDecode(m.Groups[2].Value);
            }
            string url = Attr("href");
            var match = Regex.Match(url, @"^https://booth\.pm/downloadables/(?<file>\d+)\?variation_id=(?<variant>\d+)$");
            if (!match.Success || !ids.Contains(match.Groups["variant"].Value)) continue;
            string name = Attr("title");
            if (name.Length > 0) yield return new(match.Groups["file"].Value, match.Groups["variant"].Value, SafeFileName(name), url);
        }
    }
    public async Task<bool> DownloadProduct(Product product, Settings settings, CollectorState state, CancellationToken ct)
    {
        if (!Feed.SafeId(product.Id) || product.Url != "https://booth.pm/ja/items/" + product.Id) throw new InvalidDataException("商品URLが不正です");
        var library = LibraryInfo.Read(); string root = ResolveRoot(settings, library);
        using var request = new HttpRequestMessage(HttpMethod.Get, product.Url);
        request.Headers.UserAgent.ParseAdd("BoothFreeCollector/2.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new IOException("商品ページを取得できません。既定ブラウザから商品を確認してください");
        var html = System.Text.Encoding.UTF8.GetString(await ReadBounded(response, 2 * 1024 * 1024, ct));
        var links = ParseLinks(html, product).DistinctBy(x => x.Id).ToArray();
        if (links.Length == 0) throw new IOException("無料リンクが見つかりません。販売条件を確認してください");
        foreach (var link in links)
        {
            string key = product.Id + "/" + link.Id;
            if (HasSaved(state, key) || library.HasLocalDownload(product.Id, link.Name, root)) { log("取得済み: " + link.Name); continue; }
            if (state.BrowserPending.ContainsKey(key)) { log("ブラウザの取得待ち: " + link.Name); return false; }
            using var fileRequest = new HttpRequestMessage(HttpMethod.Get, link.Url);
            fileRequest.Headers.UserAgent.ParseAdd("BoothFreeCollector/2.0");
            using var fileResponse = await http.SendAsync(fileRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)fileResponse.StatusCode is >= 300 and < 400 || fileResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                state.BrowserPending[key] = new(product.Id, link.Id, link.VariantId, link.Name, link.Url, DateTimeOffset.UtcNow);
                Storage.Save("state.json", state);
                if (settings.BrowserHandoff) { if (handoff is not null) handoff(link.Url); else Process.Start(new ProcessStartInfo(link.Url) { UseShellExecute = true }); }
                log("ブラウザの取得待ち。「取得済みファイルを登録」で整理できます: " + link.Name);
                return false;
            }
            fileResponse.EnsureSuccessStatusCode();
            if (fileResponse.Content.Headers.ContentType?.MediaType?.Contains("html") == true) throw new IOException("ファイルではなく確認ページが返されました");
            string dir = Path.Combine(root, "b" + product.Id); EnsureSafeDirectory(dir);
            string final = Path.Combine(dir, link.Name), temp = Path.Combine(dir, ".collector-" + Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                await using (var source = await fileResponse.Content.ReadAsStreamAsync(ct))
                await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[65536]; int count; long total = 0;
                    while ((count = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        total += count;
                        if (total > 2L * 1024 * 1024 * 1024 || new DriveInfo(Path.GetPathRoot(dir)!).AvailableFreeSpace < 512L * 1024 * 1024) throw new IOException("ファイル容量または空き容量の上限です");
                        await target.WriteAsync(buffer.AsMemory(0, count), ct);
                    }
                    if (total == 0) throw new IOException("ファイルが空です");
                }
                File.Move(temp, final, false); Remember(state, key, final); log("保存しました: " + link.Name);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        return true;
    }
    public static string ResolveRoot(Settings settings, LibraryInfo? library = null)
    {
        library ??= LibraryInfo.Read();
        string root = settings.ResolveFolder(library);
        if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\")) throw new IOException("ローカルの保存先を指定してください");
        return root;
    }
    public static bool HasSaved(CollectorState state, string key)
    {
        if (!state.Files.TryGetValue(key, out var saved) || !File.Exists(saved.Path)) return false;
        using var file = File.OpenRead(saved.Path);
        if (file.Length != saved.Size || Convert.ToHexString(SHA256.HashData(file)) != saved.Sha256) throw new IOException("取得済みファイルが変更されています。上書きしません");
        return true;
    }
    public static void Remember(CollectorState state, string key, string path)
    {
        using var file = File.OpenRead(path);
        state.Files[key] = new(path, Convert.ToHexString(SHA256.HashData(file)), file.Length);
        state.BrowserPending.Remove(key); Storage.Save("state.json", state);
    }
    public static void ImportFile(Settings settings, CollectorState state, string key, string productId, string filename, string source)
    {
        if (!Feed.SafeId(productId) || !key.StartsWith(productId + "/")) throw new IOException("商品IDが不正です");
        filename = SafeFileName(filename);
        if (!Path.IsPathFullyQualified(source) || source.StartsWith(@"\\") || !File.Exists(source)) throw new IOException("取得済みファイルが見つかりません");
        EnsureNoLinks(source);
        var info = new FileInfo(source);
        if (info.Length is <= 0 or > 2147483648L || info.Name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) || info.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) throw new IOException("未完了または容量上限のファイルです");
        string root = ResolveRoot(settings), dir = Path.Combine(root, "b" + productId); EnsureSafeDirectory(dir);
        string final = Path.Combine(dir, filename);
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(final), StringComparison.OrdinalIgnoreCase)) { Remember(state, key, final); return; }
        if (File.Exists(final))
        {
            using var old = File.OpenRead(final); using var incoming = File.OpenRead(source);
            if (!SHA256.HashData(old).SequenceEqual(SHA256.HashData(incoming))) throw new IOException("同名で内容の違うファイルがあります。上書きしません");
            Remember(state, key, final); return;
        }
        string temp = Path.Combine(dir, ".collector-" + Guid.NewGuid().ToString("N") + ".partial");
        try { File.Copy(source, temp, false); File.Move(temp, final, false); Remember(state, key, final); File.Delete(source); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    static async Task<byte[]> ReadBounded(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        using var result = new MemoryStream(); await using var input = await response.Content.ReadAsStreamAsync(ct);
        byte[] buffer = new byte[65536]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0) { if (result.Length + count > limit) throw new IOException("応答の容量上限です"); result.Write(buffer, 0, count); }
        return result.ToArray();
    }
    public static void EnsureNoLinks(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null) { if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("リンク先へのアクセスは行いません"); current = Path.GetDirectoryName(current); }
    }
    public static void EnsureSafeDirectory(string path) { EnsureNoLinks(path); Directory.CreateDirectory(path); }
    public static bool MatchesBrowserFile(string source, string relative, string browserFolder, string name)
    {
        string filename = Path.GetFileName(source);
        string pattern = "^" + Regex.Escape(Path.GetFileNameWithoutExtension(name)) + @" \([1-9]\d{0,8}\)" + Regex.Escape(Path.GetExtension(name)) + "$";
        if (!filename.Equals(name, StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(filename, pattern, RegexOptions.IgnoreCase)) return false;
        string parent = Path.GetDirectoryName(Path.GetFullPath(source))!.TrimEnd(Path.DirectorySeparatorChar);
        string stagedParent = Path.GetDirectoryName(relative)!.Replace('\\', '/');
        return parent.Replace('\\', '/').EndsWith('/' + stagedParent, StringComparison.OrdinalIgnoreCase) ||
            parent.Equals(Path.GetFullPath(browserFolder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }
    public static string SafeFileName(string value)
    {
        string name = Path.GetFileName(value).TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Length > 180 || Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) throw new IOException("安全に保存できないファイル名です");
        return name;
    }
    public void Dispose() => http.Dispose();
    public sealed record FreeLink(string Id, string VariantId, string Name, string Url);
}
