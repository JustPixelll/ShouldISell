using ShouldISell.Services;

namespace ShouldISell.Windows;

public sealed partial class SuiteWindow
{
    private readonly Dictionary<BuyModelCacheKey, BuyOpportunity?> buyModelCache = new();

    private readonly record struct BuyModelCacheKey(
        uint WorldId,
        uint ItemId,
        bool IsHq,
        BuyOpportunityKind Kind,
        long RawAnalysedAt,
        long LiveObservedAt,
        int CurrentOwned,
        bool HasOwnListing,
        int MinimumProfit,
        int MinimumRoiTenths,
        int MaximumHoldingTenths);

    private IReadOnlyList<BuyOpportunity> GetModelAdjustedBuyOpportunities(uint worldId)
    {
        var raw = plugin.BuyScanner.GetOpportunities()
            .Where(x => x.WorldId == worldId)
            .ToList();
        if (raw.Count == 0)
            return raw;

        var ownedByVariant = plugin.Inventory.GetKnownOwnedStacks()
            .GroupBy(x => (x.ItemId, x.IsHq))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        var listedVariants = Plugin.PlayerState.ContentId == 0
            ? new HashSet<(uint ItemId, bool IsHq)>()
            : plugin.Store.GetOwnListings(Plugin.PlayerState.ContentId)
                .Select(x => (x.ItemId, x.IsHq))
                .ToHashSet();

        var adjusted = new List<BuyOpportunity>(raw.Count);
        foreach (var opportunity in raw)
        {
            ownedByVariant.TryGetValue((opportunity.Item.ItemId, opportunity.IsHq), out var currentOwned);
            var hasOwnListing = listedVariants.Contains((opportunity.Item.ItemId, opportunity.IsHq));
            var modelled = AdjustBuyOpportunity(opportunity, currentOwned, hasOwnListing);
            if (modelled is not null)
                adjusted.Add(modelled);
        }

        if (buyModelCache.Count > 8_000)
            buyModelCache.Clear();

        return adjusted;
    }

    private BuyOpportunity? AdjustBuyOpportunity(
        BuyOpportunity raw,
        int currentOwned,
        bool hasOwnListing)
    {
        // If the full recommended package is now in our inventory/retainer snapshots, the user has
        // already acted on it. Leaving the same recommendation visible is noise, not useful advice.
        if (raw.AcquireQuantity > 0 && currentOwned >= raw.ExistingQuantity + raw.AcquireQuantity)
            return null;

        var live = plugin.Store.GetMarket(raw.WorldId, raw.Item.ItemId);
        var liveAt = live?.CurrentSource == MarketDataSource.LiveGame &&
                     live.ListingObservedAtUtc is { } observed &&
                     observed > raw.AnalysedAtUtc
            ? observed
            : (DateTimeOffset?)null;

        var key = new BuyModelCacheKey(
            raw.WorldId,
            raw.Item.ItemId,
            raw.IsHq,
            raw.Kind,
            raw.AnalysedAtUtc.ToUnixTimeMilliseconds(),
            liveAt?.ToUnixTimeMilliseconds() ?? 0,
            currentOwned,
            hasOwnListing,
            plugin.Configuration.BuyMinimumProfitGil,
            (int)Math.Round(plugin.Configuration.BuyMinimumRoiPercent * 10),
            (int)Math.Round(plugin.Configuration.BuyMaximumHoldingDays * 10));
        if (buyModelCache.TryGetValue(key, out var cached))
            return cached;

        BuyOpportunity? working = raw;
        if (working.Kind == BuyOpportunityKind.VendorToMarket)
            working = AdjustVendorToMarketPosition(working, currentOwned, hasOwnListing);

        if (working is not null && liveAt is { } fresh && live is not null)
            working = ReRateFromNativeSnapshot(working, live, fresh);

        if (working is not null)
            working = ApplyOneListingExecutionOverlay(working);

        buyModelCache[key] = working;
        return working;
    }

