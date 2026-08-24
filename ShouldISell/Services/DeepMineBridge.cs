using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace ShouldISell.Services;

/// <summary>
/// Passive bridge to the optional experimental Should I Deep Mine? plugin.
/// Should I? never asks FFXIV for market data through this service: it only accepts
/// snapshots that Deep Mine has already collected and published through Dalamud IPC.
/// </summary>
public sealed class DeepMineBridge : IDisposable
{
    public const string SnapshotUpdatedChannel = "ShouldIDeepMine.SnapshotUpdated.v1";
    public const string GetSnapshotsChannel = "ShouldIDeepMine.GetSnapshots.v1";

    private readonly LocalStore store;
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<string, object> snapshotSubscriber;
    private readonly ICallGateSubscriber<string> snapshotsSubscriber;
    private readonly Action<string> snapshotHandler;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public DeepMineBridge(IDalamudPluginInterface pluginInterface, LocalStore store, IPluginLog log)
    {
        this.store = store;
        this.log = log;
        snapshotSubscriber = pluginInterface.GetIpcSubscriber<string, object>(SnapshotUpdatedChannel);
        snapshotsSubscriber = pluginInterface.GetIpcSubscriber<string>(GetSnapshotsChannel);
        snapshotHandler = ImportSnapshotJson;
        snapshotSubscriber.Subscribe(snapshotHandler);
        TrySynchronizeCachedSnapshots();
    }

    public bool IsConnected { get; private set; }
    public DateTimeOffset? LastSnapshotUtc { get; private set; }
    public uint LastItemId { get; private set; }
    public string Status { get; private set; } = "Should I Deep Mine? not detected yet. Passive market listening remains active.";

    public void Dispose()
    {
        snapshotSubscriber.Unsubscribe(snapshotHandler);
    }

    public void TrySynchronizeCachedSnapshots()
    {
        try
        {
            var json = snapshotsSubscriber.InvokeFunc();
            if (string.IsNullOrWhiteSpace(json))
                return;

            var snapshots = JsonSerializer.Deserialize<List<DeepMineSnapshotDto>>(json, JsonOptions);
            if (snapshots is null)
                return;

            var imported = 0;
            foreach (var snapshot in snapshots)
            {
                if (ImportSnapshot(snapshot, flush: false))
                    imported++;
            }

            if (imported > 0)
            {
                store.Flush();
                IsConnected = true;
                Status = $"Imported {imported:N0} cached Should I Deep Mine? snapshot(s).";
            }
        }
        catch (IpcNotReadyError)
        {
            // Deep Mine is optional. Absence is a normal state for the official plugin.
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not synchronize cached Should I Deep Mine? snapshots.");
        }
    }

    private void ImportSnapshotJson(string json)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<DeepMineSnapshotDto>(json, JsonOptions);
            if (snapshot is null || !ImportSnapshot(snapshot, flush: true))
                return;

            IsConnected = true;
            LastSnapshotUtc = snapshot.ListingObservedAtUtc ?? snapshot.HistoryObservedAtUtc ?? DateTimeOffset.UtcNow;
            LastItemId = snapshot.ItemId;
            Status = $"Received fresh Deep Mine data for item #{snapshot.ItemId}.";
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not import one Should I Deep Mine? snapshot.");
        }
    }

    private bool ImportSnapshot(DeepMineSnapshotDto snapshot, bool flush)
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
                snapshot.Sales.Select(x => new MarketSale(
                    snapshot.ItemId,
                    x.PricePerUnit,
                    x.Quantity,
                    x.IsHq,
                    x.SoldAtUtc,
                    MarketDataSource.LiveGame)),
                historyAt);
        }

        store.AppendLiveListings(
            snapshot.WorldId,
            snapshot.ItemId,
            snapshot.Listings.Select(x => new MarketListing(
                snapshot.ItemId,
                x.PricePerUnit,
                x.Quantity,
                x.IsHq,
                x.ListingId,
                x.RetainerId,
                x.RetainerName ?? string.Empty,
                listingAt,
                MarketDataSource.LiveGame)),
            listingAt);

        if (flush)
            store.Flush();
        return true;
    }

    private sealed record DeepMineSnapshotDto(
        uint WorldId,
        uint ItemId,
        DateTimeOffset? ListingObservedAtUtc,
        DateTimeOffset? HistoryObservedAtUtc,
        List<DeepMineListingDto> Listings,
        List<DeepMineSaleDto> Sales);

    private sealed record DeepMineListingDto(
        uint PricePerUnit,
        uint Quantity,
        bool IsHq,
        ulong ListingId,
        ulong RetainerId,
        string? RetainerName);

    private sealed record DeepMineSaleDto(
        uint PricePerUnit,
        uint Quantity,
        bool IsHq,
        DateTimeOffset SoldAtUtc);
}
