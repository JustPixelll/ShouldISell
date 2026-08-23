namespace ShouldISell.Services;

public sealed class ScoreCalculator
{
    public const double PriceWeight = 0.25;
    public const double DemandWeight = 0.17;
    public const double SupplyWeight = 0.12;
    public const double LiquidityWeight = 0.11;
    public const double StabilityWeight = 0.09;
    public const double TrendWeight = 0.05;
    public const double ValueWeight = 0.11;
    public const double VendorEconomicsWeight = 0.10;
    public const double ScoreContrast = 2.20;

    // Conservative standard seller tax. FFXIV can temporarily reduce seller tax in selected
    // market cities, but 5% is the normal rate and is the safe comparison against guaranteed
    // NPC gil. Suggested listing prices remain gross because that is the number the player enters;
    // value/vendor economics use the after-tax net.
    public const double MarketSellerTaxRate = 0.05;

    public SellRating? Calculate(
        ItemInfo item,
        bool isHq,
        MarketSnapshot? market,
        int valueThresholdGil,
        int quantityForSale = 1,
        DateTimeOffset? nowOverride = null)
    {
        if (market is null)
            return null;

        var now = nowOverride ?? DateTimeOffset.UtcNow;
        var listings = market.Listings
            .Where(x => x.IsHq == isHq && x.PricePerUnit > 0 && x.Quantity > 0)
            .OrderBy(x => x.PricePerUnit)
            .ToList();
        var sales90 = market.Sales
            .Where(x => x.IsHq == isHq && x.PricePerUnit > 0 && x.Quantity > 0 && x.SoldAtUtc >= now.AddDays(-90))
            .OrderBy(x => x.SoldAtUtc)
            .ToList();
        var sales30 = sales90
            .Where(x => x.SoldAtUtc >= now.AddDays(-30))
            .ToList();

        if (listings.Count == 0 && sales90.Count == 0)
            return null;

        var notes = new List<string>();
        var realisticCurrentPrice = EstimateRealisticCurrentPrice(listings, sales30, notes);
        var weightedMedian30 = WeightedMedian(sales30, now, 12.0);
        var weightedMedian90 = WeightedMedian(sales90, now, 28.0);
        var weightedMedian = weightedMedian30 ?? weightedMedian90;
        var q1 = Quantile(sales30.Select(x => (double)x.PricePerUnit).ToList(), 0.25)
                 ?? Quantile(sales90.Select(x => (double)x.PricePerUnit).ToList(), 0.25);
        var q3 = Quantile(sales30.Select(x => (double)x.PricePerUnit).ToList(), 0.75)
                 ?? Quantile(sales90.Select(x => (double)x.PricePerUnit).ToList(), 0.75);
        var med7 = Median(sales30.Where(x => x.SoldAtUtc >= now.AddDays(-7)).Select(x => (double)x.PricePerUnit).ToList());
        var med30 = Median(sales30.Select(x => (double)x.PricePerUnit).ToList());
        if (weightedMedian30 is null && weightedMedian90 is not null)
            notes.Add("No sales in the recent 30-day price window; the executable-price anchor falls back to older 90-day sales with low confidence.");

        var units = sales30.Sum(x => (double)x.Quantity);
        var transactions = sales30.Count;
        var coverageDays = sales30.Count == 0 ? 30.0 : Math.Clamp((now - sales30.Min(x => x.SoldAtUtc)).TotalDays, 1.0, 30.0);
        var unitsPerDay = units / coverageDays;
        var transactionsPerDay = transactions / coverageDays;
        var currentUnits = listings.Sum(x => (double)x.Quantity);
        var daysSupply = unitsPerDay > 0.01 ? currentUnits / unitsPerDay : (double?)null;

        var recommendation = SuggestPriceAndStack(
            listings,
            sales30,
            sales90,
            weightedMedian,
            q1,
            q3,
            med7,
            med30,
            unitsPerDay,
            daysSupply,
            Math.Max(1, quantityForSale),
            item.StackSize,
            now,
            notes);
        var suggestion = recommendation.Price;
        var stackRecommendation = recommendation.Stack;

        double? queueDays = null;
        var queuePrice = suggestion.Price ?? realisticCurrentPrice;
        if (queuePrice is { } p && unitsPerDay > 0.01)
        {
            var unitsAhead = listings.Where(x => x.PricePerUnit <= p).Sum(x => (double)x.Quantity);
            queueDays = unitsAhead / unitsPerDay;
        }

        // The rating measures an executable opportunity, not a fantasy ask. Current board asks
        // remain visible, but price/value/vendor economics use the recommendation the engine believes
        // is realistically listable and the gil the seller actually keeps after market tax.
        var executablePrice = suggestion.Price ?? realisticCurrentPrice;
        var netExecutablePrice = NetAfterSellerTax(executablePrice);
        var netHistoricalMedian = NetAfterSellerTax(weightedMedian);
        var priceScore = ScorePrice(netExecutablePrice, netHistoricalMedian);
        var demandScore = Clamp01(Math.Log10(1 + unitsPerDay) / Math.Log10(1 + 30));
        var supplyScore = daysSupply is null ? 0.10 : Math.Exp(-daysSupply.Value / 12.0);
        var liquidityScore = queueDays is null ? 0.10 : Math.Exp(-queueDays.Value / 7.0);
        var stabilityScore = ScoreStability(q1, q3, weightedMedian);
        var trendScore = ScoreTrend(med7, med30);

        // v0.8 semantics: the user's gil input is a reference for one RECOMMENDED LISTING,
        // not the entire stockpile and not a per-unit cutoff. This makes the overview answer
        // "how worthwhile is the next listing I would actually create?" A 105-unit position that
        // is best sold one-at-a-time therefore gets value credit for ~one unit per listing rather
        // than pretending all 105 units are realized in a single sale.
        var valueUnitNet = (double?)netExecutablePrice ?? netHistoricalMedian;
        var recommendedListingQuantity = RecommendedListingQuantity(
            stackRecommendation,
            Math.Max(1, quantityForSale),
            item.StackSize);
        var expectedNetRecommendedListing = valueUnitNet is null
            ? (double?)null
            : valueUnitNet.Value * recommendedListingQuantity;
        var valueScore = ScoreAbsoluteValue(expectedNetRecommendedListing, valueThresholdGil);
        var vendor = ScoreVendorEconomics(item, isHq, netExecutablePrice, sales30.Count, demandScore);

        var weighted =
            PriceWeight * priceScore +
            DemandWeight * demandScore +
            SupplyWeight * supplyScore +
            LiquidityWeight * liquidityScore +
            StabilityWeight * stabilityScore +
            TrendWeight * trendScore +
            ValueWeight * valueScore +
            VendorEconomicsWeight * vendor.Score;

        // Stars intentionally use a contrast-expanded score so 5★ can mean "excellent" without
        // requiring every component to be perfect. The numeric 0–100 score below deliberately does
        // NOT use this contrast expansion; 100 therefore remains a genuinely near-perfect fit.
        var calibrated = Clamp01(0.5 + (weighted - 0.5) * ScoreContrast);
        var opportunitySignal = weighted;

        // A recommendation that requires a very large number of separate listings can be an
        // excellent market but still a weaker practical selling opportunity. Apply only a mild,
        // logarithmic execution-friction penalty; the stack optimizer has already tried to balance
        // buyer preference against fragmentation, so this is a final usability adjustment rather
        // than a second stack-size model.
        if (stackRecommendation is { RecommendedListingCount: > 12 } executionPlan)
        {
            var burden = Clamp01(
                Math.Log(executionPlan.RecommendedListingCount / 12.0) /
                Math.Log(120.0 / 12.0));
            opportunitySignal *= 1.0 - 0.08 * burden;
            calibrated *= 1.0 - 0.05 * burden;
            notes.Add(
                $"The recommended stack implies about {executionPlan.RecommendedListingCount:N0} separate listings; " +
                "manual fragmentation slightly reduces the practical opportunity score.");
        }

        // Vendor buyback is a guaranteed, instant buyer with no retainer slot or wait. If the
        // after-tax board recommendation cannot beat that floor, both stars and numeric score must
        // reflect that the market-board route is categorically poor.
        if (vendor.HardFloorViolation)
        {
            calibrated = Math.Min(calibrated, 0.10);
            opportunitySignal = Math.Min(opportunitySignal, 0.10);
        }
        else if (vendor.FloorMargin is { } floorMargin && floorMargin < 0.10)
        {
            calibrated *= 0.45;
            opportunitySignal *= 0.45;
        }
        else if (vendor.FloorMargin is { } floorMargin2 && floorMargin2 < 0.25)
        {
            calibrated *= 0.75;
            opportunitySignal *= 0.75;
        }

        // A repeatedly traded NQ item whose after-tax market price materially exceeds a normal
        // gil-vendor purchase price has real convenience-arbitrage value. Stars get the stronger
        // usability boost; the numeric score gets a smaller bonus so it still rarely reaches 100.
        if (vendor.ArbitrageMargin is > 0 && sales30.Count > 0 && unitsPerDay > 0.01)
        {
            var marginSignal = Clamp01(Math.Log(1.0 + vendor.ArbitrageMargin.Value) / Math.Log(3.0));
            var evidence = Clamp01(0.20 + 0.45 * Clamp01(sales30.Count / 10.0) + 0.35 * demandScore);
            calibrated = Clamp01(calibrated + 0.12 * marginSignal * evidence);
            opportunitySignal = Clamp01(opportunitySignal + 0.05 * marginSignal * evidence);
        }

        var opportunityScore = Clamp01(opportunitySignal) * 100.0;
        var raw = 1.0 + 4.0 * calibrated;
        var stars = Math.Clamp((int)Math.Round(raw, MidpointRounding.AwayFromZero), 1, 5);
        var confidence = ScoreConfidence(sales30, market, now);
        if (!string.IsNullOrWhiteSpace(vendor.Reason))
            notes.Add(vendor.Reason);

        if (sales30.Count < 5)
            notes.Add($"Only {sales30.Count} sales in the 30-day sample.");
        if (market.ListingObservedAtUtc is { } listingAt && now - listingAt > TimeSpan.FromHours(24))
            notes.Add("Current listings are more than 24 hours old.");
        if (sales30.Count > 0 && now - sales30.Max(x => x.SoldAtUtc) > TimeSpan.FromDays(14))
            notes.Add("The last actual sale is more than 14 days old; this is low demand, not merely stale listing data.");

        return new SellRating(
            item.ItemId,
            isHq,
            raw,
            opportunityScore,
            stars,
            Label(stars),
            confidence,
            ConfidenceLabel(confidence),
            realisticCurrentPrice,
            suggestion.Price,
            suggestion.Reason,
            suggestion.Confidence,
            netExecutablePrice,
            item.VendorBuybackPrice,
            isHq ? null : item.VendorGilShopPrice,
            vendor.FloorMargin,
            vendor.ArbitrageMargin,
            vendor.Reason,
            stackRecommendation,
            weightedMedian,
            q1,
            q3,
            unitsPerDay,
            transactionsPerDay,
            daysSupply,
            queueDays,
            med7,
            med30,
            market.ListingObservedAtUtc,
            sales30.Count == 0 ? null : sales30.Max(x => x.SoldAtUtc),
            sales30.Count,
            new ScoreBreakdown(priceScore, demandScore, supplyScore, liquidityScore, stabilityScore, trendScore, valueScore, vendor.Score),
            notes);
    }

