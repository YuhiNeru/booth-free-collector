using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace BoothFreeCollector;

internal static class AppStorageLocation
{
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string Locator => Path.Combine(Home, ".booth-free-collector-location.json");
    internal static string PersistentRoot => Path.Combine(Home, ".booth-free-collector");
    internal static string LegacyRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoothFreeCollector");
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    internal static string ResolveDirectory(string path)
    {
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        var result = new StringBuilder(32768);
        if (handle.IsInvalid || GetFinalPathNameByHandle(handle, result, (uint)result.Capacity, 0) == 0)
            throw new IOException("アプリ専用の設定保存場所を確認できません");
        string actual = result.ToString();
        return actual.StartsWith(@"\\?\") ? actual[4..] : actual;
    }
    static bool AllowedRoot(string root)
    {
        root = Path.GetFullPath(root);
        if (root.Equals(PersistentRoot, StringComparison.OrdinalIgnoreCase) || root.Equals(LegacyRoot, StringComparison.OrdinalIgnoreCase)) return true;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var parts = Path.GetRelativePath(Path.Combine(local, "Packages"), root).Split(Path.DirectorySeparatorChar);
        return parts.Length == 4 && System.Text.RegularExpressions.Regex.IsMatch(parts[0], @"^[A-Za-z0-9._-]+$") && parts[0] is not "." and not ".." &&
            parts[1].Equals("LocalCache", StringComparison.OrdinalIgnoreCase) && parts[2].Equals("Local", StringComparison.OrdinalIgnoreCase) &&
            parts[3].Equals("BoothFreeCollector", StringComparison.OrdinalIgnoreCase);
    }
    internal static string ReadRoot()
    {
        if (!File.Exists(Locator)) return Directory.Exists(PersistentRoot) ? PersistentRoot : LegacyRoot;
        var location = JsonSerializer.Deserialize<Location>(File.ReadAllText(Locator), Storage.Json);
        if (location is not { Schema: 1 } || string.IsNullOrWhiteSpace(location.Root) || !AllowedRoot(location.Root) || !Directory.Exists(location.Root))
            throw new IOException("設定の保存場所を確認できません。別の場所へ新規作成せず停止しました");
        DownloadEngine.EnsureNoLinks(location.Root);
        return location.Root;
    }
    internal static string Initialize()
    {
        if (File.Exists(Locator)) return ReadRoot();
        // A fixed user-home location is independent of the launching app's
        // AppData redirection. This moves Collector data, never BOOTH files.
        string source = Directory.Exists(LegacyRoot) ? ResolveDirectory(LegacyRoot) : LegacyRoot;
        if (!AllowedRoot(source)) throw new IOException("以前のアプリ設定の場所を確認できません");
        BrowserBridge.Unregister(LegacyRoot);
        string root = Relocate(source, PersistentRoot);
        string temp = Locator + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Location { Schema = 1, Root = root }, Storage.Json));
        File.Move(temp, Locator, false);
        return root;
    }
    internal static string Relocate(string source, string destination)
    {
        source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
        if (source.StartsWith(@"\\") || destination.StartsWith(@"\\") || source.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("設定の引き継ぎ先を確認できません");
        DownloadEngine.EnsureNoLinks(source); DownloadEngine.EnsureNoLinks(destination);
        if (!Directory.Exists(source)) { Directory.CreateDirectory(destination); return destination; }
        if (Directory.Exists(destination)) throw new IOException("設定保存先が複数あります。上書きせず停止しました");
        // On another drive, pin the actual existing app-owned directory rather
        // than copying a live authentication profile or silently starting over.
        if (!string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase)) return source;
        var pending = new Stack<string>(); pending.Push(source);
        while (pending.TryPop(out string? directory))
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("設定領域のリンクを確認してください");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else { using var unused = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.None); }
            }
        // Same-volume rename is atomic, preserves authentication in one place,
        // and leaves the source intact if an outstanding browser lock rejects it.
        Directory.Move(source, destination);
        return destination;
    }
    sealed class Location { public Location() { } public int Schema { get; set; } public string Root { get; set; } = ""; }
}
