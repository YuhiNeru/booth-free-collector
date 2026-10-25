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

public sealed record LibraryInfo(string? Folder, HashSet<string> Registered, string? Error)
{
    public static string DbPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pm.booth.library-manager", "data.db");
    public static LibraryInfo Read()
    {
        if (!File.Exists(DbPath)) return new(null, [], null);
        try
        {
            using var db = new ReadOnlySqlite(DbPath);
            var row = db.Query("SELECT item_directory_path FROM preferences LIMIT 1", 1, true).FirstOrDefault();
            string? folder = row is null ? null : Encoding.Unicode.GetString(row[0]).TrimEnd('\0');
            if (folder is not null && !Path.IsPathFullyQualified(folder)) throw new IOException("保存先形式を確認できません");
            var ids = db.Query("SELECT DISTINCT booth_item_id FROM registered_items WHERE booth_item_id IS NOT NULL", 1)
                .Select(r => Encoding.UTF8.GetString(r[0])).ToHashSet();
            return new(folder, ids, null);
        }
        catch (Exception ex) when (ex is IOException or DllNotFoundException or EntryPointNotFoundException) { return new(null, [], ex.Message); }
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
    {
        if (!Feed.SafeId(id)) return false;
        filename = DownloadEngine.SafeFileName(filename);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var root in new[] { Folder, destination }.Where(x => !string.IsNullOrEmpty(x)).Distinct())
        {
            string product = Path.Combine(root!, "b" + id);
            if (!Directory.Exists(product)) continue;
            if (Directory.EnumerateFiles(product, "*", options).Any(f => Path.GetFileName(f).Equals(filename, StringComparison.OrdinalIgnoreCase))) return true;
            // Library Manager may have expanded the archive to its basename folder.
            string extracted = Path.Combine(product, Path.GetFileNameWithoutExtension(filename));
            if (Directory.Exists(extracted) && (File.GetAttributes(extracted) & FileAttributes.ReparsePoint) == 0 && Directory.EnumerateFiles(extracted, "*", options).Any()) return true;
        }
        return false;
    }
}
