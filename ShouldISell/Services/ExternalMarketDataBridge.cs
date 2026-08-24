using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace ShouldISell.Services;

/// <summary>
/// Passive bridge for optional local market-data providers. Should I? never requests game-server
/// data through this service; it only imports already-published snapshots and exposes safe local
/// item-ID scopes. The provider is optional and anonymous from Should I?'s point of view.
/// </summary>
public sealed class ExternalMarketDataBridge : IDisposable
{
    public const string SnapshotUpdatedChannel = "ShouldI.ExternalMarketData.SnapshotUpdated.v1";
    public const string GetSnapshotsChannel = "ShouldI.ExternalMarketData.GetSnapshots.v1";
    public const string GetOwnedItemIdsChannel = "ShouldI.ExternalMarketData.GetOwnedMarketableItemIds.v1";
    public const string GetListingItemIdsChannel = "ShouldI.ExternalMarketData.GetCurrentListingItemIds.v1";

    private readonly LocalStore store;
    private readonly InventoryScanner inventory;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<string, object> snapshotSubscriber;
    private readonly ICallGateSubscriber<string> snapshotsSubscriber;
    private readonly ICallGateProvider<string> ownedIdsProvider;
    private readonly ICallGateProvider<string> listingIdsProvider;
    private readonly Action<string> snapshotHandler;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ExternalMarketDataBridge(IDalamudPluginInterface pluginInterface, LocalStore store, InventoryScanner inventory, IPlayerState playerState, IPluginLog log)
    {
        this.store = store;
        this.inventory = inventory;
        this.playerState = playerState;
        this.log = log;

        snapshotSubscriber = pluginInterface.GetIpcSubscriber<string, object>(SnapshotUpdatedChannel);
        snapshotsSubscriber = pluginInterface.GetIpcSubscriber<string>(GetSnapshotsChannel);
        ownedIdsProvider = pluginInterface.GetIpcProvider<string>(GetOwnedItemIdsChannel);
        listingIdsProvider = pluginInterface.GetIpcProvider<string>(GetListingItemIdsChannel);

        snapshotHandler = ImportSnapshotJson;
        snapshotSubscriber.Subscribe(snapshotHandler);
        ownedIdsProvider.RegisterFunc(GetOwnedItemIdsJson);
        listingIdsProvider.RegisterFunc(GetListingItemIdsJson);
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

    private sealed record ExternalSnapshotDto(uint WorldId, uint ItemId, DateTimeOffset? ListingObservedAtUtc, DateTimeOffset? HistoryObservedAtUtc, List<ExternalListingDto> Listings, List<ExternalSaleDto> Sales);
    private sealed record ExternalListingDto(uint PricePerUnit, uint Quantity, bool IsHq, ulong ListingId, ulong RetainerId, string? RetainerName);
    private sealed record ExternalSaleDto(uint PricePerUnit, uint Quantity, bool IsHq, DateTimeOffset SoldAtUtc);
}
