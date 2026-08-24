using Dalamud.Plugin.Services;

namespace ShouldISell.Services;

public sealed partial class MarketDataCoordinator
{
    private readonly IPlayerState playerState;
    private readonly Configuration configuration;
    private readonly LocalStore store;
    private readonly GameItemCatalog catalog;
    private readonly InventoryScanner inventory;
    private readonly UniversalisClient universalis;
    private readonly ScoreCalculator scores;
    private readonly IPluginLog log;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private uint acceptingWorldId;
    private readonly Dictionary<RatingCacheKey, SellRating?> ratingCache = new();

    private readonly record struct RatingCacheKey(
        uint WorldId,
        uint ItemId,
        bool IsHq,
        int Quantity,
        int ValueThresholdGil,
        long ListingVersion,
        long HistoryVersion,
        int ListingCount,
        int SalesCount,
        ulong ExcludedRetainerId,
        short ExcludedMarketSlot,
        uint ExcludedPrice);

    public MarketDataCoordinator(
        IPlayerState playerState,
        Configuration configuration,
        LocalStore store,
        GameItemCatalog catalog,
        InventoryScanner inventory,
        UniversalisClient universalis,
        ScoreCalculator scores,
        IPluginLog log)
    {
        this.playerState = playerState;
        this.configuration = configuration;
        this.store = store;
        this.catalog = catalog;
        this.inventory = inventory;
        this.universalis = universalis;
        this.scores = scores;
        this.log = log;

        universalis.CurrentBatchReceived += OnCurrentBatchReceived;
        universalis.HistoryBatchReceived += OnHistoryBatchReceived;
    }

    public bool IsFetching { get; private set; }
    public string FetchStatus { get; private set; } = "Idle";
    public DateTimeOffset? LastFetchCompletedUtc { get; private set; }

    public async Task RefreshOwnedFromUniversalisAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!playerState.IsLoaded)
            return;
        if (!await refreshGate.WaitAsync(0, cancellationToken))
            return;

        try
        {
            IsFetching = true;
            var worldId = playerState.CurrentWorld.RowId;
            acceptingWorldId = worldId;
            var ids = inventory.GetUniqueMarketableItemIds();
            if (ids.Count == 0)
            {
                FetchStatus = "No known marketable owned items.";
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var currentNeeded = new List<uint>();
            var historyNeeded = new List<uint>();
            foreach (var itemId in ids)
            {
                var market = store.GetMarket(worldId, itemId);
                var listingAt = market?.ListingObservedAtUtc;
                var historyAt = market?.HistoryObservedAtUtc;
                if (force || listingAt is null ||
                    now - listingAt.Value > TimeSpan.FromMinutes(configuration.UniversalisCurrentTtlMinutes))
                    currentNeeded.Add(itemId);

                if (force || historyAt is null ||
                    now - historyAt.Value > TimeSpan.FromMinutes(configuration.UniversalisHistoryTtlMinutes))
                    historyNeeded.Add(itemId);
            }

            FetchStatus = $"Universalis: {currentNeeded.Count} current + {historyNeeded.Count} history item(s)...";
            if (currentNeeded.Count > 0)
                await universalis.FetchCurrentAsync(worldId, currentNeeded, cancellationToken);
            if (historyNeeded.Count > 0)
                await universalis.FetchHistoryAsync(worldId, historyNeeded, cancellationToken);

            store.Flush();
            LastFetchCompletedUtc = DateTimeOffset.UtcNow;
            FetchStatus = $"Ready: {ids.Count} known marketable item(s).";
        }
        catch (OperationCanceledException)
        {
            FetchStatus = "Universalis refresh cancelled.";
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Universalis owned-item refresh failed.");
            FetchStatus = $"Universalis error: {ex.Message}";
        }
        finally
        {
            acceptingWorldId = 0;
            IsFetching = false;
            refreshGate.Release();
        }
    }


    public async Task RefreshSalesHistoryBenchmarksAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0)
            return;
        if (!await refreshGate.WaitAsync(0, cancellationToken))
            return;

        try
        {
            IsFetching = true;
            var worldId = playerState.CurrentWorld.RowId;
            acceptingWorldId = worldId;
            var ids = store.GetPersonalSales(playerState.ContentId)
                .Select(x => x.ItemId)
                .Distinct()
                .Where(id => catalog.Get(id).IsMarketable)
                .ToList();
            if (ids.Count == 0)
            {
                FetchStatus = "No captured marketable sales to benchmark.";
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var historyNeeded = ids
                .Where(itemId =>
                {
                    var observed = store.GetMarket(worldId, itemId)?.HistoryObservedAtUtc;
                    return force || observed is null ||
                           now - observed.Value > TimeSpan.FromMinutes(configuration.UniversalisHistoryTtlMinutes);
                })
                .ToList();

            FetchStatus = historyNeeded.Count == 0
                ? $"Sales benchmarks ready: {ids.Count:N0} sold item(s) already fresh."
                : $"Sales benchmarks: refreshing 90-day history for {historyNeeded.Count:N0} sold item(s)...";

            if (historyNeeded.Count > 0)
                await universalis.FetchHistoryAsync(worldId, historyNeeded, cancellationToken);

            store.Flush();
            LastFetchCompletedUtc = DateTimeOffset.UtcNow;
            FetchStatus = $"Sales benchmarks ready: {ids.Count:N0} sold item(s).";
        }
        catch (OperationCanceledException)
        {
            FetchStatus = "Sales benchmark refresh cancelled.";
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Universalis sales-history benchmark refresh failed.");
            FetchStatus = $"Sales benchmark error: {ex.Message}";
        }
        finally
        {
            acceptingWorldId = 0;
            IsFetching = false;
            refreshGate.Release();
        }
    }

    public IReadOnlyList<RatedOwnedItem> GetRatedOwnedItems()
    {
        if (!playerState.IsLoaded)
            return Array.Empty<RatedOwnedItem>();

        var worldId = playerState.CurrentWorld.RowId;
        return inventory.GetKnownOwnedStacks()
            .GroupBy(x => (x.ItemId, x.IsHq))
            .Select(group =>
            {
                var item = catalog.Get(group.Key.ItemId);
                var market = store.GetMarket(worldId, group.Key.ItemId);
                var quantity = group.Sum(x => x.Quantity);
                var rating = CalculateCached(worldId, item, group.Key.IsHq, market, quantity);
                var locations = group
                    .GroupBy(x => (x.OwnerKind, x.OwnerId, x.OwnerName, x.Container))
                    .Select(g => FormatLocation(g.Key.OwnerKind, g.Key.OwnerName, g.Key.Container, g.Sum(x => x.Quantity)))
                    .ToList();
                var ownership = group
                    .GroupBy(x => (x.OwnerKind, x.OwnerId, x.OwnerName))
                    .Select(g => new OwnedLocationSummary(
                        g.Key.OwnerKind,
                        g.Key.OwnerId,
                        g.Key.OwnerName,
                        g.Sum(x => x.Quantity)))
                    .ToList();
                return new RatedOwnedItem(
                    item,
                    group.Key.IsHq,
                    quantity,
                    locations,
                    ownership,
                    rating,
                    group.Max(x => (DateTimeOffset?)x.ObservedAtUtc));
            })
            .OrderByDescending(x => x.Rating?.Stars ?? 0)
            .ThenByDescending(x => x.Rating?.OpportunityScore ?? 0)
            .ThenBy(x => x.Item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public HashSet<(uint ItemId, bool IsHq)> GetKnownOwnListedVariants()
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0)
            return new HashSet<(uint ItemId, bool IsHq)>();

        return store.GetOwnListings(playerState.ContentId)
            .Select(x => (x.ItemId, x.IsHq))
            .ToHashSet();
    }

    public IReadOnlyList<RatedOwnListing> GetRatedOwnListings()
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0)
            return Array.Empty<RatedOwnListing>();

        inventory.ScanLoadedContainers();
        var worldId = playerState.CurrentWorld.RowId;
        var totalOwned = inventory.GetKnownOwnedStacks()
            .GroupBy(x => (x.ItemId, x.IsHq))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        var ownListings = store.GetOwnListings(playerState.ContentId);
        var ownRetainerIds = ownListings
            .Where(x => x.RetainerId != 0)
            .Select(x => x.RetainerId)
            .ToHashSet();
        var ownRetainerNames = ownListings
            .Select(x => x.RetainerName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return ownListings
            .Select(listing =>
            {
                var item = catalog.Get(listing.ItemId);
                var market = CloneMarketSnapshot(store.GetMarket(worldId, listing.ItemId));
                RemoveOwnListingsFromMarket(market, listing, ownRetainerIds, ownRetainerNames);
                totalOwned.TryGetValue((listing.ItemId, listing.IsHq), out var owned);
                var planningQuantity = Math.Max(listing.Quantity, owned);
                var rating = CalculateCached(
                    worldId, item, listing.IsHq, market, planningQuantity,
                    listing.RetainerId, listing.MarketSlot, listing.UnitPrice);
                return new RatedOwnListing(listing, item, rating, owned);
            })
            .OrderByDescending(x => PriceChangeMagnitude(x))
            .ThenBy(x => x.Item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private SellRating? CalculateCached(
        uint worldId,
        ItemInfo item,
        bool isHq,
        MarketSnapshot? market,
        int quantity,
        ulong excludedRetainerId = 0,
        short excludedMarketSlot = -1,
        uint excludedPrice = 0)
    {
        var key = new RatingCacheKey(
            worldId,
            item.ItemId,
            isHq,
            Math.Max(1, quantity),
            configuration.ValueThresholdGil,
            market?.ListingObservedAtUtc?.ToUnixTimeMilliseconds() ?? 0,
            market?.HistoryObservedAtUtc?.ToUnixTimeMilliseconds() ?? 0,
            market?.Listings.Count ?? 0,
            market?.Sales.Count ?? 0,
            excludedRetainerId,
            excludedMarketSlot,
            excludedPrice);

        if (ratingCache.TryGetValue(key, out var cached))
            return cached;

        var rating = scores.Calculate(
            item, isHq, market, configuration.ValueThresholdGil, Math.Max(1, quantity));
        ratingCache[key] = rating;

        // Keep the cache bounded across inventory/price churn. Market timestamps in the key make old
        // entries naturally obsolete, so a simple bounded clear is sufficient here.
        if (ratingCache.Count > 4_000)
        {
            var keep = rating;
            ratingCache.Clear();
            ratingCache[key] = keep;
        }

        return rating;
    }


    private static MarketSnapshot? CloneMarketSnapshot(MarketSnapshot? source)
    {
        if (source is null)
            return null;

        // Current-listing repricing must exclude the player's own listing, but never mutate the
        // shared LocalStore snapshot. Mutating the store here caused repeated UI rebuilds to
        // progressively delete matching listings from the in-memory market depth.
        return new MarketSnapshot
        {
            WorldId = source.WorldId,
            ItemId = source.ItemId,
            ListingObservedAtUtc = source.ListingObservedAtUtc,
            HistoryObservedAtUtc = source.HistoryObservedAtUtc,
            UniversalisLastUploadUtc = source.UniversalisLastUploadUtc,
            CurrentSource = source.CurrentSource,
            Listings = source.Listings.ToList(),
            // ScoreCalculator treats sale history as read-only; share it to avoid cloning potentially
            // thousands of history entries for every visible own-listing row on every UI rebuild.
            Sales = source.Sales,
        };
    }

    private static void RemoveOwnListingsFromMarket(
        MarketSnapshot? market,
        OwnMarketListing own,
        IReadOnlySet<ulong> ownRetainerIds,
        IReadOnlySet<string> ownRetainerNames)
    {
        if (market is null || market.Listings.Count == 0)
            return;

        // Remove every listing belonging to one of this character's known retainers for this
        // item/HQ variant, independent of its cached price. Previously we removed only an exact
        // price+quantity match. After repricing, a slightly stale market snapshot could therefore
        // leave our OLD listing in the competitor depth and make the recommendation bounce e.g.
        // 81 -> 79 -> 81. Identity-based exclusion makes repricing stable across that refresh gap.
        market.Listings.RemoveAll(x =>
            x.ItemId == own.ItemId &&
            x.IsHq == own.IsHq &&
            ((x.RetainerId != 0 && ownRetainerIds.Contains(x.RetainerId)) ||
             (!string.IsNullOrWhiteSpace(x.RetainerName) && ownRetainerNames.Contains(x.RetainerName))));

        // Some data sources occasionally omit retainer identity. Keep the exact-current-listing
        // fallback for that case without guessing that an arbitrary same-price competitor is ours.
        if (market.Listings.Any(x =>
                x.ItemId == own.ItemId && x.IsHq == own.IsHq &&
                (x.RetainerId != 0 || !string.IsNullOrWhiteSpace(x.RetainerName))))
            return;

        var exactIndex = market.Listings.FindIndex(x =>
            x.ItemId == own.ItemId &&
            x.IsHq == own.IsHq &&
            x.PricePerUnit == own.UnitPrice &&
            x.Quantity == own.Quantity);
        if (exactIndex >= 0)
            market.Listings.RemoveAt(exactIndex);
    }

    private static double PriceChangeMagnitude(RatedOwnListing row)
    {
        if (row.Rating?.SuggestedPrice is not { } suggested || row.Listing.UnitPrice == 0)
            return 0;
        return Math.Abs((double)suggested - row.Listing.UnitPrice) / row.Listing.UnitPrice;
    }

    private void OnCurrentBatchReceived(IReadOnlyList<UniversalisCurrentItem> items)
    {
        var worldId = acceptingWorldId;
        if (worldId == 0)
            return;
        foreach (var item in items)
            store.MergeUniversalisCurrent(worldId, item.ItemId, item.LastUploadUtc, item.Listings);
    }

    private void OnHistoryBatchReceived(IReadOnlyList<UniversalisHistoryItem> items)
    {
        var worldId = acceptingWorldId;
        if (worldId == 0)
            return;
        foreach (var item in items)
            store.MergeUniversalisHistory(worldId, item.ItemId, item.LastUploadUtc, item.Sales);
    }

    private static string FormatLocation(InventoryOwnerKind kind, string ownerName, FFXIVClientStructs.FFXIV.Client.Game.InventoryType container, int quantity)
        => kind == InventoryOwnerKind.Retainer
            ? $"{ownerName} / {container} x{quantity}"
            : $"{container} x{quantity}";
}

