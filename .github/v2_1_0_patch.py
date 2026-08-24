from pathlib import Path
import re

ROOT = Path('.')

def read(path):
    return (ROOT / path).read_text(encoding='utf-8')

def write(path, text):
    (ROOT / path).write_text(text, encoding='utf-8')

def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected exactly one match, got {count}')
    return text.replace(old, new, 1)

def regex_once(text, pattern, repl, label, flags=re.S):
    out, count = re.subn(pattern, repl, text, count=1, flags=flags)
    if count != 1:
        raise RuntimeError(f'{label}: expected exactly one regex match, got {count}')
    return out

# --- Version -----------------------------------------------------------------
path = 'ShouldISell/ShouldISell.csproj'
text = read(path)
text = re.sub(r'<Version>[^<]+</Version>', '<Version>2.1.0.0</Version>', text, count=1)
write(path, text)

# --- Purchase source model ----------------------------------------------------
path = 'ShouldISell/TradingModels.cs'
text = read(path)
text = replace_once(text,
'''    MarketToVendor,\n}\n\npublic sealed record BuyAcquisitionLot(''',
'''    MarketToVendor,\n}\n\npublic enum PurchaseSourceKind\n{\n    MarketBoard,\n    VendorManual,\n}\n\npublic sealed record BuyAcquisitionLot(''',
'purchase source enum')
text = replace_once(text,
'''    double? PredictedPackageProfit,\n    DateTimeOffset? PredictionObservedAtUtc);''',
'''    double? PredictedPackageProfit,\n    DateTimeOffset? PredictionObservedAtUtc,\n    PurchaseSourceKind SourceKind = PurchaseSourceKind.MarketBoard);''',
'personal purchase source field')
write(path, text)

# --- Trader store: source-aware purchases + safe wallet correlation -----------
path = 'ShouldISell/Services/TraderStore.cs'
text = read(path)
text = replace_once(text,
'''                x.TotalCost == purchase.TotalCost &&\n                ((purchase.ListingId != 0 && x.ListingId == purchase.ListingId) ||''',
'''                x.TotalCost == purchase.TotalCost &&\n                x.SourceKind == purchase.SourceKind &&\n                ((purchase.ListingId != 0 && x.ListingId == purchase.ListingId) ||''',
'purchase duplicate source')
text = replace_once(text,
'''            document.Purchases = document.Purchases\n                .OrderByDescending(x => x.PurchasedAtUtc)\n                .Take(25_000)\n                .ToList();\n            dirty = true;''',
'''            document.Purchases = document.Purchases\n                .OrderByDescending(x => x.PurchasedAtUtc)\n                .Take(25_000)\n                .ToList();\n            document.Version = Math.Max(document.Version, 3);\n            dirty = true;''',
'purchase document version')
insert = r'''
    public bool TryClassifyRecentMatchingOutflow(
        ulong characterContentId,
        long totalCost,
        GilFlowCategory category,
        string source,
        string note,
        TimeSpan? window = null,
        bool flush = true)
    {
        if (characterContentId == 0 || totalCost <= 0)
            return false;

        var now = DateTimeOffset.UtcNow;
        var tolerance = window ?? TimeSpan.FromMinutes(5);
        var changed = false;
        lock (gate)
        {
            var match = document.GilFlows
                .Select((flow, index) => (flow, index))
                .Where(x => x.flow.CharacterContentId == characterContentId &&
                            x.flow.Amount == -totalCost &&
                            x.flow.Category == GilFlowCategory.Unclassified &&
                            (now - x.flow.AtUtc).Duration() <= tolerance)
                .OrderBy(x => (now - x.flow.AtUtc).Duration())
                .FirstOrDefault();

            if (match.flow is null)
                return false;

            document.GilFlows[match.index] = match.flow with
            {
                Category = category,
                Source = string.IsNullOrWhiteSpace(source) ? match.flow.Source : source,
                AutoClassified = false,
                Note = note,
            };
            document.Version = Math.Max(document.Version, 3);
            dirty = true;
            changed = true;
        }

        if (changed && flush)
            Flush();
        return changed;
    }

'''
text = replace_once(text,
'''    public string GetPurchaseKey(PersonalPurchase purchase)''',
insert + '''    public string GetPurchaseKey(PersonalPurchase purchase)''',
'insert wallet matcher')
text = text.replace('public int Version { get; set; } = 2;', 'public int Version { get; set; } = 3;')
write(path, text)

