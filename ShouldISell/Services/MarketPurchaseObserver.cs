using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;

namespace ShouldISell.Services;

public sealed class MarketPurchaseObserver : IDisposable
{
    private readonly IMarketBoard marketBoard;
    private readonly IPlayerState playerState;
    private readonly TradingLedgerStore ledger;
    private readonly BuyOpportunityEngine buyEngine;
    private readonly IPluginLog log;
    private readonly object gate = new();
    private readonly Queue<PendingPurchase> pending = new();

    private sealed record PendingPurchase(
        uint CatalogId,
        bool IsHq,
        uint Quantity,
        uint PricePerUnit,
        ulong ListingId,
        ulong RetainerId,
        int RetainerCityId,
        uint TotalTax,
        uint WorldId,
        DateTimeOffset RequestedAtUtc);

    public MarketPurchaseObserver(
        IMarketBoard marketBoard,
        IPlayerState playerState,
        TradingLedgerStore ledger,
        BuyOpportunityEngine buyEngine,
        IPluginLog log)
    {
        this.marketBoard = marketBoard;
        this.playerState = playerState;
        this.ledger = ledger;
        this.buyEngine = buyEngine;
        this.log = log;
        marketBoard.PurchaseRequested += OnPurchaseRequested;
        marketBoard.ItemPurchased += OnItemPurchased;
    }

    public void Dispose()
    {
        marketBoard.PurchaseRequested -= OnPurchaseRequested;
        marketBoard.ItemPurchased -= OnItemPurchased;
        ledger.Flush();
    }

    private void OnPurchaseRequested(IMarketBoardPurchaseHandler request)
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0 || request.CatalogId == 0 || request.ItemQuantity == 0)
            return;

        lock (gate)
        {
            while (pending.Count > 0 && DateTimeOffset.UtcNow - pending.Peek().RequestedAtUtc > TimeSpan.FromSeconds(45))
                pending.Dequeue();

            pending.Enqueue(new PendingPurchase(
                request.CatalogId,
                request.IsHq,
                request.ItemQuantity,
                request.PricePerUnit,
                request.ListingId,
                request.RetainerId,
                request.RetainerCityId,
                request.TotalTax,
                playerState.CurrentWorld.RowId,
                DateTimeOffset.UtcNow));
        }
    }

    private void OnItemPurchased(IMarketBoardPurchase purchase)
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0 || purchase.ItemQuantity == 0)
            return;

        PendingPurchase? matched = null;
        lock (gate)
        {
            var items = pending.ToList();
            pending.Clear();
            for (var i = 0; i < items.Count; i++)
            {
                var row = items[i];
                var itemMatches = purchase.CatalogId == row.CatalogId ||
                                  purchase.CatalogId == row.CatalogId + 1_000_000;
                if (matched is null && itemMatches && purchase.ItemQuantity == row.Quantity)
                {
                    matched = row;
                    continue;
                }
                if (DateTimeOffset.UtcNow - row.RequestedAtUtc <= TimeSpan.FromSeconds(45))
                    pending.Enqueue(row);
            }
        }

        if (matched is null)
            return;

        try
        {
            var totalCost = (long)matched.PricePerUnit * matched.Quantity + matched.TotalTax;
            var opportunity = buyEngine.MatchPurchase(
                matched.CatalogId,
                matched.IsHq,
                matched.PricePerUnit,
                (int)matched.Quantity,
                matched.ListingId);
            long? predictedProfit = null;
            if (opportunity is not null && opportunity.BuyQuantity > 0)
                predictedProfit = (long)Math.Round(opportunity.PotentialProfit * (matched.Quantity / (double)opportunity.BuyQuantity));

            ledger.AddPurchase(new PersonalPurchase(
                Guid.NewGuid(),
                playerState.ContentId,
                matched.WorldId,
                matched.CatalogId,
                matched.IsHq,
                (int)matched.Quantity,
                matched.PricePerUnit,
                matched.TotalTax,
                totalCost,
                matched.ListingId,
                matched.RetainerId,
                matched.RetainerCityId,
                DateTimeOffset.UtcNow,
                opportunity?.Strategy,
                opportunity?.SuggestedSellUnitPrice,
                opportunity?.EstimatedLiquidationDays,
                predictedProfit,
                opportunity?.Roi));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not record a successful Market Board purchase in the trading ledger.");
        }
    }
}
