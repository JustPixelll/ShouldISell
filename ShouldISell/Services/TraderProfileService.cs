namespace ShouldISell.Services;

public sealed class TraderProfileService
{
    private readonly IPlayerState playerState;
    private readonly LocalStore sellStore;
    private readonly TradingLedgerStore buyStore;
    private readonly GameItemCatalog catalog;

    public TraderProfileService(
        IPlayerState playerState,
        LocalStore sellStore,
        TradingLedgerStore buyStore,
        GameItemCatalog catalog)
    {
        this.playerState = playerState;
        this.sellStore = sellStore;
        this.buyStore = buyStore;
        this.catalog = catalog;
    }

    public TraderProfile Build()
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0)
            return Empty();

        var characterId = playerState.ContentId;
        var purchases = buyStore.GetPurchases(characterId)
            .OrderBy(x => x.PurchasedAtUtc)
            .ToList();
        var sales = sellStore.GetPersonalSales(characterId)
            .Where(x => x.Quantity > 0 && x.NetGil > 0)
            .OrderBy(x => x.SoldAtUtc)
            .ToList();

        var remaining = purchases.ToDictionary(x => x.PurchaseId, x => x.Quantity);
        var allocations = new List<ClosedTradeAllocation>();
        var matchedSaleTransactions = 0;
        var matchedUnits = 0;
        long matchedRevenue = 0;

        foreach (var sale in sales)
        {
            var available = purchases
                .Where(p => p.ItemId == sale.ItemId && p.IsHq == sale.IsHq && p.PurchasedAtUtc <= sale.SoldAtUtc)
                .Where(p => remaining.TryGetValue(p.PurchaseId, out var qty) && qty > 0)
                .OrderBy(p => p.PurchasedAtUtc)
                .ToList();
            if (available.Count == 0)
                continue;

            var saleRemaining = sale.Quantity;
            var actualNetUnit = sale.NetGil / (double)sale.Quantity;
            var transactionMatched = 0;
            foreach (var purchase in available)
            {
                if (saleRemaining <= 0)
                    break;

                var open = remaining[purchase.PurchaseId];
                var take = Math.Min(open, saleRemaining);
                if (take <= 0)
                    continue;

                var costPerUnit = purchase.TotalCost / (double)Math.Max(1, purchase.Quantity);
                var allocatedCost = (long)Math.Round(costPerUnit * take);
                var allocatedRevenue = (long)Math.Round(actualNetUnit * take);
                var profit = allocatedRevenue - allocatedCost;
                var roi = allocatedCost > 0 ? profit / (double)allocatedCost : 0;
                var holding = Math.Max(0, (sale.SoldAtUtc - purchase.PurchasedAtUtc).TotalDays);
                var predictedNet = purchase.PredictedSellUnitPrice is { } predictedGross
                    ? predictedGross * (1.0 - ScoreCalculator.MarketSellerTaxRate)
                    : (double?)null;

                allocations.Add(new ClosedTradeAllocation(
                    purchase.PurchaseId,
                    purchase.ItemId,
                    purchase.IsHq,
                    purchase.MatchedStrategy,
                    take,
                    allocatedCost,
                    allocatedRevenue,
                    profit,
                    roi,
                    holding,
                    purchase.PredictedLiquidationDays,
                    predictedNet,
                    actualNetUnit));

                remaining[purchase.PurchaseId] = open - take;
                saleRemaining -= take;
                transactionMatched += take;
                matchedUnits += take;
                matchedRevenue += allocatedRevenue;
            }

            if (transactionMatched > 0)
                matchedSaleTransactions++;
        }

        var openPositions = purchases
            .Where(p => remaining.TryGetValue(p.PurchaseId, out var qty) && qty > 0)
            .GroupBy(p => (p.ItemId, p.IsHq))
            .Select(g =>
            {
                var rows = g.Select(p => (Purchase: p, Qty: remaining[p.PurchaseId])).Where(x => x.Qty > 0).ToList();
                var qty = rows.Sum(x => x.Qty);
                var cost = rows.Sum(x => (long)Math.Round(x.Purchase.TotalCost / (double)Math.Max(1, x.Purchase.Quantity) * x.Qty));
                var strategy = rows
                    .Where(x => x.Purchase.MatchedStrategy is not null)
                    .GroupBy(x => x.Purchase.MatchedStrategy!.Value)
                    .OrderByDescending(x => x.Sum(y => y.Qty))
                    .Select(x => (BuyStrategy?)x.Key)
                    .FirstOrDefault();
                return new OpenTradePosition(
                    g.Key.ItemId,
                    g.Key.IsHq,
                    catalog.Get(g.Key.ItemId).Name,
                    qty,
                    cost,
                    qty > 0 ? cost / (double)qty : 0,
                    strategy,
                    rows.Min(x => x.Purchase.PurchasedAtUtc));
            })
            .OrderByDescending(x => x.RemainingCostBasis)
            .ToList();

        var strategyStats = allocations
            .Where(x => x.Strategy is not null)
            .GroupBy(x => x.Strategy!.Value)
            .Select(g =>
            {
                var cost = g.Sum(x => x.AllocatedCost);
                var profit = g.Sum(x => x.RealizedProfit);
                var units = g.Sum(x => x.Quantity);
                return new TraderStrategyStats(
                    g.Key,
                    units,
                    profit,
                    cost > 0 ? profit / (double)cost : 0,
                    WeightedAverage(g, x => x.HoldingDays, x => x.Quantity),
                    g.Count() > 0 ? g.Count(x => x.RealizedProfit > 0) / (double)g.Count() : 0);
            })
            .OrderByDescending(x => x.RealizedProfit)
            .ToList();

        var totalCost = allocations.Sum(x => x.AllocatedCost);
        var realizedProfit = allocations.Sum(x => x.RealizedProfit);
        var salesUnits = sales.Sum(x => x.Quantity);
        var sellTimeErrors = allocations
            .Where(x => x.PredictedLiquidationDays is not null)
            .Select(x => Math.Abs(x.HoldingDays - x.PredictedLiquidationDays!.Value))
            .ToList();
        var exitPriceErrors = allocations
            .Where(x => x.PredictedNetUnitPrice is > 0)
            .Select(x => Math.Abs(x.ActualNetUnitPrice - x.PredictedNetUnitPrice!.Value) / x.PredictedNetUnitPrice.Value)
            .ToList();

        return new TraderProfile(
            purchases.Count,
            purchases.Sum(x => x.TotalCost),
            matchedSaleTransactions,
            matchedUnits,
            matchedRevenue,
            realizedProfit,
            totalCost > 0 ? realizedProfit / (double)totalCost : 0,
            allocations.Count > 0 ? allocations.Count(x => x.RealizedProfit > 0) / (double)allocations.Count : 0,
            allocations.Count > 0 ? WeightedAverage(allocations, x => x.HoldingDays, x => x.Quantity) : 0,
            openPositions.Sum(x => x.RemainingCostBasis),
            openPositions.Sum(x => x.Quantity),
            salesUnits > 0 ? Math.Min(1.0, matchedUnits / (double)salesUnits) : 0,
            sellTimeErrors.Count > 0 ? sellTimeErrors.Average() : null,
            exitPriceErrors.Count > 0 ? exitPriceErrors.Average() * 100.0 : null,
            strategyStats.FirstOrDefault()?.Strategy,
            strategyStats,
            openPositions,
            allocations.OrderByDescending(x => x.HoldingDays).ToList());
    }

    private static double WeightedAverage<T>(IEnumerable<T> rows, Func<T, double> value, Func<T, int> weight)
    {
        var materialized = rows.ToList();
        var totalWeight = materialized.Sum(x => Math.Max(0, weight(x)));
        return totalWeight <= 0
            ? 0
            : materialized.Sum(x => value(x) * Math.Max(0, weight(x))) / totalWeight;
    }

    private static TraderProfile Empty()
        => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, null,
            Array.Empty<TraderStrategyStats>(), Array.Empty<OpenTradePosition>(), Array.Empty<ClosedTradeAllocation>());
}