# --- Buy scanner: independent Market Board and Vendor lanes -------------------
path = 'ShouldISell/Services/BuyOpportunityScanner.cs'
text = read(path)
text = replace_once(text,
'''namespace ShouldISell.Services;\n\n/// <summary>''',
'''namespace ShouldISell.Services;\n\npublic enum BuyScanLane\n{\n    MarketBoard,\n    Vendor,\n}\n\n/// <summary>''',
'buy scan lane enum')
text = text.replace('new ProductInfoHeaderValue("ShouldI", "2.0.1")', 'new ProductInfoHeaderValue("ShouldI", "2.1.0")')
text = replace_once(text,
'''    private CancellationTokenSource? scanCts;\n    private List<BuyOpportunity> opportunities = new();''',
'''    private CancellationTokenSource? scanCts;\n    private List<BuyOpportunity> marketOpportunities = new();\n    private List<BuyOpportunity> vendorOpportunities = new();''',
'separate scanner result stores')
text = replace_once(text,
'''    public DateTimeOffset? LastCompletedUtc { get; private set; }\n\n    public IReadOnlyList<BuyOpportunity> GetOpportunities()\n    {\n        lock (resultGate)\n            return opportunities.ToList();\n    }''',
'''    public DateTimeOffset? LastCompletedUtc { get; private set; }\n    public DateTimeOffset? LastMarketCompletedUtc { get; private set; }\n    public DateTimeOffset? LastVendorCompletedUtc { get; private set; }\n    public BuyScanLane? ActiveLane { get; private set; }\n\n    public IReadOnlyList<BuyOpportunity> GetOpportunities()\n    {\n        lock (resultGate)\n            return marketOpportunities.Concat(vendorOpportunities)\n                .OrderByDescending(x => x.OpportunityScore)\n                .ThenByDescending(x => x.RiskAdjustedProfit)\n                .ToList();\n    }\n\n    public IReadOnlyList<BuyOpportunity> GetMarketOpportunities()\n    {\n        lock (resultGate)\n            return marketOpportunities.ToList();\n    }\n\n    public IReadOnlyList<BuyOpportunity> GetVendorOpportunities()\n    {\n        lock (resultGate)\n            return vendorOpportunities.ToList();\n    }''',
'scanner getters')
text = replace_once(text,
'''    public async Task ScanAsync(CancellationToken cancellationToken = default)\n    {\n        if (!playerState.IsLoaded || !await scanGate.WaitAsync(0, cancellationToken))''',
'''    public Task ScanAsync(CancellationToken cancellationToken = default)\n        => ScanMarketAsync(cancellationToken);\n\n    public Task ScanMarketAsync(CancellationToken cancellationToken = default)\n        => ScanInternalAsync(BuyScanLane.MarketBoard, cancellationToken);\n\n    public Task ScanVendorAsync(CancellationToken cancellationToken = default)\n        => ScanInternalAsync(BuyScanLane.Vendor, cancellationToken);\n\n    private async Task ScanInternalAsync(BuyScanLane lane, CancellationToken cancellationToken = default)\n    {\n        if (!playerState.IsLoaded || !await scanGate.WaitAsync(0, cancellationToken))''',
'scan wrappers')
text = replace_once(text,
'''            IsScanning = true;\n            BroadItemsScanned = 0;''',
'''            IsScanning = true;\n            ActiveLane = lane;\n            BroadItemsScanned = 0;''',
'active scan lane')
text = replace_once(text,
'''            var settings = SnapshotSettings();\n            var worldId = playerState.CurrentWorld.RowId;\n            var universe = catalog.GetAllMarketableEntries()\n                .Where(x => settings.IncludeEquipment || !x.IsEquipment)\n                .Where(x => !settings.UseCategoryFilter || settings.CategoryIds.Contains(x.UiCategoryId))\n                .ToList();''',
'''            var baseSettings = SnapshotSettings();\n            var settings = lane == BuyScanLane.Vendor\n                ? baseSettings with\n                {\n                    EnableMarketToMarket = false,\n                    EnableMarketToVendor = false,\n                    EnableVendorToMarket = baseSettings.EnableVendorToMarket,\n                }\n                : baseSettings with { EnableVendorToMarket = false };\n            var worldId = playerState.CurrentWorld.RowId;\n            var universe = catalog.GetAllMarketableEntries()\n                .Where(x => settings.IncludeEquipment || !x.IsEquipment)\n                .Where(x => !settings.UseCategoryFilter || settings.CategoryIds.Contains(x.UiCategoryId))\n                .Where(x => lane != BuyScanLane.Vendor || x.Item.VendorGilShopPrice is > 0)\n                .ToList();''',
'lane-specific scan universe')
text = text.replace('lock (resultGate) opportunities = new List<BuyOpportunity>();', 'ReplaceLaneResults(lane, new List<BuyOpportunity>());')
text = replace_once(text,
'''            RescueDeepCandidates = selectedIds.Count(id =>\n                !selectedVariants.Any(x => x.Entry.Item.ItemId == id && x.LocalMarketSignal) &&\n                selectedVariants.Any(x => x.Entry.Item.ItemId == id && x.RareRescueSignal));\n\n            DeepItemsTotal = selectedIds.Count;''',
'''            RescueDeepCandidates = selectedIds.Count(id =>\n                !selectedVariants.Any(x => x.Entry.Item.ItemId == id && x.LocalMarketSignal) &&\n                selectedVariants.Any(x => x.Entry.Item.ItemId == id && x.RareRescueSignal));\n            if (lane == BuyScanLane.Vendor)\n            {\n                // Vendor candidates are intentionally their own world-local lane. They do not need\n                // to masquerade as Market -> Market local signals to win deep-analysis slots.\n                LocalDeepCandidates = selectedIds.Count;\n                RescueDeepCandidates = 0;\n            }\n\n            DeepItemsTotal = selectedIds.Count;''',
'vendor candidate diagnostics')
text = replace_once(text,
'''                if (settings.EnableMarketToMarket && !HasRenewableVendorSupply(candidate.Entry.Item, candidate.IsHq))\n                    TryAddBestMarketFlip(final, worldId, candidate, deep, existingQuantity, settings);\n                if (settings.EnableVendorToMarket && !candidate.IsHq && candidate.Entry.Item.VendorGilShopPrice is > 0)\n                    TryAddVendorToMarket(final, worldId, candidate, deep, existingQuantity, settings);\n                if (settings.EnableMarketToVendor && candidate.Entry.Item.VendorBuybackPrice > 0)\n                    TryAddMarketToVendor(final, worldId, candidate, deep, existingQuantity, settings);''',
'''                if (lane == BuyScanLane.MarketBoard && settings.EnableMarketToMarket && !HasRenewableVendorSupply(candidate.Entry.Item, candidate.IsHq))\n                    TryAddBestMarketFlip(final, worldId, candidate, deep, existingQuantity, settings);\n                if (lane == BuyScanLane.Vendor && settings.EnableVendorToMarket && !candidate.IsHq && candidate.Entry.Item.VendorGilShopPrice is > 0)\n                    TryAddVendorToMarket(final, worldId, candidate, deep, existingQuantity, settings);\n                if (lane == BuyScanLane.MarketBoard && settings.EnableMarketToVendor && candidate.Entry.Item.VendorBuybackPrice > 0)\n                    TryAddMarketToVendor(final, worldId, candidate, deep, existingQuantity, settings);''',
'final lane strategies')
text = replace_once(text,
'''            lock (resultGate)\n                opportunities = final;\n\n            LastCompletedUtc = DateTimeOffset.UtcNow;\n            Status = $"Ready: {final.Count:N0} opportunity package(s) from {universe.Count:N0} items. " +\n                     $"Broad signals {BroadSignalVariants:N0}; detailed {DeepItemsTotal:N0} " +\n                     $"({LocalDeepCandidates:N0} local + {RescueDeepCandidates:N0} rare rescue).";''',
'''            ReplaceLaneResults(lane, final);\n\n            LastCompletedUtc = DateTimeOffset.UtcNow;\n            if (lane == BuyScanLane.Vendor)\n                LastVendorCompletedUtc = LastCompletedUtc;\n            else\n                LastMarketCompletedUtc = LastCompletedUtc;\n            var laneLabel = lane == BuyScanLane.Vendor ? "Vendor -> Market" : "Market Board";\n            Status = $"{laneLabel} ready: {final.Count:N0} opportunity package(s) from {universe.Count:N0} scoped items. " +\n                     $"Broad signals {BroadSignalVariants:N0}; detailed {DeepItemsTotal:N0} " +\n                     $"({LocalDeepCandidates:N0} local + {RescueDeepCandidates:N0} rare rescue).";''',
'store lane result')
text = replace_once(text,
'''        finally\n        {\n            IsScanning = false;\n            scanGate.Release();\n        }\n    }\n\n    public PurchasePredictionContext? FindPredictionForPurchase''',
'''        finally\n        {\n            IsScanning = false;\n            ActiveLane = null;\n            scanGate.Release();\n        }\n    }\n\n    private void ReplaceLaneResults(BuyScanLane lane, List<BuyOpportunity> results)\n    {\n        lock (resultGate)\n        {\n            if (lane == BuyScanLane.Vendor)\n                vendorOpportunities = results;\n            else\n                marketOpportunities = results;\n        }\n    }\n\n    public PurchasePredictionContext? FindPredictionForPurchase''',
'result helper')
text = replace_once(text,
'''            match = opportunities\n                .Where(x => x.Item.ItemId == itemId && x.IsHq == isHq)''',
'''            match = marketOpportunities\n                .Where(x => x.Item.ItemId == itemId && x.IsHq == isHq)''',
'MB prediction lane')
write(path, text)

