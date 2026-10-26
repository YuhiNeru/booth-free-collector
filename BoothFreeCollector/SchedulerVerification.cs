namespace BoothFreeCollector;

internal static class SchedulerVerification
{
    public static void Run(Action<bool,string> check)
    {
        var now = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        var settings = new Settings();
        Product Product(string id) => new() { Id = id, Title = "fixture", AgeRating = "all_ages", Categories = ["3D衣装"], Variants = [new() { Id = "2" }] };
        var state = new CollectorState { GeneratedAt = now.ToString("O"), Queue = [Product("1"), Product("2")] };
        DownloadScheduler.Reconcile(state, settings, now);
        check(state.WorkBatches.Single().Initial && state.WorkBatches[0].Deadline == now.AddHours(12), "initial desktop deadline is twelve hours");
        Storage.Save("scheduler-test.json", state);
        var restored = Storage.Load<CollectorState>("scheduler-test.json");
        DownloadScheduler.Reconcile(restored, settings, now.AddHours(2));
        check(restored.WorkBatches.Single().Deadline == now.AddHours(12), "restart does not reset download deadline");
        DownloadScheduler.Dispatched(state, settings, now, .1);
        var gate = state.NextImportDispatch;
        check(!DownloadScheduler.CanDispatch(state, now) && DownloadScheduler.CanDispatch(state, gate!.Value), "desktop dispatch gate cannot be bypassed by immediate manual resume");
        Storage.Save("scheduler-test.json", state);
        check(Storage.Load<CollectorState>("scheduler-test.json").NextImportDispatch == gate, "dispatch spacing survives restart");
        state.ManagerImports["1/10"] = new("1", "10", "2", "a.zip", now);
        state.ManagerImports["2/20"] = new("2", "20", "2", "b.zip", now);
        check(!DownloadScheduler.CanDispatch(state, now.AddHours(1)), "unknown long-running imports do not release concurrency or cause duplicate retries");
        check(DownloadScheduler.Jobs(state, settings, now, true, []).Length == 0, "pending imports do not block confirmation while preventing duplicate product work");
        state.ManagerImports["1/10"] = state.ManagerImports["1/10"] with { Confirmed = true };
        check(DownloadScheduler.CanDispatch(state, gate!.Value), "confirmed receipt releases an import slot");
        state.ManagerImports["2/20"] = state.ManagerImports["2/20"] with { Confirmed = true };
        state.Queue.Clear(); DownloadScheduler.Reconcile(state, settings, now.AddHours(1));
        check(state.InitialDownloadCompleted, "initial completion is retained");
        state.GeneratedAt = now.AddHours(12).ToString("O"); state.Queue.AddRange([Product("3"), Product("1")]);
        DownloadScheduler.Reconcile(state, settings, now.AddHours(13), now.AddHours(12));
        var regular = state.WorkBatches.Single(b => !b.Initial);
        check(regular.Deadline == now.AddHours(16), "regular deadline is four hours after publication even when delivery is delayed");
        check(state.ProductBatches["1"] == regular.Id && regular.TotalProducts == 2,
            "a previously completed product gets a new regular batch instead of its expired initial deadline");
        var urgent = new CollectorState { InitialDownloadCompleted = true, Queue = Enumerable.Range(1,200).Select(i => Product(i.ToString())).ToList() };
        DownloadScheduler.Reconcile(urgent, settings, now);
        check(DownloadScheduler.Gap(urgent, settings, now.AddHours(3.9)) < DownloadScheduler.Gap(urgent, settings, now), "remaining work and deadline tighten the dispatch interval");
        check(DownloadScheduler.Gap(urgent, settings, now.AddHours(5)) == DownloadScheduler.MinimumGap, "overdue work is flagged and uses the bounded fastest pace");
        settings.SetupCompleted = true;
        urgent.NextImportDispatch = now.AddSeconds(10);
        check(DownloadScheduler.PollIntervalMs(urgent, settings, now) == 10000, "resident timer wakes for the next dispatch rather than waiting a fixed thirty seconds");
        check(DownloadScheduler.PollIntervalMs(urgent, settings, now.AddSeconds(11)) == 5000, "resident active polling has a bounded five-second floor");
        check(DownloadScheduler.PollIntervalMs(new(), settings, now) == 30000, "resident idle polling does not spin at the active rate");
        settings.SetupCompleted = false;
        check(DownloadScheduler.PollIntervalMs(urgent, settings, now) == 30000, "unfinished setup cannot enable active acquisition polling");
        urgent.ProductNotBefore["1"] = now.AddMinutes(5);
        check(DownloadScheduler.Jobs(urgent, settings, now, true, []).All(p => p.Id != "1"), "temporary network backoff is controlled by the desktop");
    }
}
