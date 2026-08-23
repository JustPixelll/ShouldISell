using Dalamud.Plugin.Services;

namespace ShouldISell.Services;

public sealed record TycoonSalesItemInsight(
    uint ItemId,
    bool IsHq,
    string ItemName,
    int SaleEvents,
    int Units,
    long NetGil,
    double AverageNetUnitPrice,
    DateTimeOffset LastSaleUtc);

public sealed record TycoonSaleInsight(
    DateTimeOffset SoldAtUtc,
    uint ItemId,
    bool IsHq,
    string ItemName,
    string RetainerName,
    int Quantity,
    long NetGil,
    double NetUnitPrice,
    PersonalSaleSource Source,
    bool ListingTraceable,
    double? TimeToSellDays,
    int PriceChanges,
    int SizeChanges,
    uint? LastObservedListingPrice,
    string? ListingLifecycleId);

public sealed record TycoonListingInsight(
    string LifecycleId,
    bool IsActive,
    string RetainerName,
    uint ItemId,
    bool IsHq,
    string ItemName,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastObservedUtc,
    DateTimeOffset? RemovedAtUtc,
    int InitialQuantity,
    int LastQuantity,
    uint InitialUnitPrice,
    uint LastUnitPrice,
    int PriceChanges,
    int SizeChanges,
    bool IsRelist,
    int? PreviousQuantity,
    uint? PreviousUnitPrice,
    DateTimeOffset? SoldAtUtc,
    double? TimeToSellDays,
    long? SaleNetGil);

public sealed record TycoonInsightSnapshot(
    int SaleEvents,
    int SoldUnits,
    long NetSalesGil,
    int TraceableSaleEvents,
    IReadOnlyList<TycoonSalesItemInsight> TopSalesItems,
    IReadOnlyList<TycoonSaleInsight> RecentSales,
    IReadOnlyList<TycoonListingInsight> ListingInsights,
    DateTimeOffset CalculatedAtUtc);

/// <summary>
/// Descriptive analytics over every captured personal sale. Cost basis and ROI remain the job of
/// TraderAnalyzer; this service can safely learn from gathered, crafted, dropped, gifted and older
/// pre-tracking stock without pretending those items had a known acquisition cost.
/// </summary>
public sealed class TycoonInsightService
{
    private readonly IPlayerState playerState;
    private readonly LocalStore sellStore;
    private readonly GameItemCatalog catalog;
    private readonly ListingHistoryTracker listingHistory;
    private TycoonInsightSnapshot? cached;
    private DateTimeOffset cacheUntilUtc;

    public TycoonInsightService(
        IPlayerState playerState,
        LocalStore sellStore,
        GameItemCatalog catalog,
        ListingHistoryTracker listingHistory)
    {
        this.playerState = playerState;
        this.sellStore = sellStore;
        this.catalog = catalog;
        this.listingHistory = listingHistory;
    }

    public TycoonInsightSnapshot GetSnapshot(bool force = false)
    {
        if (!force && cached is not null && DateTimeOffset.UtcNow < cacheUntilUtc)
            return cached;

        cached = Calculate();
        cacheUntilUtc = DateTimeOffset.UtcNow.AddSeconds(2);
        return cached;
    }

    private TycoonInsightSnapshot Calculate()
    {
        var now = DateTimeOffset.UtcNow;
        if (!playerState.IsLoaded || playerState.ContentId == 0)
            return new TycoonInsightSnapshot(0, 0, 0, 0,
                Array.Empty<TycoonSalesItemInsight>(), Array.Empty<TycoonSaleInsight>(), Array.Empty<TycoonListingInsight>(), now);

        var contentId = playerState.ContentId;
        var sales = sellStore.GetPersonalSales(contentId).OrderBy(x => x.SoldAtUtc).ToList();
        var lifecycles = listingHistory.GetLifecycles(contentId).ToList();
        var traceBySale = CorrelateSalesToListings(sales, lifecycles);

        var saleRows = sales.OrderByDescending(x => x.SoldAtUtc).Select(sale =>
        {
            traceBySale.TryGetValue(SaleKey(sale), out var trace);
            var state = trace is null ? ((uint Price, int Quantity)?)null : StateAt(trace, sale.SoldAtUtc);
            return new TycoonSaleInsight(
                sale.SoldAtUtc,
                sale.ItemId,
                sale.IsHq,
                catalog.Get(sale.ItemId).Name,
                sale.RetainerName,
                sale.Quantity,
                sale.NetGil,
                sale.NetGil / (double)Math.Max(1, sale.Quantity),
                sale.Source,
                trace is not null,
                trace is null ? null : Math.Max(0, (sale.SoldAtUtc - trace.FirstSeenUtc).TotalDays),
                trace?.Events.Count(x => x.Kind == ListingTraceEventKind.PriceChanged) ?? 0,
                trace?.Events.Count(x => x.Kind == ListingTraceEventKind.QuantityChanged) ?? 0,
                state?.Price,
                trace?.Id);
        }).ToList();

        var topItems = saleRows
            .GroupBy(x => (x.ItemId, x.IsHq, x.ItemName))
            .Select(g => new TycoonSalesItemInsight(
                g.Key.ItemId,
                g.Key.IsHq,
                g.Key.ItemName,
                g.Count(),
                g.Sum(x => x.Quantity),
                g.Sum(x => x.NetGil),
                g.Sum(x => x.NetGil) / (double)Math.Max(1, g.Sum(x => x.Quantity)),
                g.Max(x => x.SoldAtUtc)))
            .OrderByDescending(x => x.NetGil)
            .ThenByDescending(x => x.SaleEvents)
            .Take(100)
            .ToList();

        var saleByLifecycle = saleRows
            .Where(x => x.ListingLifecycleId is not null)
            .GroupBy(x => x.ListingLifecycleId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.SoldAtUtc).First(), StringComparer.Ordinal);

