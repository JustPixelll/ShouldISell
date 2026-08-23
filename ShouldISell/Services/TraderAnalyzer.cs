using Dalamud.Plugin.Services;

namespace ShouldISell.Services;

/// <summary>
/// Joins the purchase ledger captured by Should I Buy?/Dalamud with Should I Sell?'s personal
/// retainer-sale ledger. Cost basis is matched FIFO per item/HQ variant and only purchases that
/// happened before a sale can fund that sale. This keeps old/offline sales from fabricating profit.
/// </summary>
public sealed class TraderAnalyzer
{
    private readonly IPlayerState playerState;
    private readonly TraderStore traderStore;
    private readonly LocalStore sellStore;
    private readonly MarketDataCoordinator coordinator;
    private readonly GameItemCatalog catalog;
    private readonly object cacheGate = new();
    private TraderSnapshot? cached;
    private DateTimeOffset cacheUntilUtc;

    public TraderAnalyzer(
        IPlayerState playerState,
        TraderStore traderStore,
        LocalStore sellStore,
        MarketDataCoordinator coordinator,
        GameItemCatalog catalog)
    {
        this.playerState = playerState;
        this.traderStore = traderStore;
        this.sellStore = sellStore;
        this.coordinator = coordinator;
        this.catalog = catalog;
    }

    public TraderSnapshot GetSnapshot(bool force = false)
    {
        lock (cacheGate)
        {
            if (!force && cached is not null && DateTimeOffset.UtcNow < cacheUntilUtc)
                return cached;
        }

        var snapshot = Calculate();
        lock (cacheGate)
        {
            cached = snapshot;
            cacheUntilUtc = DateTimeOffset.UtcNow.AddSeconds(2);
        }
        return snapshot;
    }

