namespace BoothFreeCollector;

// File bytes only. Browser credentials and signed URLs never cross the bridge.
public sealed class DirectFileTransfer : IDisposable
{
    sealed record Transfer(string Key, string Final, string Temp, FileStream Stream, long? Expected, DateTimeOffset At);
    readonly Dictionary<string, Transfer> transfers = [];
    public int Count => transfers.Count;
    public string Begin(string ticket, string key, string productId, string name, string root, long? expected)
    {
        foreach (var stale in transfers.Where(t => DateTimeOffset.UtcNow - t.Value.At > TimeSpan.FromHours(1)).Select(t => t.Key).ToArray()) Abort(stale);
        if (transfers.Count >= 2 || transfers.ContainsKey(ticket)) throw new IOException("直接保存の処理数上限です");
        if (!Feed.SafeId(productId) || !key.StartsWith(productId + "/")) throw new IOException("商品IDが不正です");
        if (expected is <= 0 or > 2147483648L) throw new IOException("ファイル容量の上限です");
        string folder = Path.Combine(root, "b" + productId); DownloadEngine.EnsureSafeDirectory(folder);
        string final = Path.Combine(folder, DownloadEngine.SafeFileName(name));
        if (File.Exists(final)) throw new IOException("保存先に同名ファイルがあります。再確認してください");
        string temp = Path.Combine(folder, ".collector-" + Guid.NewGuid().ToString("N") + ".partial");
        transfers.Add(ticket, new(key, final, temp, new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None), expected, DateTimeOffset.UtcNow));
        return final;
    }
    public long Append(string ticket, long offset, string data)
    {
        if (!transfers.TryGetValue(ticket, out var transfer)) throw new IOException("直接保存の予約がありません");
        if (data.Length > 350000) throw new IOException("転送データが大きすぎます");
        byte[] bytes = Convert.FromBase64String(data);
        long total = transfer.Stream.Length + bytes.Length;
        if (bytes.Length == 0 || offset != transfer.Stream.Length || total > 2147483648L || (transfer.Expected is long expected && total > expected)) throw new IOException("転送位置または容量が一致しません");
        if (new DriveInfo(Path.GetPathRoot(transfer.Final)!).AvailableFreeSpace < 512L * 1024 * 1024) throw new IOException("保存先の空き容量が足りません");
        transfer.Stream.Write(bytes);
        transfers[ticket] = transfer with { At = DateTimeOffset.UtcNow };
        return total;
    }
    public SavedFile Finish(string ticket, long size, CollectorState state)
    {
        if (!transfers.TryGetValue(ticket, out var transfer)) throw new IOException("直接保存の予約がありません");
        if (size <= 0 || transfer.Stream.Length != size || (transfer.Expected is long expected && size != expected)) throw new IOException("ファイル容量が一致しません");
        transfer.Stream.Flush(true); transfer.Stream.Dispose();
        try
        {
            DownloadEngine.EnsureNoLinks(transfer.Final);
            File.Move(transfer.Temp, transfer.Final, false);
            DownloadEngine.Remember(state, transfer.Key, transfer.Final);
            transfers.Remove(ticket);
            return state.Files[transfer.Key];
        }
        catch { Abort(ticket); throw; }
    }
    public void Abort(string ticket)
    {
        if (!transfers.Remove(ticket, out var transfer)) return;
        transfer.Stream.Dispose();
        if (File.Exists(transfer.Temp)) File.Delete(transfer.Temp);
    }
    public void Dispose() { foreach (var key in transfers.Keys.ToArray()) Abort(key); }
}