    private sealed record PriceSuggestion(uint? Price, string Reason, double Confidence);
    private sealed record StackPlanResult(PriceSuggestion Price, StackRecommendation? Stack);
    private sealed record NormalizedSale(int Quantity, double PremiumRatio, double TotalSpend, double Weight);
    private sealed record StackCandidateWork(
        int StackSize,
        uint? Price,
        int ListingCount,
        double RawAffinity,
        double PremiumRatio,
        double PremiumConfidence,
        double Affordability,
        double SpeedFit,
        double FragmentationPenalty);

    private sealed record VendorEconomicsResult(
        double Score,
        bool HardFloorViolation,
        double? FloorMargin,
        double? ArbitrageMargin,
        string Reason);

    private static StackPlanResult SuggestPriceAndStack(
        List<MarketListing> listings,
        List<MarketSale> priceSales,
        List<MarketSale> stackSales,
        double? weightedMedian,
        double? q1,
        double? q3,
        double? med7,
        double? med30,
        double unitsPerDay,
        double? daysSupply,
        int quantityForSale,
        uint itemStackSize,
        DateTimeOffset now,
        List<string> notes)
    {
        var baseSuggestion = SuggestPrice(
            listings, priceSales, weightedMedian, q1, q3, med7, med30, unitsPerDay, daysSupply,
            quantityForSale, now, notes);

        var stackLimit = (int)Math.Clamp(itemStackSize == 0 ? 999u : itemStackSize, 1u, (uint)int.MaxValue);
        var maxCandidate = Math.Max(1, Math.Min(quantityForSale, stackLimit));

        if (stackSales.Count == 0)
        {
            var conservativeStack = maxCandidate;
            var listingCount = DivideRoundUp(quantityForSale, conservativeStack);
            var stack = new StackRecommendation(
                conservativeStack,
                listingCount,
                baseSuggestion.Price,
                conservativeStack,
                listingCount,
                baseSuggestion.Price,
                0.0,
                0.0,
                0.10,
                "No sale-quantity history exists, so stack sizing stays conservative and minimizes fragmentation.",
                "Same as the recommendation because there is not enough buyer-size evidence for a second plan.",
                Array.Empty<StackCandidateScore>());
            return new StackPlanResult(baseSuggestion, stack);
        }

        var normalizedSales = NormalizeSales(stackSales, weightedMedian, now);
        var typicalSpend = WeightedQuantile(normalizedSales.Select(x => (x.TotalSpend, x.Weight)).ToList(), 0.50) ?? 0.0;
        var spendQ1 = WeightedQuantile(normalizedSales.Select(x => (x.TotalSpend, x.Weight)).ToList(), 0.25);
        var spendQ3 = WeightedQuantile(normalizedSales.Select(x => (x.TotalSpend, x.Weight)).ToList(), 0.75);
        var candidates = GenerateStackCandidates(maxCandidate, quantityForSale, stackSales);
        var works = new List<StackCandidateWork>();

        foreach (var stackSize in candidates)
        {
            var localNotes = new List<string>();
            var stackBase = SuggestPrice(
                listings, priceSales, weightedMedian, q1, q3, med7, med30, unitsPerDay, daysSupply,
                stackSize, now, localNotes);

            var rawAffinity = 0.0;
            var premiumLogSum = 0.0;
            var premiumWeight = 0.0;
            foreach (var sale in normalizedSales)
            {
                var distance = Math.Abs(Math.Log((sale.Quantity + 0.5) / (stackSize + 0.5)));
                var match = Math.Exp(-distance / 0.55);
                var weight = sale.Weight * match;
                rawAffinity += weight;
                premiumWeight += weight;
                premiumLogSum += weight * Math.Log(Math.Clamp(sale.PremiumRatio, 0.55, 1.80));
            }

            var premiumRatio = premiumWeight > 0.0001 ? Math.Exp(premiumLogSum / premiumWeight) : 1.0;
            var premiumConfidence = Clamp01(premiumWeight / 6.0);
            var premiumAdjustment = Math.Clamp((premiumRatio - 1.0) * premiumConfidence * 0.65, -0.15, 0.20);

            uint? candidatePrice = stackBase.Price ?? baseSuggestion.Price;
            if (candidatePrice is { } basePrice)
            {
                var adjusted = basePrice * (1.0 + premiumAdjustment);
                adjusted = Math.Clamp(adjusted, basePrice * 0.85, basePrice * 1.25);
                candidatePrice = ToPrice(adjusted);
            }

            var totalSpend = candidatePrice is { } p ? (double)p * stackSize : 0.0;
            var affordability = ScoreAffordability(totalSpend, typicalSpend, spendQ1, spendQ3);
            var speedFit = unitsPerDay > 0.01
                ? Math.Exp(-Math.Max(0.0, stackSize / unitsPerDay - 1.0) / 2.5)
                : 0.20;
            var listingCount = DivideRoundUp(quantityForSale, stackSize);
            var lowTicket = totalSpend <= 0 ? 1.0 : 1.0 / (1.0 + totalSpend / 10_000.0);
            var fragmentation = Clamp01(
                Math.Log(1.0 + Math.Max(0, listingCount - 1)) / Math.Log(21.0) * (0.65 + 0.35 * lowTicket));
            if (listingCount > 20)
                fragmentation = Clamp01(fragmentation + Math.Min(0.35, (listingCount - 20) / 40.0));

            works.Add(new StackCandidateWork(
                stackSize, candidatePrice, listingCount, rawAffinity, premiumRatio, premiumConfidence,
                affordability, speedFit, fragmentation));
        }

        var maxAffinity = Math.Max(0.0001, works.Max(x => x.RawAffinity));
        var burstAdjustedEvidence = normalizedSales.Sum(x => x.Weight);
        var distinctSizes = stackSales.Select(x => x.Quantity).Distinct().Count();
        var sampleConfidence = Clamp01(burstAdjustedEvidence / 20.0);
        var varietyConfidence = Clamp01(distinctSizes / 6.0);
        var activityConfidence = Clamp01(Math.Log10(1.0 + Math.Max(0.0, unitsPerDay)) / Math.Log10(21.0));
        var stackConfidence = Clamp01(0.60 * sampleConfidence + 0.20 * varietyConfidence + 0.20 * activityConfidence);

        var scored = works.Select(work =>
        {
            var demandFit = Clamp01(work.RawAffinity / maxAffinity);
            var premiumScore = Clamp01(0.5 + (work.PremiumRatio - 1.0) * 2.5);
            var evidenceUtility =
                0.32 * demandFit +
                0.22 * premiumScore +
                0.23 * work.Affordability +
                0.23 * work.SpeedFit -
                0.30 * work.FragmentationPenalty;

            // When quantity history is weak, shrink toward a low-maintenance/default answer instead
            // of letting a few noisy purchases force tiny stacks.
            var fallbackUtility = 0.55 * (1.0 - work.FragmentationPenalty) + 0.45 * work.SpeedFit;
            var utility = stackConfidence * evidenceUtility + (1.0 - stackConfidence) * fallbackUtility;

            return new StackCandidateScore(
                work.StackSize,
                work.Price,
                work.ListingCount,
                utility,
                demandFit,
                work.PremiumRatio - 1.0,
                work.Affordability,
                work.SpeedFit,
                work.FragmentationPenalty);
        }).OrderByDescending(x => x.Utility).ToList();

        var best = scored[0];
        var desiredLowMaintenanceListings = Math.Max(1, Math.Min(3, DivideRoundUp(best.ListingCount, 2)));
        var lowMaintenancePool = scored
            .Where(x => x.StackSize >= best.StackSize &&
                        (x.ListingCount <= desiredLowMaintenanceListings || x.ListingCount <= Math.Max(1, best.ListingCount / 2)))
            .ToList();
        var lowMaintenance = lowMaintenancePool
            .OrderByDescending(x => x.Utility + 0.30 * (1.0 - x.FragmentationPenalty))
            .FirstOrDefault() ?? best;

        // Do not advertise a dramatically worse "low maintenance" option just for the sake of being different.
        if (lowMaintenance.Utility < best.Utility - 0.28)
            lowMaintenance = best;

        var conveniencePremium = best.ConveniencePremium;
        var premiumText = Math.Abs(conveniencePremium) < 0.015
            ? "no meaningful stack-size price premium"
            : conveniencePremium > 0
                ? $"about {conveniencePremium:P0} historical convenience premium"
                : $"about {Math.Abs(conveniencePremium):P0} historical bulk discount";

        var typicalSpendText = typicalSpend <= 0 ? "unknown" : $"{typicalSpend:N0}g";
        var reason = $"Buyer-size history favors roughly {best.StackSize:N0} per listing with {premiumText}; " +
                     $"the plan targets a typical purchase around {typicalSpendText} " +
                     $"and needs about {best.ListingCount:N0} listing(s) for your {quantityForSale:N0} unit(s).";
        var lowReason = lowMaintenance.StackSize == best.StackSize
            ? "The recommended stack is already the best low-maintenance option supported by the data."
            : $"Use about {lowMaintenance.StackSize:N0} per listing for roughly {lowMaintenance.ListingCount:N0} listing(s); " +
              $"this trades some historical buyer/price fit for substantially less listing management.";

        notes.Add($"Suggested stack {best.StackSize:N0}; low-maintenance alternative {lowMaintenance.StackSize:N0}. Purchase bursts are down-weighted so one market sweep does not masquerade as many independent buyers.");

        var stackRecommendation = new StackRecommendation(
            best.StackSize,
            best.ListingCount,
            best.SuggestedUnitPrice,
            lowMaintenance.StackSize,
            lowMaintenance.ListingCount,
            lowMaintenance.SuggestedUnitPrice,
            conveniencePremium,
            typicalSpend,
            stackConfidence,
            reason,
            lowReason,
            scored.Take(5).ToList());

        var finalPriceReason = baseSuggestion.Reason;
        if (best.SuggestedUnitPrice is { } bestPrice && baseSuggestion.Price is { } basePrice2 && bestPrice != basePrice2)
        {
            var delta = (bestPrice - (double)basePrice2) / basePrice2;
            finalPriceReason += delta > 0
                ? $" Historical stack-size behavior supports about a {delta:P0} premium for the recommended stack."
                : $" Historical stack-size behavior implies about a {Math.Abs(delta):P0} discount for the recommended stack.";
        }

        var finalPrice = new PriceSuggestion(
            best.SuggestedUnitPrice ?? baseSuggestion.Price,
            finalPriceReason,
            Clamp01(0.70 * baseSuggestion.Confidence + 0.30 * stackConfidence));
        return new StackPlanResult(finalPrice, stackRecommendation);
    }