    private TraderSnapshot Calculate()
    {
        var now = DateTimeOffset.UtcNow;
        if (!playerState.IsLoaded || playerState.ContentId == 0)
            return Empty(now, "Log in to build your trader profile.");

        var contentId = playerState.ContentId;
        var purchases = traderStore.GetPurchases(contentId)
            .Where(x => !traderStore.IsPurchaseExcluded(x))
            .OrderBy(x => x.PurchasedAtUtc)
            .ToList();
        var sales = sellStore.GetPersonalSales(contentId).OrderBy(x => x.SoldAtUtc).ToList();
        var closed = new List<ClosedTrade>();
        var openLots = new List<MutableLot>();
        var unmatchedSaleUnits = 0;

        var variants = purchases.Select(x => (x.ItemId, x.IsHq))
            .Concat(sales.Select(x => (x.ItemId, x.IsHq)))
            .Distinct()
            .ToList();

        foreach (var variant in variants)
        {
            var events = new List<TradeEvent>();
            events.AddRange(purchases
                .Where(x => x.ItemId == variant.ItemId && x.IsHq == variant.IsHq)
                .Select(x => new TradeEvent(x.PurchasedAtUtc, 0, x, null)));
            events.AddRange(sales
                .Where(x => x.ItemId == variant.ItemId && x.IsHq == variant.IsHq)
                .Select(x => new TradeEvent(x.SoldAtUtc, 1, null, x)));
            events = events.OrderBy(x => x.AtUtc).ThenBy(x => x.Kind).ToList();

            var fifo = new List<MutableLot>();
            foreach (var tradeEvent in events)
            {
                if (tradeEvent.Purchase is { } purchase)
                {
                    fifo.Add(new MutableLot(purchase));
                    continue;
                }

                var sale = tradeEvent.Sale!;
                if (sale.Quantity <= 0 || sale.NetGil <= 0)
                    continue;

                var remainingSale = sale.Quantity;
                var consumed = new List<(MutableLot Lot, int Quantity)>();
                foreach (var lot in fifo.Where(x => x.Remaining > 0).OrderBy(x => x.Purchase.PurchasedAtUtc))
                {
                    if (remainingSale <= 0)
                        break;
                    var quantity = Math.Min(remainingSale, lot.Remaining);
                    if (quantity <= 0)
                        continue;
                    lot.Remaining -= quantity;
                    remainingSale -= quantity;
                    consumed.Add((lot, quantity));
                }

                unmatchedSaleUnits += Math.Max(0, remainingSale);
                if (consumed.Count == 0)
                    continue;

                var trackedQuantity = consumed.Sum(x => x.Quantity);
                var costBasis = consumed.Sum(x => x.Quantity * x.Lot.UnitCost);
                var saleNetPerUnit = sale.NetGil / (double)Math.Max(1, sale.Quantity);
                var netRevenue = saleNetPerUnit * trackedQuantity;
                var profit = netRevenue - costBasis;
                var roi = costBasis > 0 ? profit / costBasis : 0;
                var holdingDays = consumed.Sum(x => x.Quantity * Math.Max(0, (sale.SoldAtUtc - x.Lot.Purchase.PurchasedAtUtc).TotalDays)) /
                                  Math.Max(1, trackedQuantity);
                var strategy = consumed
                    .GroupBy(x => x.Lot.Purchase.Strategy)
                    .OrderByDescending(g => g.Sum(x => x.Quantity))
                    .Select(g => g.Key)
                    .FirstOrDefault() ?? "Unknown";
                var predictedExit = WeightedNullable(
                    consumed.Select(x => (x.Lot.Purchase.PredictedExitUnitPrice is { } p ? (double?)p : null, x.Quantity)));
                var predictedDays = WeightedNullable(
                    consumed.Select(x => (x.Lot.Purchase.PredictedLiquidationDays, x.Quantity)));

                closed.Add(new ClosedTrade(
                    variant.ItemId,
                    variant.IsHq,
                    catalog.Get(variant.ItemId).Name,
                    trackedQuantity,
                    costBasis,
                    netRevenue,
                    profit,
                    roi,
                    holdingDays,
                    strategy,
                    sale.SoldAtUtc,
                    predictedExit,
                    predictedDays));
            }

            openLots.AddRange(fifo.Where(x => x.Remaining > 0));
        }

        var ratedOwned = coordinator.GetRatedOwnedItems()
            .ToDictionary(x => (x.Item.ItemId, x.IsHq));
        var ownListings = sellStore.GetOwnListings(contentId)
            .GroupBy(x => (x.ItemId, x.IsHq))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        var openPositions = openLots
            .GroupBy(x => (x.Purchase.ItemId, x.Purchase.IsHq))
            .Select(group =>
            {
                var quantity = group.Sum(x => x.Remaining);
                var cost = group.Sum(x => x.Remaining * x.UnitCost);
                var key = group.Key;
                ownListings.TryGetValue(key, out var listedQuantity);
                ratedOwned.TryGetValue(key, out var rated);
                var netSuggested = rated?.Rating?.NetSuggestedPriceAfterTax;
                var estimatedValue = netSuggested is { } net ? net * (double)quantity : (double?)null;
                var strategy = group
                    .GroupBy(x => x.Purchase.Strategy)
                    .OrderByDescending(g => g.Sum(x => x.Remaining))
                    .Select(g => g.Key)
                    .FirstOrDefault() ?? "Unknown";
                return new OpenTraderPosition(
                    key.ItemId,
                    key.IsHq,
                    catalog.Get(key.ItemId).Name,
                    quantity,
                    cost,
                    quantity > 0 ? cost / quantity : 0,
                    listedQuantity,
                    rated?.Rating?.SuggestedPrice,
                    estimatedValue,
                    estimatedValue is { } value ? value - cost : null,
                    strategy,
                    group.Min(x => x.Purchase.PurchasedAtUtc));
            })
            .OrderByDescending(x => x.CostBasis)
            .ToList();

        var topItems = closed
            .GroupBy(x => (x.ItemId, x.IsHq, x.ItemName))
            .Select(g =>
            {
                var cost = g.Sum(x => x.CostBasis);
                var revenue = g.Sum(x => x.NetRevenue);
                return new TraderItemPerformance(
                    g.Key.ItemId,
                    g.Key.IsHq,
                    g.Key.ItemName,
                    g.Sum(x => x.Quantity),
                    cost,
                    revenue,
                    revenue - cost,
                    cost > 0 ? (revenue - cost) / cost : 0,
                    WeightedAverage(g.Select(x => (x.HoldingDays, x.Quantity))));
            })
            .OrderByDescending(x => x.Profit)
            .Take(20)
            .ToList();

        var strategyStats = closed
            .GroupBy(x => x.Strategy)
            .Select(g =>
            {
                var cost = g.Sum(x => x.CostBasis);
                var revenue = g.Sum(x => x.NetRevenue);
                return new TraderStrategyPerformance(
                    g.Key,
                    g.Sum(x => x.Quantity),
                    g.Count(),
                    cost,
                    revenue,
                    revenue - cost,
                    cost > 0 ? (revenue - cost) / cost : 0,
                    WeightedAverage(g.Select(x => (x.HoldingDays, x.Quantity))));
            })
            .OrderByDescending(x => x.Profit)
            .ToList();

        var capitalInvested = purchases.Sum(x => (double)x.TotalCost);
        var realizedRevenue = closed.Sum(x => x.NetRevenue);
        var realizedCost = closed.Sum(x => x.CostBasis);
        var realizedProfit = realizedRevenue - realizedCost;
        var realizedRoi = realizedCost > 0 ? realizedProfit / realizedCost : 0;
        var winRate = closed.Count > 0 ? closed.Count(x => x.Profit > 0) / (double)closed.Count : 0;
        var medianHolding = Median(closed.Select(x => x.HoldingDays).ToList());
        var openCost = openPositions.Sum(x => x.CostBasis);
        var knownOpenValues = openPositions.Where(x => x.EstimatedNetMarketValue is not null).ToList();
        var openValue = knownOpenValues.Count > 0
            ? knownOpenValues.Sum(x => x.EstimatedNetMarketValue!.Value)
            : (double?)null;
        double? unrealized = openValue is { } v ? v - knownOpenValues.Sum(x => x.CostBasis) : null;

        var priceErrors = closed
            .Where(x => x.PredictedExitUnitPrice is > 0)
            .Select(x =>
            {
                var predictedNet = x.PredictedExitUnitPrice!.Value * (1 - ScoreCalculator.MarketSellerTaxRate);
                var actualNet = x.NetRevenue / Math.Max(1, x.Quantity);
                return predictedNet > 0 ? Math.Abs(actualNet - predictedNet) / predictedNet : double.NaN;
            })
            .Where(double.IsFinite)
            .ToList();
        var holdingErrors = closed
            .Where(x => x.PredictedLiquidationDays is > 0.01)
            .Select(x => Math.Abs(x.HoldingDays - x.PredictedLiquidationDays!.Value) / x.PredictedLiquidationDays.Value)
            .Where(double.IsFinite)
            .ToList();

        var profile = BuildProfile(closed, strategyStats, realizedRoi, medianHolding);
        return new TraderSnapshot(
            profile.Name,
            profile.Description,
            purchases.Count,
            closed.Count,
            closed.Sum(x => x.Quantity),
            openPositions.Sum(x => x.Quantity),
            capitalInvested,
            realizedRevenue,
            realizedProfit,
            realizedRoi,
            winRate,
            medianHolding,
            openCost,
            openValue,
            unrealized,
            unmatchedSaleUnits,
            priceErrors.Count == 0 ? null : priceErrors.Average(),
            holdingErrors.Count == 0 ? null : holdingErrors.Average(),
            closed.OrderByDescending(x => x.SoldAtUtc).Take(100).ToList(),
            openPositions,
            topItems,
            strategyStats,
            now);
    }

