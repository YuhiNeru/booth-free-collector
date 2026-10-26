using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace BoothFreeCollector;

// A replaced pathname can refer to a different DB than the one the official app
// still has open. Read that open file, never the application's memory or cookies.
public sealed class ActiveLibraryDatabase : IDisposable
{
    public string Path { get; }
    public bool Live { get; }
    public LibraryInfo? Cached { get; }
    readonly string? snapshotHash;
    bool settingsCached;
    static DateTimeOffset lastCleanup;
    ActiveLibraryDatabase(string path, bool live, string? hash = null, LibraryInfo? cached = null)
    { Path = path; Live = live; snapshotHash = hash; Cached = cached; settingsCached = cached is not null; }
    [StructLayout(LayoutKind.Sequential)]
    struct Entry
    {
        public IntPtr Object; public nuint ProcessId, Handle; public uint Access;
        public ushort Trace, Type; public uint Attributes, Reserved;
    }
    [DllImport("ntdll")] static extern int NtQuerySystemInformation(int kind, IntPtr data, int size, out int needed);
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32", SetLastError = true)] static extern bool DuplicateHandle(IntPtr source, IntPtr handle, IntPtr target, out SafeFileHandle copy, uint access, bool inherit, uint options);
    [DllImport("kernel32")] static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder name, uint size, uint flags);
    public static ActiveLibraryDatabase Open(string dbPath)
    {
        // Tests use an explicit, isolated DB. A test app never inspects another
        // test process or the user's official process for its fixture settings.
        if (Environment.GetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE") is string testRoot)
            return new(System.IO.Path.Combine(testRoot, "manager.db"), false);
        CleanupOldSnapshots();
        var pids = Process.GetProcessesByName("booth_library_manager_client").Select(p => { using (p) return p.Id; }).ToHashSet();
        return OpenForProcesses(dbPath, pids);
    }
    internal static bool IsSettingsPath(string openedPath, string expectedPath, string localAppData)
    {
        if (openedPath.StartsWith(@"\\?\")) openedPath = openedPath[4..];
        if (openedPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase)) return true;
        if (!expectedPath.EndsWith(@"\pm.booth.library-manager\data.db", StringComparison.OrdinalIgnoreCase)) return false;
        // Packaged desktop launchers can virtualize Roaming into their own
        // LocalCache. Only accept this exact settings filename, actually held
        // open by the official process, inside the current user's Packages.
        string root = System.IO.Path.Combine(localAppData, "Packages");
        string relative = System.IO.Path.GetRelativePath(root, openedPath);
        string[] parts = relative.Split(System.IO.Path.DirectorySeparatorChar);
        return parts.Length == 5 && System.Text.RegularExpressions.Regex.IsMatch(parts[0], @"^[A-Za-z0-9._-]+$") && parts[0] is not "." and not ".." &&
            parts[1].Equals("LocalCache", StringComparison.OrdinalIgnoreCase) && parts[2].Equals("Roaming", StringComparison.OrdinalIgnoreCase) &&
            parts[3].Equals("pm.booth.library-manager", StringComparison.OrdinalIgnoreCase) && parts[4].Equals("data.db", StringComparison.OrdinalIgnoreCase);
    }
    internal static ActiveLibraryDatabase OpenForProcesses(string dbPath, HashSet<int> pids)
    {
        string cachePath = System.IO.Path.Combine(Storage.Root, "manager-current.db");
        if (pids.Count == 0)
        {
            var known = Storage.Load<SourceFingerprint>("manager-source.json");
            if (new[] { known.Active, known.Disk }.Any(value => value is not null &&
                !System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-F0-9]{64}$")))
                throw new IOException("Library Managerの設定参照情報を再確認してください");
            if (known.Disk is not null && known.Active is not null && File.Exists(dbPath) &&
                Hash(File.ReadAllBytes(dbPath)) == known.Disk && known.Active != known.Disk)
            {
                var cached = Storage.Load<SettingsCache>("manager-settings.json");
                if (cached.Source == known.Active && cached.Folder is not null &&
                    System.IO.Path.IsPathFullyQualified(cached.Folder) && !cached.Folder.StartsWith(@"\\") &&
                    cached.Registered is not null && cached.Registered.All(Feed.SafeId))
                    return new("", false, cached: new(cached.Folder, cached.Registered, null));
                // Migrate the previous verified full-DB cache without ever
                // accepting the stale pathname's different destination.
                if (File.Exists(cachePath) && Hash(File.ReadAllBytes(cachePath)) == known.Active)
                    return new(cachePath, false, known.Active);
                throw new IOException("確認済みのLibrary Manager設定を再取得するため公式アプリを起動してください");
            }
            return new(dbPath, false);
        }
        int size = 1024 * 1024; IntPtr data = IntPtr.Zero;
        byte[]? snapshot = null;
        try
        {
            while (true)
            {
                data = Marshal.AllocHGlobal(size);
                int status = NtQuerySystemInformation(64, data, size, out int needed);
                if (status == 0) break;
                Marshal.FreeHGlobal(data); data = IntPtr.Zero;
                if (status != unchecked((int)0xC0000004) || size > 128 * 1024 * 1024) throw new IOException("公式アプリの設定ファイルを確認できません");
                size = Math.Max(size * 2, needed + 65536);
            }
            long count = Marshal.ReadIntPtr(data).ToInt64(); int stride = Marshal.SizeOf<Entry>();
            foreach (int pid in pids)
            {
                IntPtr process = OpenProcess(0x40, false, pid);
                if (process == IntPtr.Zero) continue;
                try
                {
                    for (long i = 0; i < count; i++)
                    {
                        var item = Marshal.PtrToStructure<Entry>(data + checked(IntPtr.Size * 2 + (int)(i * stride)));
                        if (item.ProcessId != (nuint)pid || !DuplicateHandle(process, (IntPtr)item.Handle, GetCurrentProcess(), out var handle, 0, false, 2)) continue;
                        using (handle)
                        {
                            if (GetFileType(handle) != 1) continue;
                            var path = new StringBuilder(32768);
                            if (GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0) == 0 ||
                                !IsSettingsPath(path.ToString(), dbPath, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))) continue;
                            byte[] first = ReadStable(handle);
                            if (snapshot is not null && !snapshot.AsSpan().SequenceEqual(first)) throw new IOException("複数のLibrary Managerが異なる設定DBを使用中です");
                            snapshot = first;
                        }
                    }
                }
                finally { CloseHandle(process); }
            }
        }
        finally { if (data != IntPtr.Zero) Marshal.FreeHGlobal(data); }
        if (snapshot is null) throw new IOException("起動中のLibrary Managerの設定を確認できません。別の保存先は使用しません");
        Directory.CreateDirectory(Storage.Root);
        string activeHash = Hash(snapshot);
        Storage.Save("manager-source.json", new SourceFingerprint { Active = activeHash, Disk = TryDiskHash(dbPath) });
        string temp = System.IO.Path.Combine(Storage.Root, ".manager-read-" + Guid.NewGuid().ToString("N") + ".db");
        File.WriteAllBytes(temp, snapshot);
        return new(temp, true, activeHash);
    }
    public sealed class SourceFingerprint { public string? Active { get; set; } public string? Disk { get; set; } }
    public sealed class SettingsCache
    {
        public string? Source { get; set; }
        public string? Folder { get; set; }
        public HashSet<string> Registered { get; set; } = [];
    }
    public void Remember(LibraryInfo info)
    {
        if (snapshotHash is not null)
            Storage.Save("manager-settings.json", new SettingsCache { Source = snapshotHash, Folder = info.Folder, Registered = info.Registered });
        settingsCached = true;
    }
    internal static void CleanupOldSnapshots(bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - lastCleanup < TimeSpan.FromMinutes(1)) return;
        lastCleanup = now;
        if (!Directory.Exists(Storage.Root)) return;
        foreach (string path in Directory.EnumerateFiles(Storage.Root, ".manager-read-*.db", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || now - File.GetLastWriteTimeUtc(path) < TimeSpan.FromMinutes(10)) continue;
                using (var unused = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static string? TryDiskHash(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Hash(ReadStable(file.SafeFileHandle));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    internal static byte[] ReadStable(SafeFileHandle handle)
    {
        byte[] first = Copy(handle), second = Copy(handle);
        if (!first.AsSpan().SequenceEqual(second)) throw new IOException("Library Managerの設定変更中です。次の確認を待ちます");
        return first;
    }
    static byte[] Copy(SafeFileHandle handle)
    {
        long size = RandomAccess.GetLength(handle);
        if (size < 100 || size > 128 * 1024 * 1024) throw new IOException("公式設定DBの容量を確認できません");
        byte[] bytes = new byte[(int)size]; int offset = 0;
        while (offset < bytes.Length)
        {
            int read = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (read == 0) throw new IOException("公式設定DBを最後まで読み取れません");
            offset += read;
        }
        if (bytes[18] != 1 || bytes[19] != 1) throw new IOException("公式設定DBの書き込みが完了するまで待機します");
        return bytes;
    }
    public void Dispose()
    {
        if (Live && File.Exists(Path)) File.Delete(Path);
        // Only the private, derived cache owned by Collector is removed. The
        // official database is never opened for writing or deleted.
        string legacy = System.IO.Path.Combine(Storage.Root, "manager-current.db");
        if (settingsCached && File.Exists(legacy)) File.Delete(legacy);
    }
}
