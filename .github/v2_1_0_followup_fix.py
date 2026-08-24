from pathlib import Path


def patch(path, old, new, label):
    p = Path(path)
    text = p.read_text(encoding='utf-8')
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected 1 match, got {count}')
    p.write_text(text.replace(old, new, 1), encoding='utf-8')

# Render quick cashflow glyphs through Dalamud's actual icon font.
patch(
    'ShouldISell/Windows/SuiteWindow.TycoonCashflow.cs',
    'using Dalamud.Bindings.ImGui;\nusing Dalamud.Interface.Utility;',
    'using Dalamud.Bindings.ImGui;\nusing Dalamud.Interface.Components;\nusing Dalamud.Interface.Utility;',
    'add icon component namespace')
patch(
    'ShouldISell/Windows/SuiteWindow.TycoonCashflow.cs',
    '// FontAwesome 5 Free glyphs are included in Dalamud\'s default UI font. Keep the common\n    // high-frequency categories one click away; the full category popup remains available.',
    '// Render these glyphs through ImGuiComponents.IconButton, which explicitly pushes Dalamud\'s\n    // icon font. Keep common high-frequency categories one click away; the full popup remains.',
    'icon font comment')
patch(
    'ShouldISell/Windows/SuiteWindow.TycoonCashflow.cs',
    'if (ImGui.SmallButton($"{entry.Icon}##gil-quick-{entry.Category}"))',
    'if (ImGuiComponents.IconButton($"{entry.Icon}##gil-quick-{entry.Category}"))',
    'icon button renderer')
patch(
    'ShouldISell/Windows/SuiteWindow.TycoonCashflow.cs',
    'vendorPurchaseUnitPrice = checked((int)Math.Min(int.MaxValue, match.Item.VendorGilShopPrice!.Value));',
    'vendorPurchaseUnitPrice = checked((int)Math.Min((uint)int.MaxValue, match.Item.VendorGilShopPrice!.Value));',
    'uint/int vendor price conversion')
patch(
    'ShouldISell/Windows/SuiteWindow.TycoonCashflow.cs',
    'ImGui.TextUnformatted($"Selected: {item.Name} • game-data vendor price {item.VendorGilShopPrice:N0}g/unit");',
    'ImGui.TextUnformatted($"Selected: {item.Name} • game-data vendor price {item.VendorGilShopPrice.Value:N0}g/unit");',
    'nullable vendor display')
patch(
    'ShouldISell/Windows/SuiteWindow.TycoonCashflow.cs',
    ': "Exclude this purchase lot from trading positions/P&L while keeping its real Market Board spending in the cashflow ledger.");',
    ': "Exclude this purchase lot from trading positions/P&L while keeping the real acquisition in your purchase/cashflow history.");',
    'purchase exclusion tooltip')

# User-entered vendor acquisitions are attribution evidence. Never let them be mistaken for MB buys;
# if the wallet delta arrives before/after the manual entry, reconcile it as Vendor instead.
p = Path('ShouldISell/Services/GilLedgerTracker.cs')
text = p.read_text(encoding='utf-8')
old = '''        var purchases = store.GetPurchases(contentId)\n            .Where(x => x.PurchasedAtUtc >= fromUtc.AddSeconds(-3) && x.PurchasedAtUtc <= toUtc.AddSeconds(6))\n            .OrderBy(x => x.PurchasedAtUtc)\n            .ToList();\n        var spending = -delta;\n        var exactSingle = purchases.FirstOrDefault(x => x.TotalCost == spending);\n        if (exactSingle is not null)\n        {\n            return (\n                GilFlowCategory.MarketBoardPurchase,\n                exactSingle.Strategy,\n                true,\n                $"Matched exact confirmed Market Board cost: {exactSingle.Quantity:N0} unit(s), {exactSingle.TotalCost:N0}g including buyer tax.");\n        }\n\n        var exactTotal = purchases.Sum(x => x.TotalCost);\n        if (purchases.Count > 1 && exactTotal == spending)\n        {\n            return (\n                GilFlowCategory.MarketBoardPurchase,\n                $"{purchases.Count:N0} Market Board purchases",\n                true,\n                $"Matched the wallet decrease to {purchases.Count:N0} confirmed Market Board purchases totaling {exactTotal:N0}g.");\n        }'''
new = '''        var recentPurchases = store.GetPurchases(contentId)\n            .Where(x => x.PurchasedAtUtc >= fromUtc.AddSeconds(-3) && x.PurchasedAtUtc <= toUtc.AddSeconds(6))\n            .OrderBy(x => x.PurchasedAtUtc)\n            .ToList();\n        var spending = -delta;\n\n        // A manually recorded vendor lot is explicit user attribution. If its exact cost matches\n        // this wallet decrease, classify it as Vendor rather than allowing the generic purchase\n        // matcher to call it a Market Board purchase.\n        var exactVendor = recentPurchases.FirstOrDefault(x =>\n            x.SourceKind == PurchaseSourceKind.VendorManual && x.TotalCost == spending);\n        if (exactVendor is not null)\n        {\n            return (\n                GilFlowCategory.Vendor,\n                $"Vendor purchase: {exactVendor.Strategy}",\n                false,\n                $"Matched user-entered vendor cost basis: {exactVendor.Quantity:N0} unit(s), {exactVendor.TotalCost:N0}g total.");\n        }\n\n        var purchases = recentPurchases\n            .Where(x => x.SourceKind == PurchaseSourceKind.MarketBoard)\n            .ToList();\n        var exactSingle = purchases.FirstOrDefault(x => x.TotalCost == spending);\n        if (exactSingle is not null)\n        {\n            return (\n                GilFlowCategory.MarketBoardPurchase,\n                exactSingle.Strategy,\n                true,\n                $"Matched exact confirmed Market Board cost: {exactSingle.Quantity:N0} unit(s), {exactSingle.TotalCost:N0}g including buyer tax.");\n        }\n\n        var exactTotal = purchases.Sum(x => x.TotalCost);\n        if (purchases.Count > 1 && exactTotal == spending)\n        {\n            return (\n                GilFlowCategory.MarketBoardPurchase,\n                $"{purchases.Count:N0} Market Board purchases",\n                true,\n                $"Matched the wallet decrease to {purchases.Count:N0} confirmed Market Board purchases totaling {exactTotal:N0}g.");\n        }'''
if text.count(old) != 1:
    raise RuntimeError(f'ClassifyDelta block: expected 1, got {text.count(old)}')
