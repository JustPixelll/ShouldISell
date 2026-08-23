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
$refresh = 'ShouldISell/Services/ExperimentalRefreshEngine.cs'
$scanner = 'ShouldISell/Services/BuyOpportunityScanner.cs'
$project = 'ShouldISell/ShouldISell.csproj'

Replace-Exact $buy @'
        DrawBuyControls();
        DrawBuyPortfolio();
'@ @'
        DrawBuyControls();
        DrawBuyScreenerAndDeepScan();
        DrawBuyPortfolio();
'@

Replace-Exact $buy @'
        var opportunities = GetCurrentWorldBuyOpportunities();
        if (opportunities.Count == 0 || plugin.BuyScanner.IsScanning)
'@ @'
        var opportunities = GetFilteredBuyOpportunities();
        if (opportunities.Count == 0 || plugin.BuyScanner.IsScanning)
'@

Replace-Exact $buy @'
    private void DrawBuyResults()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##buy-result-search", "Search opportunity item...", ref buySearch, 128);
        Tooltip("Filter the current scan results by item name. Sorting and the underlying scan results are not changed.");

        var filtered = GetCurrentWorldBuyOpportunities()
            .Where(x => string.IsNullOrWhiteSpace(buySearch) || x.Item.Name.Contains(buySearch, StringComparison.CurrentCultureIgnoreCase));
        var rows = SortBuyRows(filtered);
'@ @'
    private void DrawBuyResults()
    {
        var rows = SortBuyRows(GetFilteredBuyOpportunities());
'@

Replace-Exact $buy 'if (ImGui.BeginTable("##buy-portfolio-table", 9, flags))' 'if (ImGui.BeginTable("##buy-portfolio-table", 10, flags))'
Replace-Exact $buy @'
                ImGui.TableSetupColumn("ROI", ImGuiTableColumnFlags.WidthFixed, 60 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Liquidate", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
'@ @'
                ImGui.TableSetupColumn("ROI", ImGuiTableColumnFlags.WidthFixed, 60 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Live", ImGuiTableColumnFlags.WidthFixed, 82 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Liquidate", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
'@
Replace-Exact $buy @'
        HeaderCell(7, "ROI", "Potential profit divided by acquisition cost.");
        HeaderCell(8, "Liquidate", "Estimated time for the modeled resulting position to fully sell, including queue time where applicable.");
'@ @'
        HeaderCell(7, "ROI", "Potential profit divided by acquisition cost.");
        HeaderCell(8, "Live", "Native FFXIV verification state for this recommendation: Verified, Changed, Refreshed, or Not checked.");
        HeaderCell(9, "Liquidate", "Estimated time for the modeled resulting position to fully sell, including queue time where applicable.");
'@
Replace-Exact $buy @'
        ImGui.TableSetColumnIndex(7); ImGui.TextUnformatted(Percent(row.Roi));
        ImGui.TableSetColumnIndex(8); ImGui.TextUnformatted(Days(row.EstimatedLiquidationDays));
'@ @'
        ImGui.TableSetColumnIndex(7); ImGui.TextUnformatted(Percent(row.Roi));
        ImGui.TableSetColumnIndex(8); ImGui.TextUnformatted(LiveStateLabel(GetBuyLiveState(row)));
        ImGui.TableSetColumnIndex(9); ImGui.TextUnformatted(Days(row.EstimatedLiquidationDays));
'@

Replace-Exact $buy 'if (ImGui.BeginTable("##buy-opportunity-table", 11, flags, new Vector2(0, -1)))' 'if (ImGui.BeginTable("##buy-opportunity-table", 12, flags, new Vector2(0, -1)))'
Replace-Exact $buy @'
            ImGui.TableSetupColumn("ROI", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Liquidate", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
'@ @'
            ImGui.TableSetupColumn("ROI", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Live", ImGuiTableColumnFlags.WidthFixed, 85 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Liquidate", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
'@
Replace-Exact $buy @'
        SortableHeader(9, "ROI", BuySortColumn.Roi, "Potential profit divided by total acquisition cost.");
        SortableHeader(10, "Liquidate", BuySortColumn.Liquidation, "Estimated time to sell the full modeled position, not merely the first unit.");
'@ @'
        SortableHeader(9, "ROI", BuySortColumn.Roi, "Potential profit divided by total acquisition cost.");
        HeaderCell(10, "Live", "Native FFXIV verification state. Use the Live filter above to show only Verified, Changed, Refreshed or Not checked opportunities.");
        SortableHeader(11, "Liquidate", BuySortColumn.Liquidation, "Estimated time to sell the full modeled position, not merely the first unit.");
'@
Replace-Exact $buy @'
        ImGui.TableSetColumnIndex(9); ImGui.TextUnformatted(Percent(row.Roi));
        ImGui.TableSetColumnIndex(10); ImGui.TextUnformatted(Days(row.EstimatedLiquidationDays));
'@ @'
        ImGui.TableSetColumnIndex(9); ImGui.TextUnformatted(Percent(row.Roi));
        ImGui.TableSetColumnIndex(10); ImGui.TextUnformatted(LiveStateLabel(GetBuyLiveState(row)));
        ImGui.TableSetColumnIndex(11); ImGui.TextUnformatted(Days(row.EstimatedLiquidationDays));
'@

Replace-Exact $refresh @'
    public void StartForItem(uint itemId, string scope = "selected item")
    {
        if (IsRunning || !playerState.IsLoaded || itemId == 0)
            return;
        StartQueue(new[] { itemId }, scope, onlyStale: false);
    }
'@ @'
    public void StartForItem(uint itemId, string scope = "selected item")
    {
        if (IsRunning || !playerState.IsLoaded || itemId == 0)
            return;
        StartQueue(new[] { itemId }, scope, onlyStale: false);
    }

    /// <summary>
    /// Force-refreshes an arbitrary ranked item sequence. Should I Buy? uses this for its explicit
    /// native Deep Scan after Universalis discovery. Input order is preserved so the strongest
    /// opportunities are requested first; duplicate item IDs are collapsed by the queue.
    /// </summary>
    public void StartForItems(IEnumerable<uint> itemIds, string scope)
    {
        if (IsRunning || !playerState.IsLoaded)
            return;
        StartQueue(itemIds, scope, onlyStale: false);
    }
'@
Replace-Exact $refresh 'foreach (var itemId in itemIds.Distinct().Order())' 'foreach (var itemId in itemIds.Distinct())'

Replace-Exact $scanner 'new ProductInfoHeaderValue("ShouldI", "1.1.2")' 'new ProductInfoHeaderValue("ShouldI", "1.1.3")'
Replace-Exact $project '<Version>1.1.2.0</Version>' '<Version>1.1.3.0</Version>'

Write-Host 'v1.1.3 Buy deep-scan/filter patches applied successfully.'
