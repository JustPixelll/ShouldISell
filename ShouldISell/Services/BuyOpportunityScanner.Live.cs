namespace ShouldISell.Services;

public sealed partial class BuyOpportunityScanner
{
    /// <summary>
    /// Re-rates cached Buy opportunities from complete native FFXIV ItemSearch snapshots after a
    /// single-item LIVE VERIFY or multi-item Deep Scan has finished. This is deliberately called by
    /// the UI only while the native refresh queue is idle, so partial offering pages never become a
    /// temporary recommendation.
    /// </summary>
    public void ApplyLiveSnapshots()
    {
        if (!playerState.IsLoaded)
            return;

        inventory.ScanLoadedContainers();
        var worldId = playerState.CurrentWorld.RowId;
        var ownedByVariant = inventory.GetKnownOwnedStacks()
            .GroupBy(x => (x.ItemId, x.IsHq))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        var listedVariants = playerState.ContentId == 0
            ? new HashSet<(uint ItemId, bool IsHq)>()
            : store.GetOwnListings(playerState.ContentId)
                .Select(x => (x.ItemId, x.IsHq))
                .ToHashSet();

        lock (resultGate)
        {
            var changed = false;
            var refreshed = new List<BuyOpportunity>(opportunities.Count);

            foreach (var opportunity in opportunities)
            {
                if (opportunity.WorldId != worldId)
                {
                    refreshed.Add(opportunity);
                    continue;
                }

                ownedByVariant.TryGetValue((opportunity.Item.ItemId, opportunity.IsHq), out var currentOwned);

                // Once the player has actually filled the recommended position, it is no longer a
                // useful Buy recommendation. Vendor stock is especially noisy because it is always
                // available again later, so an active own listing suppresses Vendor -> Market too.
                if ((opportunity.AcquireQuantity > 0 &&
                     currentOwned >= opportunity.ExistingQuantity + opportunity.AcquireQuantity) ||
                    (opportunity.Kind == BuyOpportunityKind.VendorToMarket &&
                     (currentOwned > opportunity.ExistingQuantity ||
                      listedVariants.Contains((opportunity.Item.ItemId, opportunity.IsHq)))))
                {
                    changed = true;
                    continue;
                }

                var live = store.GetMarket(worldId, opportunity.Item.ItemId);
                var liveAt = live?.ListingObservedAtUtc;
                if (live is null || live.CurrentSource != MarketDataSource.LiveGame || liveAt is null ||
                    liveAt.Value <= opportunity.AnalysedAtUtc)
                {
                    refreshed.Add(opportunity);
                    continue;
                }

                var rerated = ReevaluateFromLive(opportunity, live, liveAt.Value);
                if (rerated is not null)
                    refreshed.Add(rerated);
                changed = true;
            }

            if (!changed)
                return;

            opportunities = refreshed
                .OrderByDescending(x => x.OpportunityScore)
                .ThenByDescending(x => x.RiskAdjustedProfit)
                .ThenByDescending(x => x.PotentialProfit)
                .Take(500)
                .ToList();
        }
    }

