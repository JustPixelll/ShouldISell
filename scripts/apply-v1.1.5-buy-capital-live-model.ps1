$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Get-Content $Path -Raw
    if (-not $text.Contains($Old)) {
        throw "Expected patch text not found in $Path`n--- OLD ---`n$Old"
    }
    $text = $text.Replace($Old, $New)
    Set-Content $Path $text -Encoding UTF8
}

$scanner = 'ShouldISell/Services/BuyOpportunityScanner.cs'
$plugin = 'ShouldISell/Plugin.cs'
$buyUi = 'ShouldISell/Windows/SuiteWindow.Buy.cs'
$project = 'ShouldISell/ShouldISell.csproj'

Replace-Exact $scanner 'public sealed class BuyOpportunityScanner : IDisposable' 'public sealed partial class BuyOpportunityScanner : IDisposable'
Replace-Exact $scanner @'
    private readonly InventoryScanner inventory;
    private readonly ScoreCalculator scores;
'@ @'
    private readonly InventoryScanner inventory;
    private readonly LocalStore store;
    private readonly ScoreCalculator scores;
'@
Replace-Exact $scanner @'
        GameItemCatalog catalog,
        InventoryScanner inventory,
        ScoreCalculator scores,
'@ @'
        GameItemCatalog catalog,
        InventoryScanner inventory,
        LocalStore store,
        ScoreCalculator scores,
'@
Replace-Exact $scanner @'
        this.catalog = catalog;
        this.inventory = inventory;
        this.scores = scores;
'@ @'
        this.catalog = catalog;
        this.inventory = inventory;
        this.store = store;
        this.scores = scores;
'@
Replace-Exact $scanner 'new ProductInfoHeaderValue("ShouldI", "1.1.3")' 'new ProductInfoHeaderValue("ShouldI", "1.1.5")'

Replace-Exact $scanner @'
            var ownedByVariant = inventory.GetKnownOwnedStacks()
                .GroupBy(x => (x.ItemId, x.IsHq))
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

            var final = new List<BuyOpportunity>();
'@ @'
            var ownedByVariant = inventory.GetKnownOwnedStacks()
                .GroupBy(x => (x.ItemId, x.IsHq))
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
            var ownListedVariants = playerState.ContentId == 0
                ? new HashSet<(uint ItemId, bool IsHq)>()
                : store.GetOwnListings(playerState.ContentId)
                    .Select(x => (x.ItemId, x.IsHq))
                    .ToHashSet();

            var final = new List<BuyOpportunity>();
'@
Replace-Exact $scanner @'
                if (settings.EnableVendorToMarket && !candidate.IsHq && candidate.Entry.Item.VendorGilShopPrice is > 0)
                    TryAddVendorToMarket(final, worldId, candidate, deep, existingQuantity, settings);
'@ @'
                if (settings.EnableVendorToMarket && !candidate.IsHq && candidate.Entry.Item.VendorGilShopPrice is > 0)
                    TryAddVendorToMarket(
                        final,
                        worldId,
                        candidate,
                        deep,
                        existingQuantity,
                        ownListedVariants.Contains((candidate.Entry.Item.ItemId, false)),
                        settings);
'@

Replace-Exact $scanner @'
            var potentialProfit = netExit * (double)cumulativeQuantity - cumulativeCost;
            var roi = cumulativeCost > 0 ? potentialProfit / cumulativeCost : 0;
            if (potentialProfit < settings.MinimumProfitGil || roi < settings.MinimumRoi)
                continue;

            var liquidationDays = EstimateLiquidationDays(rating, resultingPosition);
            if (liquidationDays is null || liquidationDays > settings.MaximumHoldingDays)
                continue;

            var firstSaleDays = rating.EstimatedQueueDays;
            var liquidityFit = Math.Exp(-liquidationDays.Value / Math.Max(1.0, settings.MaximumHoldingDays));
            var riskFactor = Clamp01(
                rating.Confidence *
                (0.55 + 0.45 * liquidityFit) *
                (0.75 + 0.25 * rating.Breakdown.Stability));
            var riskAdjustedProfit = Math.Max(0, potentialProfit) * riskFactor;
            var score = ScoreBuyOpportunity(
                roi,
                potentialProfit,
                liquidationDays.Value,
                settings.MaximumHoldingDays,
                PriceAdvantage(cumulativeCost, cumulativeQuantity, netExit),
                rating.Breakdown.Demand,
                rating.Breakdown.Stability,
                rating.Confidence,
                i + 1,
                rating.StackRecommendation?.RecommendedListingCount ?? 1,
                settings.MinimumProfitGil);

            var stackSize = rating.StackRecommendation?.RecommendedStackSize ?? Math.Max(1, cumulativeQuantity);
            var exitListings = rating.StackRecommendation?.RecommendedListingCount ?? DivideRoundUp(resultingPosition, stackSize);
