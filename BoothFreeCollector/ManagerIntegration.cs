using System.Diagnostics;

namespace BoothFreeCollector;

public static class ManagerIntegration
{
    public static object DescribeLink(string link)
    {
        ValidateLink(link);
        var fields = new List<string>(); var destinations = new List<object>();
        foreach (string part in new Uri(link).Query[1..].Split('&'))
        {
            string[] pair = part.Split('=', 2);
            string key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            fields.Add(key is "item_id" or "variation_id" or "order_id" or "dlurl" or "dlurls[]" or "downloadable_filename" ? key : "unknown");
            if (key is "dlurl" or "dlurls[]")
            {
                string value = Uri.UnescapeDataString((pair.Length == 2 ? pair[1] : "").Replace('+', ' '));
                bool valid = Uri.TryCreate(value, UriKind.Absolute, out var target);
                string host = valid ? target!.Host : "";
                destinations.Add(new { host = host is "booth.pm" or "booth.pximg.net" ||
                        System.Text.RegularExpressions.Regex.IsMatch(host, @"^s[1-9]\d{0,2}\.booth\.pm$") ? host : "other",
                    scheme = valid && target!.Scheme is "https" or "http" ? target.Scheme : "other",
                    value_kind = value.StartsWith("https%", StringComparison.OrdinalIgnoreCase) ? "encoded_https" :
                        value.StartsWith('{') || value.StartsWith('[') ? "json" : valid ? "uri" : "other",
                    downloadable_path = valid && System.Text.RegularExpressions.Regex.IsMatch(target!.AbsolutePath, @"^/downloadables/\d+$") });
            }
        }
        return new { fields, destinations };
    }
    public static void ValidateBoundLink(string link, string productId, string fileId, string variantId, string name)
    {
        ValidateLink(link);
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string part in new Uri(link).Query[1..].Split('&'))
        {
            string[] pair = part.Split('=', 2);
            string key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            string value = Uri.UnescapeDataString((pair.Length == 2 ? pair[1] : "").Replace('+', ' '));
            if (key is not ("item_id" or "variation_id" or "order_id" or "dlurl" or "dlurls[]" or "downloadable_filename"))
                throw new IOException("公式取り込みリンクに未対応の項目があります");
            if (!values.TryGetValue(key, out var list)) values[key] = list = [];
            list.Add(value);
        }
        if (!values.TryGetValue("item_id", out var items) || items.Count != 1 || items[0] != productId ||
            values.Where(p => p.Key != "dlurls[]").Any(p => p.Value.Count != 1) ||
            !values.TryGetValue("variation_id", out var variants) || variants[0] != variantId ||
            values.TryGetValue("downloadable_filename", out var names) && names[0] != name ||
            values.TryGetValue("order_id", out var orders) && orders[0].Length > 0 && !Feed.SafeId(orders[0]))
            throw new IOException("公式取り込みリンクと取得予約が一致しません");
        var downloads = values.Where(p => p.Key is "dlurl" or "dlurls[]").SelectMany(p => p.Value).ToArray();
        if (downloads.Length != 1) throw new IOException("単一ファイルの公式取り込みリンクを確認できません");
        if (!Uri.TryCreate(downloads[0], UriKind.Absolute, out var download) || download.Scheme != "https" ||
            !download.IsDefaultPort || download.UserInfo.Length != 0 || download.Fragment.Length != 0 ||
            downloads[0].Any(char.IsControl)) throw new IOException("公式取り込み先が不正です");
        bool ordinary = download.Host == "booth.pm" && download.AbsolutePath == "/downloadables/" + fileId;
        bool cdn = download.Host == "booth.pximg.net" &&
            (Uri.UnescapeDataString(download.Segments.Last()) == name || names is not null);
        // BOOTH's authenticated response uses sN.booth.pm storage URLs with
        // opaque object names, plus the explicit downloadable_filename. Require
        // that filename and the product/variant above to match the ticket.
        bool storage = System.Text.RegularExpressions.Regex.IsMatch(download.Host, @"^s[1-9]\d{0,2}\.booth\.pm$") && names is not null;
        if (!ordinary && !cdn && !storage) throw new IOException("公式取り込み先と無料ファイルの予約が一致しません");
    }
    public static void ValidateLink(string link)
    {
        if (link.Length > 16000 || !Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
            uri.Scheme != "booth-library-manager" || uri.Host != "item-import" ||
            (uri.AbsolutePath != "" && uri.AbsolutePath != "/") || uri.Port != -1 ||
            uri.Fragment.Length != 0 || uri.Query.Length == 0 ||
            uri.UserInfo.Length != 0 || link.Any(c => char.IsControl(c)))
            throw new IOException("公式Library Managerの取り込みリンクではありません");
    }
    public static string RunningExecutable()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcessesByName("booth_library_manager_client"))
        {
            using (process)
            {
                string? path = process.MainModule?.FileName;
                if (path is not null && File.Exists(path))
                {
                    found.Add(path);
                    if (process.MainWindowHandle != IntPtr.Zero) visible.Add(path);
                }
            }
        }
        return SelectExecutable(found, visible);
    }
    internal static string SelectExecutable(IEnumerable<string> running, IEnumerable<string> visible)
    {
        var windows = visible.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var all = running.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (windows.Length == 1) return windows[0];
        if (windows.Length == 0 && all.Length == 1) return all[0];
        throw new IOException("公式Library Managerを1つ起動してから再開してください");
    }
    public static bool IsAvailable()
    {
        try { _ = RunningExecutable(); return true; }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }
    public static void Send(string link, string productId, string fileId, string variantId, string name)
    {
        ValidateBoundLink(link, productId, fileId, variantId, name);
        // Use the running official application, including its tray state. A stale protocol handler
        // can point to another installation with a different settings database.
        string executable = RunningExecutable();
        using var verified = OfficialExecutableTrust.OpenVerified(executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(link);
        Process.Start(start)?.Dispose();
    }
}