    private BuyOpportunity? ReevaluateFromLive(
        BuyOpportunity original,
        MarketSnapshot live,
        DateTimeOffset liveAt)
    {
        if (original.Kind == BuyOpportunityKind.MarketToVendor)
            return ReevaluateLiveMarketToVendor(original, live, liveAt);

        var variantListings = live.Listings
            .Where(x => x.IsHq == original.IsHq && x.PricePerUnit > 0 && x.Quantity > 0)
            .OrderBy(x => x.PricePerUnit)
            .ThenBy(x => x.Quantity)
            .ToList();

        if (original.AcquisitionLots.Count > 0)
        {
            var allStillExact = original.AcquisitionLots.All(lot =>
                lot.ListingId != 0 && variantListings.Any(x =>
                    x.ListingId == lot.ListingId &&
                    x.PricePerUnit == lot.UnitPrice &&
                    x.Quantity == lot.Quantity));
            if (!allStillExact)
                return MarkLiveChanged(original, liveAt,
                    "Native FFXIV re-score: at least one recommended acquisition listing no longer exists at the scanned price/quantity, so this package is no longer executable as rated.");
        }

        var acquiredIds = original.AcquisitionLots
            .Where(x => x.ListingId != 0)
            .Select(x => x.ListingId)
            .ToHashSet();
        var counterfactualListings = variantListings
            .Where(x => !acquiredIds.Contains(x.ListingId))
            .ToList();
        var market = new MarketSnapshot
        {
            WorldId = original.WorldId,
            ItemId = original.Item.ItemId,
            ListingObservedAtUtc = live.ListingObservedAtUtc,
            HistoryObservedAtUtc = live.HistoryObservedAtUtc,
            UniversalisLastUploadUtc = live.UniversalisLastUploadUtc,
            CurrentSource = MarketDataSource.LiveGame,
            Listings = counterfactualListings,
            Sales = live.Sales,
        };

        var resultingPosition = Math.Max(1, original.ExistingQuantity + original.AcquireQuantity);
        var rating = scores.Calculate(
            original.Item,
            original.IsHq,
            market,
            configuration.ValueThresholdGil,
            resultingPosition);
        if (rating?.NetSuggestedPriceAfterTax is not { } netExit || rating.SuggestedPrice is not { } grossExit)
            return MarkLiveChanged(original, liveAt,
                "Native FFXIV re-score could not support the previous Market Board exit from the fresh board/history snapshot.");

        var settings = SnapshotSettings();
        var potentialProfit = netExit * (double)original.AcquireQuantity - original.AcquisitionCost;
        var roi = original.AcquisitionCost > 0 ? potentialProfit / original.AcquisitionCost : 0;
        var stackSize = Math.Max(1, rating.StackRecommendation?.RecommendedStackSize ?? original.SuggestedExitStackSize);
        var exitListings = DivideRoundUp(resultingPosition, stackSize);
        var liquidationDays = EstimateLiquidationDays(rating, resultingPosition, stackSize);
        var liquidityFit = liquidationDays is { } days
            ? Math.Exp(-days / Math.Max(1.0, settings.MaximumHoldingDays))
            : 0.0;
        var riskFactor = Clamp01(
            rating.Confidence *
            (0.55 + 0.45 * liquidityFit) *
            (0.75 + 0.25 * rating.Breakdown.Stability));
        var oneListingRecovery = OneListingCapitalRecovery(
            original.AcquisitionCost,
            original.AcquireQuantity,
            stackSize,
            netExit);
        var riskAdjustedProfit = Math.Max(0, potentialProfit) * riskFactor * OneListingDeploymentFactor(oneListingRecovery);
        var score = ScoreBuyOpportunity(
            roi,
            potentialProfit,
            liquidationDays ?? settings.MaximumHoldingDays * 4,
            settings.MaximumHoldingDays,
            PriceAdvantage(original.AcquisitionCost, original.AcquireQuantity, netExit),
            rating.Breakdown.Demand,
            rating.Breakdown.Stability,
            rating.Confidence,
            Math.Max(1, original.AcquisitionLots.Count),
            exitListings,
            settings.MinimumProfitGil,
            oneListingRecovery);

        var violatesHardMinimum = potentialProfit < settings.MinimumProfitGil ||
                                  roi < settings.MinimumRoi ||
                                  liquidationDays is null ||
                                  liquidationDays > settings.MaximumHoldingDays;
        if (violatesHardMinimum)
            score = Math.Min(score, 34.0);

        var notes = original.Notes.ToList();
        notes.Add($"Native FFXIV Deep Scan re-rated this opportunity from a fresh {liveAt.ToLocalTime():HH:mm:ss} board/history snapshot; the displayed score and exit plan now use that live data.");
        if (violatesHardMinimum)
            notes.Add("The fresh native re-score no longer satisfies at least one configured minimum (profit, ROI or holding time), so its score is capped below the normal recommendation range.");
        notes.Add(OneListingModelNote(original.AcquisitionCost, original.AcquireQuantity, stackSize, netExit, exitListings));

        return original with
        {
            Stars = Stars(score),
            OpportunityScore = score,
            Confidence = rating.Confidence,
            SuggestedExitUnitPrice = grossExit,
            NetExitUnitPrice = netExit,
            SuggestedExitStackSize = stackSize,
            SuggestedExitListingCount = exitListings,
            PotentialProfit = potentialProfit,
            RiskAdjustedProfit = riskAdjustedProfit,
            Roi = roi,
            EstimatedFirstSaleDays = rating.EstimatedQueueDays,
            EstimatedLiquidationDays = liquidationDays,
            MaximumRecommendedBuyPrice = CalculateMaximumBuyPrice(netExit, settings.MinimumRoi),
            UnitsPerDay = rating.UnitsPerDay,
            SalesSampleCount = rating.SalesSampleCount,
            MarketFreshnessUtc = liveAt,
            Notes = notes,
            AnalysedAtUtc = liveAt,
        };
    }

    private BuyOpportunity ReevaluateLiveMarketToVendor(
        BuyOpportunity original,
        MarketSnapshot live,
        DateTimeOffset liveAt)
    {
        var variantListings = live.Listings
            .Where(x => x.IsHq == original.IsHq)
            .ToList();
        var allStillExact = original.AcquisitionLots.All(lot =>
            lot.ListingId != 0 && variantListings.Any(x =>
                x.ListingId == lot.ListingId &&
                x.PricePerUnit == lot.UnitPrice &&
                x.Quantity == lot.Quantity));

        if (!allStillExact)
            return MarkLiveChanged(original, liveAt,
                "Native FFXIV verification no longer finds every Market -> Vendor acquisition listing exactly, so the guaranteed-exit package itself is stale.");

        var notes = original.Notes.ToList();
        notes.Add($"Native FFXIV verification at {liveAt.ToLocalTime():HH:mm:ss} confirmed every acquisition listing in this guaranteed vendor-exit package.");
        return original with
        {
            MarketFreshnessUtc = liveAt,
            Notes = notes,
            AnalysedAtUtc = liveAt,
        };
    }

    private static BuyOpportunity MarkLiveChanged(
        BuyOpportunity original,
        DateTimeOffset liveAt,
        string reason)
    {
        var score = Math.Min(original.OpportunityScore, 20.0);
        var notes = original.Notes.ToList();
        notes.Add(reason);
        return original with
        {
            Stars = Stars(score),
            OpportunityScore = score,
            Confidence = Math.Min(original.Confidence, 0.25),
            RiskAdjustedProfit = 0,
            MarketFreshnessUtc = liveAt,
            Notes = notes,
            AnalysedAtUtc = liveAt,
        };
    }
}
