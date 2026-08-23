$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Get-Content $Path -Raw
    if (-not $text.Contains($Old)) { throw "Expected patch text not found in $Path`n--- OLD ---`n$Old" }
    $text = $text.Replace($Old, $New)
    Set-Content $Path $text -Encoding UTF8
}

$score = 'ShouldISell/Services/ScoreCalculator.cs'
$buyModel = 'ShouldISell/Windows/SuiteWindow.BuyModel.cs'
$plugin = 'ShouldISell/Plugin.cs'
$tycoon = 'ShouldISell/Windows/SuiteWindow.Tycoon.cs'

# Global FFXIV Market Board maximum listing quantity: 99.
Replace-Exact $score @'
        var stackLimit = (int)Math.Clamp(itemStackSize == 0 ? 999u : itemStackSize, 1u, (uint)int.MaxValue);
'@ @'
        var stackLimit = Math.Min(
            MarketBoardRules.MaxListingQuantity,
            (int)Math.Clamp(itemStackSize == 0 ? (uint)MarketBoardRules.MaxListingQuantity : itemStackSize, 1u, (uint)int.MaxValue));
'@

Replace-Exact $score @'
        var owned = Math.Max(1, quantityForSale);
        if (recommendation is { RecommendedStackSize: > 0 })
            return Math.Clamp(recommendation.RecommendedStackSize, 1, owned);

        var stackLimit = itemStackSize == 0
            ? owned
            : (int)Math.Min((uint)owned, itemStackSize);
        return Math.Max(1, stackLimit);
'@ @'
        var owned = Math.Max(1, quantityForSale);
        var maxListable = Math.Min(owned, MarketBoardRules.MaxListingQuantity);
        if (recommendation is { RecommendedStackSize: > 0 })
            return Math.Clamp(recommendation.RecommendedStackSize, 1, maxListable);

        var stackLimit = itemStackSize == 0
            ? maxListable
            : (int)Math.Min((uint)maxListable, itemStackSize);
        return Math.Max(1, Math.Min(stackLimit, MarketBoardRules.MaxListingQuantity));
'@

# Fix the interrupted v1.1.5 compile error and enforce the 99-unit rule in every Buy execution overlay.
Replace-Exact $buyModel @'
        var targetStack = Math.Max(1, opportunity.SuggestedExitStackSize);
'@ @'
        var targetStack = Math.Min(MarketBoardRules.MaxListingQuantity, Math.Max(1, opportunity.SuggestedExitStackSize));
'@

Replace-Exact $buyModel @'
        if (opportunity.Kind == BuyOpportunityKind.MarketToVendor)
        {
            var notes = opportunity.Notes.ToList();
            notes.Add($"Native FFXIV Deep Scan at {liveAt.ToLocalTime():HH:mm:ss} confirmed the acquisition listing(s) for this guaranteed vendor exit.");
            return opportunity with
            {
                MarketFreshnessUtc = liveAt,
                Notes = notes,
                AnalysedAtUtc = liveAt,
            };
        }
'@ @'
        if (opportunity.Kind == BuyOpportunityKind.MarketToVendor)
        {
            var verificationNotes = opportunity.Notes.ToList();
            verificationNotes.Add($"Native FFXIV Deep Scan at {liveAt.ToLocalTime():HH:mm:ss} confirmed the acquisition listing(s) for this guaranteed vendor exit.");
            return opportunity with
            {
                MarketFreshnessUtc = liveAt,
                Notes = verificationNotes,
                AnalysedAtUtc = liveAt,
            };
        }
'@

Replace-Exact $buyModel @'
        var stackSize = Math.Max(1, rating.StackRecommendation?.RecommendedStackSize ?? opportunity.SuggestedExitStackSize);
'@ @'
        var stackSize = Math.Min(
            MarketBoardRules.MaxListingQuantity,
            Math.Max(1, rating.StackRecommendation?.RecommendedStackSize ?? opportunity.SuggestedExitStackSize));
'@

Replace-Exact $buyModel @'
        var recovery = OneListingCapitalRecovery(opportunity);
        var cycles = SequentialListingCycles(opportunity);
'@ @'
        var listingStack = Math.Min(MarketBoardRules.MaxListingQuantity, Math.Max(1, opportunity.SuggestedExitStackSize));
        var recovery = OneListingCapitalRecovery(opportunity);
        var cycles = SequentialListingCycles(opportunity);
'@

Replace-Exact $buyModel 'one active {Math.Max(1, opportunity.SuggestedExitStackSize):N0}-unit listing' 'one active {listingStack:N0}-unit listing'

Replace-Exact $buyModel @'
            RiskAdjustedProfit = adjustedRiskProfit,
            SuggestedExitListingCount = cycles,
            Notes = notes,
'@ @'
            RiskAdjustedProfit = adjustedRiskProfit,
            SuggestedExitStackSize = listingStack,
            SuggestedExitListingCount = cycles,
            Notes = notes,
