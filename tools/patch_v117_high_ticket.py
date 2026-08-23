from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"Expected one match in {path}, found {count}: {old[:120]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


def replace_region(path: str, start_marker: str, end_marker: str, new_region: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    start = text.find(start_marker)
    if start < 0:
        raise SystemExit(f"Start marker not found in {path}: {start_marker!r}")
    end = text.find(end_marker, start)
    if end < 0:
        raise SystemExit(f"End marker not found in {path}: {end_marker!r}")
    p.write_text(text[:start] + new_region + text[end:], encoding="utf-8")


scanner = "ShouldISell/Services/BuyOpportunityScanner.cs"

replace_once(
    scanner,
    'http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ShouldI", "1.1.6"));',
    'http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ShouldI", "1.1.7"));',
)

replace_once(
    scanner,
    """            var selectedIds = selectedVariants
                .GroupBy(x => x.Entry.Item.ItemId)
                .OrderByDescending(g => g.Max(x => x.RoughScore))
                .Take(settings.DeepCandidateLimit)
                .Select(g => g.Key)
                .ToHashSet();""",
    """            var candidateGroups = selectedVariants
                .GroupBy(x => x.Entry.Item.ItemId)
                .ToList();
            var selectedIds = new HashSet<uint>();

            // v1.1.7: the old broad shortlist was dominated by raw ROI. A 1g -> 20,000g anomaly
            // could therefore outrank a 100,000g -> 1,000,000g opportunity by orders of magnitude
            // before either item ever received 90-day history. Reserve roughly one third of the
            // expensive detailed lookups for the largest plausible absolute-gil gaps, then fill
            // the rest by the balanced rough score. Final recommendations still use only detailed
            // current-world listings/history and the normal Buy safety gates.
            var highGilSlots = Math.Max(1, settings.DeepCandidateLimit / 3);
            foreach (var group in candidateGroups
                         .Where(g => g.Max(x => x.EstimatedUnitProfit) > 0)
                         .OrderByDescending(g => g.Max(x => x.EstimatedUnitProfit))
                         .ThenByDescending(g => g.Max(x => x.RoughScore))
                         .Take(highGilSlots))
                selectedIds.Add(group.Key);

            foreach (var group in candidateGroups
                         .OrderByDescending(g => g.Max(x => x.RoughScore))
                         .ThenByDescending(g => g.Max(x => x.EstimatedUnitProfit)))
            {
                if (selectedIds.Count >= settings.DeepCandidateLimit)
                    break;
                selectedIds.Add(group.Key);
            }""",
)

new_rough = r'''    private void AddRoughCandidate(
        List<RoughCandidate> output,
        MarketCatalogEntry entry,
        bool isHq,
        AggregatedVariant variant,
        ScanSettings settings)
    {
        var item = entry.Item;
        var discovery = GetDiscoveryReference(variant);
        if (variant.MinListing <= 0 && discovery.Price <= 0)
            return;

        // Aggregated sale price/velocity covers only a very short recent window. Rare expensive
        // items can have no world sale in that window even though the world has useful 90-day
        // history. DC/region values and listing medians may therefore rescue an item for the deep
        // shortlist, but they NEVER become the final exit evidence.
        var conservativeUnitCost = variant.MinListing * (1.0 + ConservativeBuyerTaxRate);
        var netDiscoveryReference = discovery.Price * (1.0 - ScoreCalculator.MarketSellerTaxRate);
        var marketMargin = conservativeUnitCost > 0
            ? (netDiscoveryReference - conservativeUnitCost) / conservativeUnitCost
            : 0;
        var estimatedUnitProfit = Math.Max(0, netDiscoveryReference - conservativeUnitCost);
        var perItemBudget = Math.Min(settings.BudgetGil,
            Math.Max(1L, settings.BudgetGil * Math.Clamp(settings.MaxInvestmentPercentPerItem, 1, 100) / 100L));
        var affordableSingleUnit = conservativeUnitCost > 0 && conservativeUnitCost <= perItemBudget;
        var vendorContested = HasRenewableVendorSupply(item, isHq);
        var marketSignal = settings.EnableMarketToMarket && !vendorContested &&
                           variant.MinListing > 0 && discovery.Price > 0 && affordableSingleUnit &&
                           marketMargin >= settings.MinimumRoi * 0.60;

        // Vendor -> Market stays deliberately world-local. Broader DC/region prices should never
        // manufacture a convenience-arbitrage recommendation on a world where nobody is buying it.
        var netWorldAverage = variant.AverageSalePrice * (1.0 - ScoreCalculator.MarketSellerTaxRate);
        var vendorMarketMargin = !isHq && item.VendorGilShopPrice is > 0
            ? (netWorldAverage - item.VendorGilShopPrice.Value) / item.VendorGilShopPrice.Value
            : double.NegativeInfinity;
        var vendorMarketSignal = settings.EnableVendorToMarket && !isHq && item.VendorGilShopPrice is > 0 &&
                                 variant.DailyVelocity > 0.001 && vendorMarketMargin >= settings.MinimumRoi * 0.60;

        var vendorFloorSignal = settings.EnableMarketToVendor && item.VendorBuybackPrice > 0 && variant.MinListing > 0 &&
                                variant.MinListing * (1 + ConservativeBuyerTaxRate) < item.VendorBuybackPrice;

        if (!marketSignal && !vendorMarketSignal && !vendorFloorSignal)
            return;

        // Keep discovery ranking on the same conceptual scale as the final Buy score: ROI is
        // logarithmic/capped rather than linear, while absolute gil has real weight. This prevents
        // penny anomalies from monopolising every detailed-history slot.
        var localVelocity = Clamp01(Math.Log10(1 + Math.Max(0, variant.DailyVelocity)) / Math.Log10(31));
        var broaderVelocity = variant.DailyVelocity > 0
            ? variant.DailyVelocity
            : variant.DcDailyVelocity > 0
                ? variant.DcDailyVelocity * 0.50
                : variant.RegionDailyVelocity * 0.25;
        var discoveryVelocity = Clamp01(Math.Log10(1 + Math.Max(0, broaderVelocity)) / Math.Log10(31));

        var marketRough = 0.0;
        if (marketSignal)
        {
            var roiScore = RoughRoiScore(marketMargin);
            var profitScore = ScoreProfit(estimatedUnitProfit, settings.MinimumProfitGil);
            marketRough = 100 * (0.45 * roiScore + 0.40 * profitScore + 0.15 * discoveryVelocity) * discovery.Confidence;
        }

        var vendorRough = 0.0;
        if (vendorMarketSignal)
        {
            var vendorUnitProfit = Math.Max(0, netWorldAverage - item.VendorGilShopPrice!.Value);
            vendorRough = 100 * (
                0.55 * RoughRoiScore(vendorMarketMargin) +
                0.30 * ScoreProfit(vendorUnitProfit, settings.MinimumProfitGil) +
                0.15 * localVelocity);
        }

        var roughScore = Math.Max(marketRough, vendorRough);
        if (vendorFloorSignal)
            roughScore += 250 + 100 * Math.Max(0, (item.VendorBuybackPrice - variant.MinListing) / (double)Math.Max(1, variant.MinListing));

        output.Add(new RoughCandidate(
            entry,
            isHq,
            variant,
            roughScore,
            vendorFloorSignal,
            marketSignal ? estimatedUnitProfit : 0,
            discovery.Price,
            discovery.Label,
            discovery.IsWorldRecentSale));
    }

    private static double RoughRoiScore(double margin)
        => Clamp01(Math.Log10(1 + Math.Max(0, margin) * 20) / Math.Log10(21));

    private static DiscoveryReference GetDiscoveryReference(AggregatedVariant variant)
    {
        var candidates = new List<DiscoveryReference>(6);

        // Wider scopes are intentionally discounted because they are only a reason to spend a
        // detailed current-world lookup, not proof that the local exit exists.
        if (variant.AverageSalePrice > 0)
            candidates.Add(new DiscoveryReference(variant.AverageSalePrice, "world 4-day average sale", 1.00, true));
        if (variant.DcAverageSalePrice > 0)
            candidates.Add(new DiscoveryReference(variant.DcAverageSalePrice * 0.95, "DC 4-day average sale (discovery-only)", 0.90, false));
        if (variant.RegionAverageSalePrice > 0)
            candidates.Add(new DiscoveryReference(variant.RegionAverageSalePrice * 0.90, "region 4-day average sale (discovery-only)", 0.82, false));
        if (variant.MedianListing > 0)
            candidates.Add(new DiscoveryReference(variant.MedianListing * 0.90, "world median listing (discovery-only)", 0.72, false));
        if (variant.DcMedianListing > 0)
            candidates.Add(new DiscoveryReference(variant.DcMedianListing * 0.82, "DC median listing (discovery-only)", 0.66, false));
        if (variant.RegionMedianListing > 0)
            candidates.Add(new DiscoveryReference(variant.RegionMedianListing * 0.75, "region median listing (discovery-only)", 0.58, false));

        return candidates.Count == 0
            ? new DiscoveryReference(0, "no aggregate reference", 0, false)
            : candidates
                .OrderByDescending(x => x.Price)
                .ThenByDescending(x => x.Confidence)
                .First();
    }

'''
replace_region(
    scanner,
    "    private void AddRoughCandidate(\n",
    "    private async Task<IReadOnlyList<AggregatedItem>> FetchAggregatedAsync",
    new_rough,
)

replace_once(
    scanner,
    '''        if (candidate.Variant.AverageSalePrice > 0)\n            notes.Add($"Universalis broad-pass anchor: ~{candidate.Variant.AverageSalePrice:N0}g average sale price and {candidate.Variant.DailyVelocity:0.##} unit(s)/day over the recent aggregate window.");''',
    '''        if (candidate.DiscoveryReferencePrice > 0)\n        {\n            notes.Add($"Universalis broad-pass anchor: {candidate.DiscoveryReferenceLabel} at ~{candidate.DiscoveryReferencePrice:N0}g; local recent velocity was {candidate.Variant.DailyVelocity:0.##} unit(s)/day.");\n            if (!candidate.DiscoveryReferenceIsWorldRecentSale)\n                notes.Add("That broader/listing-based value was discovery-only. The recommendation itself still had to pass current-world listings, 90-day current-world sales, ROI, profit and holding-time checks.");\n        }''',
)

replace_once(
    scanner,
    '''            AverageSalePrice = GetNestedDouble(variant, "averageSalePrice", "world", "price"),\n            DailyVelocity = GetNestedDouble(variant, "dailySaleVelocity", "world", "quantity"),''',
    '''            AverageSalePrice = GetNestedDouble(variant, "averageSalePrice", "world", "price"),\n            DailyVelocity = GetNestedDouble(variant, "dailySaleVelocity", "world", "quantity"),\n            DcMedianListing = GetNestedDouble(variant, "medianListing", "dc", "price"),\n            RegionMedianListing = GetNestedDouble(variant, "medianListing", "region", "price"),\n            DcAverageSalePrice = GetNestedDouble(variant, "averageSalePrice", "dc", "price"),\n            RegionAverageSalePrice = GetNestedDouble(variant, "averageSalePrice", "region", "price"),\n            DcDailyVelocity = GetNestedDouble(variant, "dailySaleVelocity", "dc", "quantity"),\n            RegionDailyVelocity = GetNestedDouble(variant, "dailySaleVelocity", "region", "quantity"),''',
)

replace_once(
    scanner,
    '''    private sealed record RoughCandidate(\n        MarketCatalogEntry Entry,\n        bool IsHq,\n        AggregatedVariant Variant,\n        double RoughScore,\n        bool GuaranteedVendorSignal);''',
    '''    private sealed record RoughCandidate(\n        MarketCatalogEntry Entry,\n        bool IsHq,\n        AggregatedVariant Variant,\n        double RoughScore,\n        bool GuaranteedVendorSignal,\n        double EstimatedUnitProfit,\n        double DiscoveryReferencePrice,\n        string DiscoveryReferenceLabel,\n        bool DiscoveryReferenceIsWorldRecentSale);\n\n    private sealed record DiscoveryReference(\n        double Price,\n        string Label,\n        double Confidence,\n        bool IsWorldRecentSale);''',
)

replace_once(
    scanner,
    '''    private sealed class AggregatedVariant\n    {\n        public double MinListing { get; init; }\n        public double MedianListing { get; init; }\n        public double AverageSalePrice { get; init; }\n        public double DailyVelocity { get; init; }\n    }''',
    '''    private sealed class AggregatedVariant\n    {\n        public double MinListing { get; init; }\n        public double MedianListing { get; init; }\n        public double AverageSalePrice { get; init; }\n        public double DailyVelocity { get; init; }\n        public double DcMedianListing { get; init; }\n        public double RegionMedianListing { get; init; }\n        public double DcAverageSalePrice { get; init; }\n        public double RegionAverageSalePrice { get; init; }\n        public double DcDailyVelocity { get; init; }\n        public double RegionDailyVelocity { get; init; }\n    }''',
)

ui = "ShouldISell/Windows/SuiteWindow.Buy.cs"
replace_once(
    ui,
    '            Tooltip("Maximum share of your total scanner budget that may be tied up in one item/HQ variant. This prevents one trade from consuming the whole bankroll.");',
    '''            Tooltip("Maximum share of your total scanner budget that may be tied up in one item/HQ variant. This prevents one trade from consuming the whole bankroll.");\n            var effectivePerItemCap = Math.Min(\n                (long)c.BuyBudgetGil,\n                Math.Max(1L, (long)c.BuyBudgetGil * Math.Clamp(c.BuyMaximumInvestmentPercentPerItem, 1, 100) / 100L));\n            ImGui.TextDisabled($"Effective per-item acquisition cap: {effectivePerItemCap:N0}g.");\n            Tooltip("This is a hard acquisition-package limit. Example: a 500,000g budget at 25% permits at most about 125,000g in one item/HQ variant, so more expensive flips are intentionally excluded.");''',
)
replace_once(
    ui,
    '            Tooltip("After discovery, only this many strongest item IDs receive detailed Universalis current listings plus 90-day history. This is still Universalis data; the separate LIVE VERIFY action is the one-item native FFXIV check.");',
    '            Tooltip("After discovery, only this many item IDs receive detailed Universalis current listings plus 90-day history. The shortlist is diversified: roughly one third of its slots protect large plausible absolute-gil gaps from being crowded out by tiny extreme-ROI items. DC/region aggregate values may rescue rare items for this detailed look, but final recommendations still require current-world detailed evidence. LIVE VERIFY remains the native FFXIV check.");',
)
replace_once(
    ui,
    '            Tooltip("Include equippable items in discovery. Gear markets can be slower and more fragmented than materials, so this is off by default.");',
    '''            Tooltip("Include equippable items in discovery. Gear markets can be slower and more fragmented than materials, so this is off by default.");\n            if (!c.BuyIncludeEquipment)\n                ImGui.TextDisabled("High-ticket equippable gear/glamour opportunities are excluded while this is off.");''',
)

replace_once(
    "ShouldISell/ShouldISell.csproj",
    "<Version>1.1.6.0</Version>",
    "<Version>1.1.7.0</Version>",
)

print("v1.1.7 high-ticket discovery patch applied")