    private BuyOpportunity? AdjustVendorToMarketPosition(
        BuyOpportunity opportunity,
        int currentOwned,
        bool hasOwnListing)
    {
        // NPC vendor supply can be replenished whenever the listing sells. There is no reason to
        // recommend warehousing several days of stock or to keep recommending the same item while
        // the player already has an active listing.
        if (hasOwnListing)
            return null;

        var targetStack = Math.Min(MarketBoardRules.MaxListingQuantity, Math.Max(1, opportunity.SuggestedExitStackSize));
        if (currentOwned >= targetStack)
            return null;

        var needed = Math.Min(opportunity.AcquireQuantity, targetStack - currentOwned);
        if (needed <= 0)
            return null;

        var acquisitionCost = (long)Math.Round(opportunity.AverageAcquisitionUnitCost * needed);
        var netExit = opportunity.NetExitUnitPrice ?? 0;
        var potentialProfit = netExit * (double)needed - acquisitionCost;
        var roi = acquisitionCost > 0 ? potentialProfit / acquisitionCost : 0;
        var minimumRoi = plugin.Configuration.BuyMinimumRoiPercent / 100.0;
        if (potentialProfit < plugin.Configuration.BuyMinimumProfitGil || roi < minimumRoi)
            return null;

        var ratio = opportunity.PotentialProfit > 0
            ? Math.Clamp(potentialProfit / opportunity.PotentialProfit, 0.0, 1.0)
            : 0.0;
        var resultingPosition = currentOwned + needed;
        var cycles = DivideRoundUpBuy(resultingPosition, targetStack);
        var notes = opportunity.Notes.ToList();
        notes.Add($"v1.1.5 Vendor → Market rule: vendor supply is replenishable, so the recommendation only tops up one {targetStack:N0}-unit working listing instead of stockpiling several days of demand.");
        if (currentOwned > 0)
            notes.Add($"You already own {currentOwned:N0}; only the missing {needed:N0} unit(s) are still suggested. Once this item is actively listed, the Vendor → Market recommendation is hidden until the listing is gone.");

        return opportunity with
        {
            ExistingQuantity = currentOwned,
            AcquireQuantity = needed,
            AcquisitionCost = acquisitionCost,
            PotentialProfit = potentialProfit,
            RiskAdjustedProfit = Math.Max(0, opportunity.RiskAdjustedProfit * ratio),
            Roi = roi,
            SuggestedExitStackSize = targetStack,
            SuggestedExitListingCount = cycles,
            Notes = notes,
        };
    }