    private static List<NormalizedSale> NormalizeSales(List<MarketSale> sales, double? fallbackMedian, DateTimeOffset now)
    {
        var dayGroups = sales
            .GroupBy(x => x.SoldAtUtc.UtcDateTime.Date)
            .ToDictionary(g => g.Key, g => g.Select(x => (double)x.PricePerUnit).ToList());
        var burstCounts = sales
            .GroupBy(x => x.SoldAtUtc.ToUnixTimeSeconds() / 120)
            .ToDictionary(g => g.Key, g => g.Count());

        var result = new List<NormalizedSale>(sales.Count);
        foreach (var sale in sales)
        {
            var nearbyPrices = new List<double>();
            var day = sale.SoldAtUtc.UtcDateTime.Date;
            for (var offset = -2; offset <= 2; offset++)
            {
                if (dayGroups.TryGetValue(day.AddDays(offset), out var prices))
                    nearbyPrices.AddRange(prices);
            }

            var baseline = Median(nearbyPrices) ?? fallbackMedian ?? sale.PricePerUnit;
            baseline = Math.Max(1.0, baseline);
            var premium = Math.Clamp(sale.PricePerUnit / baseline, 0.55, 1.80);
            var recency = Math.Pow(0.5, Math.Max(0.0, (now - sale.SoldAtUtc).TotalDays) / 28.0);
            var burstCount = burstCounts[sale.SoldAtUtc.ToUnixTimeSeconds() / 120];
            var burstWeight = 1.0 / Math.Sqrt(Math.Max(1, burstCount));
            result.Add(new NormalizedSale(
                Math.Max(1, (int)sale.Quantity),
                premium,
                (double)sale.PricePerUnit * sale.Quantity,
                recency * burstWeight));
        }

        return result;
    }