    private static (string Name, string Description) BuildProfile(
        IReadOnlyList<ClosedTrade> closed,
        IReadOnlyList<TraderStrategyPerformance> strategies,
        double realizedRoi,
        double medianHoldingDays)
    {
        if (closed.Count < 3)
            return ("Building history", "Tycoon has started tracking your real Market Board cost basis. A clearer trading style will emerge after a few matched purchases and retainer sales.");

        var totalClosedUnits = Math.Max(1, closed.Sum(x => x.Quantity));
        var vendorUnits = strategies
            .Where(x => x.Strategy.Contains("Vendor", StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.ClosedUnits);
        var splitUnits = strategies
            .Where(x => x.Strategy.Contains("split", StringComparison.OrdinalIgnoreCase) || x.Strategy.Contains("consolidate", StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.ClosedUnits);

        if (vendorUnits / (double)totalClosedUnits >= 0.35)
            return ("Arbitrage specialist", "A large share of your tracked volume comes from structurally priced vendor/market opportunities rather than ordinary speculative flips.");
        if (splitUnits / (double)totalClosedUnits >= 0.35)
            return ("Stack merchant", "You frequently create value by repackaging the market: splitting inconvenient bulk or consolidating fragmented supply into buyer-friendly stacks.");
        if (medianHoldingDays <= 1.0)
            return ("Fast flipper", "Your typical tracked position turns over in about a day or less. Capital velocity is a defining part of your trading style.");
        if (realizedRoi >= 0.30)
            return ("Value hunter", "Your matched trades show unusually high realized return on cost, suggesting you favor deeper discounts over constant turnover.");
        if (medianHoldingDays >= 7.0)
            return ("Patient trader", "You are comfortable holding inventory for a week or longer when the expected margin justifies tying up capital.");
        return ("Balanced trader", "Your profile balances margin and turnover without one strategy dominating the tracked history.");
    }

    private static TraderSnapshot Empty(DateTimeOffset now, string description)
        => new(
            "No trader data yet",
            description,
            0, 0, 0, 0,
            0, 0, 0, 0, 0, 0,
            0, null, null,
            0, null, null,
            Array.Empty<ClosedTrade>(),
            Array.Empty<OpenTraderPosition>(),
            Array.Empty<TraderItemPerformance>(),
            Array.Empty<TraderStrategyPerformance>(),
            now);

    private static double WeightedAverage(IEnumerable<(double Value, int Weight)> values)
    {
        var list = values.Where(x => x.Weight > 0).ToList();
        var weight = list.Sum(x => x.Weight);
        return weight <= 0 ? 0 : list.Sum(x => x.Value * x.Weight) / weight;
    }

    private static double? WeightedNullable(IEnumerable<(double? Value, int Weight)> values)
    {
        var list = values.Where(x => x.Value is not null && x.Weight > 0).ToList();
        var weight = list.Sum(x => x.Weight);
        return weight <= 0 ? null : list.Sum(x => x.Value!.Value * x.Weight) / weight;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
            return 0;
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 0 ? (values[mid - 1] + values[mid]) / 2 : values[mid];
    }

    private sealed class MutableLot
    {
        public MutableLot(PersonalPurchase purchase)
        {
            Purchase = purchase;
            Remaining = purchase.Quantity;
            UnitCost = purchase.TotalCost / (double)Math.Max(1, purchase.Quantity);
        }

        public PersonalPurchase Purchase { get; }
        public int Remaining { get; set; }
        public double UnitCost { get; }
    }

    private sealed record TradeEvent(
        DateTimeOffset AtUtc,
        int Kind,
        PersonalPurchase? Purchase,
        PersonalSale? Sale);
}
