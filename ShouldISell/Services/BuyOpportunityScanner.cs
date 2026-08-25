using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace ShouldISell.Services;

public enum BuyScanLane
{
    MarketBoard,
    Vendor,
}

/// <summary>
/// Two-stage Should I Buy? discovery engine. The broad pass uses Universalis' aggregated endpoint
/// (up to 100 items/request), then only the strongest candidates receive full listing books and
/// 90-day sale history. The deep pass simulates actually removing purchased listings before asking
/// the existing Should I Sell? score/stack engine how the resulting position should be exited.
/// </summary>
public sealed class BuyOpportunityScanner : IDisposable
{
    private const int BatchSize = 100;
    private const int MaxListingPackagesPerVariant = 20;
    private const double ConservativeBuyerTaxRate = 0.05;

    private readonly Configuration configuration;
    private readonly IPlayerState playerState;
    private readonly GameItemCatalog catalog;
    private readonly InventoryScanner inventory;
    private readonly ScoreCalculator scores;
    private readonly IPluginLog log;
    private readonly HttpClient http = new();
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private readonly object resultGate = new();
    private CancellationTokenSource? scanCts;
    private List<BuyOpportunity> marketOpportunities = new();
    private List<BuyOpportunity> vendorOpportunities = new();

    public BuyOpportunityScanner(
        Configuration configuration,
        IPlayerState playerState,
        GameItemCatalog catalog,
        InventoryScanner inventory,
        ScoreCalculator scores,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.playerState = playerState;
        this.catalog = catalog;
        this.inventory = inventory;
        this.scores = scores;
        this.log = log;

        http.BaseAddress = new Uri("https://universalis.app/");
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ShouldI", "2.3.4"));
    }

    public bool IsScanning { get; private set; }
    public string Status { get; private set; } = "Ready to scan.";
    public int BroadItemsScanned { get; private set; }
    public int BroadItemsTotal { get; private set; }
    public int DeepItemsScanned { get; private set; }
    public int DeepItemsTotal { get; private set; }
    public int BroadSignalVariants { get; private set; }
    public int LocalDeepCandidates { get; private set; }
    public int RescueDeepCandidates { get; private set; }
    public DateTimeOffset? LastCompletedUtc { get; private set; }
    public DateTimeOffset? LastMarketCompletedUtc { get; private set; }
    public DateTimeOffset? LastVendorCompletedUtc { get; private set; }
    public BuyScanLane? ActiveLane { get; private set; }

    public IReadOnlyList<BuyOpportunity> GetOpportunities()
    {
        lock (resultGate)
            return marketOpportunities.Concat(vendorOpportunities)
                .OrderByDescending(x => x.OpportunityScore)
                .ThenByDescending(x => x.RiskAdjustedProfit)
                .ToList();
    }

    public IReadOnlyList<BuyOpportunity> GetMarketOpportunities()
    {
        lock (resultGate)
            return marketOpportunities.ToList();
    }

    public IReadOnlyList<BuyOpportunity> GetVendorOpportunities()
    {
        lock (resultGate)
            return vendorOpportunities.ToList();
    }

    public void CancelScan()
    {
        scanCts?.Cancel();
        Status = "Cancelling scan...";
    }

    public Task ScanAsync(CancellationToken cancellationToken = default)
        => ScanMarketAsync(cancellationToken);

    public Task ScanMarketAsync(CancellationToken cancellationToken = default)
        => ScanInternalAsync(BuyScanLane.MarketBoard, cancellationToken);

    public Task ScanVendorAsync(CancellationToken cancellationToken = default)
        => ScanInternalAsync(BuyScanLane.Vendor, cancellationToken);