    private static List<int> GenerateStackCandidates(int maxCandidate, int quantityForSale, List<MarketSale> sales)
    {
        var candidates = new HashSet<int>();
        int[] standard = [1, 2, 3, 5, 10, 20, 25, 50, 99, 100, 200, 250, 500, 999];
        foreach (var value in standard)
            AddCandidate(value);

        AddCandidate(quantityForSale);
        for (var listings = 2; listings <= 6; listings++)
            AddCandidate(DivideRoundUp(quantityForSale, listings));

        foreach (var group in sales.GroupBy(x => x.Quantity).OrderByDescending(g => g.Count()).Take(8))
            AddCandidate((int)group.Key);

        var quantities = sales.Select(x => (double)x.Quantity).ToList();
        foreach (var q in new[] { 0.25, 0.50, 0.75 })
        {
            if (Quantile(quantities.ToList(), q) is { } value)
                AddCandidate((int)Math.Round(value, MidpointRounding.AwayFromZero));
        }

        return candidates.Order().ToList();

        void AddCandidate(int value)
        {
            if (value >= 1 && value <= maxCandidate)
                candidates.Add(value);
        }
    }

    private static double ScoreAffordability(double candidateSpend, double typicalSpend, double? spendQ1, double? spendQ3)
    {
        if (candidateSpend <= 0 || typicalSpend <= 0)
            return 0.5;

        var score = Math.Exp(-Math.Abs(Math.Log(candidateSpend / typicalSpend)) / 0.85);
        if (spendQ1 is { } q1 && spendQ3 is { } q3 && candidateSpend >= q1 && candidateSpend <= q3)
            score = Math.Max(score, 0.90);
        return Clamp01(score);
    }

