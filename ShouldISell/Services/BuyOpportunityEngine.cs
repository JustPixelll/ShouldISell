using Dalamud.Plugin.Services;

namespace ShouldISell.Services;

public sealed class BuyOpportunityEngine : IDisposable
{
    private readonly Configuration configuration;
    private readonly IPlayerState playerState;
    private readonly LocalStore store;
    private readonly GameItemCatalog catalog;
    private readonly ScoreCalculator scores;
    private readonly BuyUniversalisClient universalis;
    private readonly IPluginLog log;
    private CancellationTokenSource? scanCts;
    private IReadOnlyList<BuyOpportunity> opportunities = Array.Empty<BuyOpportunity>();

    public BuyOpportunityEngine(
        Configuration configuration,
        IPlayerState playerState,
        LocalStore store,
        GameItemCatalog catalog,
        ScoreCalculator scores,
        BuyUniversalisClient universalis,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.playerState = playerState;
        this.store = store;
        this.catalog = catalog;
        this.scores = scores;
        this.universalis = universalis;
        this.log = log;
    }

    public bool IsScanning { get; private set; }
    public string Status { get; private set; } = "Ready to scan.";
    public DateTimeOffset? LastCompletedUtc { get; private set; }
    public IReadOnlyList<BuyOpportunity> Opportunities => opportunities;

    public void Dispose()
    {
        scanCts?.Cancel();
        scanCts?.Dispose();
    }

    public void Stop()
    {
        scanCts?.Cancel();
        Status = "Scan stopped by user.";
    }