    private async Task ScanInternalAsync(BuyScanLane lane, CancellationToken cancellationToken = default)
    {
        if (!playerState.IsLoaded || !await scanGate.WaitAsync(0, cancellationToken))
            return;

        scanCts?.Dispose();
        scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = scanCts.Token;

        try
        {
            IsScanning = true;
            ActiveLane = lane;
            BroadItemsScanned = 0;
            DeepItemsScanned = 0;
            BroadSignalVariants = 0;
            LocalDeepCandidates = 0;
            RescueDeepCandidates = 0;
            inventory.ScanLoadedContainers(forceFlush: true);

            var baseSettings = SnapshotSettings();
            var settings = lane == BuyScanLane.Vendor
                ? baseSettings with
                {
                    EnableMarketToMarket = false,
                    EnableMarketToVendor = false,
                    EnableVendorToMarket = baseSettings.EnableVendorToMarket,
                }
                : baseSettings with { EnableVendorToMarket = false };
            var worldId = playerState.CurrentWorld.RowId;
            var universe = catalog.GetAllMarketableEntries()
                .Where(x => settings.IncludeEquipment || !x.IsEquipment)
                .Where(x => string.IsNullOrWhiteSpace(settings.NameFilter) || x.Item.Name.Contains(settings.NameFilter, StringComparison.CurrentCultureIgnoreCase))
                .Where(x => !settings.UseCategoryFilter || settings.CategoryIds.Contains(x.UiCategoryId))
                .Where(x => lane != BuyScanLane.Vendor || x.Item.VendorGilShopPrice is > 0)
                .ToList();

            BroadItemsTotal = universe.Count;
            if (universe.Count == 0)
            {
                ReplaceLaneResults(lane, new List<BuyOpportunity>());
                Status = "No marketable items match the selected scope.";
                return;
            }

            Status = $"Discovery pass: 0 / {universe.Count:N0} items...";
            var rough = new List<RoughCandidate>();
            var byId = universe.ToDictionary(x => x.Item.ItemId);
            foreach (var batch in Batch(universe.Select(x => x.Item.ItemId)))
            {
                token.ThrowIfCancellationRequested();
                var aggregated = await FetchAggregatedAsync(worldId, batch, token);
                if (!playerState.IsLoaded || playerState.CurrentWorld.RowId != worldId)
                {
                    ReplaceLaneResults(lane, new List<BuyOpportunity>());
                    Status = "World changed during discovery. Results were discarded; run discovery again on your current world.";
                    return;
                }
                foreach (var row in aggregated)
                {
                    if (!byId.TryGetValue(row.ItemId, out var entry))
                        continue;

                    if (settings.IncludeNq)
                        AddRoughCandidate(rough, entry, false, row.Nq, settings);
                    if (settings.IncludeHq && entry.Item.CanBeHq)
                        AddRoughCandidate(rough, entry, true, row.Hq, settings);
                }

                BroadItemsScanned = Math.Min(BroadItemsTotal, BroadItemsScanned + batch.Count);
                Status = $"Discovery pass: {BroadItemsScanned:N0} / {BroadItemsTotal:N0} items...";
                await Task.Delay(80, token);
            }

            // Deep-query by unique item so NQ/HQ candidates share the same HTTP payload.
            var selectedVariants = rough
                .OrderByDescending(x => x.RoughScore)
                .GroupBy(x => (x.Entry.Item.ItemId, x.IsHq))
                .Select(x => x.First())
                .ToList();

            BroadSignalVariants = selectedVariants.Count;
            var candidateGroups = selectedVariants
                .GroupBy(x => x.Entry.Item.ItemId)
                .ToList();
            var selectedIds = new HashSet<uint>();

            // v2.0: keep rare-item discovery, but do not let discovery-only DC/region/listing
            // evidence crowd current-world sellers out of the expensive 90-day deep stage.
            // The main pool is current-world sale-backed. A bounded rescue pool is explicitly
            // reserved for rare items, and a separate local high-gil lane protects large flips.
            var rescueSlots = Math.Clamp(settings.DeepCandidateLimit / 6, 6, 40);
            var highGilSlots = Math.Clamp(settings.DeepCandidateLimit / 4, 8, 100);
            var balancedLocalSlots = Math.Max(1, settings.DeepCandidateLimit - rescueSlots - highGilSlots);

            var localGroups = candidateGroups
                .Where(g => g.Any(x => x.LocalMarketSignal || x.GuaranteedVendorSignal))
                .ToList();
            var rescueGroups = candidateGroups
                .Where(g => g.Any(x => x.RareRescueSignal) && !g.Any(x => x.LocalMarketSignal))
                .ToList();

            foreach (var group in localGroups
                         .OrderByDescending(g => g.Max(x => x.RoughScore))
                         .ThenByDescending(g => g.Max(x => x.EstimatedUnitProfit))
                         .Take(balancedLocalSlots))
                selectedIds.Add(group.Key);

            foreach (var group in localGroups
                         .Where(g => g.Any(x => x.LocalMarketSignal && x.EstimatedUnitProfit > 0))
                         .OrderByDescending(g => g.Where(x => x.LocalMarketSignal).Max(x => x.EstimatedUnitProfit))
                         .ThenByDescending(g => g.Max(x => x.RoughScore))
                         .Take(highGilSlots))
                selectedIds.Add(group.Key);

            foreach (var group in rescueGroups
                         .OrderByDescending(g => g.Max(x => x.RoughScore))
                         .ThenByDescending(g => g.Max(x => x.EstimatedUnitProfit))
                         .Take(rescueSlots))
                selectedIds.Add(group.Key);

            // Duplicates between the balanced and high-gil lanes can leave free slots. Fill them
            // with the best remaining groups, always preferring current-world signals first.
            foreach (var group in candidateGroups
                         .OrderByDescending(g => g.Any(x => x.LocalMarketSignal || x.GuaranteedVendorSignal))
                         .ThenByDescending(g => g.Max(x => x.RoughScore))
                         .ThenByDescending(g => g.Max(x => x.EstimatedUnitProfit)))
            {
                if (selectedIds.Count >= settings.DeepCandidateLimit)
                    break;
                selectedIds.Add(group.Key);
            }

            // Guaranteed vendor-floor opportunities are deterministic enough to deserve a deep
            // look even if the normal market-sale statistics are weak.
            foreach (var id in rough.Where(x => x.GuaranteedVendorSignal)
                         .OrderByDescending(x => x.RoughScore)
                         .Select(x => x.Entry.Item.ItemId)
                         .Distinct()
                         .Take(30))
                selectedIds.Add(id);

            selectedVariants = selectedVariants
                .Where(x => selectedIds.Contains(x.Entry.Item.ItemId))
                .ToList();

            LocalDeepCandidates = selectedIds.Count(id =>
                selectedVariants.Any(x => x.Entry.Item.ItemId == id && x.LocalMarketSignal));
            RescueDeepCandidates = selectedIds.Count(id =>
                !selectedVariants.Any(x => x.Entry.Item.ItemId == id && x.LocalMarketSignal) &&
                selectedVariants.Any(x => x.Entry.Item.ItemId == id && x.RareRescueSignal));
            if (lane == BuyScanLane.Vendor)
            {
                // Vendor candidates are intentionally their own world-local lane. They do not need
                // to masquerade as Market -> Market local signals to win deep-analysis slots.
                LocalDeepCandidates = selectedIds.Count;
                RescueDeepCandidates = 0;
            }

            DeepItemsTotal = selectedIds.Count;
            Status = $"Detailed Universalis: 0 / {DeepItemsTotal:N0} candidate items...";
            var deepByItem = new Dictionary<uint, DeepMarketData>();
            foreach (var batch in Batch(selectedIds))
            {
                token.ThrowIfCancellationRequested();
                var deep = await FetchDeepAsync(worldId, batch, token);
                if (!playerState.IsLoaded || playerState.CurrentWorld.RowId != worldId)
                {
                    ReplaceLaneResults(lane, new List<BuyOpportunity>());
                    Status = "World changed during detailed analysis. Results were discarded; run discovery again on your current world.";
                    return;
                }
                foreach (var pair in deep)
                    deepByItem[pair.Key] = pair.Value;

                DeepItemsScanned = Math.Min(DeepItemsTotal, DeepItemsScanned + batch.Count);
                Status = $"Detailed Universalis: {DeepItemsScanned:N0} / {DeepItemsTotal:N0} candidate items...";
                await Task.Delay(100, token);
            }

            var ownedByVariant = inventory.GetKnownOwnedStacks()
                .GroupBy(x => (x.ItemId, x.IsHq))
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

            var final = new List<BuyOpportunity>();
            foreach (var candidate in selectedVariants)
            {
                token.ThrowIfCancellationRequested();
                if (!deepByItem.TryGetValue(candidate.Entry.Item.ItemId, out var deep))
                    continue;

                ownedByVariant.TryGetValue((candidate.Entry.Item.ItemId, candidate.IsHq), out var existingQuantity);
                if (lane == BuyScanLane.MarketBoard && settings.EnableMarketToMarket && !HasRenewableVendorSupply(candidate.Entry.Item, candidate.IsHq))
                    TryAddBestMarketFlip(final, worldId, candidate, deep, existingQuantity, settings);
                if (lane == BuyScanLane.Vendor && settings.EnableVendorToMarket && !candidate.IsHq && candidate.Entry.Item.VendorGilShopPrice is > 0)
                    TryAddVendorToMarket(final, worldId, candidate, deep, existingQuantity, settings);
                if (lane == BuyScanLane.MarketBoard && settings.EnableMarketToVendor && candidate.Entry.Item.VendorBuybackPrice > 0)
                    TryAddMarketToVendor(final, worldId, candidate, deep, existingQuantity, settings);
            }

            final = final
                .Where(x => x.AcquisitionCost <= settings.BudgetGil)
                .OrderByDescending(x => x.OpportunityScore)
                .ThenByDescending(x => x.RiskAdjustedProfit)
                .ThenByDescending(x => x.PotentialProfit)
                .Take(500)
                .ToList();

            if (!playerState.IsLoaded || playerState.CurrentWorld.RowId != worldId)
            {
                ReplaceLaneResults(lane, new List<BuyOpportunity>());
                Status = "World changed before discovery completed. Results were discarded; run discovery again on your current world.";
                return;
            }

            ReplaceLaneResults(lane, final);

            LastCompletedUtc = DateTimeOffset.UtcNow;
            if (lane == BuyScanLane.Vendor)
                LastVendorCompletedUtc = LastCompletedUtc;
            else
                LastMarketCompletedUtc = LastCompletedUtc;
            var laneLabel = lane == BuyScanLane.Vendor ? "Vendor -> Market" : "Market Board";
            Status = $"{laneLabel} ready: {final.Count:N0} opportunity package(s) from {universe.Count:N0} scoped items. " +
                     $"Broad signals {BroadSignalVariants:N0}; detailed {DeepItemsTotal:N0} " +
                     $"({LocalDeepCandidates:N0} local + {RescueDeepCandidates:N0} rare rescue).";
        }
        catch (OperationCanceledException)
        {
            Status = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Should I Buy? scan failed.");
            Status = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            ActiveLane = null;
            scanGate.Release();
        }
    }