    private static int DivideRoundUp(int numerator, int denominator)
        => denominator <= 0 ? numerator : (numerator + denominator - 1) / denominator;

    private static double? WeightedQuantile(List<(double Value, double Weight)> points, double q)
    {
        var usable = points.Where(x => x.Weight > 0 && double.IsFinite(x.Value)).OrderBy(x => x.Value).ToList();
        if (usable.Count == 0)
            return null;
        var total = usable.Sum(x => x.Weight);
        if (total <= 0)
            return null;
        var target = total * Math.Clamp(q, 0.0, 1.0);
        var cumulative = 0.0;
        foreach (var point in usable)
        {
            cumulative += point.Weight;
            if (cumulative >= target)
                return point.Value;
        }
        return usable[^1].Value;
    }

    private static PriceSuggestion SuggestPrice(
        List<MarketListing> listings,
        List<MarketSale> sales,
        double? weightedMedian,
        double? q1,
        double? q3,
        double? med7,
        double? med30,
        double unitsPerDay,
        double? daysSupply,
        int quantityForSale,
        DateTimeOffset now,
        List<string> notes)
    {
        if (weightedMedian is null || weightedMedian <= 0)
        {
            if (listings.Count == 0)
                return new PriceSuggestion(null, "No sold-price history or current listings are available.", 0.0);

            var current = EstimateRealisticCurrentPrice(listings, sales, notes);
            return new PriceSuggestion(
                current,
                "No usable sold-price history exists, so the suggestion falls back to the current board.",
                0.20);
        }

        var anchor = weightedMedian.Value;
        var trendRatio = med7 is > 0 && med30 is > 0
            ? Math.Clamp(med7.Value / med30.Value, 0.75, 1.25)
            : 1.0;

        var supplyAdjustment = 0.0;
        if (daysSupply is { } ds)
        {
            if (ds < 0.5) supplyAdjustment = 0.12;
            else if (ds < 1.0) supplyAdjustment = 0.08;
            else if (ds < 3.0) supplyAdjustment = 0.04;
            else if (ds > 14.0) supplyAdjustment = -0.10;
            else if (ds > 7.0) supplyAdjustment = -0.05;
        }
        var trendAdjustment = Math.Clamp((trendRatio - 1.0) * 0.40, -0.08, 0.08);

        var historicalFloor = Math.Max(1.0, Math.Min(anchor * 0.90, (q1 ?? anchor * 0.90) * 0.98));
        var recent14 = sales.Where(x => x.SoldAtUtc >= now.AddDays(-14)).Select(x => (double)x.PricePerUnit).ToList();
        var recent14Q3 = Quantile(recent14, 0.75);
        var historicalUpper = Math.Max(
            anchor * 1.20,
            Math.Max((q3 ?? anchor) * 1.10, (recent14Q3 ?? anchor) * 1.08));

        var historicalTarget = Math.Clamp(
            anchor * (1.0 + supplyAdjustment + trendAdjustment),
            historicalFloor,
            historicalUpper);

        // Ask prices far outside the sold distribution are not treated as executable merely because
        // someone currently advertises them. A high ask can still be accepted if multiple recent
        // transactions support roughly that level.
        var plausibleCeiling = Math.Max(
            anchor * 1.75,
            Math.Max((q3 ?? anchor) * 1.60, (recent14Q3 ?? anchor) * 1.45));

        bool SupportedByRecentSales(uint ask)
        {
            var threshold = ask * 0.80;
            return sales.Count(x => x.SoldAtUtc >= now.AddDays(-14) && x.PricePerUnit >= threshold) >= 2;
        }

        var credible = listings
            .Where(x => x.PricePerUnit <= plausibleCeiling || SupportedByRecentSales(x.PricePerUnit))
            .GroupBy(x => x.PricePerUnit)
            .Select(g => new { Price = g.Key, Quantity = g.Sum(x => (double)x.Quantity) })
            .OrderBy(x => x.Price)
            .ToList();

        if (listings.Count > 0 && credible.Count == 0)
        {
            var lowestAsk = listings[0].PricePerUnit;
            notes.Add($"Current board starts at {lowestAsk:N0}g, but recent sold prices do not support that ask; suggestion is history-anchored instead.");
            return new PriceSuggestion(
                ToPrice(historicalTarget),
                "Current asks are outside the supported sold-price range, so the suggestion is anchored to recent actual sales.",
                SuggestionConfidence(sales, unitsPerDay, now));
        }

        if (credible.Count == 0)
        {
            return new PriceSuggestion(
                ToPrice(historicalTarget),
                "No current listings are available; the suggestion is anchored to recent actual sales.",
                SuggestionConfidence(sales, unitsPerDay, now));
        }

        var ownSellThroughDays = unitsPerDay > 0.01 ? quantityForSale / unitsPerDay : double.PositiveInfinity;
        var transientDepthDays = ownSellThroughDays switch
        {
            <= 1.0 => 0.50,
            <= 3.0 => 0.35,
            <= 7.0 => 0.20,
            _ => 0.10,
        };

        if (credible.Count == 1 && unitsPerDay > 0.01)
        {
            var only = credible[0];
            var clearDays = only.Quantity / unitsPerDay;
            if (clearDays <= transientDepthDays && only.Price < historicalTarget * 0.80)
            {
                notes.Add($"Ignored a lone {only.Quantity:N0}-unit low-price listing expected to clear in about {clearDays:0.##} day(s); no deeper competing tier was present.");
                return new PriceSuggestion(
                    ToPrice(historicalTarget),
                    "A single shallow low-price listing should clear quickly at current demand, so the suggestion stays near recent actual sale value instead of undercutting it.",
                    SuggestionConfidence(sales, unitsPerDay, now));
            }
        }

        var cumulativeUnits = 0.0;
        var selectedTier = credible[0];
        var ignoredUnits = 0.0;
        var ignoredTiers = 0;

        for (var i = 0; i < credible.Count; i++)
        {
            var tier = credible[i];
            cumulativeUnits += tier.Quantity;
            selectedTier = tier;

            if (i >= credible.Count - 1 || unitsPerDay <= 0.01)
                break;

            var next = credible[i + 1];
            var clearDays = cumulativeUnits / unitsPerDay;
            var meaningfulGap = next.Price >= tier.Price * 1.03;
            if (meaningfulGap && clearDays <= transientDepthDays)
            {
                ignoredUnits = cumulativeUnits;
                ignoredTiers++;
                continue;
            }

            break;
        }

        var boardCandidate = selectedTier.Price > 1 ? selectedTier.Price - 1 : 1;
        var boardSupportedUpper = SupportedByRecentSales(selectedTier.Price)
            ? Math.Max(historicalUpper, selectedTier.Price)
            : historicalUpper;

        double suggested;
        if (boardCandidate < historicalTarget)
        {
            // There is meaningful cheaper stock ahead of us; compete with it rather than pretending
            // the historical median is immediately executable.
            suggested = boardCandidate;
        }
        else if (ownSellThroughDays <= 3.0)
        {
            // A quantity that the market can absorb quickly can wait out shallow cheap stacks and
            // target the next credible tier, but we still cap unsupported optimism.
            suggested = Math.Min(boardCandidate, boardSupportedUpper);
        }
        else if (ownSellThroughDays <= 7.0)
        {
            suggested = Math.Min(boardCandidate, Math.Max(historicalTarget, anchor * 1.05));
        }
        else
        {
            // Large position relative to demand: favour executable turnover over a speculative premium.
            suggested = Math.Min(boardCandidate, historicalTarget);
        }

        suggested = Math.Max(1.0, suggested);
        var suggestedPrice = ToPrice(suggested);
        var reason = ignoredTiers > 0
            ? $"Skipped {ignoredUnits:N0} unit(s) of shallow lower-price depth that should clear quickly at current demand, then targeted the next credible tier within sold-price evidence."
            : boardCandidate < historicalTarget
                ? "Meaningful cheaper current supply exists, so the suggestion competes with the board rather than blindly using the historical median."
                : ownSellThroughDays <= 3.0
                    ? "Demand is high enough relative to this quantity to target the next credible board tier, capped by recent sold-price evidence."
                    : "Balanced current board depth, recent sold prices, trend, supply and the quantity being sold.";

        if (ignoredTiers > 0)
            notes.Add($"Pricing ignored {ignoredUnits:N0} low-priced unit(s) expected to clear in about {(unitsPerDay > 0 ? ignoredUnits / unitsPerDay : 0):0.##} day(s).");

        if (listings.Count > 0 && suggestedPrice > 0)
        {
            var lowestAsk = listings[0].PricePerUnit;
            if (lowestAsk >= suggestedPrice * 3.0 && !SupportedByRecentSales(lowestAsk))
                notes.Add($"Lowest current ask ({lowestAsk:N0}g) is far above the suggested executable price ({suggestedPrice:N0}g) and lacks recent sale support.");
        }

        return new PriceSuggestion(
            suggestedPrice,
            reason,
            SuggestionConfidence(sales, unitsPerDay, now));
    }