# --- Suite shell: separate current-world lanes --------------------------------
path = 'ShouldISell/Windows/SuiteWindow.cs'
text = read(path)
text = replace_once(text,
'''    private string buySearch = string.Empty;\n    private string buyCategorySearch = string.Empty;''',
'''    private string buySearch = string.Empty;\n    private string vendorBuySearch = string.Empty;\n    private string buyCategorySearch = string.Empty;''',
'vendor search field')
text = replace_once(text,
'''        return GetModelAdjustedBuyOpportunities(worldId);\n    }\n\n    private static void ItemNameContextMenu''',
'''        return GetModelAdjustedBuyOpportunities(worldId)\n            .Where(x => x.Kind != BuyOpportunityKind.VendorToMarket)\n            .ToList();\n    }\n\n    private IReadOnlyList<BuyOpportunity> GetCurrentWorldVendorOpportunities()\n    {\n        var worldId = CurrentBuyWorldId;\n        if (worldId == 0)\n            return Array.Empty<BuyOpportunity>();\n        return GetModelAdjustedBuyOpportunities(worldId)\n            .Where(x => x.Kind == BuyOpportunityKind.VendorToMarket)\n            .ToList();\n    }\n\n    private static void ItemNameContextMenu''',
'separate world opportunity getters')
write(path, text)

