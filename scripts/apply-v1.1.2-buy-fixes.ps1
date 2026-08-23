$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Get-Content $Path -Raw
    if (-not $text.Contains($Old)) {
        throw "Expected patch text not found in $Path`n--- OLD ---`n$Old"
    }
    $text = $text.Replace($Old, $New)
    Set-Content $Path $text -Encoding UTF8
}

$buy = 'ShouldISell/Windows/SuiteWindow.Buy.cs'
$scanner = 'ShouldISell/Services/BuyOpportunityScanner.cs'

Replace-Exact $buy @'
    private void DrawBuyModule()
    {
        if (buyDetailsOpen && selectedBuyOpportunity is { } selected)
        {
            DrawBuyDetailPage(selected);
            return;
        }

        ImGui.TextWrapped("Scan a configurable slice of the market for executable purchases within your budget. Discovery is cheap and broad; only promising items get full listings + history and a counterfactual Should I Sell? exit simulation.");
        ImGui.Spacing();
'@ @'
    private void DrawBuyModule()
    {
        var currentWorldId = CurrentBuyWorldId;
        if (buyDetailsOpen && selectedBuyOpportunity is { } staleSelected && staleSelected.WorldId != currentWorldId)
        {
            selectedBuyOpportunity = null;
            buyDetailsOpen = false;
            buyPortfolioPlan = null;
        }

        if (buyDetailsOpen && selectedBuyOpportunity is { } selected)
        {
            DrawBuyDetailPage(selected);
            return;
        }

        ImGui.TextWrapped("Scan a configurable slice of the market for executable purchases within your budget. Discovery is cheap and broad; only promising items get full listings + history and a counterfactual Should I Sell? exit simulation.");
        if (currentWorldId != 0)
        {
            ImGui.TextDisabled($"Current-world scope: {CurrentBuyWorldName}. Recommendations from other worlds are hidden and cannot be live-verified here.");
            var hiddenOtherWorld = plugin.BuyScanner.GetOpportunities().Count(x => x.WorldId != currentWorldId);
            if (hiddenOtherWorld > 0)
                ImGui.TextWrapped($"{hiddenOtherWorld:N0} cached recommendation(s) belong to another world and are hidden. Rerun discovery on {CurrentBuyWorldName} to replace them. Cross-world trading will be a separate explicit opt-in mode rather than being mixed into normal results.");
        }
        ImGui.Spacing();
'@

Replace-Exact $buy 'ImGui.InputInt("Deep-analysis item limit", ref deepLimit, 10, 50)' 'ImGui.InputInt("Detailed Universalis item limit", ref deepLimit, 10, 50)'
Replace-Exact $buy 'Tooltip("The broad aggregated pass still checks every scoped marketable item. This caps how many strongest item IDs receive full current listings plus 90-day history.");' 'Tooltip("After discovery, only this many strongest item IDs receive detailed Universalis current listings plus 90-day history. This is still Universalis data; the separate LIVE VERIFY action is the one-item native FFXIV check.");'
Replace-Exact $buy 'ImGui.Button("SCAN FOR GOOD BUYS")' 'ImGui.Button("DISCOVER GOOD BUYS (UNIVERSALIS)")'
Replace-Exact $buy 'Tooltip("Run a new broad Universalis discovery pass and then deep-analyze the strongest candidates with current listings and 90-day sales history.");' 'Tooltip("This is the only action that starts the broad market-universe pass. It then uses detailed Universalis listings/history for the strongest candidates. LIVE VERIFY on an item is separate and never starts this broad pass.");'
Replace-Exact $buy '$"Deep analysis {plugin.BuyScanner.DeepItemsScanned:N0}/{plugin.BuyScanner.DeepItemsTotal:N0}"' '$"Detailed Universalis {plugin.BuyScanner.DeepItemsScanned:N0}/{plugin.BuyScanner.DeepItemsTotal:N0}"'

Replace-Exact $buy @'
    private void DrawBuyPortfolio()
    {
        var opportunities = plugin.BuyScanner.GetOpportunities();
        if (opportunities.Count == 0 || plugin.BuyScanner.IsScanning)
            return;

        var c = plugin.Configuration;
'@ @'
    private void DrawBuyPortfolio()
    {
        var opportunities = GetCurrentWorldBuyOpportunities();
        if (opportunities.Count == 0 || plugin.BuyScanner.IsScanning)
            return;

        if (buyPortfolioPlan is { } oldPlan && oldPlan.Selections.Any(x => x.WorldId != CurrentBuyWorldId))
            buyPortfolioPlan = null;

        var c = plugin.Configuration;
'@

# This exact item-cell line occurs in both Buy tables. Give both the same right-click copy behavior.
Replace-Exact $buy 'ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(row.Item.Name + (row.IsHq ? " [HQ]" : string.Empty));' @'
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(row.Item.Name + (row.IsHq ? " [HQ]" : string.Empty));
        ItemNameContextMenu($"##copy-buy-name-{row.Item.ItemId}-{row.IsHq}-{row.Kind}-{row.AcquisitionCost}", row.Item.Name);
'@

Replace-Exact $buy @'
        var filtered = plugin.BuyScanner.GetOpportunities()
            .Where(x => string.IsNullOrWhiteSpace(buySearch) || x.Item.Name.Contains(buySearch, StringComparison.CurrentCultureIgnoreCase));
'@ @'
        var filtered = GetCurrentWorldBuyOpportunities()
            .Where(x => string.IsNullOrWhiteSpace(buySearch) || x.Item.Name.Contains(buySearch, StringComparison.CurrentCultureIgnoreCase));
'@

Replace-Exact $buy @'
        ImGui.TextUnformatted($"{opportunity.Item.Name}{(opportunity.IsHq ? " [HQ]" : string.Empty)}");
        ImGui.TextUnformatted($"{Stars(opportunity.Stars)}  {opportunity.OpportunityScore:0.0}/100  ·  {opportunity.StrategyLabel}");
'@ @'
        ImGui.TextUnformatted($"{opportunity.Item.Name}{(opportunity.IsHq ? " [HQ]" : string.Empty)}");
        ItemNameContextMenu($"##copy-detail-name-{opportunity.Item.ItemId}-{opportunity.IsHq}", opportunity.Item.Name);
        ImGui.TextDisabled($"Market world: {plugin.Catalog.GetWorldName(opportunity.WorldId)} (world ID {opportunity.WorldId})");
        ImGui.TextUnformatted($"{Stars(opportunity.Stars)}  {opportunity.OpportunityScore:0.0}/100  ·  {opportunity.StrategyLabel}");
'@

Replace-Exact $scanner 'new ProductInfoHeaderValue("ShouldI", "1.1.0")' 'new ProductInfoHeaderValue("ShouldI", "1.1.2")'
Replace-Exact $scanner 'Status = $"Deep analysis: 0 / {DeepItemsTotal:N0} candidate items...";' 'Status = $"Detailed Universalis: 0 / {DeepItemsTotal:N0} candidate items...";'
Replace-Exact $scanner 'Status = $"Deep analysis: {DeepItemsScanned:N0} / {DeepItemsTotal:N0} candidate items...";' 'Status = $"Detailed Universalis: {DeepItemsScanned:N0} / {DeepItemsTotal:N0} candidate items...";'

Replace-Exact $scanner @'
                var aggregated = await FetchAggregatedAsync(worldId, batch, token);
                foreach (var row in aggregated)
'@ @'
                var aggregated = await FetchAggregatedAsync(worldId, batch, token);
                if (!playerState.IsLoaded || playerState.CurrentWorld.RowId != worldId)
                {
                    lock (resultGate) opportunities = new List<BuyOpportunity>();
                    Status = "World changed during discovery. Results were discarded; run discovery again on your current world.";
                    return;
                }
                foreach (var row in aggregated)
'@

Replace-Exact $scanner @'
                var deep = await FetchDeepAsync(worldId, batch, token);
                foreach (var pair in deep)
'@ @'
                var deep = await FetchDeepAsync(worldId, batch, token);
                if (!playerState.IsLoaded || playerState.CurrentWorld.RowId != worldId)
                {
                    lock (resultGate) opportunities = new List<BuyOpportunity>();
                    Status = "World changed during detailed analysis. Results were discarded; run discovery again on your current world.";
                    return;
                }
                foreach (var pair in deep)
'@

Replace-Exact $scanner @'
            lock (resultGate)
                opportunities = final;
'@ @'
            if (!playerState.IsLoaded || playerState.CurrentWorld.RowId != worldId)
            {
                lock (resultGate) opportunities = new List<BuyOpportunity>();
                Status = "World changed before discovery completed. Results were discarded; run discovery again on your current world.";
                return;
            }

            lock (resultGate)
                opportunities = final;
'@

Write-Host 'v1.1.2 Buy patches applied successfully.'