'@

Replace-Exact $buyModel @'
    private static int OneListingUnits(BuyOpportunity opportunity)
        => Math.Max(1, Math.Min(opportunity.AcquireQuantity, Math.Max(1, opportunity.SuggestedExitStackSize)));
'@ @'
    private static int OneListingUnits(BuyOpportunity opportunity)
    {
        var stack = Math.Min(MarketBoardRules.MaxListingQuantity, Math.Max(1, opportunity.SuggestedExitStackSize));
        return Math.Max(1, Math.Min(opportunity.AcquireQuantity, stack));
    }
'@

Replace-Exact $buyModel @'
    private static int SequentialListingCycles(BuyOpportunity opportunity)
    {
        var stack = Math.Max(1, opportunity.SuggestedExitStackSize);
        var position = Math.Max(1, opportunity.ExistingQuantity + opportunity.AcquireQuantity);
        return DivideRoundUpBuy(position, stack);
    }
'@ @'
    private static int SequentialListingCycles(BuyOpportunity opportunity)
    {
        var position = Math.Max(1, opportunity.ExistingQuantity + opportunity.AcquireQuantity);
        return MarketBoardRules.ListingCycles(position, opportunity.SuggestedExitStackSize);
    }
'@

Replace-Exact $buyModel @'
        var effectiveUnitsPerDay = rating.UnitsPerDay;
        if (rating.TransactionsPerDay > 0.001)
            effectiveUnitsPerDay = Math.Min(effectiveUnitsPerDay, rating.TransactionsPerDay * Math.Max(1, stackSize));
'@ @'
        stackSize = Math.Min(MarketBoardRules.MaxListingQuantity, Math.Max(1, stackSize));
        var effectiveUnitsPerDay = rating.UnitsPerDay;
        if (rating.TransactionsPerDay > 0.001)
            effectiveUnitsPerDay = Math.Min(effectiveUnitsPerDay, rating.TransactionsPerDay * stackSize);
'@

# Wire listing history and all-sale insights into the plugin lifecycle.
Replace-Exact $plugin @'
    public TraderAnalyzer TraderAnalyzer { get; }
'@ @'
    public TraderAnalyzer TraderAnalyzer { get; }
    public ListingHistoryTracker ListingHistory { get; }
    public TycoonInsightService TycoonInsights { get; }
'@

Replace-Exact $plugin @'
        TraderAnalyzer = new TraderAnalyzer(PlayerState, TraderStore, Store, Coordinator, Catalog);
'@ @'
        TraderAnalyzer = new TraderAnalyzer(PlayerState, TraderStore, Store, Coordinator, Catalog);
        ListingHistory = new ListingHistoryTracker(PluginInterface, PlayerState, Store, Log);
        TycoonInsights = new TycoonInsightService(PlayerState, Store, Catalog, ListingHistory);
'@

Replace-Exact $plugin @'
        PurchaseObserver.Dispose();
        BuyScanner.Dispose();
'@ @'
        PurchaseObserver.Dispose();
        ListingHistory.Dispose();
        BuyScanner.Dispose();
'@

Replace-Exact $plugin @'
        Inventory.ScanLoadedContainers();
'@ @'
        Inventory.ScanLoadedContainers();
        ListingHistory.Capture();
'@

# Surface the descriptive all-sale and listing-lifecycle views in Tycoon.
Replace-Exact $tycoon @'
        ImGui.TextWrapped("Tycoon joins real Market Board purchases with Should I Sell?'s captured retainer sales. Cost basis is FIFO per item/HQ variant, so realized profit, holding time, open positions and prediction accuracy come from your own trading history rather than generic market statistics.");
'@ @'
        ImGui.TextWrapped("Tycoon combines real Market Board purchases with every captured retainer sale. P&L remains FIFO and only uses known purchase cost basis, while Sales Insights also learns from gathered, crafted, dropped, gifted and pre-tracking stock. Listing Insights studies your own traceable listing lifecycle: repricing, stack-size changes, relists and time-to-sale.");
'@

Replace-Exact $tycoon @'
        DrawTraderMetrics(snapshot);
        ImGui.Separator();
'@ @'
        DrawTraderMetrics(snapshot);
        DrawTycoonInsightSummary(snapshot);
        ImGui.Separator();
'@

Replace-Exact $tycoon @'
            if (ImGui.BeginTabItem("Strategies"))
            {
                DrawTraderStrategies(snapshot);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Model Accuracy"))
'@ @'
            if (ImGui.BeginTabItem("Strategies"))
            {
                DrawTraderStrategies(snapshot);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Sales Insights"))
            {
                DrawTycoonSalesInsights();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Listing Insights"))
            {
                DrawTycoonListingInsights();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Model Accuracy"))
'@

Write-Host 'v1.1.5 source finalization applied.'
