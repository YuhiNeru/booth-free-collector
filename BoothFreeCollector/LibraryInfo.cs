using System.Runtime.InteropServices;
using System.Text;

namespace BoothFreeCollector;

// Uses Windows' SQLite, read-only. Never modifies the official app's database.
public sealed class ReadOnlySqlite : IDisposable
{
    IntPtr db;
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_blob(IntPtr statement, int col);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_text(IntPtr statement, int col);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_bytes(IntPtr statement, int col);
    public ReadOnlySqlite(string path)
    {
        if (sqlite3_open_v2(Encoding.UTF8.GetBytes(path + '\0'), out db, 1, IntPtr.Zero) != 0) { Dispose(); throw new IOException("Library Manager DBを読み取れません"); }
    }
    public List<byte[][]> Query(string sql, int columns, bool blob = false)
    {
        if (sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes(sql + '\0'), -1, out var statement, IntPtr.Zero) != 0) throw new IOException("Library Manager DBの形式を確認できません");
        try
        {
            var rows = new List<byte[][]>(); int rc;
            while ((rc = sqlite3_step(statement)) == 100)
            {
                var row = new byte[columns][];
                for (int i = 0; i < columns; i++)
                {
                    var ptr = blob ? sqlite3_column_blob(statement, i) : sqlite3_column_text(statement, i);
                    row[i] = new byte[sqlite3_column_bytes(statement, i)];
                    if (row[i].Length > 0) Marshal.Copy(ptr, row[i], 0, row[i].Length);
                }
                rows.Add(row);
            }
            if (rc != 101) throw new IOException("Library Manager DBが使用中です");
            return rows;
        }
        finally { sqlite3_finalize(statement); }
    }
    public void Dispose() { if (db != IntPtr.Zero) { sqlite3_close(db); db = IntPtr.Zero; } }
}

public enum LocalDownloadState { Missing, Busy, Complete }

public sealed record LibraryInfo(string? Folder, HashSet<string> Registered, string? Error)
{
    public static string DbPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pm.booth.library-manager", "data.db");
    public static LibraryInfo Read()
    {
        try
        {
            using var source = ActiveLibraryDatabase.Open(DbPath);
            return ReadSnapshot(source);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) { return new(null, [], ex.Message); }
    }
    public static LibraryInfo ReadActive()
    {
        try
        {
            using var source = ActiveLibraryDatabase.Open(DbPath);
            return source.Live ? ReadSnapshot(source) : new(null, [], "公式Managerの設定の準備を待っています");
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { return new(null, [], "公式Managerの設定の準備を待っています"); }
    }
    internal static LibraryInfo ReadSnapshot(ActiveLibraryDatabase source)
    {
        if (source.Cached is not null) return source.Cached;
        if (!File.Exists(source.Path)) return new(null, [], "公式Library Managerで保存先を設定してください");
        using var db = new ReadOnlySqlite(source.Path);
        if (db.Query("PRAGMA quick_check", 1).Any(r => Encoding.UTF8.GetString(r[0]) != "ok")) throw new IOException("公式設定DBの読み取りを再確認してください");
        var row = db.Query("SELECT item_directory_path FROM preferences LIMIT 1", 1, true).FirstOrDefault();
        string? folder = row is null ? null : Encoding.Unicode.GetString(row[0]).TrimEnd('\0');
        if (folder is not null && !Path.IsPathFullyQualified(folder)) throw new IOException("保存先形式を確認できません");
        var ids = db.Query("SELECT DISTINCT booth_item_id FROM registered_items WHERE booth_item_id IS NOT NULL", 1)
            .Select(r => Encoding.UTF8.GetString(r[0])).ToHashSet();
        var info = new LibraryInfo(folder, ids, null);
        source.Remember(info);
        return info;
    }
    public bool HasLocalProduct(string id, string destination)
    {
        if (!Feed.SafeId(id)) return false;
        foreach (var root in new[] { Folder, destination }.Where(x => !string.IsNullOrEmpty(x)).Distinct())
        {
            string path = Path.Combine(root!, "b" + id);
            if (Directory.Exists(path) && Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Any(f => !f.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).StartsWith(".collector-"))) return true;
        }
        return false;
    }
    public bool HasLocalDownload(string id, string filename, string destination)
        => InspectLocalDownload(id, filename, destination) == LocalDownloadState.Complete;
    public LocalDownloadState InspectLocalDownload(string id, string filename, string destination)
    {
        if (!Feed.SafeId(id)) return LocalDownloadState.Missing;
        filename = DownloadEngine.SafeFileName(filename);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var root in new[] { Folder, destination }.Where(x => !string.IsNullOrEmpty(x)).Distinct())
        {
            string product = Path.Combine(root!, "b" + id);
            if (!Directory.Exists(product)) continue;
            // A file nested inside another extracted archive is not evidence
            // that this independent downloadable was obtained.
            string direct = Path.Combine(product, filename);
            if (File.Exists(direct)) return InspectFile(direct, Path.GetExtension(filename).Equals(".zip", StringComparison.OrdinalIgnoreCase));
            // Library Manager may have expanded the archive to its basename folder.
            string extracted = Path.Combine(product, Path.GetFileNameWithoutExtension(filename));
            if (Directory.Exists(extracted) && (File.GetAttributes(extracted) & FileAttributes.ReparsePoint) == 0)
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(extracted) < TimeSpan.FromSeconds(5)) return LocalDownloadState.Busy;
                bool found = false;
                foreach (string file in Directory.EnumerateFiles(extracted, "*", options))
                {
                    found = true;
                    if (InspectFile(file, false, true) != LocalDownloadState.Complete) return LocalDownloadState.Busy;
                }
                return found ? LocalDownloadState.Complete : LocalDownloadState.Busy;
            }
        }
        return LocalDownloadState.Missing;
    }
    static LocalDownloadState InspectFile(string path, bool zip, bool allowEmpty = false)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
                DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromSeconds(5)) return LocalDownloadState.Busy;
            // A paused writer still holds write access. Refuse write sharing so
            // an open output cannot be mistaken for a finished download.
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length == 0 && !allowEmpty) return LocalDownloadState.Busy;
            // Check ZIP end structure without expanding entries or treating a
            // file digest as an authenticity or malware check.
            if (zip) { using var archive = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Read, true); }
            return LocalDownloadState.Complete;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { return LocalDownloadState.Busy; }
    }
}
