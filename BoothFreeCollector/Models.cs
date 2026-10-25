using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoothFreeCollector;

public static class Storage
{
    public static string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoothFreeCollector");
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    public static T Load<T>(string name) where T : new()
    {
        string path = Path.Combine(Root, name);
        if (!File.Exists(path)) return new();
        // Corrupt state must never reset the schedule or downloaded history silently.
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException(path);
    }
    public static void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(Root);
        string path = Path.Combine(Root, name), temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, true);
    }
}

public sealed class Settings
{
    public static string DefaultDownloadFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BOOTHLibraryManager");
    public string FeedUrl { get; set; } = "https://booth-free-collector.vrc-ranking-board.workers.dev/catalog.json";
    public bool Configured { get; set; }
    public bool FollowLibraryFolder { get; set; } = true;
    public string DownloadFolder { get; set; } = DefaultDownloadFolder;
    public bool AutoDownload { get; set; } = true;
    public bool IncludeAdult { get; set; }
    public bool IncludeExisting { get; set; } = true;
    public string Search { get; set; } = "";
    public string Category { get; set; } = "";
    public string[] DownloadCategories { get; set; } = ["clothing", "hair"];
    public bool Startup { get; set; }
    public bool BrowserHandoff { get; set; } = true;
    public string BrowserDownloadsFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public string ResolveFolder(LibraryInfo? library = null)
    {
        if (!FollowLibraryFolder) return DownloadFolder;
        library ??= LibraryInfo.Read();
        // Absence/setup not completed is normal; a broken or busy DB is not.
        if (library.Error is not null) throw new IOException("Library Managerの設定を読み取れません: " + library.Error);
        return library.Folder ?? DownloadFolder;
    }
}

public sealed class CollectorState
{
    public DateTimeOffset? LastAttempt { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public string? Etag { get; set; }
    public string? GeneratedAt { get; set; }
    public bool BaselineCreated { get; set; }
    public HashSet<string> Seen { get; set; } = [];
    public Dictionary<string, HashSet<string>> KnownVariants { get; set; } = [];
    public List<Product> Queue { get; set; } = [];
    public Dictionary<string, SavedFile> Files { get; set; } = [];
    public Dictionary<string, BrowserPending> BrowserPending { get; set; } = [];
    public string Status { get; set; } = "初期設定をしてください";
    [JsonIgnore] public DateTimeOffset NextAttempt => LastAttempt?.AddHours(12) ?? DateTimeOffset.MinValue;
    public bool Due(DateTimeOffset now) => now >= NextAttempt;
}
public sealed record SavedFile(string Path, string Sha256, long Size);
public sealed record BrowserPending(string ProductId, string DownloadableId, string VariantId, string Name, string Url, DateTimeOffset At);
public sealed record DownloadCategory(string Id, string Label, string Priority, string[] Names);
public static class DownloadCategories
{
    public static readonly DownloadCategory[] All = [
        new("clothing", "衣装", "高", ["3D衣装", "衣装"]),
        new("hair", "髪型", "高", ["3D髪型", "髪型"]),
        new("texture", "テクスチャ", "中", ["3Dテクスチャ", "テクスチャ"]),
        new("accessory", "装飾品", "中", ["3D装飾品", "装飾品"]),
        new("prop", "小道具", "中", ["3D小道具", "小道具"]),
        new("motion", "3Dモーション・モーション", "中", ["3Dモーション・アニメーション", "3Dモーション", "モーション"]),
        new("world", "ワールド系", "低", ["3D環境・ワールド", "ワールド"]),
        new("tool", "ツール類", "低", ["3Dツール・システム", "ツール"])
    ];
    public static bool Matches(Product product, IEnumerable<string> selected) => All.Any(c => selected.Contains(c.Id) && product.Categories.Any(n => c.Names.Contains(n)));
    public static bool HighPriority(Product product) => Matches(product, ["clothing", "hair"]);
}
public sealed class Product
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Shop { get; set; } = "";
    public string Url { get; set; } = "";
    public string AgeRating { get; set; } = "unknown";
    public string[] Categories { get; set; } = [];
    public string[] Tags { get; set; } = [];
    public Variant[] Variants { get; set; } = [];
    public bool Matches(Settings s) => (s.IncludeAdult || AgeRating == "all_ages") &&
        DownloadCategories.Matches(this, s.DownloadCategories) &&
        (s.Category.Length == 0 || Categories.Any(c => c.Contains(s.Category, StringComparison.OrdinalIgnoreCase))) &&
        System.Text.RegularExpressions.Regex.Split(s.Search.Normalize(System.Text.NormalizationForm.FormKC), @"\s+").Where(t => t.Length > 0)
            .All(t => (Title + " " + Shop + " " + string.Join(' ', Tags)).Normalize(System.Text.NormalizationForm.FormKC).Contains(t, StringComparison.OrdinalIgnoreCase));
}
public sealed class Variant { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }
public sealed class Feed
{
    public int Schema { get; set; }
    public int IntervalHours { get; set; }
    public string GeneratedAt { get; set; } = "";
    public string? CrawlerFinishedAt { get; set; }
    public string CrawlerStatus { get; set; } = "unknown";
    public Product[] Items { get; set; } = [];
    public void Validate()
    {
        if (Schema != 1 || IntervalHours != 12 || Items.Length > 30000 || !DateTimeOffset.TryParse(GeneratedAt, out _)) throw new InvalidDataException("配信データの形式が違います");
        var ids = new HashSet<string>();
        foreach (var p in Items)
            if (!SafeId(p.Id) || !ids.Add(p.Id) || p.Url != "https://booth.pm/ja/items/" + p.Id || p.Title.Length > 4000 || p.Variants.Length == 0 || p.Variants.Any(v => !SafeId(v.Id))) throw new InvalidDataException("商品データを検証できません");
    }
    public static bool SafeId(string id) => id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit);
}
