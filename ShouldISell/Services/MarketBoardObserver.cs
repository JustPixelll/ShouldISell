using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;

namespace ShouldISell.Services;

public sealed class MarketBoardObserver : IDisposable
{
    private readonly IMarketBoard marketBoard;
    private readonly IPlayerState playerState;
    private readonly LocalStore store;
    private readonly IPluginLog log;
    private uint lastHistoryItemId;

    public MarketBoardObserver(IMarketBoard marketBoard, IPlayerState playerState, LocalStore store, IPluginLog log)
    {
        this.marketBoard = marketBoard;
        this.playerState = playerState;
        this.store = store;
        this.log = log;
        marketBoard.HistoryReceived += OnHistoryReceived;
        marketBoard.OfferingsReceived += OnOfferingsReceived;
    }

    public event Action<uint, MarketPacketKind>? PacketObserved;

    public void Dispose()
    {
        marketBoard.HistoryReceived -= OnHistoryReceived;
        marketBoard.OfferingsReceived -= OnOfferingsReceived;
    }

    private void OnHistoryReceived(IMarketBoardHistory history)
    {
        if (!playerState.IsLoaded || history.ItemId == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var worldId = playerState.CurrentWorld.RowId;
        lastHistoryItemId = history.ItemId;

        // A history packet marks the start of a complete normal item lookup in Dalamud's own
        // market uploader path. Clear the prior listing snapshot so offering pages can rebuild it.
        store.BeginLiveObservation(worldId, history.ItemId, now);
        store.SetLiveHistory(worldId, history.ItemId, history.HistoryListings.Select(x => new MarketSale(
            history.ItemId,
            x.SalePrice,
            x.Quantity,
            x.IsHq,
            new DateTimeOffset(DateTime.SpecifyKind(x.PurchaseTime, DateTimeKind.Utc)),
            MarketDataSource.LiveGame)), now);

        PacketObserved?.Invoke(history.ItemId, MarketPacketKind.History);
    }

    private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
    {
        if (!playerState.IsLoaded)
            return;

        var listings = offerings.ItemListings;
        var itemId = listings.Count > 0 ? listings[0].ItemId : lastHistoryItemId;
        if (itemId == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var worldId = playerState.CurrentWorld.RowId;
        store.AppendLiveListings(worldId, itemId, listings.Select(x => new MarketListing(
            itemId,
            x.PricePerUnit,
            x.ItemQuantity,
            x.IsHq,
            x.ListingId,
            x.RetainerId,
            x.RetainerName,
            now,
            MarketDataSource.LiveGame)), now);

        PacketObserved?.Invoke(itemId, MarketPacketKind.Offerings);
    }
}

public enum MarketPacketKind
{
    History,
    Offerings,
}
