using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace ShouldISell.Services;

/// <summary>
/// Passive bridge for optional local market-data providers. Should I? never requests game-server
/// data through this service; it only imports already-published snapshots and exposes safe local
/// item-ID scopes / read-only candidate hints. The provider is optional and anonymous from
/// Should I?'s point of view.
/// </summary>
public sealed class ExternalMarketDataBridge : IDisposable
{
    public const string SnapshotUpdatedChannel = "ShouldI.ExternalMarketData.SnapshotUpdated.v1";
    public const string GetSnapshotsChannel = "ShouldI.ExternalMarketData.GetSnapshots.v1";
    public const string GetOwnedItemIdsChannel = "ShouldI.ExternalMarketData.GetOwnedMarketableItemIds.v1";
    public const string GetListingItemIdsChannel = "ShouldI.ExternalMarketData.GetCurrentListingItemIds.v1";
    public const string GetSmartCandidatesChannel = "ShouldI.ExternalMarketData.GetSmartCandidates.v1";

    private readonly LocalStore store;
    private readonly InventoryScanner inventory;
    private readonly IPlayerState playerState;
    private readonly MarketDataCoordinator coordinator;
    private readonly BuyOpportunityScanner buyScanner;
    private readonly ProductionOpportunityScanner productionScanner;
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<string, object> snapshotSubscriber;
    private readonly ICallGateSubscriber<string> snapshotsSubscriber;
    private readonly ICallGateProvider<string> ownedIdsProvider;
    private readonly ICallGateProvider<string> listingIdsProvider;
    private readonly ICallGateProvider<string> smartCandidatesProvider;
    private readonly Action<string> snapshotHandler;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ExternalMarketDataBridge(
        IDalamudPluginInterface pluginInterface,
        LocalStore store,
        InventoryScanner inventory,
        IPlayerState playerState,
        MarketDataCoordinator coordinator,
        BuyOpportunityScanner buyScanner,
        ProductionOpportunityScanner productionScanner,
        IPluginLog log)
    {
        this.store = store;
        this.inventory = inventory;
        this.playerState = playerState;
        this.coordinator = coordinator;
        this.buyScanner = buyScanner;
        this.productionScanner = productionScanner;
        this.log = log;

        snapshotSubscriber = pluginInterface.GetIpcSubscriber<string, object>(SnapshotUpdatedChannel);
        snapshotsSubscriber = pluginInterface.GetIpcSubscriber<string>(GetSnapshotsChannel);
        ownedIdsProvider = pluginInterface.GetIpcProvider<string>(GetOwnedItemIdsChannel);
        listingIdsProvider = pluginInterface.GetIpcProvider<string>(GetListingItemIdsChannel);
        smartCandidatesProvider = pluginInterface.GetIpcProvider<string>(GetSmartCandidatesChannel);

        snapshotHandler = ImportSnapshotJson;
        snapshotSubscriber.Subscribe(snapshotHandler);
        ownedIdsProvider.RegisterFunc(GetOwnedItemIdsJson);
        listingIdsProvider.RegisterFunc(GetListingItemIdsJson);
        smartCandidatesProvider.RegisterFunc(GetSmartCandidatesJson);
        TrySynchronizeCachedSnapshots();
    }

    public bool IsConnected { get; private set; }
    public DateTimeOffset? LastSnapshotUtc { get; private set; }
    public uint LastItemId { get; private set; }

    public void Dispose()
    {
        snapshotSubscriber.Unsubscribe(snapshotHandler);
        ownedIdsProvider.UnregisterFunc();
        listingIdsProvider.UnregisterFunc();
        smartCandidatesProvider.UnregisterFunc();
    }

    public void TrySynchronizeCachedSnapshots()
    {
        try
        {
            var json = snapshotsSubscriber.InvokeFunc();
            if (string.IsNullOrWhiteSpace(json))
                return;

            var snapshots = JsonSerializer.Deserialize<List<ExternalSnapshotDto>>(json, JsonOptions);
            if (snapshots is null)
                return;

            var imported = 0;
            foreach (var snapshot in snapshots)
                if (ImportSnapshot(snapshot, flush: false))
                    imported++;

            if (imported > 0)
            {
                store.Flush();
                IsConnected = true;
            }
        }
        catch (IpcNotReadyError)
        {
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not synchronize optional local market snapshots.");
        }
    }