'@ @'
            var potentialProfit = netExit * (double)cumulativeQuantity - cumulativeCost;
            var roi = cumulativeCost > 0 ? potentialProfit / cumulativeCost : 0;
            if (potentialProfit < settings.MinimumProfitGil || roi < settings.MinimumRoi)
                continue;

            var stackSize = Math.Max(1, rating.StackRecommendation?.RecommendedStackSize ?? cumulativeQuantity);
            var exitListings = DivideRoundUp(resultingPosition, stackSize);
            var liquidationDays = EstimateLiquidationDays(rating, resultingPosition, stackSize);
            if (liquidationDays is null || liquidationDays > settings.MaximumHoldingDays)
                continue;

            var firstSaleDays = rating.EstimatedQueueDays;
            var liquidityFit = Math.Exp(-liquidationDays.Value / Math.Max(1.0, settings.MaximumHoldingDays));
            var riskFactor = Clamp01(
                rating.Confidence *
                (0.55 + 0.45 * liquidityFit) *
                (0.75 + 0.25 * rating.Breakdown.Stability));
            var oneListingRecovery = OneListingCapitalRecovery(cumulativeCost, cumulativeQuantity, stackSize, netExit);
            var riskAdjustedProfit = Math.Max(0, potentialProfit) * riskFactor * OneListingDeploymentFactor(oneListingRecovery);
            var score = ScoreBuyOpportunity(
                roi,
                potentialProfit,
                liquidationDays.Value,
                settings.MaximumHoldingDays,
                PriceAdvantage(cumulativeCost, cumulativeQuantity, netExit),
                rating.Breakdown.Demand,
                rating.Breakdown.Stability,
                rating.Confidence,
                i + 1,
                exitListings,
                settings.MinimumProfitGil,
                oneListingRecovery);