    private void ReplaceLaneResults(BuyScanLane lane, List<BuyOpportunity> results)
    {
        lock (resultGate)
        {
            if (lane == BuyScanLane.Vendor)
                vendorOpportunities = results;
            else
                marketOpportunities = results;
        }
    }

    public PurchasePredictionContext? FindPredictionForPurchase(ulong listingId, uint itemId, bool isHq)
    {
        BuyOpportunity? match;
        lock (resultGate)
        {
            match = marketOpportunities
                .Where(x => x.Item.ItemId == itemId && x.IsHq == isHq)
                .Where(x => listingId == 0 || x.AcquisitionLots.Any(l => l.ListingId == listingId))
                .Where(x => DateTimeOffset.UtcNow - x.AnalysedAtUtc <= TimeSpan.FromHours(6))
                .OrderByDescending(x => listingId != 0 && x.AcquisitionLots.Any(l => l.ListingId == listingId))
                .ThenByDescending(x => x.OpportunityScore)
                .FirstOrDefault();
        }

        return match is null
            ? null
            : new PurchasePredictionContext(
                match.Kind,
                match.StrategyLabel,
                match.OpportunityScore,
                match.SuggestedExitUnitPrice,
                match.EstimatedLiquidationDays,
                match.PotentialProfit,
                match.AnalysedAtUtc);
    }

    public void Dispose()
    {
        scanCts?.Cancel();
        scanCts?.Dispose();
        http.Dispose();
        scanGate.Dispose();
    }

    private static bool HasRenewableVendorSupply(ItemInfo item, bool isHq)
        => !isHq && item.VendorGilShopPrice is > 0;