    private string GetOwnedItemIdsJson()
    {
        try
        {
            return JsonSerializer.Serialize(inventory.GetUniqueMarketableItemIds());
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not expose owned item IDs to a local market-data provider.");
            return "[]";
        }
    }

    private string GetListingItemIdsJson()
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0)
            return "[]";
        try
        {
            return JsonSerializer.Serialize(store.GetOwnListings(playerState.ContentId)
                .Select(x => x.ItemId)
                .Distinct()
                .Order()
                .ToList());
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not expose listing item IDs to a local market-data provider.");
            return "[]";
        }
    }

    private string GetSmartCandidatesJson()
    {
        if (!playerState.IsLoaded)
            return "[]";

        try
        {
            var output = new List<SmartCandidateDto>();
            var currentWorldId = playerState.CurrentWorld.RowId;

            var ownListings = coordinator.GetRatedOwnListings();
            for (var i = 0; i < ownListings.Count; i++)
            {
                var row = ownListings[i];
                output.Add(new SmartCandidateDto(
                    "Sell",
                    row.Item.ItemId,
                    row.Item.Name,
                    Math.Max(900, 1000 - i),
                    "Current listing: fresh competition can directly change repricing guidance.",
                    row.Rating?.OpportunityScore,
                    row.Rating?.Confidence,
                    row.Rating?.ListingFreshnessUtc));
            }

            var owned = coordinator.GetRatedOwnedItems()
                .Where(x => x.Rating is not null)
                .OrderByDescending(x => x.Rating!.Stars)
                .ThenByDescending(x => x.Rating!.OpportunityScore)
                .ThenByDescending(x => (x.Rating!.SuggestedPrice ?? x.Rating.RealisticCurrentPrice ?? 0) * (long)Math.Max(1, x.Quantity))
                .ToList();
            for (var i = 0; i < owned.Count; i++)
            {
                var row = owned[i];
                output.Add(new SmartCandidateDto(
                    "Sell",
                    row.Item.ItemId,
                    row.Item.Name,
                    Math.Max(650, 850 - i),
                    $"Owned item: {row.Rating!.Stars}★ sell candidate with {row.Quantity:N0} unit(s) known.",
                    row.Rating.OpportunityScore,
                    row.Rating.Confidence,
                    row.Rating.ListingFreshnessUtc));
            }

            var marketBuys = buyScanner.GetMarketOpportunities()
                .Where(x => x.WorldId == currentWorldId)
                .OrderByDescending(x => x.OpportunityScore)
                .ThenByDescending(x => x.RiskAdjustedProfit)
                .ToList();
            for (var i = 0; i < marketBuys.Count; i++)
            {
                var row = marketBuys[i];
                output.Add(new SmartCandidateDto(
                    "BuyMB",
                    row.Item.ItemId,
                    row.Item.Name,
                    Math.Max(700, 940 - i),
                    $"Market-board buy candidate: {row.StrategyLabel}, modeled risk-adjusted profit {row.RiskAdjustedProfit:N0}g.",
                    row.OpportunityScore,
                    row.Confidence,
                    row.MarketFreshnessUtc));
            }

            var vendorBuys = buyScanner.GetVendorOpportunities()
                .Where(x => x.WorldId == currentWorldId)
                .OrderByDescending(x => x.OpportunityScore)
                .ThenByDescending(x => x.RiskAdjustedProfit)
                .ToList();
            for (var i = 0; i < vendorBuys.Count; i++)
            {
                var row = vendorBuys[i];
                output.Add(new SmartCandidateDto(
                    "BuyVendor",
                    row.Item.ItemId,
                    row.Item.Name,
                    Math.Max(650, 900 - i),
                    $"Vendor-to-market candidate: native listings can confirm the exit side before buying stock.",
                    row.OpportunityScore,
                    row.Confidence,
                    row.MarketFreshnessUtc));
            }

            var crafts = productionScanner.GetCraftOpportunities()
                .Where(x => x.WorldId == currentWorldId)
                .OrderByDescending(x => x.OpportunityScore)
                .ThenByDescending(x => x.EconomicProfit)
                .ToList();
            for (var i = 0; i < crafts.Count; i++)
            {
                var row = crafts[i];
                output.Add(new SmartCandidateDto(
                    "Craft",
                    row.Item.ItemId,
                    row.Item.Name,
                    Math.Max(650, 900 - i),
                    $"Craft output: modeled economic profit {row.EconomicProfit:N0}g; verify the sell side first.",
                    row.OpportunityScore,
                    row.Confidence,
                    row.AnalysedAtUtc));

                var majorInputs = row.Ingredients
                    .Where(x => x.Route == ProductionAcquisitionRoute.MarketBoard)
                    .OrderByDescending(x => x.EconomicCost)
                    .Take(3)
                    .ToList();
                for (var inputIndex = 0; inputIndex < majorInputs.Count; inputIndex++)
                {
                    var input = majorInputs[inputIndex];
                    output.Add(new SmartCandidateDto(
                        "Craft",
                        input.Item.ItemId,
                        input.Item.Name,
                        Math.Max(500, 700 - i - inputIndex * 10),
                        $"Major market-board input for {row.Item.Name}; economic material cost {input.EconomicCost:N0}g.",
                        row.OpportunityScore,
                        row.Confidence,
                        row.AnalysedAtUtc));
                }
            }

            var gathers = productionScanner.GetGatherOpportunities()
                .Where(x => x.WorldId == currentWorldId)
                .OrderByDescending(x => x.OpportunityScore)
                .ThenByDescending(x => x.EstimatedGilPerActiveMinute)
                .ToList();
            for (var i = 0; i < gathers.Count; i++)
            {
                var row = gathers[i];
                output.Add(new SmartCandidateDto(
                    "Gather",
                    row.Item.ItemId,
                    row.Item.Name,
                    Math.Max(550, 820 - i),
                    $"Gather candidate: modeled {row.EstimatedGilPerActiveMinute:N0}g per active minute.",
                    row.OpportunityScore,
                    row.Confidence,
                    row.AnalysedAtUtc));
            }

            return JsonSerializer.Serialize(output, JsonOptions);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not expose smart Deep Mine candidate hints.");
            return "[]";
        }
    }

    private void ImportSnapshotJson(string json)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<ExternalSnapshotDto>(json, JsonOptions);
            if (snapshot is null || !ImportSnapshot(snapshot, flush: true))
                return;

            IsConnected = true;
            LastSnapshotUtc = snapshot.ListingObservedAtUtc ?? snapshot.HistoryObservedAtUtc ?? DateTimeOffset.UtcNow;
            LastItemId = snapshot.ItemId;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not import an optional local market snapshot.");
        }
    }

    private bool ImportSnapshot(ExternalSnapshotDto snapshot, bool flush)
    {
        if (snapshot.WorldId == 0 || snapshot.ItemId == 0)
            return false;

        var listingAt = snapshot.ListingObservedAtUtc ?? DateTimeOffset.UtcNow;
        store.BeginLiveObservation(snapshot.WorldId, snapshot.ItemId, listingAt);

        if (snapshot.Sales.Count > 0)
        {
            var historyAt = snapshot.HistoryObservedAtUtc ?? listingAt;
            store.SetLiveHistory(
                snapshot.WorldId,
                snapshot.ItemId,
                snapshot.Sales.Select(x => new MarketSale(snapshot.ItemId, x.PricePerUnit, x.Quantity, x.IsHq, x.SoldAtUtc, MarketDataSource.LiveGame)),
                historyAt);
        }

        store.AppendLiveListings(
            snapshot.WorldId,
            snapshot.ItemId,
            snapshot.Listings.Select(x => new MarketListing(snapshot.ItemId, x.PricePerUnit, x.Quantity, x.IsHq, x.ListingId, x.RetainerId, x.RetainerName ?? string.Empty, listingAt, MarketDataSource.LiveGame)),
            listingAt);

        if (flush)
            store.Flush();
        return true;
    }

    private sealed record SmartCandidateDto(
        string Module,
        uint ItemId,
        string ItemName,
        int Priority,
        string Reason,
        double? OpportunityScore,
        double? Confidence,
        DateTimeOffset? MarketFreshnessUtc);

    private sealed record ExternalSnapshotDto(uint WorldId, uint ItemId, DateTimeOffset? ListingObservedAtUtc, DateTimeOffset? HistoryObservedAtUtc, List<ExternalListingDto> Listings, List<ExternalSaleDto> Sales);
    private sealed record ExternalListingDto(uint PricePerUnit, uint Quantity, bool IsHq, ulong ListingId, ulong RetainerId, string? RetainerName);
    private sealed record ExternalSaleDto(uint PricePerUnit, uint Quantity, bool IsHq, DateTimeOffset SoldAtUtc);
}
