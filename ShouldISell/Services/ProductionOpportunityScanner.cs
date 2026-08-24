using System.Net.Http.Headers;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace ShouldISell.Services;

/// <summary>
/// Shared production-economy engine behind Should I Craft?, Should I Gather? and the unified
/// Opportunities view. Material economics are market-value based: inventory reduces cash required,
/// but owned materials still retain their opportunity cost.
/// </summary>
public sealed class ProductionOpportunityScanner : IDisposable
{
    private const int BatchSize = 100;
    private const int DeepHistoryLimit = 180;
    private const int MaxCraftRecursionDepth = 5;

    private readonly IPlayerState playerState;
    private readonly IDataManager data;
    private readonly GameItemCatalog catalog;
    private readonly InventoryScanner inventory;
    private readonly IPluginLog log;
    private readonly HttpClient http = new();
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private readonly object resultGate = new();
    private CancellationTokenSource? scanCts;
    private List<CraftOpportunity> craftOpportunities = new();
    private List<GatherOpportunity> gatherOpportunities = new();

    public ProductionOpportunityScanner(
        IPlayerState playerState,
        IDataManager data,
        GameItemCatalog catalog,
        InventoryScanner inventory,
        IPluginLog log)
    {
        this.playerState = playerState;
        this.data = data;
        this.catalog = catalog;
        this.inventory = inventory;
        this.log = log;

        http.BaseAddress = new Uri("https://universalis.app/");
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ShouldI", "2.2.0"));
    }

    public bool IsScanning { get; private set; }
    public string Status { get; private set; } = "Ready to scan production opportunities.";
    public DateTimeOffset? LastCompletedUtc { get; private set; }

    public IReadOnlyList<CraftOpportunity> GetCraftOpportunities()
    {
        lock (resultGate)
            return craftOpportunities.ToList();
    }

    public IReadOnlyList<GatherOpportunity> GetGatherOpportunities()
    {
        lock (resultGate)
            return gatherOpportunities.ToList();
    }

    public IReadOnlyList<UnifiedOpportunity> GetUnifiedOpportunities(IEnumerable<BuyOpportunity> buyOpportunities)
    {
        List<CraftOpportunity> crafts;
        List<GatherOpportunity> gathers;
        lock (resultGate)
        {
            crafts = craftOpportunities.ToList();
            gathers = gatherOpportunities.ToList();
        }

        var gatherByItem = gathers
            .Where(x => x.OpportunityScore >= 50)
            .GroupBy(x => x.Item.ItemId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.OpportunityScore).First());
        var output = new List<UnifiedOpportunity>();

        foreach (var buy in buyOpportunities)
        {
            output.Add(new UnifiedOpportunity(
                UnifiedOpportunityKind.Buy,
                buy.Item.ItemId,
                buy.Item.Name,
                buy.Stars,
                buy.OpportunityScore,
                buy.Confidence,
                buy.PotentialProfit,
                buy.Roi,
                null,
                buy.EstimatedLiquidationDays,
                $"Buy {buy.AcquireQuantity:N0} and execute {buy.StrategyLabel.ToLowerInvariant()}.",
                $"Modeled profit {buy.PotentialProfit:N0}g with {buy.UnitsPerDay:0.##} recent unit(s)/day.",
                buy.AnalysedAtUtc));
        }

        foreach (var craft in crafts)
        {
            var gatherHelpers = craft.Ingredients
                .Where(x => gatherByItem.ContainsKey(x.Item.ItemId))
                .Select(x => gatherByItem[x.Item.ItemId])
                .OrderByDescending(x => x.OpportunityScore)
                .ToList();
            var mixed = gatherHelpers.Count > 0;
            var helperText = mixed
                ? $" Strong gather substitute: {gatherHelpers[0].Item.Name} (~{gatherHelpers[0].EstimatedGilPerActiveMinute:N0}g/active min)."
                : string.Empty;

            output.Add(new UnifiedOpportunity(
                mixed ? UnifiedOpportunityKind.CraftAndGather : UnifiedOpportunityKind.Craft,
                craft.Item.ItemId,
                craft.Item.Name,
                craft.Stars,
                craft.OpportunityScore,
                craft.Confidence,
                craft.EconomicProfit,
                craft.Roi,
                craft.EstimatedProfitPerActiveMinute,
                craft.EstimatedLiquidationDays,
                mixed
                    ? $"Craft {craft.ResultQuantity:N0}; gathering one or more inputs is also attractive."
                    : $"Craft {craft.ResultQuantity:N0} with {craft.CrafterName}.",
                $"Economic profit {craft.EconomicProfit:N0}g/craft; cash required about {craft.CashMaterialCost:N0}g.{helperText}",
                craft.AnalysedAtUtc));
        }