    private BuyOpportunity ReRateFromNativeSnapshot(
        BuyOpportunity opportunity,
        MarketSnapshot live,
        DateTimeOffset liveAt)
    {
        var variantListings = live.Listings
            .Where(x => x.IsHq == opportunity.IsHq && x.PricePerUnit > 0 && x.Quantity > 0)
            .OrderBy(x => x.PricePerUnit)
            .ThenBy(x => x.Quantity)
            .ToList();

        if (opportunity.AcquisitionLots.Count > 0)
        {
            var exact = opportunity.AcquisitionLots.All(lot =>
                lot.ListingId != 0 && variantListings.Any(x =>
                    x.ListingId == lot.ListingId &&
                    x.PricePerUnit == lot.UnitPrice &&
                    x.Quantity == lot.Quantity));
            if (!exact)
                return MarkNativePackageChanged(opportunity, liveAt);
        }

        if (opportunity.Kind == BuyOpportunityKind.MarketToVendor)
        {
            var verificationNotes = opportunity.Notes.ToList();
            verificationNotes.Add($"Fresh FFXIV snapshot at {liveAt.ToLocalTime():HH:mm:ss} confirmed the acquisition listing(s) for this guaranteed vendor exit.");
            return opportunity with
            {
                MarketFreshnessUtc = liveAt,
                Notes = verificationNotes,
                AnalysedAtUtc = liveAt,
            };
        }

        var acquiredIds = opportunity.AcquisitionLots
            .Where(x => x.ListingId != 0)
            .Select(x => x.ListingId)
            .ToHashSet();
        var counterfactualListings = variantListings
            .Where(x => !acquiredIds.Contains(x.ListingId))
            .ToList();
        var market = new MarketSnapshot
        {
            WorldId = opportunity.WorldId,
            ItemId = opportunity.Item.ItemId,
            ListingObservedAtUtc = live.ListingObservedAtUtc,
            HistoryObservedAtUtc = live.HistoryObservedAtUtc,
            UniversalisLastUploadUtc = live.UniversalisLastUploadUtc,
            CurrentSource = MarketDataSource.LiveGame,
            Listings = counterfactualListings,
            Sales = live.Sales,
        };

        var resultingPosition = Math.Max(1, opportunity.ExistingQuantity + opportunity.AcquireQuantity);
        var rating = plugin.Scores.Calculate(
            opportunity.Item,
            opportunity.IsHq,
            market,
            plugin.Configuration.ValueThresholdGil,
            resultingPosition);
        if (rating?.NetSuggestedPriceAfterTax is not { } netExit || rating.SuggestedPrice is not { } grossExit)
            return MarkNativePackageChanged(opportunity, liveAt,
                "The fresh native board/history snapshot no longer supports a usable Market Board exit for this package.");

        var potentialProfit = netExit * (double)opportunity.AcquireQuantity - opportunity.AcquisitionCost;
        var roi = opportunity.AcquisitionCost > 0 ? potentialProfit / opportunity.AcquisitionCost : 0;
        var stackSize = Math.Min(
            MarketBoardRules.MaxListingQuantity,
            Math.Max(1, rating.StackRecommendation?.RecommendedStackSize ?? opportunity.SuggestedExitStackSize));
        var exitCycles = DivideRoundUpBuy(resultingPosition, stackSize);
        var liquidationDays = EstimateNativeSequentialLiquidation(rating, resultingPosition, stackSize);
        var maxHolding = Math.Max(0.25, plugin.Configuration.BuyMaximumHoldingDays);
        var liquidityFit = liquidationDays is { } days ? Math.Exp(-days / maxHolding) : 0.0;
        var riskFactor = Clamp01Buy(
            rating.Confidence *
            (0.55 + 0.45 * liquidityFit) *
            (0.75 + 0.25 * rating.Breakdown.Stability));
        var riskAdjustedProfit = Math.Max(0, potentialProfit) * riskFactor;
        var baseScore = ScoreNativeBaseOpportunity(
            roi,
            potentialProfit,
            liquidationDays ?? maxHolding * 4,
            maxHolding,
            PriceAdvantageBuy(opportunity.AcquisitionCost, opportunity.AcquireQuantity, netExit),
            rating.Breakdown.Demand,
            rating.Breakdown.Stability,
            rating.Confidence,
            Math.Max(1, opportunity.AcquisitionLots.Count),
            exitCycles,
            plugin.Configuration.BuyMinimumProfitGil);

        var minimumRoi = plugin.Configuration.BuyMinimumRoiPercent / 100.0;
        var violatesMinimum = potentialProfit < plugin.Configuration.BuyMinimumProfitGil ||
                              roi < minimumRoi ||
                              liquidationDays is null ||
                              liquidationDays > maxHolding;
        if (violatesMinimum)
            baseScore = Math.Min(baseScore, 34.0);

        var notes = opportunity.Notes.ToList();
        notes.Add($"Fresh FFXIV snapshot re-rated this opportunity from the fresh {liveAt.ToLocalTime():HH:mm:ss} board/history snapshot. Exit price, confidence, liquidity, profit and the displayed rating now react to that native data.");
        if (violatesMinimum)
            notes.Add("The native re-score no longer satisfies at least one configured minimum (profit, ROI or holding time), so its base score is capped below the normal recommendation range.");

        return opportunity with
        {
            Stars = BuyStars(baseScore),
            OpportunityScore = baseScore,
            Confidence = rating.Confidence,
            SuggestedExitUnitPrice = grossExit,
            NetExitUnitPrice = netExit,
            SuggestedExitStackSize = stackSize,
            SuggestedExitListingCount = exitCycles,
            PotentialProfit = potentialProfit,
            RiskAdjustedProfit = riskAdjustedProfit,
            Roi = roi,
            EstimatedFirstSaleDays = rating.EstimatedQueueDays,
            EstimatedLiquidationDays = liquidationDays,
            MaximumRecommendedBuyPrice = CalculateMaximumBuyPriceBuy(netExit, minimumRoi),
            UnitsPerDay = rating.UnitsPerDay,
            SalesSampleCount = rating.SalesSampleCount,
            MarketFreshnessUtc = liveAt,
            Notes = notes,
            AnalysedAtUtc = liveAt,
        };
    }