# --- Buy filters: Market Board strategies only --------------------------------
path = 'ShouldISell/Windows/SuiteWindow.BuyFilters.cs'
text = read(path)
text = replace_once(text,
'''    private readonly HashSet<BuyOpportunityKind> buyStrategyFilter = Enum.GetValues<BuyOpportunityKind>().ToHashSet();''',
'''    private static readonly BuyOpportunityKind[] MarketBuyStrategyKinds = Enum.GetValues<BuyOpportunityKind>()\n        .Where(x => x != BuyOpportunityKind.VendorToMarket)\n        .ToArray();\n    private readonly HashSet<BuyOpportunityKind> buyStrategyFilter = MarketBuyStrategyKinds.ToHashSet();''',
'market strategy set')
text = text.replace('foreach (var kind in Enum.GetValues<BuyOpportunityKind>())', 'foreach (var kind in MarketBuyStrategyKinds)')
text = text.replace('var allCount = Enum.GetValues<BuyOpportunityKind>().Length;', 'var allCount = MarketBuyStrategyKinds.Length;')
write(path, text)

# --- Buy UI: two scanners + independent vendor table --------------------------
path = 'ShouldISell/Windows/SuiteWindow.Buy.cs'
text = read(path)
text = replace_once(text,
'''        ImGui.TextWrapped("Scan a configurable slice of the market for executable purchases within your budget. Discovery is cheap and broad; only promising items get full listings + history and a counterfactual Should I Sell? exit simulation.");''',
'''        ImGui.TextWrapped("Should I Buy? now keeps two acquisition lanes separate: Market Board buys are scanned/ranked independently from renewable Vendor -> Market opportunities. Each lane keeps its own cached results so one cannot crowd the other out of detailed analysis.");''',
'buy intro')
text = replace_once(text,
'''        DrawBuyControls();\n        DrawBuyScreenerAndDeepScan();\n        DrawBuyPortfolio();\n        ImGui.Separator();\n        DrawBuyResults();''',
'''        DrawBuyControls();\n        DrawVendorBuyResults();\n        ImGui.Separator();\n        ImGui.TextDisabled("MARKET BOARD ACQUISITION OPPORTUNITIES");\n        DrawBuyScreenerAndDeepScan();\n        DrawBuyPortfolio();\n        ImGui.Separator();\n        DrawBuyResults();''',
'draw separate vendor lane')
pattern = r'''        ImGui\.Spacing\(\);\n        if \(!plugin\.BuyScanner\.IsScanning\)\n        \{.*?\n        \}\n    \}\n\n    private void DrawBuyCategoryScope'''
replacement = r'''        ImGui.Spacing();
        var scanner = plugin.BuyScanner;
        if (!scanner.IsScanning)
        {
            if (ImGui.Button("SCAN MARKET BOARD BUYS"))
            {
                selectedBuyOpportunity = null;
                buyDetailsOpen = false;
                buyPortfolioPlan = null;
                _ = scanner.ScanMarketAsync();
            }
            Tooltip("Scan only acquisition routes that spend gil on the Market Board: Market -> Market strategies plus Market -> Vendor. Vendor -> Market candidates do not consume this scan's deep-analysis slots.");

            ImGui.SameLine();
            if (ImGui.Button("SCAN VENDOR -> MARKET"))
            {
                selectedBuyOpportunity = null;
                buyDetailsOpen = false;
                _ = scanner.ScanVendorAsync();
            }
            Tooltip("Independent vendor lane. It scans only items verified in game data as normal-gil vendor purchases, then deep-checks their current-world Market Board exit. Results persist when you later run the Market Board scan.");
        }
        else
        {
            if (ImGui.Button("Stop scan"))
                scanner.CancelScan();
            ImGui.SameLine();
            var active = scanner.ActiveLane == BuyScanLane.Vendor ? "Vendor -> Market" : "Market Board";
            ImGui.TextDisabled($"{active}: {scanner.Status}");
        }

        if (!scanner.IsScanning)
        {
            ImGui.TextDisabled($"Cached lanes: {scanner.GetMarketOpportunities().Count:N0} Market Board package(s) • {scanner.GetVendorOpportunities().Count:N0} Vendor -> Market package(s). {scanner.Status}");
        }
        else if (scanner.BroadItemsTotal > 0 && scanner.BroadItemsScanned < scanner.BroadItemsTotal)
        {
            var fraction = scanner.BroadItemsScanned / (float)Math.Max(1, scanner.BroadItemsTotal);
            var active = scanner.ActiveLane == BuyScanLane.Vendor ? "Vendor discovery" : "Market discovery";
            ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"{active} {scanner.BroadItemsScanned:N0}/{scanner.BroadItemsTotal:N0}");
        }
        else if (scanner.DeepItemsTotal > 0)
        {
            var fraction = scanner.DeepItemsScanned / (float)Math.Max(1, scanner.DeepItemsTotal);
            var active = scanner.ActiveLane == BuyScanLane.Vendor ? "Vendor detailed" : "Market detailed";
            ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"{active} {scanner.DeepItemsScanned:N0}/{scanner.DeepItemsTotal:N0}");
        }
    }

    private void DrawBuyCategoryScope'''