    private void TryAddBestMarketFlip(
        List<BuyOpportunity> output,
        uint worldId,
        RoughCandidate candidate,
        DeepMarketData deep,
        int existingQuantity,
        ScanSettings settings)
    {
        // A normal-gil vendor is effectively renewable external supply. Buying out player listings
        // does not create durable scarcity because any player can immediately restock at the fixed
        // NPC price. Never model those NQ items as Market → Market buyouts/undercut sweeps.
        if (HasRenewableVendorSupply(candidate.Entry.Item, candidate.IsHq))
            return;

        var variantListings = deep.Listings
            .Where(x => x.Listing.IsHq == candidate.IsHq && x.Listing.PricePerUnit > 0 && x.Listing.Quantity > 0)
            .OrderBy(x => x.Listing.PricePerUnit)
            .ThenBy(x => x.Listing.Quantity)
            .ToList();
        if (variantListings.Count == 0)
            return;

        var perItemBudget = Math.Min(settings.BudgetGil,
            Math.Max(1L, settings.BudgetGil * Math.Clamp(settings.MaxInvestmentPercentPerItem, 1, 100) / 100L));
        long cumulativeCost = 0;
        var cumulativeQuantity = 0;
        BuyOpportunity? best = null;

        for (var i = 0; i < Math.Min(MaxListingPackagesPerVariant, variantListings.Count); i++)
        {
            var acquired = variantListings[i];
            cumulativeQuantity += checked((int)acquired.Listing.Quantity);
            cumulativeCost += acquired.TotalCost;
            if (cumulativeCost > settings.BudgetGil || cumulativeCost > perItemBudget)
                break;

            var market = new MarketSnapshot
            {
                WorldId = worldId,
                ItemId = candidate.Entry.Item.ItemId,
                ListingObservedAtUtc = deep.ListingObservedAtUtc,
                HistoryObservedAtUtc = deep.HistoryObservedAtUtc,
                UniversalisLastUploadUtc = deep.ListingObservedAtUtc,
                CurrentSource = MarketDataSource.Universalis,
                Listings = variantListings.Skip(i + 1).Select(x => x.Listing).ToList(),
                Sales = deep.Sales,
            };

            var resultingPosition = Math.Max(1, existingQuantity + cumulativeQuantity);
            var rating = scores.Calculate(
                candidate.Entry.Item,
                candidate.IsHq,
                market,
                configuration.ValueThresholdGil,
                resultingPosition);
            if (rating?.NetSuggestedPriceAfterTax is not { } netExit || rating.SuggestedPrice is not { } grossExit)
                continue;

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
            var kind = ClassifyMarketStrategy(
                variantListings,
                i,
                cumulativeQuantity,
                stackSize);
            var label = StrategyLabel(kind);
            var maxBuy = CalculateMaximumBuyPrice(netExit, settings.MinimumRoi);
            var notes = BuildMarketNotes(candidate, rating, cumulativeQuantity, cumulativeCost, existingQuantity, variantListings, i);
            var opportunity = new BuyOpportunity(
                worldId,
                candidate.Entry.Item,
                candidate.IsHq,
                kind,
                label,
                Stars(score),
                score,
                rating.Confidence,
                existingQuantity,
                cumulativeQuantity,
                cumulativeCost,
                cumulativeCost / (double)cumulativeQuantity,
                grossExit,
                netExit,
                stackSize,
                exitListings,
                potentialProfit,
                riskAdjustedProfit,
                roi,
                firstSaleDays,
                liquidationDays,
                maxBuy,
                rating.UnitsPerDay,
                rating.SalesSampleCount,
                rating.ListingFreshnessUtc,
                variantListings.Take(i + 1).Select(ToAcquisitionLot).ToList(),
                notes,
                DateTimeOffset.UtcNow);

            if (best is null || opportunity.OpportunityScore > best.OpportunityScore ||
                (Math.Abs(opportunity.OpportunityScore - best.OpportunityScore) < 0.5 && opportunity.RiskAdjustedProfit > best.RiskAdjustedProfit))
                best = opportunity;
        }

        if (best is not null)
            output.Add(best);
    }

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
        var stackBound = Math.Min(
            MarketBoardRules.MaxListingQuantity,
            (int)Math.Clamp(candidate.Entry.Item.StackSize == 0 ? (uint)MarketBoardRules.MaxListingQuantity : candidate.Entry.Item.StackSize, 1u, int.MaxValue));
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
            "Normal-gil vendor supply is renewable for every player, so this strategy never assumes that buying out competing Market Board listings will create durable scarcity.",
            $"Quantity is demand-capped and hard-capped to one listable working stack (maximum 99 units) rather than stockpiling renewable vendor supply.",
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

    private void TryAddMarketToVendor(
        List<BuyOpportunity> output,
        uint worldId,
        RoughCandidate candidate,
        DeepMarketData deep,
        int existingQuantity,
        ScanSettings settings)
    {
        var floor = candidate.Entry.Item.VendorBuybackPrice;
        if (floor == 0)
            return;

        var listings = deep.Listings
            .Where(x => x.Listing.IsHq == candidate.IsHq)
            .OrderBy(x => x.Listing.PricePerUnit)
            .ToList();
        if (listings.Count == 0)
            return;

        var perItemBudget = Math.Min(settings.BudgetGil,
            Math.Max(1L, settings.BudgetGil * Math.Clamp(settings.MaxInvestmentPercentPerItem, 1, 100) / 100L));
        long cost = 0;
        var quantity = 0;
        var taken = new List<BuyListingWork>();
        foreach (var listing in listings.Take(MaxListingPackagesPerVariant))
        {
            var payout = (long)floor * listing.Listing.Quantity;
            if (listing.TotalCost >= payout)
                break;
            if (cost + listing.TotalCost > settings.BudgetGil || cost + listing.TotalCost > perItemBudget)
                continue;

            cost += listing.TotalCost;
            quantity += checked((int)listing.Listing.Quantity);
            taken.Add(listing);
        }

        if (quantity <= 0 || cost <= 0)
            return;

        var vendorPayout = (double)floor * quantity;
        var profit = vendorPayout - cost;
        var roi = profit / cost;
        if (profit < settings.MinimumProfitGil || roi < settings.MinimumRoi)
            return;

        // The exit itself is guaranteed and immediate; confidence only describes our acquisition
        // observation, not whether the NPC will pay its fixed buyback price.
        var priceScore = Clamp01(Math.Log10(1 + roi * 20) / Math.Log10(21));
        var profitScore = ScoreProfit(profit, settings.MinimumProfitGil);
        var score = 100 * Clamp01(0.55 * priceScore + 0.40 * profitScore + 0.05);
        score = Math.Max(score, 70);
        var notes = new List<string>
        {
            $"Guaranteed NPC exit: the item can be sold to a vendor for {floor:N0}g/unit immediately.",
            "The calculation includes the buyer tax reported by Universalis for each selected listing.",
            "No Market Board sale velocity is required because the exit does not depend on another player buying the item.",
        };

        output.Add(new BuyOpportunity(
            worldId,
            candidate.Entry.Item,
            candidate.IsHq,
            BuyOpportunityKind.MarketToVendor,
            StrategyLabel(BuyOpportunityKind.MarketToVendor),
            Stars(score),
            score,
            1.0,
            existingQuantity,
            quantity,
            cost,
            cost / (double)quantity,
            floor,
            floor,
            quantity,
            1,
            profit,
            profit,
            roi,
            0,
            0,
            (uint)Math.Max(1, Math.Floor(floor / (1 + ConservativeBuyerTaxRate))),
            0,
            0,
            deep.ListingObservedAtUtc,
            taken.Select(ToAcquisitionLot).ToList(),
            notes,
            DateTimeOffset.UtcNow));
    }