    private static BuyOpportunity MarkNativePackageChanged(
        BuyOpportunity opportunity,
        DateTimeOffset liveAt,
        string? extra = null)
    {
        var score = Math.Min(opportunity.OpportunityScore, 20.0);
        var notes = opportunity.Notes.ToList();
        notes.Add("Fresh FFXIV snapshot changed the recommendation: at least one required acquisition listing is no longer present at the scanned listing ID, price and quantity.");
        if (!string.IsNullOrWhiteSpace(extra))
            notes.Add(extra);
        return opportunity with
        {
            Stars = BuyStars(score),
            OpportunityScore = score,
            Confidence = Math.Min(opportunity.Confidence, 0.25),
            RiskAdjustedProfit = 0,
            MarketFreshnessUtc = liveAt,
            Notes = notes,
            AnalysedAtUtc = liveAt,
        };
    }

    private BuyOpportunity ApplyOneListingExecutionOverlay(BuyOpportunity opportunity)
    {
        if (opportunity.Kind == BuyOpportunityKind.MarketToVendor)
            return opportunity;

        var listingStack = Math.Min(MarketBoardRules.MaxListingQuantity, Math.Max(1, opportunity.SuggestedExitStackSize));
        var recovery = OneListingCapitalRecovery(opportunity);
        var cycles = SequentialListingCycles(opportunity);
        var recoveryCap = recovery switch
        {
            < 0.025 => 45.0,
            < 0.05 => 55.0,
            < 0.10 => 65.0,
            < 0.20 => 75.0,
            < 0.35 => 85.0,
            _ => 100.0,
        };
        var cycleCap = cycles switch
        {
            >= 100 => 45.0,
            >= 50 => 55.0,
            >= 25 => 65.0,
            >= 10 => 75.0,
            >= 5 => 85.0,
            _ => 100.0,
        };
        var score = Math.Min(opportunity.OpportunityScore, Math.Min(recoveryCap, cycleCap));
        var deploymentFactor = Clamp01Buy(Math.Sqrt(Math.Max(0, recovery) / 0.50));
        var cycleFactor = Clamp01Buy(Math.Sqrt(5.0 / Math.Max(5, cycles)));
        var adjustedRiskProfit = opportunity.RiskAdjustedProfit * deploymentFactor * cycleFactor;

        var notes = opportunity.Notes.ToList();
        notes.Add($"v1.1.5 one-listing execution model: one active {listingStack:N0}-unit listing exposes about {OneListingNetRevenue(opportunity):N0}g net, recovering {recovery:P1} of the {opportunity.AcquisitionCost:N0}g new capital per sale; the resulting position needs about {cycles:N0} sequential listing cycle(s). Base score {opportunity.OpportunityScore:0.0} → displayed score {score:0.0} after the one-listing capital/cycle ceilings.");

        return opportunity with
        {
            Stars = BuyStars(score),
            OpportunityScore = score,
            RiskAdjustedProfit = adjustedRiskProfit,
            SuggestedExitStackSize = listingStack,
            SuggestedExitListingCount = cycles,
            Notes = notes,
        };
    }

    private void RefreshSelectedBuyOpportunityFromModel()
    {
        if (selectedBuyOpportunity is not { } selected)
            return;
        var worldId = CurrentBuyWorldId;
        if (worldId == 0)
            return;

        var updated = GetModelAdjustedBuyOpportunities(worldId).FirstOrDefault(x =>
            x.Item.ItemId == selected.Item.ItemId &&
            x.IsHq == selected.IsHq &&
            x.Kind == selected.Kind);
        if (updated is not null)
        {
            selectedBuyOpportunity = updated;
            return;
        }

        selectedBuyOpportunity = null;
        buyDetailsOpen = false;
        buyPortfolioPlan = null;
    }

