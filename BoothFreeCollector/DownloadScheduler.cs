namespace BoothFreeCollector;

// The desktop is authoritative for both the durable deadline and dispatch gate.
// Timers and manual resume in a browser cannot move this gate earlier.
public static class DownloadScheduler
{
    public const int ConcurrentImports = 2;
    public static int PollIntervalMs(CollectorState state, Settings settings, DateTimeOffset now)
    {
        if (!settings.AutoDownload || !settings.SetupCompleted || !state.Queue.Any(p => p.Matches(settings))) return 30000;
        return Math.Clamp(DelayMs(state, now), 5000, 30000);
    }
    public static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaximumGap = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan ImportTimeout = TimeSpan.FromMinutes(30);
    public static void Reconcile(CollectorState state, Settings settings, DateTimeOffset now, DateTimeOffset? publishedAt = null)
    {
        // A product can reappear with a new free variation after its previous
        // batch finished. Its new work needs a fresh regular deadline, while
        // unfinished batches retain their original deadline across restarts.
        foreach (var product in state.Queue)
            if (state.ProductBatches.TryGetValue(product.Id, out var owner) &&
                state.WorkBatches.FirstOrDefault(b => b.Id == owner) is not { CompletedAt: null })
                state.ProductBatches.Remove(product.Id);
        var missing = state.Queue.Where(p => !state.ProductBatches.ContainsKey(p.Id) && p.Matches(settings)).ToArray();
        if (missing.Length > 0)
        {
            bool initial = settings.IncludeExisting && !state.InitialDownloadCompleted && !state.WorkBatches.Any(b => b.Initial);
            if (initial) missing = missing.Where(DownloadCategories.HighPriority).ToArray();
            if (missing.Length > 0)
            {
                var batch = state.WorkBatches.FirstOrDefault(b => b.CompletedAt is null && b.Generation == state.GeneratedAt && b.Initial == initial);
                if (batch is null)
                {
                    DateTimeOffset start = initial ? state.InitialBatchStartedAt ?? state.ManagerImports.Values.Select(i => (DateTimeOffset?)i.At).Min() ?? now : publishedAt ?? now;
                    if (!initial && start > now) start = now;
                    batch = new() { Initial = initial, Generation = state.GeneratedAt, StartedAt = start, Deadline = start.AddHours(initial ? 12 : 4) };
                    state.WorkBatches.Add(batch);
                    if (initial) state.InitialBatchStartedAt = start;
                }
                foreach (var product in missing)
                {
                    state.ProductBatches[product.Id] = batch.Id; batch.TotalProducts++;
                    if (batch.Initial) state.InitialProductIds.Add(product.Id);
                }
            }
        }
        var queued = state.Queue.Select(p => p.Id).ToHashSet();
        foreach (var batch in state.WorkBatches.Where(b => b.CompletedAt is null))
        {
            if (!state.ProductBatches.Any(p => p.Value == batch.Id && queued.Contains(p.Key)) &&
                !state.ManagerImports.Values.Any(i => !i.Confirmed && state.ProductBatches.TryGetValue(i.ProductId, out var owner) && owner == batch.Id))
            {
                batch.CompletedAt = now;
                if (batch.Initial) state.InitialDownloadCompleted = true;
            }
        }
    }
    // A timeout is an alert, not proof that the official app stopped. Do not
    // release concurrency or send the same file again while its outcome is unknown.
    public static bool IsPending(ManagerImport import, DateTimeOffset now) => !import.Confirmed;
    public static int Active(CollectorState state, DateTimeOffset now) => state.ManagerImports.Values.Count(i => IsPending(i, now));
    public static DateTimeOffset Deadline(CollectorState state, string productId, DateTimeOffset now)
    {
        if (state.ProductBatches.TryGetValue(productId, out var id) && state.WorkBatches.FirstOrDefault(b => b.Id == id) is DownloadBatch batch) return batch.Deadline;
        return now.AddHours(4);
    }
    public static TimeSpan Gap(CollectorState state, Settings settings, DateTimeOffset now)
    {
        var products = state.Queue.Where(p => p.Matches(settings) && state.ProductBatches.ContainsKey(p.Id)).ToArray();
        if (products.Length == 0) return MaximumGap;
        var earliest = products.Min(p => Deadline(state, p.Id, now));
        // Unseen products are budgeted conservatively as at least two files per
        // variation. Actual discovered files replace that estimate immediately.
        int work = products.Sum(p => state.ExpectedFiles.TryGetValue(p.Id, out var files)
            ? Math.Max(1, files.Count(f => !state.ManagerImports.TryGetValue(p.Id + "/" + f.FileId, out var i) || !i.Confirmed))
            : Math.Max(2, p.Variants.Length * 2));
        double seconds = (earliest - now).TotalSeconds * .65 / Math.Max(1, work);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, MinimumGap.TotalSeconds, MaximumGap.TotalSeconds));
    }
    public static int DelayMs(CollectorState state, DateTimeOffset now)
    {
        if (Active(state, now) >= ConcurrentImports) return 5000;
        return (int)Math.Clamp(((state.NextImportDispatch ?? now) - now).TotalMilliseconds, 0, 90000);
    }
    public static bool CanDispatch(CollectorState state, DateTimeOffset now) => Active(state, now) < ConcurrentImports && now >= (state.NextImportDispatch ?? DateTimeOffset.MinValue);
    public static void Dispatched(CollectorState state, Settings settings, DateTimeOffset now, double jitter = 0)
    {
        double seconds = Gap(state, settings, now).TotalSeconds;
        state.NextImportDispatch = now.AddSeconds(Math.Clamp(seconds * (1 + Math.Clamp(jitter, 0, .15)), MinimumGap.TotalSeconds, MaximumGap.TotalSeconds));
    }
    public static Product[] Jobs(CollectorState state, Settings settings, DateTimeOffset now, bool highOnly, IEnumerable<string> excluded)
    {
        Reconcile(state, settings, now);
        var exclude = excluded.ToHashSet();
        var activeProducts = state.ManagerImports.Values.Where(i => IsPending(i, now)).Select(i => i.ProductId).ToHashSet();
        return state.Queue.Where(p => p.Matches(settings) && state.ProductBatches.ContainsKey(p.Id) && (!highOnly || DownloadCategories.HighPriority(p)) &&
            !exclude.Contains(p.Id) && !activeProducts.Contains(p.Id) && (!state.ProductNotBefore.TryGetValue(p.Id, out var retry) || now >= retry))
            .OrderBy(p => Deadline(state, p.Id, now)).Take(2).ToArray();
    }
    public static object Summary(CollectorState state, Settings settings, DateTimeOffset now)
    {
        Reconcile(state, settings, now);
        var queued = state.Queue.Where(p => p.Matches(settings)).ToArray();
        var deadlines = queued.Where(p => state.ProductBatches.ContainsKey(p.Id)).Select(p => Deadline(state, p.Id, now)).ToArray();
        return new { pending_products = queued.Length, active_imports = Active(state, now), concurrent_limit = ConcurrentImports,
            next_dispatch = state.NextImportDispatch, deadline = deadlines.Length > 0 ? (DateTimeOffset?)deadlines.Min() : null,
            overdue = deadlines.Any(d => d < now), initial_completed = state.InitialDownloadCompleted,
            uncertain_imports = state.ManagerImports.Values.Count(i => !i.Confirmed && now - i.At >= ImportTimeout),
            gap_seconds = Gap(state, settings, now).TotalSeconds };
    }
}