    private static double SuggestionConfidence(List<MarketSale> sales, double unitsPerDay, DateTimeOffset now)
    {
        if (sales.Count == 0)
            return 0.20;
        var sample = Clamp01(sales.Count / 25.0);
        var recency = Math.Exp(-(now - sales.Max(x => x.SoldAtUtc)).TotalDays / 14.0);
        var activity = Clamp01(Math.Log10(1 + unitsPerDay) / Math.Log10(1 + 20));
        return Clamp01(0.55 * sample + 0.25 * recency + 0.20 * activity);
    }

    private static uint ToPrice(double price)
        => (uint)Math.Clamp(Math.Round(price, MidpointRounding.AwayFromZero), 1, uint.MaxValue);

    private static uint? EstimateRealisticCurrentPrice(List<MarketListing> listings, List<MarketSale> sales, List<string> notes)
    {
        if (listings.Count == 0)
            return null;
        if (listings.Count == 1)
            return listings[0].PricePerUnit;

        var first = listings[0];
        var second = listings[1];
        var typicalStack = sales.Count == 0 ? 1.0 : Median(sales.Select(x => (double)x.Quantity).ToList()) ?? 1.0;

        var secondClusterCount = listings.Skip(1).Take(5)
            .Count(x => x.PricePerUnit <= second.PricePerUnit * 1.06);
        if (first.Quantity <= Math.Max(1, typicalStack) &&
            second.PricePerUnit >= first.PricePerUnit * 1.25 &&
            secondClusterCount >= 2)
        {
            notes.Add($"Ignored isolated undercut at {first.PricePerUnit:N0} gil; market cluster begins near {second.PricePerUnit:N0}.");
            return second.PricePerUnit;
        }

        return first.PricePerUnit;
    }