text = regex_once(text, pattern, replacement, 'replace buy scan controls')

vendor_method = r'''
    private void DrawVendorBuyResults()
    {
        var all = GetCurrentWorldVendorOpportunities()
            .OrderByDescending(x => x.OpportunityScore)
            .ThenByDescending(x => x.RiskAdjustedProfit)
            .ThenByDescending(x => x.PotentialProfit)
            .ToList();

        if (!ImGui.CollapsingHeader($"VENDOR -> MARKET OPPORTUNITIES ({all.Count:N0})##vendor-buy-results", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextWrapped("Renewable NPC supply gets its own opportunity board. This table never competes with Market Board acquisitions for shortlist/deep-analysis slots, and a Market Board scan does not erase these results.");
        ImGui.SetNextItemWidth(320 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##vendor-buy-search", "Filter vendor opportunities by item...", ref vendorBuySearch, 128);
        var rows = all
            .Where(x => string.IsNullOrWhiteSpace(vendorBuySearch) || x.Item.Name.Contains(vendorBuySearch, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        ImGui.SameLine();
        ImGui.TextDisabled($"{rows.Count:N0}/{all.Count:N0} shown");

        if (rows.Count == 0)
        {
            ImGui.TextDisabled(plugin.BuyScanner.LastVendorCompletedUtc is null
                ? "No vendor scan yet. Use SCAN VENDOR -> MARKET above."
                : "No current Vendor -> Market opportunity survives the configured ROI/profit/holding rules.");
            return;
        }

        var height = Math.Min(285, 48 + rows.Count * 25) * ImGuiHelpers.GlobalScale;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("##vendor-buy-table", 10, flags, new Vector2(0, height)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Rating", ImGuiTableColumnFlags.WidthFixed, 118 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Vendor/u", ImGuiTableColumnFlags.WidthFixed, 78 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Buy", ImGuiTableColumnFlags.WidthFixed, 52 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 88 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Exit @", ImGuiTableColumnFlags.WidthFixed, 82 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Stack", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Profit", ImGuiTableColumnFlags.WidthFixed, 88 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("ROI", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Liquidate", ImGuiTableColumnFlags.WidthFixed, 72 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            if (ImGui.Selectable($"{Stars(row.Stars)} {row.OpportunityScore:0}##vendor-buy-{row.Item.ItemId}-{row.AcquisitionCost}", false, ImGuiSelectableFlags.SpanAllColumns))
            {
                selectedBuyOpportunity = row;
                buyDetailsOpen = true;
            }
            Tooltip($"Click for full analysis. Vendor supply is renewable; the recommendation targets one working listing rather than a speculative stockpile.\nConfidence: {row.Confidence:P0}\nRecent sales: {row.SalesSampleCount:N0}");
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(row.Item.Name);
            ItemNameContextMenu($"##copy-vendor-buy-{row.Item.ItemId}-{row.AcquisitionCost}", row.Item.Name);
            ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(Gil(row.AverageAcquisitionUnitCost));
            ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(row.AcquireQuantity.ToString("N0"));
            ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(Gil(row.AcquisitionCost));
            ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(row.SuggestedExitUnitPrice is { } exit ? $"{exit:N0}g" : "—");
            ImGui.TableSetColumnIndex(6); ImGui.TextUnformatted(row.SuggestedExitStackSize.ToString("N0"));
            ImGui.TableSetColumnIndex(7); ImGui.TextUnformatted(Gil(row.PotentialProfit));
            ImGui.TableSetColumnIndex(8); ImGui.TextUnformatted(Percent(row.Roi));
            ImGui.TableSetColumnIndex(9); ImGui.TextUnformatted(Days(row.EstimatedLiquidationDays));
        }

        ImGui.EndTable();
        ImGui.TextDisabled("Actually bought from the NPC? Record it under Should I Tycoon? -> Purchases so FIFO profit tracking gets the real vendor cost basis.");
    }

'''
text = replace_once(text,
'''    private void DrawBuyResults()''',
vendor_method + '''    private void DrawBuyResults()''',
'insert vendor results table')
text = text.replace('market flip, sweep, split, consolidate, vendor-to-market or market-to-vendor.', 'market flip, sweep, split, consolidate or market-to-vendor.')
write(path, text)

