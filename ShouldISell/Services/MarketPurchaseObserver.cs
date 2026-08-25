using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;

namespace ShouldISell.Services;

/// <summary>
/// Records the player's real Market Board cost basis. PurchaseRequested contains price, tax and
/// listing identity; ItemPurchased is the server acknowledgement. We only persist after the two
/// sides match, so cancelled/failed purchase attempts never become Tycoon trades.
/// </summary>
public sealed class MarketPurchaseObserver : IDisposable
{
    private readonly IMarketBoard marketBoard;
    private readonly IPlayerState playerState;
    private readonly TraderStore store;
    private readonly InventoryScanner inventory;
    private readonly BuyOpportunityScanner buyScanner;
    private readonly IPluginLog log;
    private readonly object gate = new();
    private readonly List<PendingPurchase> pending = new();

    public MarketPurchaseObserver(
        IMarketBoard marketBoard,
        IPlayerState playerState,
        TraderStore store,
        InventoryScanner inventory,
        BuyOpportunityScanner buyScanner,
        IPluginLog log)
    {
        this.marketBoard = marketBoard;
        this.playerState = playerState;
        this.store = store;
        this.inventory = inventory;
        this.buyScanner = buyScanner;
        this.log = log;

        marketBoard.PurchaseRequested += OnPurchaseRequested;
        marketBoard.ItemPurchased += OnItemPurchased;
    }

    public void Dispose()
    {
        marketBoard.PurchaseRequested -= OnPurchaseRequested;
        marketBoard.ItemPurchased -= OnItemPurchased;
    }

    private void OnPurchaseRequested(IMarketBoardPurchaseHandler request)
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0 || request.ItemQuantity == 0 || request.PricePerUnit == 0)
            return;

        var itemId = NormalizeItemId(request.CatalogId);
        var now = DateTimeOffset.UtcNow;
        var knownOwnedQuantity = inventory.GetKnownOwnedQuantity(itemId, request.IsHq);
        lock (gate)
        {
            pending.RemoveAll(x => now - x.RequestedAtUtc > TimeSpan.FromSeconds(30));
            pending.Add(new PendingPurchase(
                playerState.ContentId,
                playerState.CurrentWorld.RowId,
                itemId,
                request.IsHq,
                request.ItemQuantity,
                request.PricePerUnit,
                request.TotalTax,
                request.ListingId,
                knownOwnedQuantity,
                now));
        }
    }

    private void OnItemPurchased(IMarketBoardPurchase purchase)
    {
        try
        {
            var successItemId = NormalizeItemId(purchase.CatalogId);
            PendingPurchase? matched = null;
            lock (gate)
            {
                var now = DateTimeOffset.UtcNow;
                pending.RemoveAll(x => now - x.RequestedAtUtc > TimeSpan.FromSeconds(30));
                var index = pending.FindLastIndex(x =>
                    x.ItemId == successItemId &&
                    x.Quantity == purchase.ItemQuantity);
                if (index >= 0)
                {
                    matched = pending[index];
                    pending.RemoveAt(index);
                }
            }

            if (matched is null)
                return;

            var prediction = buyScanner.FindPredictionForPurchase(matched.ListingId, matched.ItemId, matched.IsHq);
            var totalCost = checked((long)matched.UnitPrice * matched.Quantity + matched.TotalTax);
            var record = new PersonalPurchase(
                matched.CharacterContentId,
                matched.WorldId,
                matched.ItemId,
                matched.IsHq,
                checked((int)matched.Quantity),
                matched.UnitPrice,
                matched.TotalTax,
                totalCost,
                matched.ListingId,
                DateTimeOffset.UtcNow,
                prediction?.StrategyLabel ?? "Manual Market Board buy",
                prediction?.OpportunityScore,
                prediction?.SuggestedExitUnitPrice,
                prediction?.EstimatedLiquidationDays,
                prediction?.PotentialPackageProfit,
                prediction?.AnalysedAtUtc,
                PurchaseSourceKind.MarketBoard,
                matched.KnownOwnedQuantityBeforePurchase);

            if (store.AddPurchase(record))
                log.Information(
                    "Should I Tycoon? recorded purchase: {Quantity}x item#{ItemId} at {Price}g (+{Tax} tax), strategy {Strategy}.",
                    record.Quantity, record.ItemId, record.UnitPrice, record.BuyerTax, record.Strategy);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not record Market Board purchase for Should I Tycoon?.");
        }
    }

    private static uint NormalizeItemId(uint itemId)
        => itemId >= 1_000_000 ? itemId - 1_000_000 : itemId;

    private sealed record PendingPurchase(
        ulong CharacterContentId,
        uint WorldId,
        uint ItemId,
        bool IsHq,
        uint Quantity,
        uint UnitPrice,
        uint TotalTax,
        ulong ListingId,
        int KnownOwnedQuantityBeforePurchase,
        DateTimeOffset RequestedAtUtc);
}