    public async Task ScanAsync()
    {
        if (IsScanning || !playerState.IsLoaded || playerState.ContentId == 0)
            return;

        scanCts?.Dispose();
        scanCts = new CancellationTokenSource();
        var token = scanCts.Token;
        IsScanning = true;
        Status = "Loading marketable item universe...";

        try
        {
            var worldId = playerState.CurrentWorld.RowId;
            var ids = await universalis.FetchMarketableItemIdsAsync(token);
            token.ThrowIfCancellationRequested();

            Status = $"Scanning {ids.Count:N0} marketable items through Universalis aggregate data...";
            var aggregate = await universalis.FetchAggregatedAsync(
                worldId,
                ids,
                (done, total) => Status = $"Discovery pass: batch {done:N0}/{total:N0}...",
                token);
            token.ThrowIfCancellationRequested();

            var candidateIds = SelectDeepCandidates(aggregate);
            if (candidateIds.Count == 0)
            {
                opportunities = Array.Empty<BuyOpportunity>();
                LastCompletedUtc = DateTimeOffset.UtcNow;
                Status = "Scan complete: no candidates survived the discovery filters.";
                return;
            }

            Status = $"Deep-analyzing {candidateIds.Count:N0} candidate items (listings + 90-day history)...";
            var currentTask = universalis.FetchCurrentAsync(worldId, candidateIds, token);
            var historyTask = universalis.FetchHistoryAsync(worldId, candidateIds, token);
            await Task.WhenAll(currentTask, historyTask);
            token.ThrowIfCancellationRequested();

            foreach (var current in currentTask.Result)
                store.MergeUniversalisCurrent(worldId, current.ItemId, current.LastUploadUtc, current.Listings);
            foreach (var history in historyTask.Result)
                store.MergeUniversalisHistory(worldId, history.ItemId, history.LastUploadUtc, history.Sales);

            var aggregateById = aggregate.ToDictionary(x => x.ItemId);
            var built = new List<BuyOpportunity>();
            var total = candidateIds.Count;
            for (var i = 0; i < total; i++)
            {
                token.ThrowIfCancellationRequested();
                var itemId = candidateIds[i];
                var item = catalog.Get(itemId);
                var market = store.GetMarket(worldId, itemId);
                if (market is null)
                    continue;
                aggregateById.TryGetValue(itemId, out var aggregateItem);

                Status = $"Scoring candidate {i + 1:N0}/{total:N0}: {item.Name}";
                BuildForVariant(item, false, market, aggregateItem?.Nq, built);
                if (configuration.BuyIncludeHq && item.CanBeHq)
                    BuildForVariant(item, true, market, aggregateItem?.Hq, built);
            }

            opportunities = built
                .Where(x => x.AcquisitionCost <= configuration.BuyBudgetGil)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.RiskAdjustedProfit)
                .Take(500)
                .ToList();
            LastCompletedUtc = DateTimeOffset.UtcNow;
            store.Flush();
            Status = $"Scan complete: {opportunities.Count:N0} actionable opportunity packages found.";
        }
        catch (OperationCanceledException)
        {
            Status = "Scan stopped by user.";
        }
        catch (Exception ex)
        {
            log.Error(ex, "Should I Buy? scan failed.");
            Status = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    public BuyPortfolio BuildPortfolio()
    {
        var budget = Math.Max(0L, configuration.BuyBudgetGil);
        var remaining = budget;
        var lines = new List<BuyPortfolioLine>();
        var usedVariants = new HashSet<(uint ItemId, bool IsHq)>();

        foreach (var opportunity in opportunities
                     .Where(x => x.AcquisitionCost > 0 && x.RiskAdjustedProfit > 0)
                     .OrderByDescending(PortfolioUtility))
        {
            var key = (opportunity.Item.ItemId, opportunity.IsHq);
            if (usedVariants.Contains(key) || opportunity.AcquisitionCost > remaining)
                continue;

            usedVariants.Add(key);
            lines.Add(new BuyPortfolioLine(
                opportunity,
                opportunity.AcquisitionCost,
                opportunity.PotentialProfit,
                opportunity.RiskAdjustedProfit));
            remaining -= opportunity.AcquisitionCost;
            if (remaining <= 0)
                break;
        }

        return new BuyPortfolio(
            budget,
            budget - remaining,
            remaining,
            lines.Sum(x => x.PotentialProfit),
            lines.Sum(x => x.RiskAdjustedProfit),
            lines);
    }

    public BuyOpportunity? MatchPurchase(uint itemId, bool isHq, uint unitPrice, int quantity, ulong listingId)
    {
        return opportunities
            .Where(x => x.Item.ItemId == itemId && x.IsHq == isHq)
            .Where(x =>
                x.Lots.Any(l => listingId != 0 && l.ListingId == listingId) ||
                (x.MaximumAcceptableBuyPrice is { } max && unitPrice <= max) ||
                x.Strategy == BuyStrategy.MarketToVendor)
            .OrderByDescending(x => x.Lots.Any(l => listingId != 0 && l.ListingId == listingId))
            .ThenByDescending(x => x.Score)
            .FirstOrDefault();
    }

    private IReadOnlyList<uint> SelectDeepCandidates(IReadOnlyList<AggregatedMarketItem> aggregate)
    {
        var ranked = new List<(uint ItemId, double Utility)>();
        foreach (var row in aggregate)
        {
            var item = catalog.Get(row.ItemId);
            if (!item.IsMarketable)
                continue;

            var utility = RoughUtility(item, row.Nq, false);
            if (configuration.BuyIncludeHq && item.CanBeHq)
                utility = Math.Max(utility, RoughUtility(item, row.Hq, true));
            if (utility > 0)
                ranked.Add((row.ItemId, utility));
        }

        return ranked
            .GroupBy(x => x.ItemId)
            .Select(g => g.OrderByDescending(x => x.Utility).First())
            .OrderByDescending(x => x.Utility)
            .Take(Math.Clamp(configuration.BuyDeepCandidateLimit, 20, 500))
            .Select(x => x.ItemId)
            .ToList();
    }

    private double RoughUtility(ItemInfo item, AggregatedVariant variant, bool isHq)
    {
        var min = variant.MinListingPrice;
        var avg = variant.AverageSalePrice;
        var velocity = variant.DailySaleVelocity;
        var best = 0.0;

        if (configuration.BuyEnableMarketToMarket && min > 0 && avg > 0 && velocity > 0)
        {
            var exitNet = avg * (1.0 - ScoreCalculator.MarketSellerTaxRate);
            var buyGross = min * (1.0 + configuration.BuyEstimatedBuyerTaxRate);
            var roi = buyGross > 0 ? (exitNet - buyGross) / buyGross : 0;
            if (roi >= configuration.BuyMinimumRoi * 0.45)
                best = Math.Max(best, Math.Max(0, roi) * Math.Log10(2.0 + velocity));
        }

        if (!isHq && configuration.BuyEnableVendorToMarket && item.VendorGilShopPrice is { } vendor &&
            vendor > 0 && avg > 0 && velocity >= 0.10)
        {
            var roi = (avg * (1.0 - ScoreCalculator.MarketSellerTaxRate) - vendor) / vendor;
            if (roi >= configuration.BuyMinimumRoi * 0.45)
                best = Math.Max(best, Math.Max(0, roi) * Math.Log10(2.0 + velocity));
        }

        if (!isHq && configuration.BuyEnableMarketToVendor && item.VendorBuybackPrice > 0 && min > 0)
        {
            var cost = min * (1.0 + configuration.BuyEstimatedBuyerTaxRate);
            if (cost < item.VendorBuybackPrice)
                best = Math.Max(best, 3.0 + (item.VendorBuybackPrice - cost) / Math.Max(1.0, cost));
        }

        return best;
    }

    private void BuildForVariant(
        ItemInfo item,
        bool isHq,
        MarketSnapshot market,
        AggregatedVariant? aggregate,
        List<BuyOpportunity> output)
    {
        if (configuration.BuyEnableMarketToMarket)
        {
            var marketOpportunity = BuildMarketToMarket(item, isHq, market);
            if (marketOpportunity is not null)
                output.Add(marketOpportunity);
        }

        if (!isHq && configuration.BuyEnableVendorToMarket)
        {
            var vendorMarket = BuildVendorToMarket(item, market, aggregate);
            if (vendorMarket is not null)
                output.Add(vendorMarket);
        }

        if (!isHq && configuration.BuyEnableMarketToVendor)
        {
            var marketVendor = BuildMarketToVendor(item, market);
            if (marketVendor is not null)
                output.Add(marketVendor);
        }
    }

    private BuyOpportunity? BuildMarketToMarket(ItemInfo item, bool isHq, MarketSnapshot market)
    {
        var listings = market.Listings
            .Where(x => x.IsHq == isHq && x.PricePerUnit > 0 && x.Quantity > 0)
            .OrderBy(x => x.PricePerUnit)
            .ThenBy(x => x.Quantity)
            .ToList();
        if (listings.Count == 0)
            return null;

        var itemBudget = Math.Min(
            (long)configuration.BuyBudgetGil,
            (long)Math.Floor(configuration.BuyBudgetGil * Math.Clamp(configuration.BuyMaximumBudgetFractionPerItem, 0.01, 1.0)));
        var existing = ExistingQuantity(item.ItemId, isHq);
        var lots = new List<BuyListingLot>();
        var purchased = new HashSet<MarketListing>();
        long cost = 0;
        int quantity = 0;
        BuyOpportunity? best = null;

        foreach (var listing in listings.Take(16))
        {
            var raw = (long)listing.PricePerUnit * listing.Quantity;
            var tax = (long)Math.Ceiling(raw * Math.Clamp(configuration.BuyEstimatedBuyerTaxRate, 0.0, 0.25));
            var lotCost = raw + tax;
            if (cost + lotCost > itemBudget || cost + lotCost > configuration.BuyBudgetGil)
                break;

            cost += lotCost;
            quantity += (int)listing.Quantity;
            purchased.Add(listing);
            lots.Add(new BuyListingLot(listing.ListingId, listing.PricePerUnit, (int)listing.Quantity, tax, lotCost));

            var simulated = CloneWithout(market, purchased);
            var sell = scores.Calculate(item, isHq, simulated, configuration.ValueThresholdGil, Math.Max(1, existing + quantity));
            if (sell?.SuggestedPrice is not { } exitPrice || sell.UnitsPerDay <= 0.001)
                continue;

            var netExit = (long)Math.Floor(exitPrice * (1.0 - ScoreCalculator.MarketSellerTaxRate));
            var proceeds = netExit * quantity;
            var profit = proceeds - cost;
            var roi = cost > 0 ? profit / (double)cost : 0;
            if (profit < configuration.BuyMinimumProfitGil || roi < configuration.BuyMinimumRoi)
                continue;

            var unitsAhead = simulated.Listings
                .Where(x => x.IsHq == isHq && x.PricePerUnit <= exitPrice)
                .Sum(x => (double)x.Quantity);
            var firstSaleDays = unitsAhead / sell.UnitsPerDay;
            var liquidationDays = (unitsAhead + existing + quantity) / sell.UnitsPerDay;
            var confidence = sell.Confidence;
            var riskAdjusted = RiskAdjust(profit, confidence, liquidationDays);
            var avgBuy = quantity > 0 ? cost / (double)quantity : 0;
            var maximumBuy = MaximumBuyPrice(netExit);
            var strategy = DetermineMarketStrategy(lots, sell.StackRecommendation?.RecommendedStackSize ?? 1);
            var score = ScoreOpportunity(
                roi,
                profit,
                liquidationDays,
                avgBuy,
                sell.HistoricalMedian,
                sell.Breakdown.Demand,
                sell.Breakdown.Stability,
                confidence,
                lots.Count + (sell.StackRecommendation?.RecommendedListingCount ?? 1),
                false);

            var notes = new List<string>
            {
                $"Counterfactual exit model removes the {lots.Count:N0} purchased listing(s) before asking Should I Sell? how the resulting position should be listed.",
                $"Existing stock: {existing:N0}; acquired: {quantity:N0}; modeled position after purchase: {existing + quantity:N0}.",
                $"The risk-adjusted profit discounts the potential profit for evidence confidence and estimated capital lock-up; it is not a guarantee.",
            };
            if (liquidationDays > configuration.BuyMaximumHoldingDays)
                notes.Add($"Estimated liquidation ({liquidationDays:0.##}d) is slower than your {configuration.BuyMaximumHoldingDays:0.##}d holding target.");
            if (strategy == BuyStrategy.SplitStack)
                notes.Add("Historical buyer-size behavior favors splitting the acquired stock into smaller listings.");
            if (strategy == BuyStrategy.ConsolidateStack)
                notes.Add("Historical buyer-size behavior favors consolidating the acquired small lots into larger listings.");

            var opportunity = new BuyOpportunity(
                item,
                isHq,
                strategy,
                Stars(score),
                score,
                confidence,
                quantity,
                existing,
                existing + quantity,
                cost,
                avgBuy,
                exitPrice,
                sell.StackRecommendation?.RecommendedStackSize ?? Math.Min(quantity, (int)Math.Max(1u, item.StackSize)),
                proceeds,
                profit,
                riskAdjusted,
                roi,
                firstSaleDays,
                liquidationDays,
                maximumBuy,
                sell.UnitsPerDay,
                false,
                market.ListingObservedAtUtc,
                BuildBreakdown(roi, profit, liquidationDays, avgBuy, sell.HistoricalMedian, sell, confidence, lots.Count),
                lots.ToList(),
                notes);

            if (best is null || OpportunityUtility(opportunity) > OpportunityUtility(best))
                best = opportunity;
        }

        return best;
    }

    private BuyOpportunity? BuildVendorToMarket(ItemInfo item, MarketSnapshot market, AggregatedVariant? aggregate)
    {
        if (item.VendorGilShopPrice is not { } vendorPrice || vendorPrice == 0 || aggregate is null)
            return null;
        if (aggregate.DailySaleVelocity < 0.10)
            return null;

        var perItemBudget = Math.Min(
            configuration.BuyBudgetGil,
            (int)Math.Floor(configuration.BuyBudgetGil * Math.Clamp(configuration.BuyMaximumBudgetFractionPerItem, 0.01, 1.0)));
        var budgetQty = Math.Max(0, perItemBudget / (int)vendorPrice);
        if (budgetQty <= 0)
            return null;

        var exposureQty = (int)Math.Ceiling(aggregate.DailySaleVelocity * Math.Max(0.25, configuration.BuyMaximumHoldingDays) * 0.75);
        var stackLimit = (int)Math.Clamp(item.StackSize == 0 ? 999u : item.StackSize, 1u, 999u);
        var buyQty = Math.Clamp(exposureQty, 1, Math.Min(stackLimit, budgetQty));
        var existing = ExistingQuantity(item.ItemId, false);
        var sell = scores.Calculate(item, false, market, configuration.ValueThresholdGil, Math.Max(1, existing + buyQty));
        if (sell?.SuggestedPrice is not { } exitPrice || sell.UnitsPerDay <= 0.001)
            return null;

        var netExit = (long)Math.Floor(exitPrice * (1.0 - ScoreCalculator.MarketSellerTaxRate));
        var cost = (long)vendorPrice * buyQty;
        var proceeds = netExit * buyQty;
        var profit = proceeds - cost;
        var roi = cost > 0 ? profit / (double)cost : 0;
        if (profit < configuration.BuyMinimumProfitGil || roi < configuration.BuyMinimumRoi)
            return null;

        var unitsAhead = market.Listings.Where(x => !x.IsHq && x.PricePerUnit <= exitPrice).Sum(x => (double)x.Quantity);
        var firstSaleDays = unitsAhead / sell.UnitsPerDay;
        var liquidationDays = (unitsAhead + existing + buyQty) / sell.UnitsPerDay;
        var confidence = sell.Confidence;
        var score = ScoreOpportunity(
            roi, profit, liquidationDays, vendorPrice, sell.HistoricalMedian,
            sell.Breakdown.Demand, sell.Breakdown.Stability, confidence,
            sell.StackRecommendation?.RecommendedListingCount ?? 1, false);
        var notes = new List<string>
        {
            $"Normal gil vendor price: {vendorPrice:N0}g/unit. Quantity is capped by observed demand, your holding target, stack limit and per-item budget exposure.",
            $"Recent Universalis aggregate velocity is {aggregate.DailySaleVelocity:0.##} units/day; the engine recommends {buyQty:N0}, not an arbitrary full stack.",
            "Vendor stock is an acquisition source; the Market Board exit still carries normal price and liquidity risk.",
        };

        return new BuyOpportunity(
            item, false, BuyStrategy.VendorToMarket, Stars(score), score, confidence,
            buyQty, existing, existing + buyQty, cost, vendorPrice, exitPrice,
            sell.StackRecommendation?.RecommendedStackSize ?? buyQty,
            proceeds, profit, RiskAdjust(profit, confidence, liquidationDays), roi,
            firstSaleDays, liquidationDays, MaximumBuyPrice(netExit), sell.UnitsPerDay,
            false, market.ListingObservedAtUtc,
            BuildBreakdown(roi, profit, liquidationDays, vendorPrice, sell.HistoricalMedian, sell, confidence, 1),
            Array.Empty<BuyListingLot>(), notes);
    }

    private BuyOpportunity? BuildMarketToVendor(ItemInfo item, MarketSnapshot market)
    {
        if (item.VendorBuybackPrice == 0)
            return null;

        var listings = market.Listings
            .Where(x => !x.IsHq && x.PricePerUnit > 0 && x.Quantity > 0)
            .OrderBy(x => x.PricePerUnit)
            .ToList();
        if (listings.Count == 0)
            return null;

        var itemBudget = Math.Min(
            (long)configuration.BuyBudgetGil,
            (long)Math.Floor(configuration.BuyBudgetGil * Math.Clamp(configuration.BuyMaximumBudgetFractionPerItem, 0.01, 1.0)));
        var lots = new List<BuyListingLot>();
        long cost = 0;
        long payout = 0;
        var quantity = 0;

        foreach (var listing in listings.Take(30))
        {
            var raw = (long)listing.PricePerUnit * listing.Quantity;
            var tax = (long)Math.Ceiling(raw * Math.Clamp(configuration.BuyEstimatedBuyerTaxRate, 0.0, 0.25));
            var lotCost = raw + tax;
            var lotPayout = (long)item.VendorBuybackPrice * listing.Quantity;
            if (lotCost >= lotPayout)
                break;
            if (cost + lotCost > itemBudget || cost + lotCost > configuration.BuyBudgetGil)
                break;

            cost += lotCost;
            payout += lotPayout;
            quantity += (int)listing.Quantity;
            lots.Add(new BuyListingLot(listing.ListingId, listing.PricePerUnit, (int)listing.Quantity, tax, lotCost));
        }

        var profit = payout - cost;
        var roi = cost > 0 ? profit / (double)cost : 0;
        if (quantity <= 0 || profit < configuration.BuyMinimumProfitGil || roi < configuration.BuyMinimumRoi)
            return null;

        var roiScore = Clamp01(Math.Log(1.0 + Math.Max(0, roi)) / Math.Log(2.0));
        var profitScore = ProfitScore(profit);
        var score = 100.0 * Clamp01(0.45 * roiScore + 0.35 * profitScore + 0.20);
        score = Math.Max(score, 72.0);
        var avg = cost / (double)quantity;
        var notes = new List<string>
        {
            $"Guaranteed NPC exit: buyback is {item.VendorBuybackPrice:N0}g/unit and every included listing remains profitable after the configured buyer-tax estimate.",
            "No Market Board resale or demand assumption is required. Execution time and travel/menu friction are not priced into gil profit.",
        };

        return new BuyOpportunity(
            item, false, BuyStrategy.MarketToVendor, Stars(score), score, 1.0,
            quantity, ExistingQuantity(item.ItemId, false), ExistingQuantity(item.ItemId, false) + quantity,
            cost, avg, null, quantity, payout, profit, profit, roi,
            0, 0, item.VendorBuybackPrice, 0, true, market.ListingObservedAtUtc,
            new BuyScoreBreakdown(roiScore, profitScore, 1, 1, 1, 1, 1, Clamp01(1.0 - Math.Max(0, lots.Count - 5) / 25.0)),
            lots, notes);
    }

    private int ExistingQuantity(uint itemId, bool isHq)
    {
        if (playerState.ContentId == 0)
            return 0;
        return store.GetInventorySnapshots(playerState.ContentId)
            .SelectMany(x => x.Items)
            .Where(x => x.ItemId == itemId && x.IsHq == isHq)
            .Sum(x => x.Quantity);
    }

    private MarketSnapshot CloneWithout(MarketSnapshot market, HashSet<MarketListing> purchased)
        => new()
        {
            WorldId = market.WorldId,
            ItemId = market.ItemId,
            ListingObservedAtUtc = market.ListingObservedAtUtc,
            HistoryObservedAtUtc = market.HistoryObservedAtUtc,
            UniversalisLastUploadUtc = market.UniversalisLastUploadUtc,
            CurrentSource = market.CurrentSource,
            Listings = market.Listings.Where(x => !purchased.Contains(x)).ToList(),
            Sales = market.Sales.ToList(),
        };

    private BuyStrategy DetermineMarketStrategy(IReadOnlyList<BuyListingLot> lots, int exitStack)
    {
        if (lots.Count == 0)
            return BuyStrategy.MarketSweep;
        var averageLot = lots.Average(x => (double)x.Quantity);
        if (exitStack <= Math.Max(1, averageLot * 0.55))
            return BuyStrategy.SplitStack;
        if (lots.Count >= 2 && exitStack >= averageLot * 1.8)
            return BuyStrategy.ConsolidateStack;
        return BuyStrategy.MarketSweep;
    }

    private long RiskAdjust(long profit, double confidence, double liquidationDays)
    {
        var evidence = 0.35 + 0.65 * Clamp01(confidence);
        var horizon = Math.Max(0.25, configuration.BuyMaximumHoldingDays);
        var timeDiscount = Math.Exp(-0.15 * Math.Max(0, liquidationDays) / horizon);
        if (liquidationDays > horizon)
            timeDiscount *= Math.Exp(-(liquidationDays - horizon) / horizon * 0.55);
        return (long)Math.Round(Math.Max(0, profit) * evidence * timeDiscount);
    }

    private uint MaximumBuyPrice(long netExitUnit)
    {
        var divisor = (1.0 + Math.Max(0, configuration.BuyMinimumRoi)) *
                      (1.0 + Math.Clamp(configuration.BuyEstimatedBuyerTaxRate, 0.0, 0.25));
        return (uint)Math.Max(0, Math.Floor(netExitUnit / Math.Max(0.0001, divisor)));
    }

    private double ScoreOpportunity(
        double roi,
        long profit,
        double liquidationDays,
        double buyUnit,
        double? historicalMedian,
        double demand,
        double stability,
        double confidence,
        int executionSteps,
        bool guaranteed)
    {
        var b = BuildBreakdownRaw(roi, profit, liquidationDays, buyUnit, historicalMedian, demand, stability, confidence, executionSteps);
        var weighted =
            0.22 * b.Roi +
            0.20 * b.Profit +
            0.18 * b.Liquidity +
            0.12 * b.PriceAdvantage +
            0.10 * b.Demand +
            0.07 * b.Stability +
            0.06 * b.Confidence +
            0.05 * b.Execution;
        if (guaranteed)
            weighted = Math.Max(weighted, 0.72);
        return 100.0 * Clamp01(weighted);
    }

    private BuyScoreBreakdown BuildBreakdown(
        double roi,
        long profit,
        double liquidationDays,
        double buyUnit,
        double? historicalMedian,
        SellRating sell,
        double confidence,
        int executionSteps)
        => BuildBreakdownRaw(
            roi, profit, liquidationDays, buyUnit, historicalMedian,
            sell.Breakdown.Demand, sell.Breakdown.Stability, confidence, executionSteps);

    private BuyScoreBreakdown BuildBreakdownRaw(
        double roi,
        long profit,
        double liquidationDays,
        double buyUnit,
        double? historicalMedian,
        double demand,
        double stability,
        double confidence,
        int executionSteps)
    {
        var roiScore = Clamp01(Math.Log(1.0 + Math.Max(0, roi)) / Math.Log(2.0));
        var profitScore = ProfitScore(profit);
        var horizon = Math.Max(0.25, configuration.BuyMaximumHoldingDays);
        var liquidity = Math.Exp(-Math.Max(0, liquidationDays) / horizon);
        var priceAdvantage = historicalMedian is > 0
            ? Clamp01((historicalMedian.Value - buyUnit) / historicalMedian.Value * 2.0)
            : 0.20;
        var execution = Clamp01(1.0 - Math.Max(0, executionSteps - 4) / 30.0);
        return new BuyScoreBreakdown(
            roiScore,
            profitScore,
            liquidity,
            priceAdvantage,
            Clamp01(demand),
            Clamp01(stability),
            Clamp01(confidence),
            execution);
    }

    private double ProfitScore(long profit)
    {
        var reference = Math.Max(1.0, configuration.BuyMinimumProfitGil);
        if (profit <= 0)
            return 0;
        return Clamp01(0.5 + 0.25 * Math.Log10(profit / reference));
    }

    private static double PortfolioUtility(BuyOpportunity x)
        => x.AcquisitionCost <= 0
            ? 0
            : (x.RiskAdjustedProfit / (double)x.AcquisitionCost) * (0.5 + x.Score / 100.0);

    private static double OpportunityUtility(BuyOpportunity x)
        => x.RiskAdjustedProfit * (0.5 + x.Score / 100.0);

    private static int Stars(double score)
        => score >= 85 ? 5 : score >= 70 ? 4 : score >= 50 ? 3 : score >= 30 ? 2 : 1;

    private static double Clamp01(double x) => Math.Clamp(x, 0.0, 1.0);
}