        var listingRows = lifecycles.Select(lifecycle =>
        {
            saleByLifecycle.TryGetValue(lifecycle.Id, out var sale);
            var predecessor = FindRelistPredecessor(lifecycle, lifecycles);
            return new TycoonListingInsight(
                lifecycle.Id,
                lifecycle.IsActive,
                lifecycle.RetainerName,
                lifecycle.ItemId,
                lifecycle.IsHq,
                catalog.Get(lifecycle.ItemId).Name,
                lifecycle.FirstSeenUtc,
                lifecycle.LastObservedUtc,
                lifecycle.RemovedAtUtc,
                lifecycle.InitialQuantity,
                lifecycle.LastQuantity,
                lifecycle.InitialUnitPrice,
                lifecycle.LastUnitPrice,
                lifecycle.Events.Count(x => x.Kind == ListingTraceEventKind.PriceChanged),
                lifecycle.Events.Count(x => x.Kind == ListingTraceEventKind.QuantityChanged),
                predecessor is not null,
                predecessor?.LastQuantity,
                predecessor?.LastUnitPrice,
                sale?.SoldAtUtc,
                sale?.TimeToSellDays,
                sale?.NetGil);
        })
        .OrderByDescending(x => x.IsActive)
        .ThenByDescending(x => x.SoldAtUtc ?? x.RemovedAtUtc ?? x.LastObservedUtc)
        .Take(1000)
        .ToList();

        return new TycoonInsightSnapshot(
            saleRows.Count,
            saleRows.Sum(x => x.Quantity),
            saleRows.Sum(x => x.NetGil),
            saleRows.Count(x => x.ListingTraceable),
            topItems,
            saleRows.Take(500).ToList(),
            listingRows,
            now);
    }

    private static Dictionary<string, ListingTraceLifecycle> CorrelateSalesToListings(
        IReadOnlyList<PersonalSale> sales,
        IReadOnlyList<ListingTraceLifecycle> lifecycles)
    {
        var result = new Dictionary<string, ListingTraceLifecycle>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sale in sales.OrderBy(x => x.SoldAtUtc))
        {
            var best = lifecycles
                .Where(x => !used.Contains(x.Id) && x.RetainerId == sale.RetainerId &&
                            x.ItemId == sale.ItemId && x.IsHq == sale.IsHq &&
                            x.FirstSeenUtc <= sale.SoldAtUtc.AddMinutes(5) &&
                            (x.RemovedAtUtc is null || x.RemovedAtUtc >= sale.SoldAtUtc.AddMinutes(-5)))
                .Select(x => new { Lifecycle = x, State = StateAt(x, sale.SoldAtUtc) })
                .Where(x => x.State.Quantity == sale.Quantity && PricePlausible(x.State.Price, sale))
                .OrderBy(x => Math.Abs(((x.Lifecycle.RemovedAtUtc ?? x.Lifecycle.LastObservedUtc) - sale.SoldAtUtc).TotalMinutes))
                .FirstOrDefault();
            if (best is null)
                continue;
            result[SaleKey(sale)] = best.Lifecycle;
            used.Add(best.Lifecycle.Id);
        }
        return result;
    }

    private static ListingTraceLifecycle? FindRelistPredecessor(
        ListingTraceLifecycle current,
        IReadOnlyList<ListingTraceLifecycle> all)
        => all.Where(x => x.Id != current.Id && x.RetainerId == current.RetainerId &&
                         x.ItemId == current.ItemId && x.IsHq == current.IsHq &&
                         x.RemovedAtUtc is { } removed && removed <= current.FirstSeenUtc &&
                         current.FirstSeenUtc - removed <= TimeSpan.FromHours(1))
            .OrderByDescending(x => x.RemovedAtUtc)
            .FirstOrDefault();

    private static (uint Price, int Quantity) StateAt(ListingTraceLifecycle lifecycle, DateTimeOffset at)
    {
        var price = lifecycle.InitialUnitPrice;
        var quantity = lifecycle.InitialQuantity;
        foreach (var evt in lifecycle.Events.Where(x => x.AtUtc <= at && x.Kind != ListingTraceEventKind.Removed).OrderBy(x => x.AtUtc))
        {
            price = evt.UnitPrice;
            quantity = evt.Quantity;
        }
        return (price, quantity);
    }

    private static bool PricePlausible(uint listedUnitPrice, PersonalSale sale)
    {
        if (listedUnitPrice == 0 || sale.Quantity <= 0 || sale.NetGil <= 0)
            return false;
        var netUnit = sale.NetGil / (double)sale.Quantity;
        return netUnit <= listedUnitPrice * 1.01 && netUnit >= listedUnitPrice * 0.94;
    }

    private static string SaleKey(PersonalSale sale)
        => string.Join('|', sale.RetainerId, sale.ItemId, sale.IsHq ? 1 : 0, sale.Quantity,
            sale.NetGil, sale.SoldAtUtc.ToUnixTimeSeconds(), sale.BuyerName ?? string.Empty);
}