# --- Tycoon cashflow icons + manual vendor purchases --------------------------
path = 'ShouldISell/Windows/SuiteWindow.TycoonCashflow.cs'
text = read(path)
fields = r'''

    // FontAwesome 5 Free glyphs are included in Dalamud's default UI font. Keep the common
    // high-frequency categories one click away; the full category popup remains available.
    private static readonly (GilFlowCategory Category, string Icon, string Label)[] QuickSpendGilCategories =
    {
        (GilFlowCategory.MarketBoardPurchase, "\uf07a", "Market Board purchase"),
        (GilFlowCategory.Vendor, "\uf54e", "Vendor"),
        (GilFlowCategory.Crafting, "\uf6e3", "Crafting / materials"),
        (GilFlowCategory.Glamour, "\uf553", "Glamour"),
        (GilFlowCategory.Housing, "\uf015", "Housing"),
        (GilFlowCategory.Teleport, "\uf14e", "Teleport"),
        (GilFlowCategory.Repair, "\uf0ad", "Repair"),
    };

    private static readonly (GilFlowCategory Category, string Icon, string Label)[] QuickIncomeGilCategories =
    {
        (GilFlowCategory.Vendor, "\uf54e", "Vendor"),
        (GilFlowCategory.Quest, "\uf005", "Quest"),
        (GilFlowCategory.Duty, "\uf091", "Duty / roulette"),
        (GilFlowCategory.PlayerTrade, "\uf362", "Player trade"),
        (GilFlowCategory.RetainerTransfer, "\uf51e", "Retainer transfer / internal"),
    };

    private string vendorPurchaseSearch = string.Empty;
    private uint vendorPurchaseItemId;
    private int vendorPurchaseQuantity = 1;
    private int vendorPurchaseUnitPrice;
    private bool vendorPurchaseTrackAsTrade = true;
    private string vendorPurchaseStatus = string.Empty;
'''
text = replace_once(text,
'''    };\n\n    private void DrawTycoonCashflowSummary''',
'''    };''' + fields + '''\n    private void DrawTycoonCashflowSummary''',
'cashflow quick fields')
text = replace_once(text,
'''        var mbSpend = allPurchases.Sum(x => (double)x.TotalCost);\n        var mbSales = sales.Sum(x => (double)x.NetGil);''',
'''        var mbSpend = allPurchases.Where(x => x.SourceKind == PurchaseSourceKind.MarketBoard).Sum(x => (double)x.TotalCost);\n        var vendorSpend = allPurchases.Where(x => x.SourceKind == PurchaseSourceKind.VendorManual).Sum(x => (double)x.TotalCost);\n        var mbSales = sales.Sum(x => (double)x.NetGil);''',
'cashflow source spend')
text = replace_once(text,
'''        MetricCell(0, "MB purchase spend", Gil(mbSpend));\n        MetricCell(1, "Captured MB sale income", Gil(mbSales));\n        MetricCell(2, "Trade realized P&L", Gil(tradeSnapshot.RealizedProfit));\n        MetricCell(3, "Unclassified wallet events", flows.Count(x => x.Category == GilFlowCategory.Unclassified).ToString("N0"));''',
'''        MetricCell(0, "MB purchase spend", Gil(mbSpend));\n        MetricCell(1, "Manual vendor spend", Gil(vendorSpend));\n        MetricCell(2, "Captured MB sale income", Gil(mbSales));\n        MetricCell(3, "Trade realized P&L", Gil(tradeSnapshot.RealizedProfit));\n        MetricCell(0, "Unclassified wallet events", flows.Count(x => x.Category == GilFlowCategory.Unclassified).ToString("N0"));''',
'cashflow summary vendor spend')
text = text.replace('ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 145 * ImGuiHelpers.GlobalScale);', 'ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 260 * ImGuiHelpers.GlobalScale);')
old_editor = r'''    private void DrawGilCategoryEditor(GilFlowEntry flow)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##gil-category", GilCategoryLabel(flow.Category)))
            return;

        foreach (var category in EditableGilCategories)
        {
            if (!ImGui.Selectable(GilCategoryLabel(category), category == flow.Category))
                continue;
            plugin.TraderStore.UpdateGilFlowClassification(
                flow.Id,
                category,
                GilCategoryLabel(category),
                autoClassified: false,
                note: flow.Note);
        }
        ImGui.EndCombo();
    }
'''
new_editor = r'''    private void DrawGilCategoryEditor(GilFlowEntry flow)
    {
        var quick = flow.Amount < 0 ? QuickSpendGilCategories : QuickIncomeGilCategories;
        var first = true;
        foreach (var entry in quick)
        {
            if (!first)
                ImGui.SameLine(0, 3 * ImGuiHelpers.GlobalScale);
            first = false;
            if (ImGui.SmallButton($"{entry.Icon}##gil-quick-{entry.Category}"))
                ApplyGilCategory(flow, entry.Category);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(entry.Label + (flow.Category == entry.Category ? " (current)" : string.Empty));
        }

        ImGui.SameLine(0, 4 * ImGuiHelpers.GlobalScale);
        if (ImGui.SmallButton("...##gil-category-more"))
            ImGui.OpenPopup("##gil-category-popup");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("All cashflow categories");
        if (ImGui.BeginPopup("##gil-category-popup"))
        {
            foreach (var category in EditableGilCategories)
            {
                if (ImGui.Selectable(GilCategoryLabel(category), category == flow.Category))
                    ApplyGilCategory(flow, category);
            }
            ImGui.EndPopup();
        }
        ImGui.TextDisabled(GilCategoryLabel(flow.Category));
    }

    private void ApplyGilCategory(GilFlowEntry flow, GilFlowCategory category)
        => plugin.TraderStore.UpdateGilFlowClassification(
            flow.Id,
            category,
            GilCategoryLabel(category),
            autoClassified: false,
            note: flow.Note);
'''
text = replace_once(text, old_editor, new_editor, 'icon cashflow editor')
text = replace_once(text,
'''        var contentId = Plugin.PlayerState.ContentId;\n        var purchases = plugin.TraderStore.GetPurchases(contentId);\n        ImGui.TextWrapped("Every confirmed Market Board purchase remains part of your spending history. Use the Trading position toggle to decide whether that purchase lot should participate in FIFO trade P&L/open positions. This is ideal for crafting, glamour, housing or personal-use buys: excluding a lot is reversible and does not erase the real gil spend.");\n        ImGui.Spacing();''',
'''        var contentId = Plugin.PlayerState.ContentId;\n        DrawManualVendorPurchaseEntry(contentId);\n        ImGui.Spacing();\n        var purchases = plugin.TraderStore.GetPurchases(contentId);\n        ImGui.TextWrapped("Purchases are now a unified acquisition ledger. Market Board buys are captured automatically; normal-gil vendor buys can be entered manually. Use the Trade/Personal toggle to decide whether a lot participates in FIFO trading P&L without erasing the real acquisition record.");\n        ImGui.Spacing();''',
'insert manual vendor form')
text = replace_once(text,
'''            ImGui.TextDisabled("No captured Market Board purchases yet.");''',
'''            ImGui.TextDisabled("No captured Market Board or manually entered vendor purchases yet.");''',
'empty purchases text')
# Rewrite purchase table from 7 to 8 columns and insert Source.
text = text.replace('if (!ImGui.BeginTable("##tycoon-purchase-ledger", 7, flags, new Vector2(0, -1)))', 'if (!ImGui.BeginTable("##tycoon-purchase-ledger", 8, flags, new Vector2(0, -1)))')
text = replace_once(text,
'''        ImGui.TableSetupColumn("Total cost", ImGuiTableColumnFlags.WidthFixed, 95 * ImGuiHelpers.GlobalScale);\n        ImGui.TableSetupColumn("Strategy / source", ImGuiTableColumnFlags.WidthStretch);\n        ImGui.TableSetupColumn("Position", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);\n        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 120 * ImGuiHelpers.GlobalScale);''',
'''        ImGui.TableSetupColumn("Total cost", ImGuiTableColumnFlags.WidthFixed, 95 * ImGuiHelpers.GlobalScale);\n        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 100 * ImGuiHelpers.GlobalScale);\n        ImGui.TableSetupColumn("Strategy", ImGuiTableColumnFlags.WidthStretch);\n        ImGui.TableSetupColumn("Position", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);\n        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 120 * ImGuiHelpers.GlobalScale);''',
'purchase source header')
text = replace_once(text,
'''            ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(Gil(purchase.TotalCost));\n            ImGui.TableSetColumnIndex(4); ImGui.TextWrapped(purchase.Strategy);\n            ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(excluded ? "Personal" : "Trade");\n            ImGui.TableSetColumnIndex(6);''',
'''            ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(Gil(purchase.TotalCost));\n            ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(purchase.SourceKind == PurchaseSourceKind.VendorManual ? "Vendor (manual)" : "Market Board");\n            ImGui.TableSetColumnIndex(5); ImGui.TextWrapped(purchase.Strategy);\n            ImGui.TableSetColumnIndex(6); ImGui.TextUnformatted(excluded ? "Personal" : "Trade");\n            ImGui.TableSetColumnIndex(7);''',
'purchase source cells')