    private void AddRoughCandidate(
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

        var conservativeUnitCost = variant.MinListing * (1.0 + ConservativeBuyerTaxRate);
        var perItemBudget = Math.Min(settings.BudgetGil,
            Math.Max(1L, settings.BudgetGil * Math.Clamp(settings.MaxInvestmentPercentPerItem, 1, 100) / 100L));
        var affordableSingleUnit = conservativeUnitCost > 0 && conservativeUnitCost <= perItemBudget;
        var vendorContested = HasRenewableVendorSupply(item, isHq);

        // Primary discovery is deliberately current-world and sale-backed. This is the pool that
        // produced useful breadth before v1.1.7 and it must not be displaced by thousands of
        // speculative remote/listing-only gaps.
        var netWorldAverage = variant.AverageSalePrice * (1.0 - ScoreCalculator.MarketSellerTaxRate);
        var localMargin = conservativeUnitCost > 0
            ? (netWorldAverage - conservativeUnitCost) / conservativeUnitCost
            : 0;
        var localUnitProfit = Math.Max(0, netWorldAverage - conservativeUnitCost);
        var localMarketSignal = settings.EnableMarketToMarket && !vendorContested &&
                                variant.MinListing > 0 && affordableSingleUnit &&
                                variant.AverageSalePrice > 0 && variant.DailyVelocity > 0.001 &&
                                localMargin >= settings.MinimumRoi * 0.60;

        // Rare-item rescue is intentionally a separate signal. It may use DC/region sales or
        // conservative listing medians only to earn a LIMITED deep-history slot. It never becomes
        // final exit evidence and it requires evidence of demand outside the empty local 4-day window.
        var broaderVelocity = variant.DcDailyVelocity > 0
            ? variant.DcDailyVelocity
            : variant.RegionDailyVelocity;
        var netDiscoveryReference = discovery.Price * (1.0 - ScoreCalculator.MarketSellerTaxRate);
        var discoveryMargin = conservativeUnitCost > 0
            ? (netDiscoveryReference - conservativeUnitCost) / conservativeUnitCost
            : 0;
        var discoveryUnitProfit = Math.Max(0, netDiscoveryReference - conservativeUnitCost);
        var rescueThreshold = Math.Max(settings.MinimumRoi * 0.85, 0.08);
        var rareRescueSignal = settings.EnableMarketToMarket && !vendorContested && !localMarketSignal &&
                               variant.MinListing > 0 && affordableSingleUnit && discovery.Price > 0 &&
                               broaderVelocity > 0.001 && discoveryMargin >= rescueThreshold;

        // Vendor -> Market stays deliberately world-local. Broader prices never manufacture a
        // convenience-arbitrage recommendation on a world where nobody is buying it.
        var vendorMarketMargin = !isHq && item.VendorGilShopPrice is > 0
            ? (netWorldAverage - item.VendorGilShopPrice.Value) / item.VendorGilShopPrice.Value
            : double.NegativeInfinity;
        var vendorMarketSignal = settings.EnableVendorToMarket && !isHq && item.VendorGilShopPrice is > 0 &&
                                 variant.DailyVelocity > 0.001 && vendorMarketMargin >= settings.MinimumRoi * 0.60;

        var vendorFloorSignal = settings.EnableMarketToVendor && item.VendorBuybackPrice > 0 && variant.MinListing > 0 &&
                                variant.MinListing * (1 + ConservativeBuyerTaxRate) < item.VendorBuybackPrice;

        if (!localMarketSignal && !rareRescueSignal && !vendorMarketSignal && !vendorFloorSignal)
            return;

        var localVelocityScore = Clamp01(Math.Log10(1 + Math.Max(0, variant.DailyVelocity)) / Math.Log10(31));
        var rescueVelocityScore = Clamp01(Math.Log10(1 + Math.Max(0, broaderVelocity * 0.5)) / Math.Log10(31));

        var marketRough = 0.0;
        var estimatedUnitProfit = 0.0;
        if (localMarketSignal)
        {
            marketRough = 100 * (
                0.42 * RoughRoiScore(localMargin) +
                0.40 * ScoreProfit(localUnitProfit, settings.MinimumProfitGil) +
                0.18 * localVelocityScore);
            estimatedUnitProfit = localUnitProfit;
        }
        else if (rareRescueSignal)
        {
            // Rescue candidates are intentionally discounted before shortlist competition.
            marketRough = 100 * (
                0.42 * RoughRoiScore(discoveryMargin) +
                0.40 * ScoreProfit(discoveryUnitProfit, settings.MinimumProfitGil) +
                0.18 * rescueVelocityScore) * discovery.Confidence * 0.80;
            estimatedUnitProfit = discoveryUnitProfit;
        }

        var vendorRough = 0.0;
        if (vendorMarketSignal)
        {
            var vendorUnitProfit = Math.Max(0, netWorldAverage - item.VendorGilShopPrice!.Value);
            vendorRough = 100 * (
                0.55 * RoughRoiScore(vendorMarketMargin) +
                0.30 * ScoreProfit(vendorUnitProfit, settings.MinimumProfitGil) +
                0.15 * localVelocityScore);
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
            estimatedUnitProfit,
            discovery.Price,
            discovery.Label,
            discovery.IsWorldRecentSale,
            localMarketSignal,
            rareRescueSignal));
    }

    private static double RoughRoiScore(double margin)
        => Clamp01(Math.Log10(1 + Math.Max(0, margin) * 20) / Math.Log10(21));

    private static DiscoveryReference GetDiscoveryReference(AggregatedVariant variant)
    {
        // Evidence is hierarchical, not "pick the highest number". The v1.1.7 implementation
        // could let an inflated region/listing median override a perfectly good local sale anchor,
        // which polluted the finite deep shortlist and is the core reason full scans could collapse
        // to only one surviving recommendation.
        if (variant.AverageSalePrice > 0)
            return new DiscoveryReference(variant.AverageSalePrice, "world 4-day average sale", 1.00, true, true);

        if (variant.DcAverageSalePrice > 0)
        {
            var price = variant.DcAverageSalePrice * 0.95;
            if (variant.MedianListing > 0)
                price = Math.Min(price, variant.MedianListing * 0.95);
            return new DiscoveryReference(price, "DC recent sale (discovery-only)", 0.88, false, true);
        }

        if (variant.RegionAverageSalePrice > 0)
        {
            var price = variant.RegionAverageSalePrice * 0.90;
            if (variant.MedianListing > 0)
                price = Math.Min(price, variant.MedianListing * 0.92);
            return new DiscoveryReference(price, "region recent sale (discovery-only)", 0.78, false, true);
        }

        if (variant.MedianListing > 0)
            return new DiscoveryReference(variant.MedianListing * 0.82, "world median listing (rare rescue only)", 0.58, false, false);
        if (variant.DcMedianListing > 0)
            return new DiscoveryReference(variant.DcMedianListing * 0.72, "DC median listing (rare rescue only)", 0.48, false, false);
        if (variant.RegionMedianListing > 0)
            return new DiscoveryReference(variant.RegionMedianListing * 0.65, "region median listing (rare rescue only)", 0.40, false, false);

        return new DiscoveryReference(0, "no aggregate reference", 0, false, false);
    }

    private async Task<IReadOnlyList<AggregatedItem>> FetchAggregatedAsync(uint worldId, IReadOnlyList<uint> ids, CancellationToken token)
    {
        var joined = string.Join(',', ids);
        using var response = await http.GetAsync($"api/v2/aggregated/{worldId}/{joined}", token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);

        var result = new List<AggregatedItem>();
        if (!doc.RootElement.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var row in rows.EnumerateArray())
        {
            var itemId = GetUInt(row, "itemId");
            if (itemId == 0)
                continue;
            result.Add(new AggregatedItem(
                itemId,
                ReadAggregatedVariant(row, "nq"),
                ReadAggregatedVariant(row, "hq")));
        }
        return result;
    }

    private async Task<Dictionary<uint, DeepMarketData>> FetchDeepAsync(uint worldId, IReadOnlyList<uint> ids, CancellationToken token)
    {
        var result = ids.ToDictionary(id => id, id => new DeepMarketData(id));
        var joined = string.Join(',', ids);

        using (var currentResponse = await http.GetAsync($"api/v2/{worldId}/{joined}?listings=100&entries=0", token))
        {
            currentResponse.EnsureSuccessStatusCode();
            await using var stream = await currentResponse.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            foreach (var item in ExtractItemObjects(doc.RootElement))
            {
                var itemId = GetUInt(item, "itemID");
                if (itemId == 0 || !result.TryGetValue(itemId, out var deep))
                    continue;

                deep.ListingObservedAtUtc = ParseMillis(item, "lastUploadTime");
                if (item.TryGetProperty("listings", out var listingArray) && listingArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var listing in listingArray.EnumerateArray())
                    {
                        var price = GetUInt(listing, "pricePerUnit");
                        var quantity = GetUInt(listing, "quantity");
                        if (price == 0 || quantity == 0)
                            continue;

                        var observed = deep.ListingObservedAtUtc ?? DateTimeOffset.UtcNow;
                        var marketListing = new MarketListing(
                            itemId,
                            price,
                            quantity,
                            GetBool(listing, "hq"),
                            GetULongFlexible(listing, "listingID"),
                            GetULongFlexible(listing, "retainerID"),
                            GetString(listing, "retainerName"),
                            observed,
                            MarketDataSource.Universalis);
                        var tax = GetUInt(listing, "tax");
                        var totalCost = checked((long)price * quantity + tax);
                        deep.Listings.Add(new BuyListingWork(marketListing, tax, totalCost));
                    }
                }
            }
        }

        var entriesWithinSeconds = 90 * 24 * 60 * 60;
        var statsWithinMilliseconds = entriesWithinSeconds * 1000L;
        using (var historyResponse = await http.GetAsync(
                   $"api/v2/history/{worldId}/{joined}?entriesToReturn=1800&entriesWithin={entriesWithinSeconds}&statsWithin={statsWithinMilliseconds}", token))
        {
            historyResponse.EnsureSuccessStatusCode();
            await using var stream = await historyResponse.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            foreach (var item in ExtractItemObjects(doc.RootElement))
            {
                var itemId = GetUInt(item, "itemID");
                if (itemId == 0 || !result.TryGetValue(itemId, out var deep))
                    continue;

                deep.HistoryObservedAtUtc = DateTimeOffset.UtcNow;
                if (!item.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var sale in entries.EnumerateArray())
                {
                    var timestamp = GetLong(sale, "timestamp");
                    var price = GetUInt(sale, "pricePerUnit");
                    var quantity = GetUInt(sale, "quantity");
                    if (timestamp <= 0 || price == 0 || quantity == 0)
                        continue;
                    deep.Sales.Add(new MarketSale(
                        itemId,
                        price,
                        quantity,
                        GetBool(sale, "hq"),
                        DateTimeOffset.FromUnixTimeSeconds(timestamp),
                        MarketDataSource.Universalis));
                }
            }
        }

        return result;
    }

    private ScanSettings SnapshotSettings()
    {
        // Discovery is intentionally permissive on capital/risk. Profit, ROI, acquisition cost and
        // holding time are findings filters in the UI rather than hidden reasons to omit results.
        var deepLimit = Math.Clamp(configuration.BuyDeepCandidateLimit, 20, 500);
        return new ScanSettings(
            999_999_999,
            0,
            0,
            3650,
            100,
            deepLimit,
            configuration.BuyDiscoveryNameFilter ?? string.Empty,
            configuration.BuyDiscoveryIncludeNq,
            configuration.BuyDiscoveryIncludeHq,
            configuration.BuyIncludeEquipment,
            configuration.BuyUseCategoryFilter,
            configuration.BuyIncludedCategoryIds.ToHashSet(),
            configuration.BuyEnableMarketToMarket,
            configuration.BuyEnableVendorToMarket,
            configuration.BuyEnableMarketToVendor);
    }

    private static IReadOnlyList<string> BuildMarketNotes(
        RoughCandidate candidate,
        SellRating rating,
        int acquiredQuantity,
        long acquisitionCost,
        int existingQuantity,
        IReadOnlyList<BuyListingWork> listings,
        int lastBoughtIndex)
    {
        var notes = new List<string>
        {
            $"Buy {lastBoughtIndex + 1:N0} current listing(s), {acquiredQuantity:N0} unit(s) total, for about {acquisitionCost:N0}g including reported buyer tax.",
            $"Counterfactual exit model suggests {rating.SuggestedPrice?.ToString("N0", CultureInfo.InvariantCulture) ?? "?"}g/unit with {rating.UnitsPerDay:0.##} recent unit(s)/day.",
            $"The exit model uses {rating.SalesSampleCount:N0} recent sale(s) and confidence {rating.Confidence:P0}.",
        };

        if (existingQuantity > 0)
            notes.Add($"You already own {existingQuantity:N0}; stack and liquidation planning uses the combined {existingQuantity + acquiredQuantity:N0}-unit position, but profit counts only the new purchase.");
        if (lastBoughtIndex + 1 < listings.Count)
        {
            var lastBought = listings[lastBoughtIndex].Listing.PricePerUnit;
            var next = listings[lastBoughtIndex + 1].Listing.PricePerUnit;
            if (next > lastBought)
                notes.Add($"After the recommended purchase, the next visible ask rises from {lastBought:N0}g to {next:N0}g/unit.");
        }
        if (candidate.DiscoveryReferencePrice > 0)
        {
            notes.Add($"Universalis broad-pass anchor: {candidate.DiscoveryReferenceLabel} at ~{candidate.DiscoveryReferencePrice:N0}g; local recent velocity was {candidate.Variant.DailyVelocity:0.##} unit(s)/day.");
            if (!candidate.DiscoveryReferenceIsWorldRecentSale)
                notes.Add("That broader/listing-based value was discovery-only. The recommendation itself still had to pass current-world listings, 90-day current-world sales, ROI, profit and holding-time checks.");
        }

        return notes;
    }

    private static BuyOpportunityKind ClassifyMarketStrategy(
        IReadOnlyList<BuyListingWork> listings,
        int lastBoughtIndex,
        int acquiredQuantity,
        int recommendedStack)
    {
        var boughtCount = lastBoughtIndex + 1;
        var averageBoughtStack = acquiredQuantity / (double)Math.Max(1, boughtCount);
        if (recommendedStack > 0 && averageBoughtStack >= recommendedStack * 1.8 && acquiredQuantity >= recommendedStack * 2)
            return BuyOpportunityKind.SplitStack;
        if (boughtCount >= 2 && recommendedStack >= averageBoughtStack * 1.8)
            return BuyOpportunityKind.ConsolidateStack;
        if (lastBoughtIndex + 1 < listings.Count)
        {
            var last = listings[lastBoughtIndex].Listing.PricePerUnit;
            var next = listings[lastBoughtIndex + 1].Listing.PricePerUnit;
            if (last > 0 && next >= last * 1.20)
                return BuyOpportunityKind.UndercutSweep;
        }
        return BuyOpportunityKind.MarketFlip;
    }

    private static string StrategyLabel(BuyOpportunityKind kind) => kind switch
    {
        BuyOpportunityKind.MarketFlip => "Market flip",
        BuyOpportunityKind.UndercutSweep => "Undercut sweep",
        BuyOpportunityKind.SplitStack => "Buy & split",
        BuyOpportunityKind.ConsolidateStack => "Buy & consolidate",
        BuyOpportunityKind.VendorToMarket => "Vendor → Market",
        BuyOpportunityKind.MarketToVendor => "Market → Vendor",
        _ => kind.ToString(),
    };

    private static double? EstimateLiquidationDays(SellRating rating, int resultingPosition)
    {
        if (rating.UnitsPerDay <= 0.01)
            return null;
        return Math.Max(0, rating.EstimatedQueueDays ?? 0) + resultingPosition / rating.UnitsPerDay;
    }

    private static double PriceAdvantage(long cost, int quantity, uint netExit)
    {
        if (cost <= 0 || quantity <= 0 || netExit == 0)
            return 0;
        var costPerUnit = cost / (double)quantity;
        return Clamp01((netExit - costPerUnit) / netExit);
    }

    private static uint CalculateMaximumBuyPrice(uint netExit, double minimumRoi)
    {
        if (netExit == 0)
            return 0;
        var grossAcquisitionCeiling = netExit / Math.Max(1.0, 1.0 + minimumRoi);
        var preTax = grossAcquisitionCeiling / (1.0 + ConservativeBuyerTaxRate);
        return (uint)Math.Max(1, Math.Floor(preTax));
    }

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

    private static double ScoreProfit(double profit, double minimumProfit)
    {
        if (profit <= 0)
            return 0;
        var reference = Math.Max(500, minimumProfit);
        return Clamp01(0.5 + 0.25 * Math.Log10(profit / reference));
    }

    private static int Stars(double score) => score switch
    {
        >= 80 => 5,
        >= 65 => 4,
        >= 50 => 3,
        >= 35 => 2,
        _ => 1,
    };

    private static BuyAcquisitionLot ToAcquisitionLot(BuyListingWork x)
        => new(x.Listing.ListingId, x.Listing.Quantity, x.Listing.PricePerUnit, x.Tax, x.TotalCost);

    private static AggregatedVariant ReadAggregatedVariant(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out var variant) || variant.ValueKind != JsonValueKind.Object)
            return new AggregatedVariant();
        return new AggregatedVariant
        {
            MinListing = GetNestedDouble(variant, "minListing", "world", "price"),
            MedianListing = GetNestedDouble(variant, "medianListing", "world", "price"),
            AverageSalePrice = GetNestedDouble(variant, "averageSalePrice", "world", "price"),
            DailyVelocity = GetNestedDouble(variant, "dailySaleVelocity", "world", "quantity"),
            DcMedianListing = GetNestedDouble(variant, "medianListing", "dc", "price"),
            RegionMedianListing = GetNestedDouble(variant, "medianListing", "region", "price"),
            DcAverageSalePrice = GetNestedDouble(variant, "averageSalePrice", "dc", "price"),
            RegionAverageSalePrice = GetNestedDouble(variant, "averageSalePrice", "region", "price"),
            DcDailyVelocity = GetNestedDouble(variant, "dailySaleVelocity", "dc", "quantity"),
            RegionDailyVelocity = GetNestedDouble(variant, "dailySaleVelocity", "region", "quantity"),
        };
    }

    private static double GetNestedDouble(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return 0;
        }
        return current.ValueKind == JsonValueKind.Number && current.TryGetDouble(out var value) ? value : 0;
    }

    private static IEnumerable<JsonElement> ExtractItemObjects(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            yield break;
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in items.EnumerateObject())
                yield return property.Value;
            yield break;
        }
        if (root.TryGetProperty("itemID", out _))
            yield return root;
    }

    private static IEnumerable<List<uint>> Batch(IEnumerable<uint> ids)
    {
        var bucket = new List<uint>(BatchSize);
        foreach (var id in ids.Distinct())
        {
            bucket.Add(id);
            if (bucket.Count < BatchSize)
                continue;
            yield return bucket;
            bucket = new List<uint>(BatchSize);
        }
        if (bucket.Count > 0)
            yield return bucket;
    }

    private static uint GetUInt(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.TryGetUInt32(out var n) ? n : 0;

    private static long GetLong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

    private static bool GetBool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static ulong GetULongFlexible(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetUInt64(out var numeric))
            return numeric;
        if (v.ValueKind == JsonValueKind.String && ulong.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            return numeric;
        return 0;
    }

    private static DateTimeOffset? ParseMillis(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || !v.TryGetInt64(out var millis) || millis <= 0)
            return null;
        return DateTimeOffset.FromUnixTimeMilliseconds(millis);
    }

    private static int DivideRoundUp(int value, int divisor)
        => divisor <= 0 ? value : (value + divisor - 1) / divisor;

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);

    private sealed record ScanSettings(
        int BudgetGil,
        int MinimumProfitGil,
        double MinimumRoi,
        double MaximumHoldingDays,
        int MaxInvestmentPercentPerItem,
        int DeepCandidateLimit,
        string NameFilter,
        bool IncludeNq,
        bool IncludeHq,
        bool IncludeEquipment,
        bool UseCategoryFilter,
        HashSet<uint> CategoryIds,
        bool EnableMarketToMarket,
        bool EnableVendorToMarket,
        bool EnableMarketToVendor);

    private sealed record RoughCandidate(
        MarketCatalogEntry Entry,
        bool IsHq,
        AggregatedVariant Variant,
        double RoughScore,
        bool GuaranteedVendorSignal,
        double EstimatedUnitProfit,
        double DiscoveryReferencePrice,
        string DiscoveryReferenceLabel,
        bool DiscoveryReferenceIsWorldRecentSale,
        bool LocalMarketSignal,
        bool RareRescueSignal);

    private sealed record DiscoveryReference(
        double Price,
        string Label,
        double Confidence,
        bool IsWorldRecentSale,
        bool IsSaleEvidence);

    private sealed record AggregatedItem(uint ItemId, AggregatedVariant Nq, AggregatedVariant Hq);

    private sealed class AggregatedVariant
    {
        public double MinListing { get; init; }
        public double MedianListing { get; init; }
        public double AverageSalePrice { get; init; }
        public double DailyVelocity { get; init; }
        public double DcMedianListing { get; init; }
        public double RegionMedianListing { get; init; }
        public double DcAverageSalePrice { get; init; }
        public double RegionAverageSalePrice { get; init; }
        public double DcDailyVelocity { get; init; }
        public double RegionDailyVelocity { get; init; }
    }

    private sealed record BuyListingWork(MarketListing Listing, uint Tax, long TotalCost);

    private sealed class DeepMarketData
    {
        public DeepMarketData(uint itemId) => ItemId = itemId;
        public uint ItemId { get; }
        public DateTimeOffset? ListingObservedAtUtc { get; set; }
        public DateTimeOffset? HistoryObservedAtUtc { get; set; }
        public List<BuyListingWork> Listings { get; } = new();
        public List<MarketSale> Sales { get; } = new();
    }
}












