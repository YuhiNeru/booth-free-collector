namespace BoothFreeCollector;

public sealed class SessionDownloadQueue(EmbeddedSession session)
{
    public static void RefreshReceipts(CollectorState state, LibraryInfo library)
    {
        if (library.Error is not null || library.Folder is null) return;
        foreach (var pair in state.ManagerImports.Where(p => !p.Value.Confirmed).ToArray())
            if (library.Registered.Contains(pair.Value.ProductId) && library.InspectLocalDownload(pair.Value.ProductId, pair.Value.Name, library.Folder) == LocalDownloadState.Complete)
                state.ManagerImports[pair.Key] = pair.Value with { Confirmed = true };
    }
    public static bool CompleteProduct(CollectorState state, Product product, LibraryInfo library)
    {
        if (library.Error is not null || library.Folder is null || !library.Registered.Contains(product.Id) ||
            !state.ExpectedFileGenerations.TryGetValue(product.Id, out var generation) || generation != (state.GeneratedAt ?? "") ||
            !state.ExpectedFiles.TryGetValue(product.Id, out var files) || files.Length == 0) return false;
        return files.All(f => state.ManagerImports.TryGetValue(product.Id + "/" + f.FileId, out var receipt) && receipt.Confirmed &&
            receipt.VariantId == f.VariantId && receipt.Name == f.Name && library.InspectLocalDownload(product.Id, f.Name, library.Folder) == LocalDownloadState.Complete);
    }
    public static ExpectedFreeFile[] Plan(string html, Product product)
    {
        var links = DownloadEngine.ParseLinks(html, product).DistinctBy(l => (l.Id, l.VariantId, l.Name)).ToArray();
        if (links.Length is 0 or > 1000 || links.GroupBy(l => l.Id).Any(g => g.Select(l => l.Name).Distinct(StringComparer.Ordinal).Count() != 1))
            throw new IOException("無料ファイルの取得計画を確認できません");
        // A shared file can legitimately appear in a free bundle and in its
        // individual free variants. Bind it once, in catalog variant order.
        var chosen = links.GroupBy(l => l.Id).Select(g => g.OrderBy(l => Array.FindIndex(product.Variants, v => v.Id == l.VariantId)).First()).ToArray();
        if (chosen.Select(l => l.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != chosen.Length)
            throw new IOException("無料ファイルの取得計画を確認できません");
        return chosen.Select(l => new ExpectedFreeFile(l.Id, l.VariantId, l.Name)).ToArray();
    }
    public async Task StepAsync(Settings settings, CollectorState state, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var library = await Task.Run(LibraryInfo.ReadActive, ct);
        _ = settings.ResolveFolder(library);
        var outstanding = state.ManagerImports.Where(p => !p.Value.Confirmed).ToArray();
        var confirmed = await Task.Run(() => outstanding.Where(p => library.Registered.Contains(p.Value.ProductId) &&
            library.InspectLocalDownload(p.Value.ProductId, p.Value.Name, library.Folder!) == LocalDownloadState.Complete).Select(p => p.Key).ToArray(), ct);
        foreach (string key in confirmed) state.ManagerImports[key] = state.ManagerImports[key] with { Confirmed = true };
        var completed = await Task.Run(() => state.Queue.Where(p => CompleteProduct(state, p, library)).Select(p => p.Id).ToHashSet(), ct);
        state.Queue.RemoveAll(p => completed.Contains(p.Id));
        DownloadScheduler.Reconcile(state, settings, now);
        Storage.Save("state.json", state);
        if (!settings.AutoDownload || state.Queue.All(p => !p.Matches(settings))) return;
        if (!session.Ready || session.NeedsLogin) { state.Status = "「セットアップ」でログイン状態を確認してください"; return; }
        if (!ManagerIntegration.IsAvailable()) { state.Status = "公式Library Managerの起動を待っています"; return; }
        var product = DownloadScheduler.Jobs(state, settings, now, false, []).FirstOrDefault();
        if (product is null) { state.Status = "公式Library Managerでの取得完了・次の依頼時刻を待っています"; return; }
        try
        {
            if (!state.ExpectedFileGenerations.TryGetValue(product.Id, out var generation) || generation != (state.GeneratedAt ?? ""))
            {
                var html = await session.GetProductHtmlAsync(product.Url, ct);
                state.ExpectedFiles[product.Id] = await Task.Run(() => Plan(html, product), ct);
                state.ExpectedFileGenerations[product.Id] = state.GeneratedAt ?? "";
                Storage.Save("state.json", state);
            }
            foreach (var file in state.ExpectedFiles[product.Id])
            {
                ct.ThrowIfCancellationRequested();
                string key = product.Id + "/" + file.FileId;
                var local = await Task.Run(() => library.InspectLocalDownload(product.Id, file.Name, library.Folder!), ct);
                if (local == LocalDownloadState.Complete && library.Registered.Contains(product.Id))
                { state.ManagerImports[key] = new(product.Id, file.FileId, file.VariantId, file.Name, now, true); continue; }
                if (local == LocalDownloadState.Busy || state.ManagerImports.TryGetValue(key, out var waiting) && !waiting.Confirmed)
                { state.Status = "公式Library Managerでの取得・保存の完了を待っています"; return; }
                if (!DownloadScheduler.CanDispatch(state, DateTimeOffset.UtcNow)) return;
                string uri = await session.GetImportAsync(file.FileId, file.VariantId, ct);
                ManagerIntegration.ValidateBoundLink(uri, product.Id, file.FileId, file.VariantId, file.Name);
                _ = settings.ResolveFolder(await Task.Run(LibraryInfo.ReadActive, ct));
                // Save intent and pacing before process creation. A crash cannot
                // silently convert an unknown outcome into a duplicate request.
                state.ManagerImports[key] = new(product.Id, file.FileId, file.VariantId, file.Name, DateTimeOffset.UtcNow);
                DownloadScheduler.Dispatched(state, settings, DateTimeOffset.UtcNow, Random.Shared.NextDouble() * .15);
                Storage.Save("state.json", state);
                await Task.Run(() => ManagerIntegration.Send(uri, product.Id, file.FileId, file.VariantId, file.Name));
                state.Status = "公式Library Managerへ取得・一覧登録を依頼しました: " + file.Name;
                return;
            }
            if (CompleteProduct(state, product, library)) state.Queue.RemoveAll(p => p.Id == product.Id);
        }
        catch (IOException)
        {
            state.ProductNotBefore[product.Id] = DateTimeOffset.UtcNow.AddMinutes(5);
            throw;
        }
        finally { Storage.Save("state.json", state); }
    }
}
