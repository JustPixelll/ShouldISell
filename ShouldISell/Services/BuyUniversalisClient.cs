using System.Net.Http.Headers;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace ShouldISell.Services;

public sealed class BuyUniversalisClient : IDisposable
{
    private const int BatchSize = 100;
    private readonly HttpClient http = new();
    private readonly IPluginLog log;

    public BuyUniversalisClient(IPluginLog log)
    {
        this.log = log;
        http.BaseAddress = new Uri("https://universalis.app/");
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ShouldIBuy", "0.1.0"));
    }

    public void Dispose() => http.Dispose();

    public async Task<IReadOnlyList<uint>> FetchMarketableItemIdsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v2/marketable", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<uint>();

        return doc.RootElement.EnumerateArray()
            .Select(x => x.TryGetUInt32(out var id) ? id : 0)
            .Where(x => x != 0)
            .Distinct()
            .ToList();
    }

    public async Task<IReadOnlyList<AggregatedMarketItem>> FetchAggregatedAsync(
        uint worldId,
        IReadOnlyCollection<uint> itemIds,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new List<AggregatedMarketItem>();
        var batches = Batch(itemIds).ToList();
        for (var i = 0; i < batches.Count; i++)
        {
            var ids = string.Join(',', batches[i]);
            using var response = await http.GetAsync($"api/v2/aggregated/{worldId}/{ids}", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            result.AddRange(ParseAggregated(doc.RootElement));
            progress?.Invoke(i + 1, batches.Count);
            if (i + 1 < batches.Count)
                await Task.Delay(80, cancellationToken);
        }
        return result;
    }

    public async Task<IReadOnlyList<UniversalisCurrentItem>> FetchCurrentAsync(
        uint worldId,
        IReadOnlyCollection<uint> itemIds,
        CancellationToken cancellationToken = default)
    {
        var result = new List<UniversalisCurrentItem>();
        foreach (var batch in Batch(itemIds))
        {
            var ids = string.Join(',', batch);
            using var response = await http.GetAsync($"api/v2/{worldId}/{ids}?listings=100&entries=0", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            result.AddRange(ParseCurrent(doc.RootElement));
            await Task.Delay(80, cancellationToken);
        }
        return result;
    }

    public async Task<IReadOnlyList<UniversalisHistoryItem>> FetchHistoryAsync(
        uint worldId,
        IReadOnlyCollection<uint> itemIds,
        CancellationToken cancellationToken = default)
    {
        var result = new List<UniversalisHistoryItem>();
        var entriesWithin = 90 * 24 * 60 * 60;
        foreach (var batch in Batch(itemIds))
        {
            var ids = string.Join(',', batch);
            using var response = await http.GetAsync(
                $"api/v2/history/{worldId}/{ids}?entries=1800&entriesWithin={entriesWithin}&statsWithin={entriesWithin}",
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            result.AddRange(ParseHistory(doc.RootElement));
            await Task.Delay(80, cancellationToken);
        }
        return result;
    }

    private IReadOnlyList<AggregatedMarketItem> ParseAggregated(JsonElement root)
    {
        var result = new List<AggregatedMarketItem>();
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in results.EnumerateArray())
        {
            try
            {
                var itemId = GetUInt(item, "itemId");
                if (itemId == 0)
                    continue;

                var nq = item.TryGetProperty("nq", out var nqNode)
                    ? ParseAggregatedVariant(nqNode)
                    : EmptyVariant();
                var hq = item.TryGetProperty("hq", out var hqNode)
                    ? ParseAggregatedVariant(hqNode)
                    : EmptyVariant();

                DateTimeOffset? freshest = null;
                if (item.TryGetProperty("worldUploadTimes", out var uploads) && uploads.ValueKind == JsonValueKind.Array)
                {
                    foreach (var upload in uploads.EnumerateArray())
                    {
                        if (GetUInt(upload, "worldId") != 0 && GetUInt(upload, "worldId") != GetUInt(upload, "worldID"))
                        {
                            // Keep parsing tolerant of schema casing; the timestamp is what matters here.
                        }
                        var timestamp = GetLong(upload, "timestamp");
                        if (timestamp <= 0)
                            continue;
                        var at = DateTimeOffset.FromUnixTimeSeconds(timestamp);
                        if (freshest is null || at > freshest)
                            freshest = at;
                    }
                }

                result.Add(new AggregatedMarketItem(itemId, nq, hq, freshest));
            }
            catch (Exception ex)
            {
                log.Debug(ex, "Could not parse one Universalis aggregated item.");
            }
        }

        return result;
    }

    private static AggregatedVariant ParseAggregatedVariant(JsonElement node)
    {
        var min = NestedUInt(node, "minListing", "world", "price");
        var median = NestedUInt(node, "medianListing", "world", "price");
        var average = NestedDouble(node, "averageSalePrice", "world", "price");
        var velocity = NestedDouble(node, "dailySaleVelocity", "world", "quantity");
        var recentPrice = NestedUInt(node, "recentPurchase", "world", "price");
        var timestamp = NestedLong(node, "recentPurchase", "world", "timestamp");
        var recentAt = timestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(timestamp) : (DateTimeOffset?)null;
        return new AggregatedVariant(min, median, average, velocity, recentPrice, recentAt);
    }

    private static AggregatedVariant EmptyVariant() => new(0, 0, 0, 0, 0, null);

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
                        var price = GetUInt(listing, "pricePerUnit");
                        var quantity = GetUInt(listing, "quantity");
                        if (price == 0 || quantity == 0)
                            continue;
                        listings.Add(new MarketListing(
                            itemId,
                            price,
                            quantity,
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
                log.Debug(ex, "Could not parse one Universalis current-market item for buy discovery.");
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
                        var price = GetUInt(sale, "pricePerUnit");
                        var quantity = GetUInt(sale, "quantity");
                        if (timestamp <= 0 || price == 0 || quantity == 0)
                            continue;
                        sales.Add(new MarketSale(
                            itemId,
                            price,
                            quantity,
                            GetBool(sale, "hq"),
                            DateTimeOffset.FromUnixTimeSeconds(timestamp),
                            MarketDataSource.Universalis));
                    }
                }
                result.Add(new UniversalisHistoryItem(itemId, upload, sales));
            }
            catch (Exception ex)
            {
                log.Debug(ex, "Could not parse one Universalis history item for buy discovery.");
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

    private static uint NestedUInt(JsonElement e, string a, string b, string c)
        => TryNested(e, a, b, c, out var v) && v.TryGetUInt32(out var n) ? n : 0;
    private static long NestedLong(JsonElement e, string a, string b, string c)
        => TryNested(e, a, b, c, out var v) && v.TryGetInt64(out var n) ? n : 0;
    private static double NestedDouble(JsonElement e, string a, string b, string c)
        => TryNested(e, a, b, c, out var v) && v.TryGetDouble(out var n) ? n : 0;
    private static bool TryNested(JsonElement e, string a, string b, string c, out JsonElement value)
    {
        value = default;
        return e.TryGetProperty(a, out var aNode) &&
               aNode.ValueKind == JsonValueKind.Object &&
               aNode.TryGetProperty(b, out var bNode) &&
               bNode.ValueKind == JsonValueKind.Object &&
               bNode.TryGetProperty(c, out value);
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
