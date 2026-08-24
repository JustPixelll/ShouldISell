using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace ShouldISell.Services;

public sealed class UniversalisClient : IDisposable
{
    private const int BatchSize = 100;
    private readonly HttpClient http = new();
    private readonly IPluginLog log;

    public UniversalisClient(IPluginLog log)
    {
        this.log = log;
        http.BaseAddress = new Uri("https://universalis.app/");
        http.Timeout = TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ShouldISell", "2.3.3"));
    }

    public void Dispose() => http.Dispose();

    public async Task FetchCurrentAsync(uint worldId, IReadOnlyCollection<uint> itemIds, CancellationToken cancellationToken = default)
    {
        foreach (var batch in Batch(itemIds))
        {
            var ids = string.Join(',', batch);
            using var response = await http.GetAsync($"api/v2/{worldId}/{ids}?listings=100&entries=0", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            CurrentBatchReceived?.Invoke(ParseCurrent(doc.RootElement));
            await Task.Delay(80, cancellationToken);
        }
    }

    public async Task FetchHistoryAsync(uint worldId, IReadOnlyCollection<uint> itemIds, CancellationToken cancellationToken = default)
    {
        foreach (var batch in Batch(itemIds))
        {
            var ids = string.Join(',', batch);
            // Ask for up to 1,800 sales from the last 90 days. Universalis uses seconds for
            // entriesWithin but milliseconds for statsWithin, so keep the units explicit.
            var entriesWithinSeconds = 90 * 24 * 60 * 60;
            var statsWithinMilliseconds = entriesWithinSeconds * 1000L;
            using var response = await http.GetAsync(
                $"api/v2/history/{worldId}/{ids}?entriesToReturn=1800&entriesWithin={entriesWithinSeconds}&statsWithin={statsWithinMilliseconds}",
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            HistoryBatchReceived?.Invoke(ParseHistory(doc.RootElement));
            await Task.Delay(80, cancellationToken);
        }
    }

    public event Action<IReadOnlyList<UniversalisCurrentItem>>? CurrentBatchReceived;
    public event Action<IReadOnlyList<UniversalisHistoryItem>>? HistoryBatchReceived;

    private IReadOnlyList<UniversalisCurrentItem> ParseCurrent(JsonElement root)
    {
        var result = new List<UniversalisCurrentItem>();
        foreach (var item in ExtractItemObjects(root))
        {
            try
            {
                var itemId = GetUInt(item, "itemID");
                if (itemId == 0)
                    continue;
                var upload = ParseMillis(item, "lastUploadTime");
                var listings = new List<MarketListing>();
                if (item.TryGetProperty("listings", out var listingArray) && listingArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var listing in listingArray.EnumerateArray())
                    {
                        listings.Add(new MarketListing(
                            itemId,
                            GetUInt(listing, "pricePerUnit"),
                            GetUInt(listing, "quantity"),
                            GetBool(listing, "hq"),
                            GetULong(listing, "listingID"),
                            GetULong(listing, "retainerID"),
                            GetString(listing, "retainerName"),
                            upload ?? DateTimeOffset.UtcNow,
                            MarketDataSource.Universalis));
                    }
                }

                result.Add(new UniversalisCurrentItem(itemId, upload, listings));
            }
            catch (Exception ex)
            {
                log.Debug(ex, "Could not parse one Universalis current-market item.");
            }
        }
        return result;
    }

    private IReadOnlyList<UniversalisHistoryItem> ParseHistory(JsonElement root)
    {
        var result = new List<UniversalisHistoryItem>();
        foreach (var item in ExtractItemObjects(root))
        {
            try
            {
                var itemId = GetUInt(item, "itemID");
                if (itemId == 0)
                    continue;
                var upload = ParseMillis(item, "lastUploadTime");
                var sales = new List<MarketSale>();
                if (item.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sale in entries.EnumerateArray())
                    {
                        var timestamp = GetLong(sale, "timestamp");
                        if (timestamp <= 0)
                            continue;
                        sales.Add(new MarketSale(
                            itemId,
                            GetUInt(sale, "pricePerUnit"),
                            GetUInt(sale, "quantity"),
                            GetBool(sale, "hq"),
                            DateTimeOffset.FromUnixTimeSeconds(timestamp),
                            MarketDataSource.Universalis));
                    }
                }
                result.Add(new UniversalisHistoryItem(itemId, upload, sales));
            }
            catch (Exception ex)
            {
                log.Debug(ex, "Could not parse one Universalis history item.");
            }
        }
        return result;
    }

    private static IEnumerable<JsonElement> ExtractItemObjects(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in items.EnumerateObject())
                yield return property.Value;
            yield break;
        }

        if (root.TryGetProperty("itemID", out _))
            yield return root;
    }

    private static IEnumerable<List<uint>> Batch(IEnumerable<uint> ids)
    {
        var bucket = new List<uint>(BatchSize);
        foreach (var id in ids.Distinct())
        {
            bucket.Add(id);
            if (bucket.Count < BatchSize)
                continue;
            yield return bucket;
            bucket = new List<uint>(BatchSize);
        }
        if (bucket.Count > 0)
            yield return bucket;
    }

    private static uint GetUInt(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.TryGetUInt32(out var n) ? n : 0;
    private static ulong GetULong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.TryGetUInt64(out var n) ? n : 0;
    private static long GetLong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
    private static bool GetBool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    private static string GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
    private static DateTimeOffset? ParseMillis(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || !v.TryGetInt64(out var millis) || millis <= 0)
            return null;
        return DateTimeOffset.FromUnixTimeMilliseconds(millis);
    }
}

public sealed record UniversalisCurrentItem(uint ItemId, DateTimeOffset? LastUploadUtc, List<MarketListing> Listings);
public sealed record UniversalisHistoryItem(uint ItemId, DateTimeOffset? LastUploadUtc, List<MarketSale> Sales);