vendor_form = r'''
    private void DrawManualVendorPurchaseEntry(ulong contentId)
    {
        if (!ImGui.CollapsingHeader("Record vendor purchase manually##vendor-purchase-entry", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextWrapped("FFXIV does not expose a reliable item-level vendor-purchase event to Dalamud. Enter a normal-gil NPC purchase here when you want its real cost basis tracked. Should I? never invents the purchase: you choose the item, quantity and unit cost, then confirm it.");
        ImGui.TextDisabled("If the exact wallet decrease was captured as an Unclassified event in the last five minutes, Should I? will relabel that existing event as Vendor. If no exact match exists, no synthetic wallet transaction is created.");

        ImGui.SetNextItemWidth(360 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##vendor-purchase-search", "Search normal-gil vendor item...", ref vendorPurchaseSearch, 128);
        var matches = string.IsNullOrWhiteSpace(vendorPurchaseSearch)
            ? new List<MarketCatalogEntry>()
            : plugin.Catalog.GetAllMarketableEntries()
                .Where(x => x.Item.VendorGilShopPrice is > 0 && x.Item.Name.Contains(vendorPurchaseSearch, StringComparison.CurrentCultureIgnoreCase))
                .Take(8)
                .ToList();
        if (matches.Count > 0 && (vendorPurchaseItemId == 0 || !string.Equals(plugin.Catalog.Get(vendorPurchaseItemId).Name, vendorPurchaseSearch, StringComparison.CurrentCultureIgnoreCase)))
        {
            if (ImGui.BeginChild("##vendor-purchase-matches", new Vector2(360 * ImGuiHelpers.GlobalScale, Math.Min(150, matches.Count * 24 + 8) * ImGuiHelpers.GlobalScale), true))
            {
                foreach (var match in matches)
                {
                    if (!ImGui.Selectable($"{match.Item.Name} — {match.Item.VendorGilShopPrice:N0}g##vendor-pick-{match.Item.ItemId}"))
                        continue;
                    vendorPurchaseItemId = match.Item.ItemId;
                    vendorPurchaseSearch = match.Item.Name;
                    vendorPurchaseUnitPrice = checked((int)Math.Min(int.MaxValue, match.Item.VendorGilShopPrice!.Value));
                }
                ImGui.EndChild();
            }
        }

        if (vendorPurchaseItemId == 0)
        {
            if (!string.IsNullOrWhiteSpace(vendorPurchaseStatus))
                ImGui.TextDisabled(vendorPurchaseStatus);
            return;
        }

        var item = plugin.Catalog.Get(vendorPurchaseItemId);
        if (item.VendorGilShopPrice is null)
        {
            vendorPurchaseItemId = 0;
            vendorPurchaseStatus = "That item is no longer recognized as a normal-gil vendor item in current game data.";
            return;
        }

        ImGui.TextUnformatted($"Selected: {item.Name} • game-data vendor price {item.VendorGilShopPrice:N0}g/unit");
        ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Quantity##vendor-purchase", ref vendorPurchaseQuantity, 1, 10))
            vendorPurchaseQuantity = Math.Clamp(vendorPurchaseQuantity, 1, 999_999);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Unit cost##vendor-purchase", ref vendorPurchaseUnitPrice, 1, 100))
            vendorPurchaseUnitPrice = Math.Clamp(vendorPurchaseUnitPrice, 1, 999_999_999);
        ImGui.SameLine();
        ImGui.Checkbox("Track as trade position##vendor-purchase", ref vendorPurchaseTrackAsTrade);

        var total = (long)Math.Max(1, vendorPurchaseQuantity) * Math.Max(1, vendorPurchaseUnitPrice);
        ImGui.TextDisabled($"Manual acquisition total: {total:N0}g. Vendor items are recorded as NQ with zero buyer tax.");

        if (ImGui.Button("RECORD VENDOR PURCHASE"))
        {
            var prediction = plugin.BuyScanner.GetVendorOpportunities()
                .Where(x => x.WorldId == Plugin.PlayerState.CurrentWorld.RowId && x.Item.ItemId == vendorPurchaseItemId && !x.IsHq)
                .OrderByDescending(x => x.OpportunityScore)
                .FirstOrDefault();
            var now = DateTimeOffset.UtcNow;
            var purchase = new PersonalPurchase(
                contentId,
                Plugin.PlayerState.CurrentWorld.RowId,
                vendorPurchaseItemId,
                false,
                vendorPurchaseQuantity,
                checked((uint)vendorPurchaseUnitPrice),
                0,
                total,
                0,
                now,
                prediction?.StrategyLabel ?? "Vendor -> Market (manual)",
                prediction?.OpportunityScore,
                prediction?.SuggestedExitUnitPrice,
                prediction?.EstimatedLiquidationDays,
                prediction?.PotentialProfit,
                prediction?.AnalysedAtUtc,
                PurchaseSourceKind.VendorManual);

            if (plugin.TraderStore.AddPurchase(purchase, flush: false))
            {
                if (!vendorPurchaseTrackAsTrade)
                    plugin.TraderStore.SetPurchaseExcluded(purchase, true, flush: false);
                var linked = plugin.TraderStore.TryClassifyRecentMatchingOutflow(
                    contentId,
                    total,
                    GilFlowCategory.Vendor,
                    $"Vendor purchase: {item.Name}",
                    $"Manual vendor acquisition: {vendorPurchaseQuantity:N0} × {vendorPurchaseUnitPrice:N0}g",
                    flush: false);
                plugin.TraderStore.Flush();
                plugin.TraderAnalyzer.GetSnapshot(force: true);
                vendorPurchaseStatus = linked
                    ? $"Recorded {vendorPurchaseQuantity:N0} × {item.Name} for {total:N0}g and linked the exact recent wallet outflow."
                    : $"Recorded {vendorPurchaseQuantity:N0} × {item.Name} for {total:N0}g. No exact recent Unclassified wallet outflow was changed.";
            }
            else
            {
                vendorPurchaseStatus = "That vendor purchase looks like a duplicate of an entry recorded moments ago; nothing was added.";
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Adds real vendor cost basis to Tycoon. If marked Trade, later captured sales can close it through the same FIFO accounting used for Market Board buys.");
        if (!string.IsNullOrWhiteSpace(vendorPurchaseStatus))
            ImGui.TextWrapped(vendorPurchaseStatus);
    }

'''
text = replace_once(text,
'''    private static string GilCategoryLabel(GilFlowCategory category) => category switch''',
vendor_form + '''    private static string GilCategoryLabel(GilFlowCategory category) => category switch''',
'insert vendor purchase form')
write(path, text)

print('v2.1.0 patch applied successfully')
