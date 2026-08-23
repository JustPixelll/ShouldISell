using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using ShouldISell.Services;

namespace ShouldISell.Windows;

public enum BuySortColumn
{
    Rating,
    Item,
    Strategy,
    BuyQuantity,
    Cost,
    ExitPrice,
    StackSize,
    PotentialProfit,
    RiskAdjustedProfit,
    Roi,
    Liquidation,
}

public sealed partial class SuiteWindow
{
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

        DrawBuyControls();
        DrawBuyScreenerAndDeepScan();
        DrawBuyPortfolio();
        ImGui.Separator();
        DrawBuyResults();
    }

    private void DrawBuyControls()
    {
        var c = plugin.Configuration;
        if (ImGui.CollapsingHeader("Capital, risk & scanner scope", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var budget = c.BuyBudgetGil;
            ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Budget (gil)", ref budget, 10_000, 100_000))
            {
                c.BuyBudgetGil = Math.Clamp(budget, 1_000, 999_999_999);
                c.Save();
            }
            Tooltip("Maximum gil the scanner and budget portfolio may commit. Individual trades are also constrained by the per-item budget percentage below.");

            var minProfit = c.BuyMinimumProfitGil;
            ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Minimum potential profit", ref minProfit, 500, 5_000))
            {
                c.BuyMinimumProfitGil = Math.Clamp(minProfit, 0, c.BuyBudgetGil);
                c.Save();
            }
            Tooltip("Reject opportunities whose modeled after-tax exit would not produce at least this much total profit. This is potential profit, before risk adjustment.");

            var minRoi = c.BuyMinimumRoiPercent;
            ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat("Minimum ROI %", ref minRoi, 1, 5, "%.1f"))
            {
                c.BuyMinimumRoiPercent = Math.Clamp(minRoi, 0, 1000);
                c.Save();
            }
            Tooltip("Minimum modeled return on the acquisition cost. Example: 20% means a 100,000g purchase must model at least 20,000g potential profit.");

            var hold = c.BuyMaximumHoldingDays;
            ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat("Maximum expected holding (days)", ref hold, 0.5f, 2, "%.1f"))
            {
                c.BuyMaximumHoldingDays = Math.Clamp(hold, 0.25f, 365f);
                c.Save();
            }
            Tooltip("Reject market exits expected to take longer than this to liquidate the modeled position. Lower values favor faster turnover.");

            var maxItem = c.BuyMaximumInvestmentPercentPerItem;
            ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Maximum budget per item %", ref maxItem, 1, 5))
            {
                c.BuyMaximumInvestmentPercentPerItem = Math.Clamp(maxItem, 1, 100);
                c.Save();
            }
            Tooltip("Maximum share of your total scanner budget that may be tied up in one item/HQ variant. This prevents one trade from consuming the whole bankroll.");
            var effectivePerItemCap = Math.Min(
                (long)c.BuyBudgetGil,
                Math.Max(1L, (long)c.BuyBudgetGil * Math.Clamp(c.BuyMaximumInvestmentPercentPerItem, 1, 100) / 100L));
            ImGui.TextDisabled($"Effective per-item acquisition cap: {effectivePerItemCap:N0}g.");
            Tooltip("This is a hard acquisition-package limit. Example: a 500,000g budget at 25% permits at most about 125,000g in one item/HQ variant, so more expensive flips are intentionally excluded.");

            var maxPositions = c.BuyPortfolioMaxPositions;
            ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Portfolio basket max items", ref maxPositions, 1, 2))
            {
                c.BuyPortfolioMaxPositions = Math.Clamp(maxPositions, 1, 20);
                buyPortfolioPlan = null;
                c.Save();
            }
            Tooltip("Hard cap on the number of distinct item/HQ positions the budget optimizer may place in one recommended basket. The allocator enforces this during optimization, not by trimming afterward.");

            var deepLimit = c.BuyDeepCandidateLimit;
            ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Detailed Universalis item limit", ref deepLimit, 10, 50))
            {
                c.BuyDeepCandidateLimit = Math.Clamp(deepLimit, 20, 500);
                c.Save();
            }
            Tooltip("After discovery, only this many item IDs receive detailed Universalis current listings plus 90-day history. The shortlist is diversified: roughly one third of its slots protect large plausible absolute-gil gaps from being crowded out by tiny extreme-ROI items. DC/region aggregate values may rescue rare items for this detailed look, but final recommendations still require current-world detailed evidence. LIVE VERIFY remains the native FFXIV check.");

            ImGui.Spacing();
            var marketFlip = c.BuyEnableMarketToMarket;
            if (ImGui.Checkbox("Market → Market", ref marketFlip))
            {
                c.BuyEnableMarketToMarket = marketFlip;
                c.Save();
            }
            Tooltip("Buy one or more Market Board listings and resell them using the shared Should I Sell? exit model. NQ items sold by a normal gil vendor are deliberately excluded: their supply is renewable, so buying out cheap player listings cannot be assumed to create scarcity.");

            ImGui.SameLine();
            var vendorMarket = c.BuyEnableVendorToMarket;
            if (ImGui.Checkbox("Vendor → Market", ref vendorMarket))
            {
                c.BuyEnableVendorToMarket = vendorMarket;
                c.Save();
            }
            Tooltip("Buy from a verified normal gil NPC vendor and resell on the Market Board. Because this supply is renewable, the model targets only one working listing (maximum 99 units) and never relies on buying out competing player listings.");

            ImGui.SameLine();
            var marketVendor = c.BuyEnableMarketToVendor;
            if (ImGui.Checkbox("Market → Vendor", ref marketVendor))
            {
                c.BuyEnableMarketToVendor = marketVendor;
                c.Save();
            }
            Tooltip("Look for Market Board listings whose total acquisition cost is below the item's guaranteed NPC buyback value.");

            var equipment = c.BuyIncludeEquipment;
            if (ImGui.Checkbox("Include equippable gear", ref equipment))
            {
                c.BuyIncludeEquipment = equipment;
                c.Save();
            }
            Tooltip("Include equippable items in discovery. Gear markets can be slower and more fragmented than materials, so this is off by default.");
            if (!c.BuyIncludeEquipment)
                ImGui.TextDisabled("High-ticket equippable gear/glamour opportunities are excluded while this is off.");

            var categoryFilter = c.BuyUseCategoryFilter;
            if (ImGui.Checkbox("Filter by FFXIV item UI categories", ref categoryFilter))
            {
                c.BuyUseCategoryFilter = categoryFilter;
                if (categoryFilter && c.BuyIncludedCategoryIds.Count == 0)
                    c.BuyIncludedCategoryIds = plugin.Catalog.GetCategories().Select(x => x.CategoryId).ToList();
                c.Save();
            }
            Tooltip("Restrict discovery to selected in-game item UI categories. Disable this to scan the full marketable universe allowed by the other filters.");

            if (c.BuyUseCategoryFilter)
                DrawBuyCategoryScope();
        }

        ImGui.Spacing();
        if (!plugin.BuyScanner.IsScanning)
        {
            if (ImGui.Button("DISCOVER GOOD BUYS (UNIVERSALIS)"))
            {
                selectedBuyOpportunity = null;
                buyDetailsOpen = false;
                buyPortfolioPlan = null;
                _ = plugin.BuyScanner.ScanAsync();
            }
            Tooltip("This is the only action that starts the broad market-universe pass. It then uses detailed Universalis listings/history for the strongest candidates. LIVE VERIFY on an item is separate and never starts this broad pass.");
        }
        else if (ImGui.Button("Stop scan"))
        {
            plugin.BuyScanner.CancelScan();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(plugin.BuyScanner.Status);

        if (plugin.BuyScanner.IsScanning)
        {
            if (plugin.BuyScanner.BroadItemsTotal > 0 && plugin.BuyScanner.BroadItemsScanned < plugin.BuyScanner.BroadItemsTotal)
            {
                var fraction = plugin.BuyScanner.BroadItemsScanned / (float)Math.Max(1, plugin.BuyScanner.BroadItemsTotal);
                ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"Discovery {plugin.BuyScanner.BroadItemsScanned:N0}/{plugin.BuyScanner.BroadItemsTotal:N0}");
            }
            else if (plugin.BuyScanner.DeepItemsTotal > 0)
            {
                var fraction = plugin.BuyScanner.DeepItemsScanned / (float)Math.Max(1, plugin.BuyScanner.DeepItemsTotal);
                ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"Detailed Universalis {plugin.BuyScanner.DeepItemsScanned:N0}/{plugin.BuyScanner.DeepItemsTotal:N0}");
            }
        }
    }

    private void DrawBuyCategoryScope()
    {
        var c = plugin.Configuration;
        var categories = plugin.Catalog.GetCategories();
        ImGui.SetNextItemWidth(280 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##buy-category-search", "Filter category names...", ref buyCategorySearch, 96);
        Tooltip("Filter this category checklist by name. It does not change the scanner until categories themselves are checked or unchecked.");
        ImGui.SameLine();
        if (ImGui.SmallButton("All"))
        {
            c.BuyIncludedCategoryIds = categories.Select(x => x.CategoryId).ToList();
            c.Save();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("None"))
        {
            c.BuyIncludedCategoryIds.Clear();
            c.Save();
        }

        var selected = c.BuyIncludedCategoryIds.ToHashSet();
        if (ImGui.BeginChild("##buy-category-scope", new Vector2(0, 150 * ImGuiHelpers.GlobalScale), true))
        {
            foreach (var category in categories.Where(x =>
                         string.IsNullOrWhiteSpace(buyCategorySearch) ||
                         x.Name.Contains(buyCategorySearch, StringComparison.CurrentCultureIgnoreCase)))
            {
                var enabled = selected.Contains(category.CategoryId);
                if (!ImGui.Checkbox($"{category.Name}##buy-cat-{category.CategoryId}", ref enabled))
                    continue;
                if (enabled)
                    selected.Add(category.CategoryId);
                else
                    selected.Remove(category.CategoryId);
                c.BuyIncludedCategoryIds = selected.Order().ToList();
                c.Save();
            }
            ImGui.EndChild();
        }
        ImGui.TextDisabled($"{selected.Count:N0} of {categories.Count:N0} categories selected.");
    }

    private void DrawBuyPortfolio()
    {
        var opportunities = GetFilteredBuyOpportunities();
        if (opportunities.Count == 0 || plugin.BuyScanner.IsScanning)
            return;

        if (buyPortfolioPlan is { } oldPlan && oldPlan.Selections.Any(x => x.WorldId != CurrentBuyWorldId))
            buyPortfolioPlan = null;

        var c = plugin.Configuration;
        ImGui.Spacing();
        if (ImGui.Button("BUILD BUDGET PORTFOLIO"))
            buyPortfolioPlan = PortfolioAllocator.Build(opportunities, c.BuyBudgetGil, c.BuyPortfolioMaxPositions);
        Tooltip("Build a basket that chooses at most one strategy/package per item/HQ variant, maximizes total risk-adjusted profit, stays under budget, and respects your basket-size cap.");

        if (buyPortfolioPlan is not { } plan)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"Optional: allocate your budget across up to {c.BuyPortfolioMaxPositions:N0} opportunities instead of ranking one trade at a time.");
            return;
        }

        if (plan.BudgetGil != c.BuyBudgetGil || plan.MaxPositions != c.BuyPortfolioMaxPositions)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Portfolio settings changed — rebuild to update the basket.");
        }

        ImGui.TextWrapped($"Portfolio: invest {plan.InvestedGil:N0}g of {plan.BudgetGil:N0}g across {plan.Selections.Count:N0}/{plan.MaxPositions:N0} allowed position(s), leaving {plan.ReserveGil:N0}g unallocated. The optimizer may deliberately keep cash when the scan does not contain enough worthwhile opportunities.");
        if (ImGui.BeginTable("##buy-portfolio-metrics", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableNextRow();
            MetricCell(0, "Invested", Gil(plan.InvestedGil), "Actual gil allocated by the basket. Reserve remains unused.");
            MetricCell(1, "Potential profit", Gil(plan.PotentialProfit), "Sum of modeled after-tax upside if every recommended exit succeeds as modeled.");
            MetricCell(2, "Risk-adjusted profit", Gil(plan.RiskAdjustedProfit), "Potential upside discounted by confidence, liquidity and stability. This is the allocator's main economic objective.");
            MetricCell(3, "Weighted score", plan.WeightedOpportunityScore.ToString("0.0"), "Opportunity scores weighted by invested gil across the selected basket.");
            ImGui.EndTable();
        }

        if (plan.Selections.Count == 0)
        {
            ImGui.TextDisabled("No current opportunity survived the configured scanner filters, budget and basket constraints.");
            return;
        }

        if (ImGui.CollapsingHeader($"Recommended basket ({plan.Selections.Count:N0})##buy-portfolio-basket", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable;
            if (ImGui.BeginTable("##buy-portfolio-table", 10, flags))
            {
                ImGui.TableSetupColumn("Rating", ImGuiTableColumnFlags.WidthFixed, 118 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Strategy", ImGuiTableColumnFlags.WidthFixed, 120 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Buy", ImGuiTableColumnFlags.WidthFixed, 50 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 85 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Potential", ImGuiTableColumnFlags.WidthFixed, 85 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Risk adj.", ImGuiTableColumnFlags.WidthFixed, 85 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("ROI", ImGuiTableColumnFlags.WidthFixed, 60 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Live", ImGuiTableColumnFlags.WidthFixed, 82 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Liquidate", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
                DrawPortfolioHeaders();

                foreach (var row in plan.Selections)
                    DrawPortfolioRow(row);

                ImGui.EndTable();
            }
        }
    }

    private void DrawPortfolioHeaders()
    {
        ImGui.TableNextRow();
        HeaderCell(0, "Rating", "Stars are broad quality bands; the 0–100 score is the stricter ranking used to compare opportunities.");
        HeaderCell(1, "Item", "Item and HQ/NQ variant selected for this basket position.");
        HeaderCell(2, "Strategy", "Economic route used by the recommendation: market flip, sweep, split, consolidate, vendor-to-market or market-to-vendor.");
        HeaderCell(3, "Buy", "Number of new units to acquire for this position.");
        HeaderCell(4, "Cost", "Total acquisition cost for the recommended package, including reported buyer tax on Market Board listings.");
        HeaderCell(5, "Potential", "Modeled profit if the recommended exit succeeds at the target net value.");
        HeaderCell(6, "Risk adj.", "Potential profit discounted by evidence confidence, liquidation speed and market stability.");
        HeaderCell(7, "ROI", "Potential profit divided by acquisition cost.");
        HeaderCell(8, "Live", "Native FFXIV verification state for this recommendation: Verified, Changed, Refreshed, or Not checked.");
        HeaderCell(9, "Liquidate", "Estimated time for the modeled resulting position to fully sell, including queue time where applicable.");
    }

    private void DrawPortfolioRow(BuyOpportunity row)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        var selected = selectedBuyOpportunity == row && buyDetailsOpen;
        if (ImGui.Selectable($"{Stars(row.Stars)} {row.OpportunityScore:0}##portfolio-{row.Item.ItemId}-{row.IsHq}-{row.Kind}", selected, ImGuiSelectableFlags.SpanAllColumns))
        {
            selectedBuyOpportunity = row;
            buyDetailsOpen = true;
        }
        Tooltip("Click anywhere on this highlighted row to open the full opportunity analysis.");
                ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(row.Item.Name + (row.IsHq ? " [HQ]" : string.Empty));
        ItemNameContextMenu($"##copy-buy-name-{row.Item.ItemId}-{row.IsHq}-{row.Kind}-{row.AcquisitionCost}", row.Item.Name);
        ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(row.StrategyLabel);
        ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(row.AcquireQuantity.ToString("N0"));
        ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(Gil(row.AcquisitionCost));
        ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(Gil(row.PotentialProfit));
        ImGui.TableSetColumnIndex(6); ImGui.TextUnformatted(Gil(row.RiskAdjustedProfit));
        ImGui.TableSetColumnIndex(7); ImGui.TextUnformatted(Percent(row.Roi));
        ImGui.TableSetColumnIndex(8); ImGui.TextUnformatted(LiveStateLabel(GetBuyLiveState(row)));
        ImGui.TableSetColumnIndex(9); ImGui.TextUnformatted(Days(row.EstimatedLiquidationDays));
    }

    private void DrawBuyResults()
    {
        var rows = SortBuyRows(GetFilteredBuyOpportunities());

        ImGui.TextDisabled($"{rows.Count:N0} visible opportunity package(s). Click a header to sort. Click anywhere on a row to open its full analysis.");

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
        if (ImGui.BeginTable("##buy-opportunity-table", 12, flags, new Vector2(0, -1)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Rating", ImGuiTableColumnFlags.WidthFixed, 120 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Strategy", ImGuiTableColumnFlags.WidthFixed, 120 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Buy", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Exit @", ImGuiTableColumnFlags.WidthFixed, 75 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Stack", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Profit", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Risk adj.", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("ROI", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Live", ImGuiTableColumnFlags.WidthFixed, 85 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Liquidate", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
            DrawSortableBuyHeaders();

            foreach (var row in rows)
                DrawBuyOpportunityRow(row);

            ImGui.EndTable();
        }
    }

    private void DrawSortableBuyHeaders()
    {
        ImGui.TableNextRow();
        SortableHeader(0, "Rating", BuySortColumn.Rating, "Overall 0–100 opportunity score plus 1–5 star band. Score combines ROI, profit, liquidity, price advantage, demand, stability, confidence and execution friction.");
        SortableHeader(1, "Item", BuySortColumn.Item, "Item name and HQ/NQ variant.");
        SortableHeader(2, "Strategy", BuySortColumn.Strategy, "Recommended economic route for acquiring and exiting the position.");
        SortableHeader(3, "Buy", BuySortColumn.BuyQuantity, "Recommended number of new units to acquire. Market Board listings are purchased as complete listing stacks.");
        SortableHeader(4, "Cost", BuySortColumn.Cost, "Total acquisition cost of the recommended package, including reported buyer tax when applicable.");
        SortableHeader(5, "Exit @", BuySortColumn.ExitPrice, "Suggested gross Market Board exit price per unit, or guaranteed vendor payout for Market → Vendor.");
        SortableHeader(6, "Stack", BuySortColumn.StackSize, "Suggested units per exit listing based on historical buyer stack behavior and the resulting position.");
        SortableHeader(7, "Profit", BuySortColumn.PotentialProfit, "Potential total profit after modeled seller tax if the suggested exit succeeds.");
        SortableHeader(8, "Risk adj.", BuySortColumn.RiskAdjustedProfit, "Potential profit discounted for confidence, liquidation speed and stability. Useful for comparing capital allocation choices.");
        SortableHeader(9, "ROI", BuySortColumn.Roi, "Potential profit divided by total acquisition cost.");
        HeaderCell(10, "Live", "Native FFXIV verification state. Use the Live filter above to show only Verified, Changed, Refreshed or Not checked opportunities.");
        SortableHeader(11, "Liquidate", BuySortColumn.Liquidation, "Estimated time to sell the full modeled position, not merely the first unit.");
    }

    private void DrawBuyOpportunityRow(BuyOpportunity row)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        var ratingText = $"{Stars(row.Stars)} {row.OpportunityScore:0}";
        if (ImGui.Selectable($"{ratingText}##buy-{row.Item.ItemId}-{row.IsHq}-{row.Kind}-{row.AcquisitionCost}", false, ImGuiSelectableFlags.SpanAllColumns))
        {
            selectedBuyOpportunity = row;
            buyDetailsOpen = true;
        }
        Tooltip($"Click anywhere on this row for details.\nConfidence: {row.Confidence:P0}\nRecent sales: {row.SalesSampleCount:N0}\nVelocity: {row.UnitsPerDay:0.##}/day");

                ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(row.Item.Name + (row.IsHq ? " [HQ]" : string.Empty));
        ItemNameContextMenu($"##copy-buy-name-{row.Item.ItemId}-{row.IsHq}-{row.Kind}-{row.AcquisitionCost}", row.Item.Name);
        ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(row.StrategyLabel);
        ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(row.AcquireQuantity.ToString("N0"));
        ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(Gil(row.AcquisitionCost));
        ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(row.SuggestedExitUnitPrice is { } exit ? $"{exit:N0}g" : "—");
        ImGui.TableSetColumnIndex(6); ImGui.TextUnformatted(row.SuggestedExitStackSize.ToString("N0"));
        ImGui.TableSetColumnIndex(7); ImGui.TextUnformatted(Gil(row.PotentialProfit));
        ImGui.TableSetColumnIndex(8); ImGui.TextUnformatted(Gil(row.RiskAdjustedProfit));
        ImGui.TableSetColumnIndex(9); ImGui.TextUnformatted(Percent(row.Roi));
        ImGui.TableSetColumnIndex(10); ImGui.TextUnformatted(LiveStateLabel(GetBuyLiveState(row)));
        ImGui.TableSetColumnIndex(11); ImGui.TextUnformatted(Days(row.EstimatedLiquidationDays));
    }

    private List<BuyOpportunity> SortBuyRows(IEnumerable<BuyOpportunity> source)
    {
        IOrderedEnumerable<BuyOpportunity> ordered = buySortColumn switch
        {
            BuySortColumn.Item => buySortAscending
                ? source.OrderBy(x => x.Item.Name, StringComparer.CurrentCultureIgnoreCase)
                : source.OrderByDescending(x => x.Item.Name, StringComparer.CurrentCultureIgnoreCase),
            BuySortColumn.Strategy => buySortAscending
                ? source.OrderBy(x => x.StrategyLabel, StringComparer.CurrentCultureIgnoreCase)
                : source.OrderByDescending(x => x.StrategyLabel, StringComparer.CurrentCultureIgnoreCase),
            BuySortColumn.BuyQuantity => buySortAscending ? source.OrderBy(x => x.AcquireQuantity) : source.OrderByDescending(x => x.AcquireQuantity),
            BuySortColumn.Cost => buySortAscending ? source.OrderBy(x => x.AcquisitionCost) : source.OrderByDescending(x => x.AcquisitionCost),
            BuySortColumn.ExitPrice => buySortAscending ? source.OrderBy(x => x.SuggestedExitUnitPrice ?? uint.MaxValue) : source.OrderByDescending(x => x.SuggestedExitUnitPrice ?? 0),
            BuySortColumn.StackSize => buySortAscending ? source.OrderBy(x => x.SuggestedExitStackSize) : source.OrderByDescending(x => x.SuggestedExitStackSize),
            BuySortColumn.PotentialProfit => buySortAscending ? source.OrderBy(x => x.PotentialProfit) : source.OrderByDescending(x => x.PotentialProfit),
            BuySortColumn.RiskAdjustedProfit => buySortAscending ? source.OrderBy(x => x.RiskAdjustedProfit) : source.OrderByDescending(x => x.RiskAdjustedProfit),
            BuySortColumn.Roi => buySortAscending ? source.OrderBy(x => x.Roi) : source.OrderByDescending(x => x.Roi),
            BuySortColumn.Liquidation => buySortAscending ? source.OrderBy(x => x.EstimatedLiquidationDays ?? double.MaxValue) : source.OrderByDescending(x => x.EstimatedLiquidationDays ?? double.MinValue),
            _ => buySortAscending ? source.OrderBy(x => x.OpportunityScore) : source.OrderByDescending(x => x.OpportunityScore),
        };

        return ordered
            .ThenByDescending(x => x.OpportunityScore)
            .ThenByDescending(x => x.RiskAdjustedProfit)
            .ToList();
    }

    private void SortableHeader(int column, string label, BuySortColumn sortColumn, string explanation)
    {
        ImGui.TableSetColumnIndex(column);
        var suffix = buySortColumn == sortColumn ? (buySortAscending ? " ▲" : " ▼") : string.Empty;
        if (ImGui.Selectable($"{label}{suffix}##buy-header-{sortColumn}"))
        {
            if (buySortColumn == sortColumn)
                buySortAscending = !buySortAscending;
            else
            {
                buySortColumn = sortColumn;
                buySortAscending = sortColumn is BuySortColumn.Item or BuySortColumn.Strategy;
            }
        }
        Tooltip(explanation + "\nClick to sort; click again to reverse direction.");
    }

    private static void HeaderCell(int column, string label, string explanation)
    {
        ImGui.TableSetColumnIndex(column);
        ImGui.TextDisabled(label);
        Tooltip(explanation);
    }

    private void DrawBuyDetailPage(BuyOpportunity opportunity)
    {
        if (ImGui.Button("← BACK TO OPPORTUNITIES"))
        {
            buyDetailsOpen = false;
            return;
        }
        Tooltip("Return to the current scan results without discarding the scan or portfolio.");

        ImGui.SameLine();
        ImGui.TextDisabled($"Analysed {FormatBuyAge(DateTimeOffset.UtcNow - opportunity.AnalysedAtUtc)} ago");
        ImGui.Separator();

        ImGui.TextUnformatted($"{opportunity.Item.Name}{(opportunity.IsHq ? " [HQ]" : string.Empty)}");
        ItemNameContextMenu($"##copy-detail-name-{opportunity.Item.ItemId}-{opportunity.IsHq}", opportunity.Item.Name);
        ImGui.TextDisabled($"Market world: {plugin.Catalog.GetWorldName(opportunity.WorldId)} (world ID {opportunity.WorldId})");
        ImGui.TextUnformatted($"{Stars(opportunity.Stars)}  {opportunity.OpportunityScore:0.0}/100  ·  {opportunity.StrategyLabel}");
        ImGui.TextWrapped($"Acquire {opportunity.AcquireQuantity:N0} new unit(s) for about {opportunity.AcquisitionCost:N0}g. The modeled exit targets {opportunity.SuggestedExitStackSize:N0}-unit listing(s) around {(opportunity.SuggestedExitUnitPrice is { } p ? p.ToString("N0") + "g/unit" : "the calculated exit value")}.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Trade overview");
        if (ImGui.BeginTable("##buy-detail-overview", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextRow();
            MetricCell(0, "Rating", $"{Stars(opportunity.Stars)} {opportunity.OpportunityScore:0.0}", "The star band is a broad human-readable rating. The 0–100 value is the stricter score used for ranking opportunities.");
            MetricCell(1, "Confidence", opportunity.Confidence.ToString("P0"), "Evidence confidence inherited from the exit model. Lower confidence reduces risk-adjusted profit and the opportunity score.");
            MetricCell(2, "Potential profit", Gil(opportunity.PotentialProfit), "Modeled profit on only the new acquisition if the recommended exit succeeds. Existing owned stock is not counted as trade profit.");
            MetricCell(3, "Risk-adjusted profit", Gil(opportunity.RiskAdjustedProfit), "Potential profit discounted for evidence quality, expected liquidation speed and market stability. This is not a guarantee or literal probability-weighted EV.");

            ImGui.TableNextRow();
            MetricCell(0, "ROI", Percent(opportunity.Roi), "Potential profit divided by total acquisition cost.");
            MetricCell(1, "Investment", Gil(opportunity.AcquisitionCost), "Total cost of the recommended new acquisition package.");
            MetricCell(2, "Average buy", $"{opportunity.AverageAcquisitionUnitCost:N0}g/unit", "Average acquisition cost per unit across the package, including reported Market Board buyer tax where present.");
            MetricCell(3, "Max suggested buy", opportunity.MaximumRecommendedBuyPrice is { } max ? $"{max:N0}g/unit" : "—", "Approximate highest pre-tax unit price that still satisfies your configured minimum ROI against the modeled net exit.");

            ImGui.TableNextRow();
            MetricCell(0, "First sale", Days(opportunity.EstimatedFirstSaleDays), "Estimated wait before the first modeled sale, including queue position where the exit model can estimate it.");
            MetricCell(1, "Full liquidation", Days(opportunity.EstimatedLiquidationDays), "Estimated time to sell the full resulting position, not just the newly purchased units.");
            MetricCell(2, "Units/day", $"{opportunity.UnitsPerDay:0.##}", "Recent estimated unit velocity used by the exit model.");
            MetricCell(3, "Recent sale samples", opportunity.SalesSampleCount.ToString("N0"), "Number of recent sale records available to the deep exit analysis.");

            ImGui.TableNextRow();
            MetricCell(0, "Already owned", opportunity.ExistingQuantity.ToString("N0"), "Known units you already own. They influence stack and liquidation planning but are not counted as acquisition profit.");
            MetricCell(1, "Resulting position", (opportunity.ExistingQuantity + opportunity.AcquireQuantity).ToString("N0"), "Known existing quantity plus the recommended new acquisition.");
            MetricCell(2, "Exit listings", opportunity.SuggestedExitListingCount.ToString("N0"), "Estimated number of Market Board listings needed for the modeled resulting position at the suggested stack size.");
            MetricCell(3, "Market freshness", opportunity.MarketFreshnessUtc is { } fresh ? FormatBuyAge(DateTimeOffset.UtcNow - fresh) + " ago" : "unknown", "Age of the listing snapshot used by the scanner. Use LIVE VERIFY before spending gil on a listing-sensitive opportunity.");
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Exit plan");
        if (ImGui.BeginTable("##buy-detail-exit", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextRow();
            MetricCell(0, "Gross exit price", opportunity.SuggestedExitUnitPrice is { } gross ? $"{gross:N0}g/unit" : "—", "Suggested visible Market Board price per unit before seller tax, or the vendor payout for a guaranteed vendor exit.");
            MetricCell(1, "Net exit value", opportunity.NetExitUnitPrice is { } net ? $"{net:N0}g/unit" : "—", "Modeled amount retained per unit after the seller-tax assumption. Market → Vendor has no seller tax and uses the guaranteed vendor payout.");
            MetricCell(2, "Recommended stack", opportunity.SuggestedExitStackSize.ToString("N0"), "Recommended units per listing based on historical buyer quantities, convenience effects and the resulting position.");
            MetricCell(3, "Capital efficiency", opportunity.EstimatedLiquidationDays is > 0 ? $"{opportunity.RiskAdjustedProfit / Math.Max(0.25, opportunity.EstimatedLiquidationDays.Value):N0}g risk-adj./day" : "immediate", "Risk-adjusted profit divided by modeled liquidation time. Useful for comparing how quickly different trades recycle capital.");
            ImGui.EndTable();
        }

        DrawBuyLiveVerification(opportunity);

        ImGui.Spacing();
        ImGui.TextUnformatted("Acquisition package");
        if (opportunity.AcquisitionLots.Count > 0)
        {
            if (ImGui.BeginTable("##buy-acquisition-lots", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("Listing ID", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Unit price", ImGuiTableColumnFlags.WidthFixed, 95 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Buyer tax", ImGuiTableColumnFlags.WidthFixed, 85 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Total cost", ImGuiTableColumnFlags.WidthFixed, 100 * ImGuiHelpers.GlobalScale);
                ImGui.TableNextRow();
                HeaderCell(0, "Listing ID", "Exact Universalis listing identifier used by LIVE VERIFY when available.");
                HeaderCell(1, "Quantity", "Whole listing stack quantity. FFXIV Market Board purchases take the complete listing stack.");
                HeaderCell(2, "Unit price", "Seller's listed per-unit price before buyer-side tax.");
                HeaderCell(3, "Buyer tax", "Buyer tax reported for this listing by Universalis and included in total acquisition cost.");
                HeaderCell(4, "Total cost", "Listing price × quantity plus the reported buyer tax.");

                foreach (var lot in opportunity.AcquisitionLots)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(lot.ListingId == 0 ? "unknown" : lot.ListingId.ToString());
                    ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(lot.Quantity.ToString("N0"));
                    ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted($"{lot.UnitPrice:N0}g");
                    ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted($"{lot.BuyerTax:N0}g");
                    ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted($"{lot.TotalCost:N0}g");
                }
                ImGui.EndTable();
            }
        }
        else if (opportunity.Kind == BuyOpportunityKind.VendorToMarket)
        {
            ImGui.TextWrapped($"Source the recommended {opportunity.AcquireQuantity:N0} unit(s) from a verified normal gil NPC vendor at about {opportunity.AverageAcquisitionUnitCost:N0}g/unit. The scanner demand-caps vendor quantity instead of assuming you should buy a full stack.");
        }
        else
        {
            ImGui.TextDisabled("This opportunity does not require a Market Board acquisition package.");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Why the score looks like this");
        ImGui.TextWrapped("For normal market exits, the 0–100 Buy score weights risk-adjusted trade quality across ROI (22%), absolute profit (20%), liquidity/holding time (18%), acquisition price advantage (12%), demand evidence (10%), stability (7%), confidence (6%) and execution friction (5%). The star rating is then derived from that stricter score. Guaranteed Market → Vendor opportunities use a special guaranteed-exit score instead.");
        ImGui.BulletText($"ROI input: {Percent(opportunity.Roi)}; potential profit: {Gil(opportunity.PotentialProfit)}.");
        ImGui.BulletText($"Liquidity input: {Days(opportunity.EstimatedLiquidationDays)} full liquidation versus your configured {plugin.Configuration.BuyMaximumHoldingDays:0.#}-day maximum.");
        ImGui.BulletText($"Evidence input: {opportunity.SalesSampleCount:N0} recent sale sample(s), {opportunity.UnitsPerDay:0.##} unit(s)/day, {opportunity.Confidence:P0} confidence.");
        ImGui.BulletText($"Execution input: {Math.Max(1, opportunity.AcquisitionLots.Count):N0} acquisition action(s) and about {Math.Max(1, opportunity.SuggestedExitListingCount):N0} exit listing(s).");

        ImGui.Spacing();
        ImGui.TextUnformatted("Model reasoning & cautions");
        foreach (var note in opportunity.Notes)
            ImGui.BulletText(note);

        ImGui.Spacing();
        ImGui.TextDisabled("Should I Buy? never purchases automatically. Verify listing-sensitive deals live, execute purchases yourself on the normal Market Board, and Should I Tycoon? records the successful server-confirmed purchase for later P&L analysis.");
    }

    private static void MetricCell(int column, string label, string value, string explanation)
    {
        ImGui.TableSetColumnIndex(column);
        ImGui.TextDisabled(label);
        Tooltip(explanation);
        ImGui.TextUnformatted(value);
    }

    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    private static string FormatBuyAge(TimeSpan age)
    {
        if (age.TotalSeconds < 60)
            return $"{Math.Max(0, age.TotalSeconds):0}s";
        if (age.TotalMinutes < 60)
            return $"{age.TotalMinutes:0.#}m";
        if (age.TotalHours < 24)
            return $"{age.TotalHours:0.#}h";
        return $"{age.TotalDays:0.#}d";
    }
}