        foreach (var gather in gathers)
        {
            output.Add(new UnifiedOpportunity(
                UnifiedOpportunityKind.Gather,
                gather.Item.ItemId,
                gather.Item.Name,
                gather.Stars,
                gather.OpportunityScore,
                gather.Confidence,
                null,
                null,
                gather.EstimatedGilPerActiveMinute,
                null,
                $"Gather {gather.Item.Name} as {gather.GathererName}.",
                $"Modeled {gather.EstimatedGilPerActiveMinuteLow:N0}–{gather.EstimatedGilPerActiveMinuteHigh:N0}g per active minute; market velocity {gather.UnitsPerDay:0.##}/day.",
                gather.AnalysedAtUtc));
        }

        return output
            .OrderByDescending(x => x.OpportunityScore)
            .ThenByDescending(x => x.Confidence)
            .Take(500)
            .ToList();
    }

    public Task ScanCraftAsync(CancellationToken cancellationToken = default)
        => RunScanAsync(scanCraft: true, scanGather: false, cancellationToken);

    public Task ScanGatherAsync(CancellationToken cancellationToken = default)
        => RunScanAsync(scanCraft: false, scanGather: true, cancellationToken);

    public Task ScanAllAsync(CancellationToken cancellationToken = default)
        => RunScanAsync(scanCraft: true, scanGather: true, cancellationToken);

    public void CancelScan()
    {
        scanCts?.Cancel();
        Status = "Cancelling production scan...";
    }

    public void Dispose()
    {
        scanCts?.Cancel();
        scanCts?.Dispose();
        scanGate.Dispose();
        http.Dispose();
    }

    private async Task RunScanAsync(bool scanCraft, bool scanGather, CancellationToken cancellationToken)
    {
        if (!playerState.IsLoaded || !await scanGate.WaitAsync(0, cancellationToken))
            return;

        scanCts?.Dispose();
        scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = scanCts.Token;

        try
        {
            IsScanning = true;
            inventory.ScanLoadedContainers(forceFlush: true);
            var worldId = playerState.CurrentWorld.RowId;
            if (worldId == 0)
            {
                Status = "No current world is available.";
                return;
            }

            if (scanCraft)
                await ScanCraftCoreAsync(worldId, token);
            if (scanGather)
                await ScanGatherCoreAsync(worldId, token);

            LastCompletedUtc = DateTimeOffset.UtcNow;
            Status = scanCraft && scanGather
                ? "Craft + gather opportunity scan complete."
                : scanCraft ? "Craft opportunity scan complete." : "Gather opportunity scan complete.";
        }
        catch (OperationCanceledException)
        {
            Status = "Production scan stopped.";
        }
        catch (Exception ex)
        {
            log.Error(ex, "Production opportunity scan failed.");
            Status = $"Production scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            scanGate.Release();
        }
    }

    private async Task ScanCraftCoreAsync(uint worldId, CancellationToken token)
    {
        Status = "Craft: reading recipes and your crafter levels...";
        var recipes = BuildCraftableRecipes();
        if (recipes.Count == 0)
        {
            lock (resultGate)
                craftOpportunities = new List<CraftOpportunity>();
            return;
        }

        var ids = recipes
            .SelectMany(x => x.Ingredients.Select(i => i.ItemId).Append(x.ResultItemId))
            .Where(catalog.IsMarketable)
            .Distinct()
            .ToList();

        Status = $"Craft: pricing {ids.Count:N0} result/material item(s)...";
        var quotes = await FetchAggregatedQuotesAsync(worldId, ids, "Craft", token);
        EnsureSameWorld(worldId);

        var recipeByResult = recipes
            .GroupBy(x => x.ResultItemId)
            .ToDictionary(x => x.Key, x => x.ToList());
        var owned = inventory.GetKnownOwnedStacks()
            .GroupBy(x => x.ItemId)
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity));
        var resolvedCache = new Dictionary<uint, ResolvedCost>();

        var rough = new List<CraftOpportunity>();
        foreach (var recipe in recipes)
        {
            token.ThrowIfCancellationRequested();
            var opportunity = BuildCraftOpportunity(
                worldId, recipe, quotes, null, recipeByResult, owned, resolvedCache);
            if (opportunity is not null && opportunity.EconomicProfit > 0)
                rough.Add(opportunity);
        }

        var shortlistIds = rough
            .OrderByDescending(x => x.OpportunityScore)
            .ThenByDescending(x => x.EconomicProfit)
            .Select(x => x.Item.ItemId)
            .Distinct()
            .Take(DeepHistoryLimit)
            .ToList();

        Status = $"Craft: validating {shortlistIds.Count:N0} strongest result markets with 90-day sales...";
        var histories = await FetchHistoryStatsAsync(worldId, shortlistIds, token);
        EnsureSameWorld(worldId);

        var final = rough
            .Where(x => shortlistIds.Contains(x.Item.ItemId))
            .Select(x => recipes.First(r => r.RecipeId == x.RecipeId))
            .Select(r => BuildCraftOpportunity(
                worldId,
                r,
                quotes,
                histories.GetValueOrDefault(r.ResultItemId),
                recipeByResult,
                owned,
                resolvedCache))
            .Where(x => x is not null && x.EconomicProfit > 0)
            .Cast<CraftOpportunity>()
            .OrderByDescending(x => x.OpportunityScore)
            .ThenByDescending(x => x.EconomicProfit)
            .Take(300)
            .ToList();

        lock (resultGate)
            craftOpportunities = final;
    }

    private async Task ScanGatherCoreAsync(uint worldId, CancellationToken token)
    {
        Status = "Gather: reading MIN/BTN nodes, levels and timing flags...";
        var sources = BuildGatherSources();
        if (sources.Count == 0)
        {
            lock (resultGate)
                gatherOpportunities = new List<GatherOpportunity>();
            return;
        }

        var ids = sources.Select(x => x.ItemId).Distinct().ToList();
        Status = $"Gather: pricing {ids.Count:N0} gatherable item(s)...";
        var quotes = await FetchAggregatedQuotesAsync(worldId, ids, "Gather", token);
        EnsureSameWorld(worldId);

        var rough = sources
            .Select(x => BuildGatherOpportunity(worldId, x, quotes.GetValueOrDefault(x.ItemId), null))
            .Where(x => x is not null && x.UnitsPerDay > 0.001)
            .Cast<GatherOpportunity>()
            .OrderByDescending(x => x.OpportunityScore)
            .Take(DeepHistoryLimit)
            .ToList();

        var shortlistIds = rough.Select(x => x.Item.ItemId).Distinct().ToList();
        Status = $"Gather: validating {shortlistIds.Count:N0} strongest markets with 90-day sales...";
        var histories = await FetchHistoryStatsAsync(worldId, shortlistIds, token);
        EnsureSameWorld(worldId);

        var sourceByKey = sources.ToDictionary(x => (x.ItemId, x.ClassJobId));
        var final = rough
            .Select(x => sourceByKey[(x.Item.ItemId, x.GathererClassJobId)])
            .Select(x => BuildGatherOpportunity(
                worldId,
                x,
                quotes.GetValueOrDefault(x.ItemId),
                histories.GetValueOrDefault(x.ItemId)))
            .Where(x => x is not null && x.UnitsPerDay > 0.001)
            .Cast<GatherOpportunity>()
            .OrderByDescending(x => x.OpportunityScore)
            .ThenByDescending(x => x.EstimatedGilPerActiveMinute)
            .Take(300)
            .ToList();

        lock (resultGate)
            gatherOpportunities = final;
    }

    private List<RecipeWork> BuildCraftableRecipes()
    {
        var classJobs = data.GetExcelSheet<ClassJob>();
        var output = new List<RecipeWork>();
        foreach (var recipe in data.GetExcelSheet<Recipe>())
        {
            var resultId = recipe.ItemResult.RowId;
            if (resultId == 0 || !catalog.IsMarketable(resultId))
                continue;

            var classJobId = recipe.CraftType.RowId + 8;
            if (!classJobs.TryGetRow(classJobId, out var classJob))
                continue;
            var playerLevel = playerState.GetClassJobLevel(classJob);
            var requiredLevel = (int)recipe.RecipeLevelTable.Value.ClassJobLevel;
            if (playerLevel < requiredLevel)
                continue;

            var ingredients = new List<RecipeIngredientWork>();
            for (var i = 0; i < 8; i++)
            {
                var itemId = recipe.Ingredient[i].RowId;
                var amount = (int)recipe.AmountIngredient[i];
                if (itemId != 0 && amount > 0)
                    ingredients.Add(new RecipeIngredientWork(itemId, amount));
            }
            if (ingredients.Count == 0)
                continue;

            output.Add(new RecipeWork(
                recipe.RowId,
                resultId,
                Math.Max(1, (int)recipe.AmountResult),
                classJobId,
                classJob.Name.ToString(),
                requiredLevel,
                playerLevel,
                recipe.CanQuickSynth,
                ingredients));
        }

        return output;
    }

    private List<GatherSourceWork> BuildGatherSources()
    {
        var gatheringItems = data.GetExcelSheet<GatheringItem>()
            .Where(x => x.RowId != 0 && x.Item.RowId != 0)
            .ToDictionary(x => x.RowId);
        var pointTransient = data.GetExcelSheet<GatheringPointTransient>();
        var pointsByBase = new Dictionary<uint, List<GatherPointWork>>();

        foreach (var point in data.GetExcelSheet<GatheringPoint>())
        {
            var baseId = point.GatheringPointBase.RowId;
            if (baseId == 0)
                continue;

            var place = point.PlaceName.Value.Name.ToString();
            if (string.IsNullOrWhiteSpace(place))
                place = point.TerritoryType.Value.PlaceName.Value.Name.ToString();
            if (string.IsNullOrWhiteSpace(place))
                place = $"Territory #{point.TerritoryType.RowId}";

            var timed = false;
            if (pointTransient.TryGetRow(point.RowId, out var transient))
            {
                timed = transient.GatheringRarePopTimeTable.RowId != 0 ||
                        transient.EphemeralStartTime != 0 || transient.EphemeralEndTime != 0;
            }

            if (!pointsByBase.TryGetValue(baseId, out var list))
                pointsByBase[baseId] = list = new List<GatherPointWork>();
            list.Add(new GatherPointWork(place, timed));
        }

        var classJobs = data.GetExcelSheet<ClassJob>();
        var builders = new Dictionary<(uint ItemId, uint ClassJobId), GatherSourceBuilder>();
        foreach (var pointBase in data.GetExcelSheet<GatheringPointBase>())
        {
            var typeName = pointBase.GatheringType.Value.Name.ToString();
            var classJobId = GathererClassJobId(typeName);
            if (classJobId == 0 || !classJobs.TryGetRow(classJobId, out var classJob))
                continue; // Rod fishing/spearfishing deliberately remain outside the reliable v1 ranker.

            var playerLevel = playerState.GetClassJobLevel(classJob);
            var requiredLevel = (int)pointBase.GatheringLevel;
            if (playerLevel < requiredLevel)
                continue;

            pointsByBase.TryGetValue(pointBase.RowId, out var points);
            for (var i = 0; i < 8; i++)
            {
                var gatheringRowId = pointBase.Item[i].RowId;
                if (gatheringRowId == 0 || !gatheringItems.TryGetValue(gatheringRowId, out var gatheringItem))
                    continue;
                var itemId = gatheringItem.Item.RowId;
                if (itemId == 0 || !catalog.IsMarketable(itemId))
                    continue;

                var key = (itemId, classJobId);
                if (!builders.TryGetValue(key, out var builder))
                {
                    builder = new GatherSourceBuilder(
                        itemId,
                        classJobId,
                        classJob.Name.ToString(),
                        typeName,
                        requiredLevel,
                        playerLevel,
                        gatheringItem.IsHidden);
                    builders[key] = builder;
                }
                else
                {
                    builder.RequiredLevel = Math.Min(builder.RequiredLevel, requiredLevel);
                    builder.IsHidden |= gatheringItem.IsHidden;
                }

                if (points is null)
                    continue;
                foreach (var point in points)
                {
                    builder.Locations.Add(point.PlaceName);
                    builder.IsTimed |= point.IsTimed;
                }
            }
        }

        return builders.Values
            .Select(x => new GatherSourceWork(
                x.ItemId,
                x.ClassJobId,
                x.ClassJobName,
                x.GatheringType,
                x.RequiredLevel,
                x.PlayerLevel,
                x.Locations.OrderBy(y => y, StringComparer.CurrentCultureIgnoreCase).ToList(),
                x.IsTimed,
                x.IsHidden))
            .ToList();
    }

    private CraftOpportunity? BuildCraftOpportunity(
        uint worldId,
        RecipeWork recipe,
        IReadOnlyDictionary<uint, MarketQuote> quotes,
        HistoryStats? history,
        IReadOnlyDictionary<uint, List<RecipeWork>> recipeByResult,
        IReadOnlyDictionary<uint, int> owned,
        Dictionary<uint, ResolvedCost> resolvedCache)
    {
        if (!quotes.TryGetValue(recipe.ResultItemId, out var resultQuote))
            return null;
        var salePrice = GetRealisticSalePrice(resultQuote, history);
        if (salePrice <= 0)
            return null;

        var decisions = new List<CraftIngredientDecision>();
        var economicCost = 0.0;
        var cashCost = 0.0;
        foreach (var ingredient in recipe.Ingredients)
        {
            var resolved = ResolveUnitCost(
                ingredient.ItemId, quotes, recipeByResult, resolvedCache, new HashSet<uint>(), 0);
            if (!double.IsFinite(resolved.UnitCost) || resolved.UnitCost <= 0)
                return null;

            var ownedQty = Math.Min(ingredient.Quantity, owned.GetValueOrDefault(ingredient.ItemId));
            var ingredientEconomic = resolved.UnitCost * ingredient.Quantity;
            var ingredientCash = resolved.UnitCost * Math.Max(0, ingredient.Quantity - ownedQty);
            economicCost += ingredientEconomic;
            cashCost += ingredientCash;

            quotes.TryGetValue(ingredient.ItemId, out var market);
            decisions.Add(new CraftIngredientDecision(
                catalog.Get(ingredient.ItemId),
                ingredient.Quantity,
                ownedQty,
                resolved.Route,
                market?.MinListing > 0 ? market.MinListing : null,
                resolved.UnitCost,
                ingredientEconomic,
                ingredientCash,
                resolved.Reason));
        }

        var gross = salePrice * recipe.ResultQuantity;
        var net = gross * (1.0 - ScoreCalculator.MarketSellerTaxRate);
        var profit = net - economicCost;
        var cashProfit = net - cashCost;
        var roi = economicCost > 0 ? profit / economicCost : 0;
        var velocity = history?.UnitsPerDay > 0 ? history.UnitsPerDay : resultQuote.DailyVelocity;
        var liquidation = velocity > 0.001 ? recipe.ResultQuantity / velocity : (double?)null;
        var volatility = history?.Volatility;
        var samples = history?.SampleCount ?? 0;
        var lastSale = history?.LastSaleUtc;
        var confidence = CraftConfidence(history, resultQuote, decisions);
        var stability = volatility is null ? 0.55 : Clamp01(1 - volatility.Value);
        var score = ScoreCraft(roi, profit, liquidation, velocity, stability, confidence);
        var activeMinutes = recipe.CanQuickSynth ? 0.20 : 0.75;
        var perActiveMinute = activeMinutes > 0 ? profit / activeMinutes : (double?)null;

        var notes = new List<string>
        {
            $"Economic material cost is {economicCost:N0}g; owned materials still count at opportunity cost.",
            $"Cash required is about {cashCost:N0}g after currently known direct ingredients already owned.",
            $"Expected Market Board proceeds use a conservative NQ sale reference of about {salePrice:N0}g/unit before seller tax.",
            recipe.CanQuickSynth
                ? "Active-time estimate uses a generic quick-synthesis baseline and is intentionally low-confidence."
                : "Active-time estimate uses a generic manual/macro craft baseline and is intentionally low-confidence.",
        };
        if (history is not null)
            notes.Add($"90-day validation: {history.SampleCount:N0} sale event(s), {history.UnitsPerDay:0.##} unit(s)/day, price CV {history.Volatility:P0}.");
        if (decisions.Any(x => x.Route == ProductionAcquisitionRoute.Craft))
            notes.Add("One or more intermediates are cheaper to craft recursively than to buy at the current broad-market ask.");

        return new CraftOpportunity(
            worldId,
            recipe.RecipeId,
            catalog.Get(recipe.ResultItemId),
            recipe.ClassJobId,
            recipe.ClassJobName,
            recipe.RequiredLevel,
            recipe.PlayerLevel,
            recipe.ResultQuantity,
            recipe.CanQuickSynth,
            Stars(score),
            score,
            confidence,
            gross,
            net,
            economicCost,
            cashCost,
            profit,
            cashProfit,
            roi,
            velocity,
            liquidation,
            activeMinutes,
            perActiveMinute,
            volatility,
            samples,
            lastSale,
            decisions,
            notes,
            DateTimeOffset.UtcNow);
    }

    private GatherOpportunity? BuildGatherOpportunity(
        uint worldId,
        GatherSourceWork source,
        MarketQuote? quote,
        HistoryStats? history)
    {
        if (quote is null)
            return null;
        var salePrice = GetRealisticSalePrice(quote, history);
        if (salePrice <= 0)
            return null;

        var velocity = history?.UnitsPerDay > 0 ? history.UnitsPerDay : quote.DailyVelocity;
        if (velocity <= 0)
            return null;

        // v1 intentionally models an interval rather than pretending exact gathering throughput.
        // Personal observed-session telemetry can replace this generic baseline later.
        var baseRate = source.IsHidden ? 7.5 : 10.5;
        if (source.IsTimed)
            baseRate *= 1.08;
        var levelHeadroom = Math.Max(0, source.PlayerLevel - source.RequiredLevel);
        baseRate *= 1.0 + Math.Min(0.20, levelHeadroom / 500.0);
        var lowRate = baseRate * 0.65;
        var highRate = baseRate * 1.35;
        var netUnitValue = salePrice * (1.0 - ScoreCalculator.MarketSellerTaxRate);
        var gilPerMinute = netUnitValue * baseRate;
        var gilPerMinuteLow = netUnitValue * lowRate;
        var gilPerMinuteHigh = netUnitValue * highRate;

        var volatility = history?.Volatility;
        var samples = history?.SampleCount ?? 0;
        var lastSale = history?.LastSaleUtc;
        var marketConfidence = MarketConfidence(history, quote);
        var effortConfidence = source.IsHidden ? 0.42 : source.IsTimed ? 0.50 : 0.56;
        var confidence = Clamp01(marketConfidence * 0.72 + effortConfidence * 0.28);
        var stability = volatility is null ? 0.55 : Clamp01(1 - volatility.Value);
        var score = ScoreGather(gilPerMinute, velocity, stability, confidence, source.IsTimed, source.IsHidden);

        var notes = new List<string>
        {
            $"Generic active-yield model: {lowRate:0.0}–{highRate:0.0} item(s)/active minute; midpoint {baseRate:0.0}.",
            "Travel, GP rotation, gear, node-to-node movement and player execution are not yet learned personally, so the effort estimate is a range.",
            "Gil/minute uses the modeled active gathering time only. Waiting for a timed node is shown as availability friction, not charged as active play time.",
        };
        if (source.IsTimed)
            notes.Add("Timed/ephemeral availability detected in game data; this opportunity can be excellent while active but is not always immediately available.");
        if (source.IsHidden)
            notes.Add("Hidden-node behavior lowers the generic throughput estimate and confidence.");
        if (history is not null)
            notes.Add($"90-day validation: {history.SampleCount:N0} sale event(s), {history.UnitsPerDay:0.##} unit(s)/day, price CV {history.Volatility:P0}.");

        return new GatherOpportunity(
            worldId,
            catalog.Get(source.ItemId),
            source.ClassJobId,
            source.ClassJobName,
            source.RequiredLevel,
            source.PlayerLevel,
            source.GatheringType,
            source.Locations,
            source.IsTimed,
            source.IsHidden,
            Stars(score),
            score,
            confidence,
            salePrice,
            velocity,
            baseRate,
            lowRate,
            highRate,
            gilPerMinute,
            gilPerMinuteLow,
            gilPerMinuteHigh,
            volatility,
            samples,
            lastSale,
            notes,
            DateTimeOffset.UtcNow);
    }

    private ResolvedCost ResolveUnitCost(
        uint itemId,
        IReadOnlyDictionary<uint, MarketQuote> quotes,
        IReadOnlyDictionary<uint, List<RecipeWork>> recipeByResult,
        Dictionary<uint, ResolvedCost> cache,
        HashSet<uint> visiting,
        int depth)
    {
        if (cache.TryGetValue(itemId, out var cached))
            return cached;
        if (depth >= MaxCraftRecursionDepth || !visiting.Add(itemId))
            return DirectCost(itemId, quotes);

        var best = DirectCost(itemId, quotes);
        if (recipeByResult.TryGetValue(itemId, out var recipes))
        {
            foreach (var recipe in recipes)
            {
                var total = 0.0;
                var valid = true;
                foreach (var ingredient in recipe.Ingredients)
                {
                    var child = ResolveUnitCost(
                        ingredient.ItemId, quotes, recipeByResult, cache, visiting, depth + 1);
                    if (!double.IsFinite(child.UnitCost) || child.UnitCost <= 0)
                    {
                        valid = false;
                        break;
                    }
                    total += child.UnitCost * ingredient.Quantity;
                }

                if (!valid)
                    continue;
                var unit = total / Math.Max(1, recipe.ResultQuantity);
                if (unit > 0 && unit < best.UnitCost)
                {
                    best = new ResolvedCost(
                        ProductionAcquisitionRoute.Craft,
                        unit,
                        $"Craft recursively (~{unit:N0}g material value/unit) instead of buying the current ask.");
                }
            }
        }

        visiting.Remove(itemId);
        cache[itemId] = best;
        return best;
    }

    private ResolvedCost DirectCost(uint itemId, IReadOnlyDictionary<uint, MarketQuote> quotes)
    {
        var best = new ResolvedCost(ProductionAcquisitionRoute.Unavailable, double.PositiveInfinity, "No priced acquisition route was found.");
        if (quotes.TryGetValue(itemId, out var quote) && quote.MinListing > 0)
        {
            best = new ResolvedCost(
                ProductionAcquisitionRoute.MarketBoard,
                quote.MinListing,
                $"Buy at the current broad-pass minimum ask (~{quote.MinListing:N0}g/unit).");
        }

        var vendor = catalog.Get(itemId).VendorGilShopPrice;
        if (vendor is > 0 && vendor.Value < best.UnitCost)
        {
            best = new ResolvedCost(
                ProductionAcquisitionRoute.Vendor,
                vendor.Value,
                $"Buy from a verified normal gil vendor for {vendor.Value:N0}g/unit.");
        }
        return best;
    }

    private async Task<Dictionary<uint, MarketQuote>> FetchAggregatedQuotesAsync(
        uint worldId,
        IReadOnlyList<uint> ids,
        string lane,
        CancellationToken token)
    {
        var result = new Dictionary<uint, MarketQuote>();
        var scanned = 0;
        foreach (var batch in Batch(ids))
        {
            token.ThrowIfCancellationRequested();
            var joined = string.Join(',', batch);
            using var response = await http.GetAsync($"api/v2/aggregated/{worldId}/{joined}", token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            if (doc.RootElement.TryGetProperty("results", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    var itemId = GetUInt(row, "itemId");
                    if (itemId == 0 || !row.TryGetProperty("nq", out var nq) || nq.ValueKind != JsonValueKind.Object)
                        continue;
                    result[itemId] = new MarketQuote(
                        GetNestedDouble(nq, "minListing", "world", "price"),
                        GetNestedDouble(nq, "medianListing", "world", "price"),
                        GetNestedDouble(nq, "averageSalePrice", "world", "price"),
                        GetNestedDouble(nq, "dailySaleVelocity", "world", "quantity"));
                }
            }

            scanned += batch.Count;
            Status = $"{lane}: market discovery {Math.Min(scanned, ids.Count):N0}/{ids.Count:N0} item(s)...";
            await Task.Delay(80, token);
        }
        return result;
    }

    private async Task<Dictionary<uint, HistoryStats>> FetchHistoryStatsAsync(
        uint worldId,
        IReadOnlyList<uint> ids,
        CancellationToken token)
    {
        var result = new Dictionary<uint, List<HistorySaleWork>>();
        var entriesWithin = 90 * 24 * 60 * 60;
        foreach (var batch in Batch(ids))
        {
            if (batch.Count == 0)
                continue;
            var joined = string.Join(',', batch);
            using var response = await http.GetAsync(
                $"api/v2/history/{worldId}/{joined}?entries=1800&entriesWithin={entriesWithin}&statsWithin={entriesWithin}", token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            foreach (var item in ExtractItemObjects(doc.RootElement))
            {
                var itemId = GetUInt(item, "itemID");
                if (itemId == 0 || !item.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    continue;
                if (!result.TryGetValue(itemId, out var sales))
                    result[itemId] = sales = new List<HistorySaleWork>();
                foreach (var entry in entries.EnumerateArray())
                {
                    if (GetBool(entry, "hq"))
                        continue; // v1 production model is explicitly NQ-to-NQ for deterministic comparisons.
                    var price = GetUInt(entry, "pricePerUnit");
                    var quantity = GetUInt(entry, "quantity");
                    var timestamp = GetLong(entry, "timestamp");
                    if (price == 0 || quantity == 0 || timestamp <= 0)
                        continue;
                    sales.Add(new HistorySaleWork(price, quantity, DateTimeOffset.FromUnixTimeSeconds(timestamp)));
                }
            }
            await Task.Delay(80, token);
        }

        return result.ToDictionary(x => x.Key, x => CalculateHistoryStats(x.Value));
    }

    private static HistoryStats CalculateHistoryStats(IReadOnlyList<HistorySaleWork> sales)
    {
        if (sales.Count == 0)
            return new HistoryStats(0, 0, 0, 0, null);

        var prices = sales.Select(x => (double)x.PricePerUnit).Order().ToArray();
        var median = Percentile(prices, 0.50);
        var lowerQuartile = Percentile(prices, 0.25);
        var conservative = 0.70 * median + 0.30 * lowerQuartile;
        var mean = prices.Average();
        var variance = prices.Select(x => (x - mean) * (x - mean)).Average();
        var volatility = mean > 0 ? Math.Sqrt(variance) / mean : 1;
        var last = sales.Max(x => x.SoldAtUtc);
        var first = sales.Min(x => x.SoldAtUtc);
        var observedDays = Math.Clamp((last - first).TotalDays, 7, 90);
        var unitsPerDay = sales.Sum(x => (long)x.Quantity) / observedDays;
        return new HistoryStats(conservative, unitsPerDay, volatility, sales.Count, last);
    }

    private static double GetRealisticSalePrice(MarketQuote quote, HistoryStats? history)
    {
        var reference = history is { ConservativePrice: > 0 }
            ? history.ConservativePrice
            : quote.AverageSalePrice;
        if (reference <= 0)
            return 0;
        if (quote.MinListing > 0)
            reference = Math.Min(reference, quote.MinListing * 1.05);
        return Math.Max(0, reference);
    }

    private static double CraftConfidence(
        HistoryStats? history,
        MarketQuote resultQuote,
        IReadOnlyCollection<CraftIngredientDecision> ingredients)
    {
        var market = MarketConfidence(history, resultQuote);
        var priced = ingredients.Count == 0 ? 0 : ingredients.Count(x => x.EconomicUnitCost > 0) / (double)ingredients.Count;
        var recursivePenalty = ingredients.Any(x => x.Route == ProductionAcquisitionRoute.Craft) ? 0.92 : 1.0;
        return Clamp01((0.78 * market + 0.22 * priced) * recursivePenalty);
    }

    private static double MarketConfidence(HistoryStats? history, MarketQuote quote)
    {
        if (history is null || history.SampleCount == 0)
            return quote.AverageSalePrice > 0 && quote.DailyVelocity > 0 ? 0.48 : 0.25;
        var sample = Clamp01(Math.Log10(1 + history.SampleCount) / Math.Log10(101));
        var freshness = history.LastSaleUtc is null
            ? 0
            : Math.Exp(-Math.Max(0, (DateTimeOffset.UtcNow - history.LastSaleUtc.Value).TotalDays) / 14.0);
        return Clamp01(0.20 + 0.52 * sample + 0.28 * freshness);
    }

    private static double ScoreCraft(
        double roi,
        double profit,
        double? liquidationDays,
        double unitsPerDay,
        double stability,
        double confidence)
    {
        var roiScore = Clamp01(Math.Log10(1 + Math.Max(0, roi) * 12) / Math.Log10(13));
        var profitScore = profit <= 0 ? 0 : Clamp01(Math.Log10(1 + profit) / Math.Log10(1_000_001));
        var liquidity = liquidationDays is null
            ? 0.15
            : Math.Exp(-Math.Max(0, liquidationDays.Value) / 14.0);
        var demand = Clamp01(Math.Log10(1 + Math.Max(0, unitsPerDay)) / Math.Log10(101));
        return 100 * Clamp01(
            0.29 * roiScore +
            0.25 * profitScore +
            0.18 * liquidity +
            0.08 * demand +
            0.08 * stability +
            0.12 * confidence);
    }

    private static double ScoreGather(
        double gilPerMinute,
        double unitsPerDay,
        double stability,
        double confidence,
        bool timed,
        bool hidden)
    {
        var value = Clamp01(Math.Log10(1 + Math.Max(0, gilPerMinute) / 500.0) / Math.Log10(101));
        var demand = Clamp01(Math.Log10(1 + Math.Max(0, unitsPerDay)) / Math.Log10(101));
        var convenience = timed ? 0.62 : hidden ? 0.72 : 1.0;
        return 100 * Clamp01(
            0.46 * value +
            0.22 * demand +
            0.10 * stability +
            0.14 * confidence +
            0.08 * convenience);
    }

    private static int Stars(double score) => score switch
    {
        >= 80 => 5,
        >= 65 => 4,
        >= 50 => 3,
        >= 35 => 2,
        _ => 1,
    };

    private static uint GathererClassJobId(string gatheringType)
    {
        var name = gatheringType.ToLowerInvariant();
        if (name.Contains("mining") || name.Contains("quarrying"))
            return 16;
        if (name.Contains("logging") || name.Contains("harvesting"))
            return 17;
        return 0;
    }

    private void EnsureSameWorld(uint worldId)
    {
        if (!playerState.IsLoaded || playerState.CurrentWorld.RowId != worldId)
            throw new OperationCanceledException("World changed during production scan; stale results were discarded.");
    }

    private static IEnumerable<List<uint>> Batch(IEnumerable<uint> ids)
    {
        var bucket = new List<uint>(BatchSize);
        foreach (var id in ids.Where(x => x != 0).Distinct())
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

    private static uint GetUInt(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.TryGetUInt32(out var n) ? n : 0;
    private static long GetLong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
    private static bool GetBool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static double GetNestedDouble(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return 0;
        }
        return current.TryGetDouble(out var value) ? value : 0;
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
            return 0;
        if (sorted.Count == 1)
            return sorted[0];
        var position = (sorted.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return sorted[lower];
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);

    private sealed record RecipeIngredientWork(uint ItemId, int Quantity);
    private sealed record RecipeWork(
        uint RecipeId,
        uint ResultItemId,
        int ResultQuantity,
        uint ClassJobId,
        string ClassJobName,
        int RequiredLevel,
        int PlayerLevel,
        bool CanQuickSynth,
        IReadOnlyList<RecipeIngredientWork> Ingredients);

    private sealed record GatherPointWork(string PlaceName, bool IsTimed);
    private sealed class GatherSourceBuilder
    {
        public GatherSourceBuilder(
            uint itemId,
            uint classJobId,
            string classJobName,
            string gatheringType,
            int requiredLevel,
            int playerLevel,
            bool isHidden)
        {
            ItemId = itemId;
            ClassJobId = classJobId;
            ClassJobName = classJobName;
            GatheringType = gatheringType;
            RequiredLevel = requiredLevel;
            PlayerLevel = playerLevel;
            IsHidden = isHidden;
        }

        public uint ItemId { get; }
        public uint ClassJobId { get; }
        public string ClassJobName { get; }
        public string GatheringType { get; }
        public int RequiredLevel { get; set; }
        public int PlayerLevel { get; }
        public HashSet<string> Locations { get; } = new(StringComparer.CurrentCultureIgnoreCase);
        public bool IsTimed { get; set; }
        public bool IsHidden { get; set; }
    }

    private sealed record GatherSourceWork(
        uint ItemId,
        uint ClassJobId,
        string ClassJobName,
        string GatheringType,
        int RequiredLevel,
        int PlayerLevel,
        IReadOnlyList<string> Locations,
        bool IsTimed,
        bool IsHidden);

    private sealed record MarketQuote(double MinListing, double MedianListing, double AverageSalePrice, double DailyVelocity);
    private sealed record ResolvedCost(ProductionAcquisitionRoute Route, double UnitCost, string Reason);
    private sealed record HistorySaleWork(uint PricePerUnit, uint Quantity, DateTimeOffset SoldAtUtc);
    private sealed record HistoryStats(
        double ConservativePrice,
        double UnitsPerDay,
        double Volatility,
        int SampleCount,
        DateTimeOffset? LastSaleUtc);
}