    private static int OneListingUnits(BuyOpportunity opportunity)
    {
        var stack = Math.Min(MarketBoardRules.MaxListingQuantity, Math.Max(1, opportunity.SuggestedExitStackSize));
        return Math.Max(1, Math.Min(opportunity.AcquireQuantity, stack));
    }

    private static double OneListingNetRevenue(BuyOpportunity opportunity)
        => opportunity.NetExitUnitPrice is { } net
            ? net * (double)OneListingUnits(opportunity)
            : 0;

    private static double OneListingCapitalRecovery(BuyOpportunity opportunity)
        => opportunity.AcquisitionCost > 0
            ? OneListingNetRevenue(opportunity) / opportunity.AcquisitionCost
            : 1.0;

    private static int SequentialListingCycles(BuyOpportunity opportunity)
    {
        var position = Math.Max(1, opportunity.ExistingQuantity + opportunity.AcquireQuantity);
        return MarketBoardRules.ListingCycles(position, opportunity.SuggestedExitStackSize);
    }

    private static double? EstimateNativeSequentialLiquidation(SellRating rating, int position, int stackSize)
    {
        if (rating.UnitsPerDay <= 0.01)
            return null;
        stackSize = Math.Min(MarketBoardRules.MaxListingQuantity, Math.Max(1, stackSize));
        var effectiveUnitsPerDay = rating.UnitsPerDay;
        if (rating.TransactionsPerDay > 0.001)
            effectiveUnitsPerDay = Math.Min(effectiveUnitsPerDay, rating.TransactionsPerDay * stackSize);
        if (effectiveUnitsPerDay <= 0.01)
            return null;
        return Math.Max(0, rating.EstimatedQueueDays ?? 0) + position / effectiveUnitsPerDay;
    }

    private static double ScoreNativeBaseOpportunity(
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
        var roiScore = Clamp01Buy(Math.Log10(1 + Math.Max(0, roi) * 20) / Math.Log10(21));
        var profitScore = ScoreProfitBuy(profit, minimumProfit);
        var liquidity = Math.Exp(-Math.Max(0, liquidationDays) / Math.Max(1, maxHoldingDays));
        var executionCount = Math.Max(1, acquisitionListingCount + exitListingCount);
        var friction = Clamp01Buy(Math.Log10(executionCount) / Math.Log10(50));
        var weighted =
            0.22 * roiScore +
            0.20 * profitScore +
            0.18 * liquidity +
            0.12 * Clamp01Buy(priceAdvantage * 2.5) +
            0.10 * Clamp01Buy(demand) +
            0.07 * Clamp01Buy(stability) +
            0.06 * Clamp01Buy(confidence) +
            0.05 * (1 - friction);
        return 100 * Clamp01Buy(weighted);
    }

    private static double ScoreProfitBuy(double profit, double minimumProfit)
    {
        if (profit <= 0)
            return 0;
        var reference = Math.Max(500, minimumProfit);
        return Clamp01Buy(0.5 + 0.25 * Math.Log10(profit / reference));
    }

    private static double PriceAdvantageBuy(long cost, int quantity, uint netExit)
    {
        if (cost <= 0 || quantity <= 0 || netExit == 0)
            return 0;
        var costPerUnit = cost / (double)quantity;
        return Clamp01Buy((netExit - costPerUnit) / netExit);
    }

    private static uint CalculateMaximumBuyPriceBuy(uint netExit, double minimumRoi)
    {
        if (netExit == 0)
            return 0;
        var grossAcquisitionCeiling = netExit / Math.Max(1.0, 1.0 + minimumRoi);
        var preTax = grossAcquisitionCeiling / 1.05;
        return (uint)Math.Max(1, Math.Floor(preTax));
    }

    private static int BuyStars(double score) => score switch
    {
        >= 80 => 5,
        >= 65 => 4,
        >= 50 => 3,
        >= 35 => 2,
        _ => 1,
    };

    private static int DivideRoundUpBuy(int value, int divisor)
        => divisor <= 0 ? value : (value + divisor - 1) / divisor;

    private static double Clamp01Buy(double value) => Math.Clamp(value, 0.0, 1.0);
}
