from pathlib import Path


def replace_once(path: str, old: str, new: str):
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    if old not in text:
        raise SystemExit(f"missing target in {path}: {old[:120]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


# TraderSnapshot: keep trade-level ROI, but also expose the denominator and a conservative
# realized-return-on-all-tracked-spend metric for the Tycoon headline.
replace_once(
    "ShouldISell/TradingModels.cs",
    """    double CapitalInvested,\n    double RealizedRevenue,\n    double RealizedProfit,\n    double RealizedRoi,\n    double WinRate,""",
    """    double CapitalInvested,\n    double RealizedCostBasis,\n    double RealizedRevenue,\n    double RealizedProfit,\n    double RealizedReturnOnTrackedSpend,\n    double RealizedRoi,\n    double WinRate,""")

replace_once(
    "ShouldISell/Services/TraderAnalyzer.cs",
    """        var capitalInvested = purchases.Sum(x => (double)x.TotalCost);\n        var realizedRevenue = closed.Sum(x => x.NetRevenue);\n        var realizedCost = closed.Sum(x => x.CostBasis);\n        var realizedProfit = realizedRevenue - realizedCost;\n        var realizedRoi = realizedCost > 0 ? realizedProfit / realizedCost : 0;\n        var winRate = closed.Count > 0 ? closed.Count(x => x.Profit > 0) / (double)closed.Count : 0;""",
    """        var capitalInvested = purchases.Sum(x => (double)x.TotalCost);\n        var realizedRevenue = closed.Sum(x => x.NetRevenue);\n        var realizedCost = closed.Sum(x => x.CostBasis);\n        var realizedProfit = realizedRevenue - realizedCost;\n\n        // These are intentionally two different return measures.\n        // - RealizedRoi is return on the cost basis that has actually been SOLD. This is the\n        //   correct trade/strategy ROI and can legitimately be enormous for a very cheap flip.\n        // - RealizedReturnOnTrackedSpend answers the portfolio-style question the Tycoon headline\n        //   visually implies: how much realized profit exists relative to ALL tracked Trade spend,\n        //   including purchase lots that are still open. Never substitute one denominator for the other.\n        var realizedRoi = realizedCost > 0 ? realizedProfit / realizedCost : 0;\n        var realizedReturnOnTrackedSpend = capitalInvested > 0 ? realizedProfit / capitalInvested : 0;\n        var winRate = closed.Count > 0 ? closed.Count(x => x.Profit > 0) / (double)closed.Count : 0;""")

replace_once(
    "ShouldISell/Services/TraderAnalyzer.cs",
    """            capitalInvested,\n            realizedRevenue,\n            realizedProfit,\n            realizedRoi,\n            winRate,""",
    """            capitalInvested,\n            realizedCost,\n            realizedRevenue,\n            realizedProfit,\n            realizedReturnOnTrackedSpend,\n            realizedRoi,\n            winRate,""")

replace_once(
    "ShouldISell/Services/TraderAnalyzer.cs",
    """            0, 0, 0, 0,\n            0, 0, 0, 0, 0, 0,\n            0, null, null,""",
    """            0, 0, 0, 0,\n            0, 0, 0, 0, 0, 0, 0, 0,\n            0, null, null,""")

# Tycoon headline: put each percentage next to the denominator it actually uses.
replace_once(
    "ShouldISell/Windows/SuiteWindow.Tycoon.cs",
    """        if (ImGui.BeginTable(\"##tycoon-metrics\", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))\n        {\n            MetricCell(0, \"Capital invested\", Gil(snapshot.CapitalInvested));\n            MetricCell(1, \"Realized profit\", Gil(snapshot.RealizedProfit));\n            MetricCell(2, \"Realized ROI\", Percent(snapshot.RealizedRoi));\n            MetricCell(3, \"Win rate\", Percent(snapshot.WinRate));\n            ImGui.TableNextRow();\n            MetricCell(0, \"Median holding\", Days(snapshot.MedianHoldingDays));\n            MetricCell(1, \"Open cost basis\", Gil(snapshot.OpenCostBasis));\n            MetricCell(2, \"Open est. net value\", Gil(snapshot.OpenEstimatedNetValue));\n            MetricCell(3, \"Unrealized est.\", Gil(snapshot.UnrealizedProfit));\n            ImGui.TableNextRow();\n            MetricCell(0, \"Trade purchases\", snapshot.PurchaseCount.ToString(\"N0\"));\n            MetricCell(1, \"Matched sale events\", snapshot.TrackedSaleCount.ToString(\"N0\"));\n            MetricCell(2, \"Closed units\", snapshot.ClosedUnits.ToString(\"N0\"));\n            MetricCell(3, \"Open tracked units\", snapshot.OpenUnits.ToString(\"N0\"));\n            ImGui.EndTable();\n        }\n\n        if (snapshot.UnmatchedSaleUnits > 0)""",
    """        if (ImGui.BeginTable(\"##tycoon-metrics\", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))\n        {\n            MetricCell(0, \"Tracked trade spend\", Gil(snapshot.CapitalInvested));\n            MetricCell(1, \"Realized profit\", Gil(snapshot.RealizedProfit));\n            MetricCell(2, \"Realized return / spend\", Percent(snapshot.RealizedReturnOnTrackedSpend));\n            MetricCell(3, \"Win rate (sale events)\", Percent(snapshot.WinRate));\n            ImGui.TableNextRow();\n            MetricCell(0, \"Closed cost basis\", Gil(snapshot.RealizedCostBasis));\n            MetricCell(1, \"Closed net revenue\", Gil(snapshot.RealizedRevenue));\n            MetricCell(2, \"Closed-trade ROI\", Percent(snapshot.RealizedRoi));\n            MetricCell(3, \"Median holding\", Days(snapshot.MedianHoldingDays));\n            ImGui.TableNextRow();\n            MetricCell(0, \"Open cost basis\", Gil(snapshot.OpenCostBasis));\n            MetricCell(1, \"Open est. net value\", Gil(snapshot.OpenEstimatedNetValue));\n            MetricCell(2, \"Unrealized est.\", Gil(snapshot.UnrealizedProfit));\n            MetricCell(3, \"Open tracked units\", snapshot.OpenUnits.ToString(\"N0\"));\n            ImGui.TableNextRow();\n            MetricCell(0, \"Trade purchases\", snapshot.PurchaseCount.ToString(\"N0\"));\n            MetricCell(1, \"Matched sale events\", snapshot.TrackedSaleCount.ToString(\"N0\"));\n            MetricCell(2, \"Closed units\", snapshot.ClosedUnits.ToString(\"N0\"));\n            MetricCell(3, \"Unmatched-cost units\", snapshot.UnmatchedSaleUnits.ToString(\"N0\"));\n            ImGui.EndTable();\n        }\n\n        ImGui.TextDisabled(\"Realized return / spend = realized profit ÷ all tracked Trade purchase cost. Closed-trade ROI = realized profit ÷ the cost basis of sold tracked units only.\");\n        ImGui.TextDisabled(\"Closed-trade ROI is intentionally not capped: a 100g lot sold for 3,500g really is ~3,400% ROI on that closed lot, even if thousands of gil remain tied up in other open positions.\");\n        if (snapshot.RealizedRoi >= 10.0 && snapshot.RealizedCostBasis > 0)\n            ImGui.TextWrapped(\"Very high closed-trade ROI detected. Check Closed Trades to verify the FIFO attribution. FFXIV cannot tell Tycoon whether an identical sold unit came from a tracked purchase or from pre-existing crafted/gathered/gifted stock; if the sale was not from that purchase lot, mark the purchase Personal so Tycoon does not invent cost-basis profit.\");\n\n        if (snapshot.UnmatchedSaleUnits > 0)""")

replace_once(
    "ShouldISell/Windows/SuiteWindow.Tycoon.cs",
    "ImGui.TextDisabled(\"A row represents the tracked portion of one captured retainer-sale event. If a sale consumed several purchase lots, their cost basis and predictions are quantity-weighted.\");",
    "ImGui.TextDisabled(\"A row represents the tracked portion of one captured retainer-sale event. ROI on cost = (net revenue - matched FIFO cost basis) ÷ matched FIFO cost basis. If a sale consumed several purchase lots, their cost basis and predictions are quantity-weighted.\");")
replace_once("ShouldISell/Windows/SuiteWindow.Tycoon.cs", "ImGui.TableSetupColumn(\"ROI\", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);", "ImGui.TableSetupColumn(\"ROI on cost\", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);")
replace_once("ShouldISell/Windows/SuiteWindow.Tycoon.cs", "ImGui.TableSetupColumn(\"ROI\", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);", "ImGui.TableSetupColumn(\"Closed ROI\", ImGuiTableColumnFlags.WidthFixed, 75 * ImGuiHelpers.GlobalScale);")
replace_once("ShouldISell/Windows/SuiteWindow.Tycoon.cs", "ImGui.TableSetupColumn(\"ROI\", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);", "ImGui.TableSetupColumn(\"Closed ROI\", ImGuiTableColumnFlags.WidthFixed, 75 * ImGuiHelpers.GlobalScale);")

# Release/version identity.
replace_once("ShouldISell/ShouldISell.csproj", "<Version>2.0.0.0</Version>", "<Version>2.0.1.0</Version>")
replace_once("ShouldISell/Services/BuyOpportunityScanner.cs", 'new ProductInfoHeaderValue("ShouldI", "2.0.0")', 'new ProductInfoHeaderValue("ShouldI", "2.0.1")')

Path("RELEASE_NOTES_v2.0.1.md").write_text("""# Should I? v2.0.1\n\nv2.0.1 fixes the Tycoon ROI presentation that could make a perfectly ordinary portfolio look like it had a multi-thousand-percent overall return.\n\n## ROI/accounting semantics\n\n- The Tycoon headline no longer labels closed-lot ROI as though it were return on all invested gil.\n- **Realized return / spend** is now `realized profit ÷ all tracked Trade purchase cost`, including lots that are still open. This is the conservative top-line number that belongs beside tracked trade spend.\n- **Closed-trade ROI** remains the standard trade return-on-cost calculation: `realized profit ÷ cost basis of sold tracked units`. It is kept because it is the correct metric for comparing completed trades, items and strategies.\n- The exact **closed cost basis** and **closed net revenue** are now shown next to closed-trade ROI so the denominator is never hidden.\n- Closed Trades now says **ROI on cost**; Best Items and Strategies say **Closed ROI**.\n- Win rate is explicitly labeled as sale-event based.\n\n## Extreme ROI safety\n\nTycoon does not clamp or cosmetically suppress legitimate extreme returns. A 100g acquisition sold for 3,500g really is roughly 3,400% ROI on that closed lot.\n\nHowever, identical FFXIV items are fungible and the game does not expose provenance for a sold unit. If you bought one unit while already owning crafted/gathered/gifted copies, FIFO can only be an accounting convention. When aggregate closed ROI is extremely high, Tycoon now warns you to verify the Closed Trades row and mark the purchase **Personal** if that sale did not actually come from the tracked purchase lot.\n\nThis keeps unknown cost basis unknown instead of silently turning ambiguous stock into fictional trading profit.\n""", encoding="utf-8")

print("v2.0.1 ROI/accounting patch applied")