text = text.replace(old, new, 1)
old2 = '''        var purchases = store.GetPurchases(contentId)\n            .Where(x => now - x.PurchasedAtUtc <= TimeSpan.FromSeconds(40))\n            .ToList();\n\n        foreach (var flow in unknown)\n        {\n            var exact = purchases\n                .Where(x => x.TotalCost == -flow.Amount)\n                .OrderBy(x => Math.Abs((x.PurchasedAtUtc - flow.AtUtc).TotalSeconds))\n                .FirstOrDefault(x => Math.Abs((x.PurchasedAtUtc - flow.AtUtc).TotalSeconds) <= 10);\n            if (exact is null)\n                continue;\n\n            store.UpdateGilFlowClassification(\n                flow.Id,\n                GilFlowCategory.MarketBoardPurchase,\n                exact.Strategy,\n                autoClassified: true,\n                note: $"Reconciled to exact confirmed Market Board purchase: {exact.Quantity:N0} unit(s), {exact.TotalCost:N0}g including buyer tax.",\n                flush: false);\n        }'''
new2 = '''        var purchases = store.GetPurchases(contentId)\n            .Where(x => now - x.PurchasedAtUtc <= TimeSpan.FromSeconds(40))\n            .ToList();\n\n        foreach (var flow in unknown)\n        {\n            var exactVendor = purchases\n                .Where(x => x.SourceKind == PurchaseSourceKind.VendorManual && x.TotalCost == -flow.Amount)\n                .OrderBy(x => Math.Abs((x.PurchasedAtUtc - flow.AtUtc).TotalSeconds))\n                .FirstOrDefault(x => Math.Abs((x.PurchasedAtUtc - flow.AtUtc).TotalSeconds) <= 10);\n            if (exactVendor is not null)\n            {\n                store.UpdateGilFlowClassification(\n                    flow.Id,\n                    GilFlowCategory.Vendor,\n                    $"Vendor purchase: {exactVendor.Strategy}",\n                    autoClassified: false,\n                    note: $"Reconciled to user-entered vendor purchase: {exactVendor.Quantity:N0} unit(s), {exactVendor.TotalCost:N0}g total.",\n                    flush: false);\n                continue;\n            }\n\n            var exact = purchases\n                .Where(x => x.SourceKind == PurchaseSourceKind.MarketBoard && x.TotalCost == -flow.Amount)\n                .OrderBy(x => Math.Abs((x.PurchasedAtUtc - flow.AtUtc).TotalSeconds))\n                .FirstOrDefault(x => Math.Abs((x.PurchasedAtUtc - flow.AtUtc).TotalSeconds) <= 10);\n            if (exact is null)\n                continue;\n\n            store.UpdateGilFlowClassification(\n                flow.Id,\n                GilFlowCategory.MarketBoardPurchase,\n                exact.Strategy,\n                autoClassified: true,\n                note: $"Reconciled to exact confirmed Market Board purchase: {exact.Quantity:N0} unit(s), {exact.TotalCost:N0}g including buyer tax.",\n                flush: false);\n        }'''
if text.count(old2) != 1:
    raise RuntimeError(f'Reconcile block: expected 1, got {text.count(old2)}')
text = text.replace(old2, new2, 1)
p.write_text(text, encoding='utf-8')

print('v2.1.0 follow-up fixes applied')
