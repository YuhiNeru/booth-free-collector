using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace BoothFreeCollector;

public sealed class FeedClient : IDisposable
{
    readonly HttpClient http;
    public FeedClient(HttpMessageHandler? handler = null) => http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(60) };
    public async Task<bool> Fetch(Settings settings, CollectorState state, CancellationToken ct)
    {
        if (!state.Due(DateTimeOffset.UtcNow)) return false;
        var uri = new Uri(settings.FeedUrl);
        if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/catalog.json") throw new InvalidDataException("HTTPSのカタログURLを指定してください");
        // Reserve before I/O, including failures. Restart/manual actions cannot poll early.
        state.LastAttempt = DateTimeOffset.UtcNow;
        state.Status = "新着一覧を取得しています";
        Storage.Save("state.json", state);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("BoothFreeCollector/1.0");
        if (state.Etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", state.Etag);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotModified) { state.LastSuccess = DateTimeOffset.UtcNow; state.Status = "更新はありません"; Storage.Save("state.json", state); return true; }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream(); byte[] buffer = new byte[65536]; int size;
        while ((size = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (memory.Length + size > 12 * 1024 * 1024) throw new InvalidDataException("配信データの容量上限です");
            memory.Write(buffer, 0, size);
        }
        var feed = JsonSerializer.Deserialize<Feed>(memory.ToArray(), Storage.Json) ?? throw new InvalidDataException("配信が空です");
        return Apply(settings, state, feed, response.Headers.ETag?.ToString());
    }
    public static bool Apply(Settings settings, CollectorState state, Feed feed, string? etag = null)
    {
        feed.Validate();
        if (state.GeneratedAt is not null && DateTimeOffset.Parse(feed.GeneratedAt) < DateTimeOffset.Parse(state.GeneratedAt)) throw new InvalidDataException("古い配信データのため更新しません");
        bool baseline = !state.BaselineCreated && !settings.IncludeExisting;
        foreach (var p in feed.Items)
        {
            bool newProduct = state.Seen.Add(p.Id);
            bool newVariant = state.KnownVariants.TryGetValue(p.Id, out var known) && p.Variants.Any(v => !known.Contains(v.Id));
            if ((newProduct || newVariant) && !baseline && p.Matches(settings) && !state.Queue.Any(q => q.Id == p.Id)) state.Queue.Add(p);
            state.KnownVariants[p.Id] = p.Variants.Select(v => v.Id).ToHashSet();
        }
        // Update pending metadata and remove products no longer offered free.
        var current = feed.Items.ToDictionary(p => p.Id);
        state.Queue = state.Queue.Where(p => current.ContainsKey(p.Id)).Select(p => current[p.Id]).ToList();
        state.BaselineCreated = true;
        state.GeneratedAt = feed.GeneratedAt;
        state.LastSuccess = DateTimeOffset.UtcNow;
        state.Etag = etag;
        state.Status = baseline ? $"初回の{feed.Items.Length:N0}商品を基準にしました。次回から新着を自動取得します" : $"待機中 {state.Queue.Count:N0}商品";
        Storage.Save("catalog.json", feed);
        Storage.Save("state.json", state);
        return true;
    }
    public static void MergePending(Settings settings, CollectorState state, Feed catalog, IEnumerable<string> ids)
    {
        var requested = ids.ToHashSet();
        var pending = state.Queue.Select(p => p.Id).ToHashSet();
        foreach (var product in catalog.Items)
            if (requested.Contains(product.Id) && product.Matches(settings) && pending.Add(product.Id)) state.Queue.Add(product);
    }
    public void Dispose() => http.Dispose();
}