    private static VendorEconomicsResult ScoreVendorEconomics(
        ItemInfo item,
        bool isHq,
        uint? netMarketPrice,
        int recentSales,
        double demandScore)
    {
        if (netMarketPrice is null || netMarketPrice == 0)
            return new VendorEconomicsResult(0.50, false, null, null, string.Empty);

        var net = (double)netMarketPrice.Value;
        double score = 0.50;
        bool floorViolation = false;
        double? floorMargin = null;
        double? arbitrageMargin = null;
        var parts = new List<string>();

        if (item.VendorBuybackPrice > 0)
        {
            floorMargin = (net - item.VendorBuybackPrice) / item.VendorBuybackPrice;
            if (floorMargin <= 0)
            {
                floorViolation = true;
                score = 0.0;
                parts.Add($"After 5% market tax, expected net is {netMarketPrice.Value:N0}g versus {item.VendorBuybackPrice:N0}g guaranteed NPC buyback: use the vendor instead of the market board.");
            }
            else if (floorMargin < 0.10)
            {
                score = Math.Min(score, 0.10);
                parts.Add($"After-tax market net is only {floorMargin:P0} above the {item.VendorBuybackPrice:N0}g NPC buyback floor.");
            }
            else if (floorMargin < 0.25)
            {
                score = Math.Min(score, 0.30);
                parts.Add($"After-tax market net is only {floorMargin:P0} above NPC buyback; the extra retainer friction has limited payoff.");
            }
        }

        // Normal gil vendors sell NQ items. Do not treat an HQ item as if the vendor provides an
        // equivalent HQ source. PriceMid is only used because GameItemCatalog already verified the
        // item appears in GilShopItem.
        if (!isHq && item.VendorGilShopPrice is { } vendorPrice && vendorPrice > 0)
        {
            arbitrageMargin = (net - vendorPrice) / vendorPrice;
            if (arbitrageMargin > 0)
            {
                if (recentSales > 0 && demandScore > 0.001)
                {
                    var signal = Clamp01(Math.Log(1.0 + arbitrageMargin.Value) / Math.Log(3.0));
                    var evidence = Clamp01(0.20 + 0.45 * Clamp01(recentSales / 10.0) + 0.35 * demandScore);
                    score = Math.Max(score, 0.50 + 0.50 * signal * evidence);
                    parts.Add($"NPCs sell the NQ item for {vendorPrice:N0}g; expected after-tax market net is {netMarketPrice.Value:N0}g ({arbitrageMargin.Value:+0%;-0%;0%} convenience-arbitrage margin). Real sale/demand evidence supports a bounded boost.");
                }
                else
                {
                    parts.Add($"NPCs sell the NQ item for {vendorPrice:N0}g and the current after-tax recommendation is higher, but there are no recent sales to prove the arbitrage is executable, so no vendor-arbitrage boost is applied.");
                }
            }
            else
            {
                parts.Add($"NPCs sell the NQ item for {vendorPrice:N0}g, so the after-tax market recommendation does not currently offer vendor-to-market arbitrage.");
            }
        }

        return new VendorEconomicsResult(score, floorViolation, floorMargin, arbitrageMargin, string.Join(" ", parts));
    }

