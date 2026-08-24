from pathlib import Path

def replace(path, old, new, label):
    p = Path(path)
    text = p.read_text(encoding='utf-8')
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected 1 match, got {count}')
    p.write_text(text.replace(old, new, 1), encoding='utf-8')

# Manual vendor entry: editing the search invalidates an old selection, and prediction metadata
# reflects the actual user-entered lot instead of blindly copying a differently-sized package.
replace(
    'ShouldISell/Windows/SuiteWindow.TycoonCashflow.cs',
    '''        ImGui.SetNextItemWidth(360 * ImGuiHelpers.GlobalScale);\n        ImGui.InputTextWithHint("##vendor-purchase-search", "Search normal-gil vendor item...", ref vendorPurchaseSearch, 128);\n        var matches = string.IsNullOrWhiteSpace(vendorPurchaseSearch)''',
    '''        ImGui.SetNextItemWidth(360 * ImGuiHelpers.GlobalScale);\n        if (ImGui.InputTextWithHint("##vendor-purchase-search", "Search normal-gil vendor item...", ref vendorPurchaseSearch, 128) &&\n            vendorPurchaseItemId != 0 &&\n            !string.Equals(plugin.Catalog.Get(vendorPurchaseItemId).Name, vendorPurchaseSearch, StringComparison.CurrentCultureIgnoreCase))\n        {\n            vendorPurchaseItemId = 0;\n        }\n        var matches = string.IsNullOrWhiteSpace(vendorPurchaseSearch)''',
    'invalidate stale vendor selection')
replace(
    'ShouldISell/Windows/SuiteWindow.TycoonCashflow.cs',
    '''            var prediction = plugin.BuyScanner.GetVendorOpportunities()\n                .Where(x => x.WorldId == Plugin.PlayerState.CurrentWorld.RowId && x.Item.ItemId == vendorPurchaseItemId && !x.IsHq)\n                .OrderByDescending(x => x.OpportunityScore)\n                .FirstOrDefault();\n            var now = DateTimeOffset.UtcNow;\n            var purchase = new PersonalPurchase(''',
    '''            var prediction = GetCurrentWorldVendorOpportunities()\n                .Where(x => x.Item.ItemId == vendorPurchaseItemId && !x.IsHq)\n                .OrderByDescending(x => x.OpportunityScore)\n                .FirstOrDefault();\n            var predictionQuantityMatches = prediction is not null && prediction.AcquireQuantity == vendorPurchaseQuantity;\n            var predictedProfit = prediction?.NetExitUnitPrice is { } predictedNet\n                ? predictedNet * (double)vendorPurchaseQuantity - total\n                : (double?)null;\n            var now = DateTimeOffset.UtcNow;\n            var purchase = new PersonalPurchase(''',
    'use displayed vendor recommendation')
replace(
    'ShouldISell/Windows/SuiteWindow.TycoonCashflow.cs',
    '''                prediction?.PredictedExitUnitPrice,''',
    '''                prediction?.PredictedExitUnitPrice,''',
    'noop guard') if False else None
replace(
    'ShouldISell/Windows/SuiteWindow.TycoonCashflow.cs',
    '''                prediction?.SuggestedExitUnitPrice,\n                prediction?.EstimatedLiquidationDays,\n                prediction?.PotentialProfit,\n                prediction?.AnalysedAtUtc,''',
    '''                prediction?.SuggestedExitUnitPrice,\n                predictionQuantityMatches ? prediction?.EstimatedLiquidationDays : null,\n                predictedProfit,\n                prediction?.AnalysedAtUtc,''',
    'scale vendor prediction metadata')

# User-facing Tycoon wording now covers both automatic MB and manual vendor acquisition cost basis.
replace(
    'ShouldISell/Windows/SuiteWindow.Tycoon.cs',
    '''        if (snapshot.PurchaseCount == 0)\n            ImGui.TextDisabled("Purchase tracking starts automatically for successful Market Board buys while Should I? is running. No manual trade entry is required.");''',
    '''        if (snapshot.PurchaseCount == 0)\n            ImGui.TextDisabled("Successful Market Board buys are captured automatically. Normal-gil vendor acquisitions can be entered manually under Purchases when you want their cost basis included in trading P&L.");''',
    'profile empty wording')
replace(
    'ShouldISell/Windows/SuiteWindow.Tycoon.cs',
    '''            ImGui.TextDisabled("No matched strategy history yet. Purchases made from a current Should I Buy? recommendation are tagged automatically; other successful Market Board buys are recorded as manual buys.");''',
    '''            ImGui.TextDisabled("No matched strategy history yet. Market Board purchases inherit a current Should I Buy? strategy when matched; manually recorded vendor acquisitions inherit the current Vendor -> Market recommendation when available.");''',
    'strategy empty wording')
replace(
    'ShouldISell/Windows/SuiteWindow.Tycoon.cs',
    '''        ImGui.TextWrapped("Whenever you buy an exact listing that came from the current Should I Buy? results, Tycoon stores the model's predicted exit price, liquidation time and opportunity score with the real cost basis. When Should I Sell? later captures the retainer sale, these predictions can be compared with reality.");''',
    '''        ImGui.TextWrapped("For an exact Market Board recommendation — and for a manually recorded vendor acquisition tied to the current Vendor -> Market model — Tycoon stores available exit/score predictions with the real cost basis. When Should I Sell? later captures the retainer sale, those predictions can be compared with reality. Vendor liquidation-time calibration is only stored when the entered quantity matches the recommendation.");''',
    'model accuracy wording')
replace(
    'ShouldISell/Services/TraderAnalyzer.cs',
    '''            return ("Building history", "Tycoon has started tracking your real Market Board cost basis. A clearer trading style will emerge after a few matched purchases and retainer sales.");''',
    '''            return ("Building history", "Tycoon has started tracking your real acquisition cost basis. A clearer trading style will emerge after a few matched purchases and retainer sales.");''',
    'profile acquisition wording')

print('v2.1.0 polish applied')