'@
Replace-Exact $scanner @'
            var notes = BuildMarketNotes(candidate, rating, cumulativeQuantity, cumulativeCost, existingQuantity, variantListings, i);
            var opportunity = new BuyOpportunity(
'@ @'
            var notes = BuildMarketNotes(candidate, rating, cumulativeQuantity, cumulativeCost, existingQuantity, variantListings, i);
            notes.Add(OneListingModelNote(cumulativeCost, cumulativeQuantity, stackSize, netExit, exitListings));
            var opportunity = new BuyOpportunity(
'@

$oldVendor = @'
    private void TryAddVendorToMarket(
        List<BuyOpportunity> output,
        uint worldId,
        RoughCandidate candidate,
        DeepMarketData deep,
        int existingQuantity,
        ScanSettings settings)
    {
        var vendorPrice = candidate.Entry.Item.VendorGilShopPrice;
        if (vendorPrice is not > 0 || candidate.Variant.DailyVelocity <= 0.001)
            return;

        var affordable = (int)Math.Min(int.MaxValue, settings.BudgetGil / vendorPrice.Value);
        var perItemBudget = Math.Max(1L, settings.BudgetGil * Math.Clamp(settings.MaxInvestmentPercentPerItem, 1, 100) / 100L);
        affordable = Math.Min(affordable, (int)Math.Min(int.MaxValue, perItemBudget / vendorPrice.Value));
        var demandBound = Math.Max(1, (int)Math.Ceiling(candidate.Variant.DailyVelocity * settings.MaximumHoldingDays));
        var stackBound = (int)Math.Clamp(candidate.Entry.Item.StackSize == 0 ? 999u : candidate.Entry.Item.StackSize, 1u, int.MaxValue);
        var quantity = Math.Min(affordable, Math.Min(demandBound, stackBound));
        if (quantity <= 0)
            return;

        var listings = deep.Listings
            .Where(x => !x.Listing.IsHq)
            .Select(x => x.Listing)
            .OrderBy(x => x.PricePerUnit)
            .ToList();
        var market = new MarketSnapshot
        {
            WorldId = worldId,
            ItemId = candidate.Entry.Item.ItemId,
            ListingObservedAtUtc = deep.ListingObservedAtUtc,
            HistoryObservedAtUtc = deep.HistoryObservedAtUtc,
            UniversalisLastUploadUtc = deep.ListingObservedAtUtc,
            CurrentSource = MarketDataSource.Universalis,
            Listings = listings,
            Sales = deep.Sales,
        };

        var resultingPosition = Math.Max(1, existingQuantity + quantity);
        var rating = scores.Calculate(candidate.Entry.Item, false, market, configuration.ValueThresholdGil, resultingPosition);
        if (rating?.NetSuggestedPriceAfterTax is not { } netExit || rating.SuggestedPrice is not { } grossExit)
            return;

        var cost = (long)vendorPrice.Value * quantity;
        var potentialProfit = netExit * (double)quantity - cost;
        var roi = cost > 0 ? potentialProfit / cost : 0;
        var liquidationDays = EstimateLiquidationDays(rating, resultingPosition);
        if (potentialProfit < settings.MinimumProfitGil || roi < settings.MinimumRoi ||
            liquidationDays is null || liquidationDays > settings.MaximumHoldingDays)
            return;

        var liquidityFit = Math.Exp(-liquidationDays.Value / Math.Max(1.0, settings.MaximumHoldingDays));
        var riskFactor = Clamp01(rating.Confidence * (0.55 + 0.45 * liquidityFit) * (0.75 + 0.25 * rating.Breakdown.Stability));
        var riskAdjustedProfit = potentialProfit * riskFactor;
        var score = ScoreBuyOpportunity(
            roi,
            potentialProfit,
            liquidationDays.Value,
            settings.MaximumHoldingDays,
            PriceAdvantage(cost, quantity, netExit),
            rating.Breakdown.Demand,
            rating.Breakdown.Stability,
            rating.Confidence,
            1,
            rating.StackRecommendation?.RecommendedListingCount ?? 1,
            settings.MinimumProfitGil);

        var stackSize = rating.StackRecommendation?.RecommendedStackSize ?? quantity;
        var notes = new List<string>
        {
            $"Normal gil vendor price is {vendorPrice.Value:N0}g/unit; this route does not depend on finding a cheap Market Board listing.",
            $"Quantity is demand-capped to about {settings.MaximumHoldingDays:0.#} day(s) of recent velocity rather than blindly buying a full stack.",
            $"Recent estimated demand is {rating.UnitsPerDay:0.##} unit(s)/day across {rating.SalesSampleCount:N0} sampled sale(s).",
        };
        if (existingQuantity > 0)
            notes.Add($"You already own {existingQuantity:N0}; exit planning uses the combined {resultingPosition:N0}-unit position, while profit counts only the new vendor purchase.");

        output.Add(new BuyOpportunity(
            worldId,
            candidate.Entry.Item,
            false,
            BuyOpportunityKind.VendorToMarket,
            StrategyLabel(BuyOpportunityKind.VendorToMarket),
            Stars(score),
            score,
            rating.Confidence,
            existingQuantity,
            quantity,
            cost,
            vendorPrice.Value,
            grossExit,
            netExit,
            stackSize,
            rating.StackRecommendation?.RecommendedListingCount ?? DivideRoundUp(resultingPosition, stackSize),
            potentialProfit,
            riskAdjustedProfit,
            roi,
            rating.EstimatedQueueDays,
            liquidationDays,
            CalculateMaximumBuyPrice(netExit, settings.MinimumRoi),
            rating.UnitsPerDay,
            rating.SalesSampleCount,
            rating.ListingFreshnessUtc,
            Array.Empty<BuyAcquisitionLot>(),
            notes,
            DateTimeOffset.UtcNow));
    }
'@
$newVendor = @'
    private void TryAddVendorToMarket(
        List<BuyOpportunity> output,
        uint worldId,
        RoughCandidate candidate,
        DeepMarketData deep,
        int existingQuantity,
        bool hasOwnListing,
        ScanSettings settings)
    {
        var vendorPrice = candidate.Entry.Item.VendorGilShopPrice;
        if (vendorPrice is not > 0 || candidate.Variant.DailyVelocity <= 0.001 || hasOwnListing)
            return;

        var affordable = (int)Math.Min(int.MaxValue, settings.BudgetGil / vendorPrice.Value);
        var perItemBudget = Math.Max(1L, settings.BudgetGil * Math.Clamp(settings.MaxInvestmentPercentPerItem, 1, 100) / 100L);
        affordable = Math.Min(affordable, (int)Math.Min(int.MaxValue, perItemBudget / vendorPrice.Value));
        if (affordable <= 0)
            return;

        var demandBound = Math.Max(1, (int)Math.Ceiling(candidate.Variant.DailyVelocity * settings.MaximumHoldingDays));
        var stackBound = (int)Math.Clamp(candidate.Entry.Item.StackSize == 0 ? 999u : candidate.Entry.Item.StackSize, 1u, int.MaxValue);
        var listings = deep.Listings
            .Where(x => !x.Listing.IsHq)
            .Select(x => x.Listing)
            .OrderBy(x => x.PricePerUnit)
            .ToList();
        var market = new MarketSnapshot
        {
            WorldId = worldId,
            ItemId = candidate.Entry.Item.ItemId,
            ListingObservedAtUtc = deep.ListingObservedAtUtc,
            HistoryObservedAtUtc = deep.HistoryObservedAtUtc,
            UniversalisLastUploadUtc = deep.ListingObservedAtUtc,
            CurrentSource = MarketDataSource.Universalis,
            Listings = listings,
            Sales = deep.Sales,
        };

        // Vendor supply is effectively replenishable on demand. Stockpiling several days of vendor
        // inventory is therefore unnecessary: target one recommended active listing and only top up
        // the amount the player does not already own.
        var provisionalPosition = Math.Max(1, Math.Min(demandBound, stackBound));
        var provisionalRating = scores.Calculate(
            candidate.Entry.Item,
            false,
            market,
            configuration.ValueThresholdGil,
            provisionalPosition);
        if (provisionalRating is null)
            return;

        var desiredStack = Math.Clamp(
            provisionalRating.StackRecommendation?.RecommendedStackSize ?? provisionalPosition,
            1,
            provisionalPosition);
        var targetPosition = Math.Max(1, Math.Min(desiredStack, Math.Min(demandBound, stackBound)));
        var quantity = Math.Min(affordable, Math.Max(0, targetPosition - existingQuantity));
        if (quantity <= 0)
            return;

        var resultingPosition = Math.Max(1, existingQuantity + quantity);
        var rating = scores.Calculate(candidate.Entry.Item, false, market, configuration.ValueThresholdGil, resultingPosition);
        if (rating?.NetSuggestedPriceAfterTax is not { } netExit || rating.SuggestedPrice is not { } grossExit)
            return;

        var cost = (long)vendorPrice.Value * quantity;
        var potentialProfit = netExit * (double)quantity - cost;
        var roi = cost > 0 ? potentialProfit / cost : 0;
        var stackSize = Math.Max(1, rating.StackRecommendation?.RecommendedStackSize ?? targetPosition);
        var exitListings = DivideRoundUp(resultingPosition, stackSize);
        var liquidationDays = EstimateLiquidationDays(rating, resultingPosition, stackSize);
        if (potentialProfit < settings.MinimumProfitGil || roi < settings.MinimumRoi ||
            liquidationDays is null || liquidationDays > settings.MaximumHoldingDays)
            return;

        var liquidityFit = Math.Exp(-liquidationDays.Value / Math.Max(1.0, settings.MaximumHoldingDays));
        var riskFactor = Clamp01(rating.Confidence * (0.55 + 0.45 * liquidityFit) * (0.75 + 0.25 * rating.Breakdown.Stability));
        var oneListingRecovery = OneListingCapitalRecovery(cost, quantity, stackSize, netExit);
        var riskAdjustedProfit = potentialProfit * riskFactor * OneListingDeploymentFactor(oneListingRecovery);
        var score = ScoreBuyOpportunity(
            roi,
            potentialProfit,
            liquidationDays.Value,
            settings.MaximumHoldingDays,
            PriceAdvantage(cost, quantity, netExit),
            rating.Breakdown.Demand,
            rating.Breakdown.Stability,
            rating.Confidence,
            1,
            exitListings,
            settings.MinimumProfitGil,
            oneListingRecovery);

        var notes = new List<string>
        {
            $"Normal gil vendor price is {vendorPrice.Value:N0}g/unit; this route does not depend on finding a cheap Market Board listing.",
            $"Vendor supply is replenishable, so Should I Buy? now targets only one recommended active listing ({targetPosition:N0} unit(s)) instead of stockpiling several days of demand.",
            $"Recent estimated demand is {rating.UnitsPerDay:0.##} unit(s)/day across {rating.SalesSampleCount:N0} sampled sale(s).",
            OneListingModelNote(cost, quantity, stackSize, netExit, exitListings),
        };
        if (existingQuantity > 0)
            notes.Add($"You already own {existingQuantity:N0}; this recommendation only tops up the missing {quantity:N0} unit(s) needed for the modeled single active listing.");

        output.Add(new BuyOpportunity(
            worldId,
            candidate.Entry.Item,
            false,
            BuyOpportunityKind.VendorToMarket,
            StrategyLabel(BuyOpportunityKind.VendorToMarket),
            Stars(score),
            score,
            rating.Confidence,
            existingQuantity,
            quantity,
            cost,
            vendorPrice.Value,
            grossExit,
            netExit,
            stackSize,
            exitListings,
            potentialProfit,
            riskAdjustedProfit,
            roi,
            rating.EstimatedQueueDays,
            liquidationDays,
            CalculateMaximumBuyPrice(netExit, settings.MinimumRoi),
            rating.UnitsPerDay,
            rating.SalesSampleCount,
            rating.ListingFreshnessUtc,
            Array.Empty<BuyAcquisitionLot>(),
            notes,
            DateTimeOffset.UtcNow));
    }
'@
Replace-Exact $scanner $oldVendor $newVendor

Replace-Exact $scanner @'
    private static double? EstimateLiquidationDays(SellRating rating, int resultingPosition)
    {
        if (rating.UnitsPerDay <= 0.01)
            return null;
        return Math.Max(0, rating.EstimatedQueueDays ?? 0) + resultingPosition / rating.UnitsPerDay;
    }
'@ @'
    private static double? EstimateLiquidationDays(SellRating rating, int resultingPosition, int stackSize)
    {
        if (rating.UnitsPerDay <= 0.01)
            return null;

        // Should I? assumes one active Market Board listing per item/HQ variant. Raw units/day can
        // therefore overstate sell-through when the recommended stack is tiny. Cap throughput by
        // transactions/day × the one active listing's stack size whenever transaction evidence exists.
        var effectiveUnitsPerDay = rating.UnitsPerDay;
        if (rating.TransactionsPerDay > 0.001 && stackSize > 0)
            effectiveUnitsPerDay = Math.Min(effectiveUnitsPerDay, rating.TransactionsPerDay * stackSize);
        if (effectiveUnitsPerDay <= 0.01)
            return null;

        return Math.Max(0, rating.EstimatedQueueDays ?? 0) + resultingPosition / effectiveUnitsPerDay;
    }

    private static double OneListingCapitalRecovery(long acquisitionCost, int acquireQuantity, int stackSize, uint netExit)
    {
        if (acquisitionCost <= 0)
            return 1.0;
        var exposedUnits = Math.Max(1, Math.Min(Math.Max(1, acquireQuantity), Math.Max(1, stackSize)));
        return netExit * (double)exposedUnits / acquisitionCost;
    }

    private static double OneListingDeploymentFactor(double recovery)
        => Clamp01(Math.Sqrt(Math.Max(0, recovery) / 0.50));

    private static double OneListingScoreCap(double recovery) => recovery switch
    {
        < 0.025 => 45,
        < 0.05 => 55,
        < 0.10 => 65,
        < 0.20 => 75,
        < 0.35 => 85,
        _ => 100,
    };

    private static string OneListingModelNote(long acquisitionCost, int acquireQuantity, int stackSize, uint netExit, int exitListings)
    {
        var exposedUnits = Math.Max(1, Math.Min(Math.Max(1, acquireQuantity), Math.Max(1, stackSize)));
        var oneListingNet = netExit * (double)exposedUnits;
        var recovery = acquisitionCost > 0 ? oneListingNet / acquisitionCost : 1.0;
        return $"One-listing model: one active {stackSize:N0}-unit listing exposes about {oneListingNet:N0}g net and recovers {recovery:P1} of the {acquisitionCost:N0}g new capital per completed listing; the resulting position needs about {Math.Max(1, exitListings):N0} sequential listing cycle(s).";
    }
'@

Replace-Exact $scanner @'
    private static double ScoreBuyOpportunity(
        double roi,
        double profit,
        double liquidationDays,
        double maxHoldingDays,
        double priceAdvantage,
        double demand,
        double stability,
        double confidence,
        int acquisitionListingCount,
        int exitListingCount,
        double minimumProfit)
    {
        var roiScore = Clamp01(Math.Log10(1 + Math.Max(0, roi) * 20) / Math.Log10(21));
        var profitScore = ScoreProfit(profit, minimumProfit);
        var liquidity = Math.Exp(-Math.Max(0, liquidationDays) / Math.Max(1, maxHoldingDays));
        var executionCount = Math.Max(1, acquisitionListingCount + exitListingCount);
        var friction = Clamp01(Math.Log10(executionCount) / Math.Log10(50));

        var weighted =
            0.22 * roiScore +
            0.20 * profitScore +
            0.18 * liquidity +
            0.12 * Clamp01(priceAdvantage * 2.5) +
            0.10 * Clamp01(demand) +
            0.07 * Clamp01(stability) +
            0.06 * Clamp01(confidence) +
            0.05 * (1 - friction);
        return 100 * Clamp01(weighted);
    }
'@ @'
    private static double ScoreBuyOpportunity(
        double roi,
        double profit,
        double liquidationDays,
        double maxHoldingDays,
        double priceAdvantage,
        double demand,
        double stability,
        double confidence,
        int acquisitionListingCount,
        int exitListingCount,
        double minimumProfit,
        double oneListingCapitalRecovery)
    {
        var roiScore = Clamp01(Math.Log10(1 + Math.Max(0, roi) * 20) / Math.Log10(21));
        var profitScore = ScoreProfit(profit, minimumProfit);
        var liquidity = Math.Exp(-Math.Max(0, liquidationDays) / Math.Max(1, maxHoldingDays));
        var capitalRecovery = Clamp01(Math.Max(0, oneListingCapitalRecovery) / 0.50);
        var executionCount = Math.Max(1, acquisitionListingCount + exitListingCount);
        var friction = Clamp01(Math.Log10(executionCount) / Math.Log10(100));

        // Total eventual profit is still useful, but the score now asks whether the player's one
        // active listing can actually recycle meaningful capital. This prevents a huge low-value
        // stockpile from becoming 80+ merely because its theoretical full-liquidation profit is large.
        var weighted =
            0.18 * roiScore +
            0.12 * profitScore +
            0.16 * liquidity +
            0.20 * capitalRecovery +
            0.10 * Clamp01(priceAdvantage * 2.5) +
            0.09 * Clamp01(demand) +
            0.06 * Clamp01(stability) +
            0.05 * Clamp01(confidence) +
            0.04 * (1 - friction);
        var raw = 100 * Clamp01(weighted);
        return Math.Min(raw, OneListingScoreCap(oneListingCapitalRecovery));
    }
'@

Replace-Exact $plugin @'
        BuyScanner = new BuyOpportunityScanner(Configuration, PlayerState, Catalog, Inventory, Scores, Log);
'@ @'
        BuyScanner = new BuyOpportunityScanner(Configuration, PlayerState, Catalog, Inventory, Store, Scores, Log);
'@

Replace-Exact $buyUi @'
    private void DrawBuyModule()
    {
        var currentWorldId = CurrentBuyWorldId;
'@ @'
    private void DrawBuyModule()
    {
        RefreshBuyLiveRatings();
        var currentWorldId = CurrentBuyWorldId;
'@
Replace-Exact $buyUi @'
            MetricCell(2, "Potential profit", Gil(opportunity.PotentialProfit), "Modeled profit on only the new acquisition if the recommended exit succeeds. Existing owned stock is not counted as trade profit.");
            MetricCell(3, "Risk-adjusted profit", Gil(opportunity.RiskAdjustedProfit), "Potential profit discounted for evidence quality, expected liquidation speed and market stability. This is not a guarantee or literal probability-weighted EV.");
'@ @'
            MetricCell(2, "Potential profit", Gil(opportunity.PotentialProfit), "Theoretical full-liquidation profit on only the new acquisition. This remains useful context, but v1.1.5 no longer lets a huge eventual total dominate the rating when only a tiny listing can be active at once.");
            MetricCell(3, "Risk-adjusted profit", Gil(opportunity.RiskAdjustedProfit), "Potential profit discounted for evidence quality, sequential one-listing liquidation, market stability and how much new capital one active listing can actually recycle. This is not a guarantee or literal probability-weighted EV.");
'@
Replace-Exact $buyUi @'
            MetricCell(0, "First sale", Days(opportunity.EstimatedFirstSaleDays), "Estimated wait before the first modeled sale, including queue position where the exit model can estimate it.");
            MetricCell(1, "Full liquidation", Days(opportunity.EstimatedLiquidationDays), "Estimated time to sell the full resulting position, not just the newly purchased units.");
'@ @'
            MetricCell(0, "First sale", Days(opportunity.EstimatedFirstSaleDays), "Estimated wait before the first modeled sale, including queue position where the exit model can estimate it.");
            MetricCell(1, "Full liquidation", Days(opportunity.EstimatedLiquidationDays), "Estimated time to sell the full resulting position under the one-active-listing assumption. Throughput is capped by transaction frequency × recommended stack size when transaction evidence exists.");
'@
Replace-Exact $buyUi @'
            MetricCell(2, "Recommended stack", opportunity.SuggestedExitStackSize.ToString("N0"), "Recommended units per listing based on historical buyer quantities, convenience effects and the resulting position.");
            MetricCell(3, "Capital efficiency", opportunity.EstimatedLiquidationDays is > 0 ? $"{opportunity.RiskAdjustedProfit / Math.Max(0.25, opportunity.EstimatedLiquidationDays.Value):N0}g risk-adj./day" : "immediate", "Risk-adjusted profit divided by modeled liquidation time. Useful for comparing how quickly different trades recycle capital.");
            ImGui.EndTable();
'@ @'
            MetricCell(2, "Recommended stack", opportunity.SuggestedExitStackSize.ToString("N0"), "Recommended units per listing based on historical buyer quantities, convenience effects and the resulting position.");
            MetricCell(3, "Capital efficiency", opportunity.EstimatedLiquidationDays is > 0 ? $"{opportunity.RiskAdjustedProfit / Math.Max(0.25, opportunity.EstimatedLiquidationDays.Value):N0}g risk-adj./day" : "immediate", "Execution-adjusted profit divided by modeled liquidation time. Useful for comparing how quickly different trades recycle capital.");

            ImGui.TableNextRow();
            MetricCell(0, "One-listing net", Gil(OneListingNetRevenue(opportunity)), "Approximate net revenue exposed by one active listing at the recommended stack size. This is the practical sell power available before that listing has to sell and be relisted.");
            MetricCell(1, "Capital recovery/listing", Percent(OneListingCapitalRecovery(opportunity)), "One active listing's modeled net revenue divided by the full new acquisition cost. Very low recovery means lots of capital is parked behind a tiny sell surface and now caps the rating.");
            MetricCell(2, "Sequential cycles", SequentialListingCycles(opportunity).ToString("N0"), "Approximate number of one-at-a-time listing cycles required to move the full resulting position at the recommended stack size.");
            MetricCell(3, "Active listings/item", "1", "Should I Buy? v1.1.5 models your stated behavior: only one Market Board listing per item/HQ variant is active at a time.");
            ImGui.EndTable();
'@
Replace-Exact $buyUi @'
        ImGui.TextWrapped("For normal market exits, the 0–100 Buy score weights risk-adjusted trade quality across ROI (22%), absolute profit (20%), liquidity/holding time (18%), acquisition price advantage (12%), demand evidence (10%), stability (7%), confidence (6%) and execution friction (5%). The star rating is then derived from that stricter score. Guaranteed Market → Vendor opportunities use a special guaranteed-exit score instead.");
'@ @'
        ImGui.TextWrapped("For normal market exits, the 0–100 Buy score now models one active listing per item: ROI (18%), eventual profit (12%), sequential liquidity (16%), one-listing capital recovery (20%), acquisition price advantage (10%), demand (9%), stability (6%), confidence (5%) and execution friction (4%). Very weak capital recovery from one listing also imposes a score ceiling, so a 50,000g stockpile that can only expose ~1,000g per listing cannot remain an 80+ trade just because eventual total profit is large. Guaranteed Market → Vendor opportunities keep their special immediate-exit score.");
'@
Replace-Exact $buyUi @'
        ImGui.BulletText($"Execution input: {Math.Max(1, opportunity.AcquisitionLots.Count):N0} acquisition action(s) and about {Math.Max(1, opportunity.SuggestedExitListingCount):N0} exit listing(s).");
'@ @'
        ImGui.BulletText($"Execution input: {Math.Max(1, opportunity.AcquisitionLots.Count):N0} acquisition action(s), {SequentialListingCycles(opportunity):N0} sequential exit cycle(s), and {Percent(OneListingCapitalRecovery(opportunity))} of new capital recoverable per active listing.");
'@
Replace-Exact $buyUi @'
            ImGui.TextWrapped($"Source the recommended {opportunity.AcquireQuantity:N0} unit(s) from a verified normal gil NPC vendor at about {opportunity.AverageAcquisitionUnitCost:N0}g/unit. The scanner demand-caps vendor quantity instead of assuming you should buy a full stack.");
'@ @'
            ImGui.TextWrapped($"Source the recommended {opportunity.AcquireQuantity:N0} unit(s) from a verified normal gil NPC vendor at about {opportunity.AverageAcquisitionUnitCost:N0}g/unit. Vendor supply is replenishable, so the scanner now tops up only one recommended active listing and suppresses the suggestion while you already have that item listed.");
'@
Replace-Exact $buyUi @'
        SortableHeader(0, "Rating", BuySortColumn.Rating, "Overall 0–100 opportunity score plus 1–5 star band. Score combines ROI, profit, liquidity, price advantage, demand, stability, confidence and execution friction.");
'@ @'
        SortableHeader(0, "Rating", BuySortColumn.Rating, "Overall 0–100 opportunity score plus 1–5 star band. Normal market scores now explicitly model one active listing, including one-listing capital recovery and sequential listing cycles in addition to ROI, profit, liquidity, price advantage, demand, stability and confidence.");
'@
Replace-Exact $buyUi @'
        SortableHeader(8, "Risk adj.", BuySortColumn.RiskAdjustedProfit, "Potential profit discounted for confidence, liquidation speed and stability. Useful for comparing capital allocation choices.");
'@ @'
        SortableHeader(8, "Risk adj.", BuySortColumn.RiskAdjustedProfit, "Potential profit discounted for confidence, sequential one-listing liquidation, stability and how much acquisition capital one active listing can recycle. Useful for comparing capital allocation choices.");
'@
Replace-Exact $buyUi @'
        SortableHeader(11, "Liquidate", BuySortColumn.Liquidation, "Estimated time to sell the full modeled position, not merely the first unit.");
'@ @'
        SortableHeader(11, "Liquidate", BuySortColumn.Liquidation, "Estimated time to sell the full modeled position with only one active listing for this item at a time, not merely the first unit.");
'@

Replace-Exact $project '<Version>1.1.4.0</Version>' '<Version>1.1.5.0</Version>'

Write-Host 'v1.1.5 Buy capital/live model patches applied successfully.'