    public static uint? NetAfterSellerTax(uint? grossPrice)
    {
        if (grossPrice is null)
            return null;
        return (uint)Math.Max(0, Math.Floor(grossPrice.Value * (1.0 - MarketSellerTaxRate)));
    }

    private static double? NetAfterSellerTax(double? grossPrice)
    {
        if (grossPrice is null)
            return null;
        return Math.Max(0.0, grossPrice.Value * (1.0 - MarketSellerTaxRate));
    }

    private static double ScorePrice(uint? executable, double? historic)
    {
        if (executable is null || historic is null || historic <= 0)
            return 0.35;
        var ratio = executable.Value / historic.Value;
        return Clamp01((ratio - 0.65) / 0.70);
    }

    private static double ScoreStability(double? q1, double? q3, double? median)
    {
        if (q1 is null || q3 is null || median is null || median <= 0)
            return 0.25;
        var relativeIqr = (q3.Value - q1.Value) / median.Value;
        return 1.0 - Clamp01(relativeIqr / 1.25);
    }

    private static double ScoreTrend(double? med7, double? med30)
    {
        if (med7 is null || med30 is null || med30 <= 0)
            return 0.5;
        var ratio = med7.Value / med30.Value;
        return Clamp01((ratio - 0.75) / 0.50);
    }

    private static int RecommendedListingQuantity(
        StackRecommendation? recommendation,
        int quantityForSale,
        uint itemStackSize)
    {
        var owned = Math.Max(1, quantityForSale);
        if (recommendation is { RecommendedStackSize: > 0 })
            return Math.Clamp(recommendation.RecommendedStackSize, 1, owned);

        var stackLimit = itemStackSize == 0
            ? owned
            : (int)Math.Min((uint)owned, itemStackSize);
        return Math.Max(1, stackLimit);
    }

    public static double ScoreAbsoluteValue(double? expectedNetValue, int valueReferenceGil)
    {
        if (expectedNetValue is null || expectedNetValue <= 0)
            return 0;

        var reference = Math.Clamp(valueReferenceGil, 1, 999_999_999);
        var logRatio = Math.Log10(Math.Max(1.0, expectedNetValue.Value) / reference);
        return 1.0 / (1.0 + Math.Exp(-2.4 * logRatio));
    }

    private static double ScoreConfidence(List<MarketSale> sales, MarketSnapshot market, DateTimeOffset now)
    {
        var sample = Clamp01(sales.Count / 30.0);
        var listingFreshness = market.ListingObservedAtUtc is null
            ? 0.0
            : Math.Exp(-(now - market.ListingObservedAtUtc.Value).TotalHours / 24.0);
        var lastSale = sales.Count == 0
            ? 0.0
            : Math.Exp(-(now - sales.Max(x => x.SoldAtUtc)).TotalDays / 30.0);
        return Clamp01(0.55 * sample + 0.30 * listingFreshness + 0.15 * lastSale);
    }

    private static double? WeightedMedian(List<MarketSale> sales, DateTimeOffset now, double halfLifeDays)
    {
        if (sales.Count == 0)
            return null;
        var weighted = sales
            .Select(x => (Price: (double)x.PricePerUnit, Weight: Math.Pow(0.5, Math.Max(0, (now - x.SoldAtUtc).TotalDays) / halfLifeDays)))
            .OrderBy(x => x.Price)
            .ToList();
        var target = weighted.Sum(x => x.Weight) / 2.0;
        var cumulative = 0.0;
        foreach (var point in weighted)
        {
            cumulative += point.Weight;
            if (cumulative >= target)
                return point.Price;
        }
        return weighted[^1].Price;
    }

    private static double? Median(List<double> values) => Quantile(values, 0.5);

    private static double? Quantile(List<double> values, double q)
    {
        if (values.Count == 0)
            return null;
        values.Sort();
        var position = (values.Count - 1) * q;
        var lo = (int)Math.Floor(position);
        var hi = (int)Math.Ceiling(position);
        if (lo == hi)
            return values[lo];
        var fraction = position - lo;
        return values[lo] * (1 - fraction) + values[hi] * fraction;
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);

    private static string Label(int stars) => stars switch
    {
        5 => "Excellent selling opportunity",
        4 => "Good selling opportunity",
        3 => "Normal / reasonable",
        2 => "Weak selling opportunity",
        _ => "Poor selling opportunity",
    };

    private static string ConfidenceLabel(double confidence) => confidence switch
    {
        >= 0.80 => "Very high",
        >= 0.60 => "High",
        >= 0.40 => "Medium",
        >= 0.20 => "Low",
        _ => "Very low",
    };
}
