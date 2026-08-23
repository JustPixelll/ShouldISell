using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace ShouldISell.Windows;

public sealed partial class SuiteWindow
{
    private void DrawBuyModule()
    {
        ImGui.TextWrapped("Scan a configurable slice of the market for executable purchases within your budget. Discovery is cheap and broad; only promising items get full listings + history and a counterfactual Should I Sell? exit simulation.");
        ImGui.Spacing();

        DrawBuyControls();
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

            var minProfit = c.BuyMinimumProfitGil;
            ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Minimum potential profit", ref minProfit, 500, 5_000))
            {
                c.BuyMinimumProfitGil = Math.Clamp(minProfit, 0, c.BuyBudgetGil);
                c.Save();
            }

            var minRoi = c.BuyMinimumRoiPercent;
            ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat("Minimum ROI %", ref minRoi, 1, 5, "%.1f"))
            {
                c.BuyMinimumRoiPercent = Math.Clamp(minRoi, 0, 1000);
                c.Save();
            }

            var hold = c.BuyMaximumHoldingDays;
            ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat("Maximum expected holding (days)", ref hold, 0.5f, 2, "%.1f"))
            {
                c.BuyMaximumHoldingDays = Math.Clamp(hold, 0.25f, 365f);
                c.Save();
            }

            var maxItem = c.BuyMaximumInvestmentPercentPerItem;
            ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Maximum budget per item %", ref maxItem, 1, 5))
            {
                c.BuyMaximumInvestmentPercentPerItem = Math.Clamp(maxItem, 1, 100);
                c.Save();
            }

            var deepLimit = c.BuyDeepCandidateLimit;
            ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Deep-analysis item limit", ref deepLimit, 10, 50))
            {
                c.BuyDeepCandidateLimit = Math.Clamp(deepLimit, 20, 500);
                c.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The broad aggregated pass still checks every scoped marketable item. This only caps how many strongest item IDs receive full listing books + 90-day history.");

            ImGui.Spacing();
            var marketFlip = c.BuyEnableMarketToMarket;
            if (ImGui.Checkbox("Market → Market", ref marketFlip))
            {
                c.BuyEnableMarketToMarket = marketFlip;
                c.Save();
            }
            ImGui.SameLine();
            var vendorMarket = c.BuyEnableVendorToMarket;
            if (ImGui.Checkbox("Vendor → Market", ref vendorMarket))
            {
                c.BuyEnableVendorToMarket = vendorMarket;
                c.Save();
            }
            ImGui.SameLine();
            var marketVendor = c.BuyEnableMarketToVendor;
            if (ImGui.Checkbox("Market → Vendor", ref marketVendor))
            {
                c.BuyEnableMarketToVendor = marketVendor;
                c.Save();
            }

            var equipment = c.BuyIncludeEquipment;
            if (ImGui.Checkbox("Include equippable gear", ref equipment))
            {
                c.BuyIncludeEquipment = equipment;
                c.Save();
            }

            var categoryFilter = c.BuyUseCategoryFilter;
            if (ImGui.Checkbox("Filter by FFXIV item UI categories", ref categoryFilter))
            {
                c.BuyUseCategoryFilter = categoryFilter;
                if (categoryFilter && c.BuyIncludedCategoryIds.Count == 0)
                    c.BuyIncludedCategoryIds = plugin.Catalog.GetCategories().Select(x => x.CategoryId).ToList();
                c.Save();
            }

            if (c.BuyUseCategoryFilter)
                DrawBuyCategoryScope();
        }

        ImGui.Spacing();
        if (!plugin.BuyScanner.IsScanning)
        {
            if (ImGui.Button("SCAN FOR GOOD BUYS"))
            {
                selectedBuyOpportunity = null;
                _ = plugin.BuyScanner.ScanAsync();
            }
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
                ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"Deep analysis {plugin.BuyScanner.DeepItemsScanned:N0}/{plugin.BuyScanner.DeepItemsTotal:N0}");
            }
        }
    }

    private void DrawBuyCategoryScope()
    {
        var c = plugin.Configuration;
        var categories = plugin.Catalog.GetCategories();
        ImGui.SetNextItemWidth(280 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##buy-category-search", "Filter category names...", ref buyCategorySearch, 96);
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

    private void DrawBuyResults()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##buy-result-search", "Search opportunity item...", ref buySearch, 128);

        var all = plugin.BuyScanner.GetOpportunities();
        var rows = all
            .Where(x => string.IsNullOrWhiteSpace(buySearch) || x.Item.Name.Contains(buySearch, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(x => x.OpportunityScore)
            .ThenByDescending(x => x.RiskAdjustedProfit)
            .ToList();

        ImGui.TextDisabled($"{rows.Count:N0} visible opportunity package(s). Potential profit assumes the modeled exit succeeds; risk-adjusted profit discounts that upside for evidence quality, liquidity and market stability.");

        var tableHeight = selectedBuyOpportunity is null ? -1 : 320 * ImGuiHelpers.GlobalScale;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
        if (ImGui.BeginTable("##buy-opportunity-table", 11, flags, new Vector2(0, tableHeight)))
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
            ImGui.TableSetupColumn("Liquidate", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted($"{Stars(row.Stars)} {row.OpportunityScore:0}");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Confidence: {row.Confidence:P0}\nRecent sales: {row.SalesSampleCount:N0}\nVelocity: {row.UnitsPerDay:0.##}/day");

                ImGui.TableSetColumnIndex(1);
                var selected = selectedBuyOpportunity == row;
                var itemLabel = row.Item.Name + (row.IsHq ? " [HQ]" : string.Empty);
                if (ImGui.Selectable($"{itemLabel}##buy-{row.Item.ItemId}-{row.IsHq}-{row.Kind}-{row.AcquisitionCost}", selected))
                    selectedBuyOpportunity = selected ? null : row;

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(row.StrategyLabel);
                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(row.AcquireQuantity.ToString("N0"));
                ImGui.TableSetColumnIndex(4);
                ImGui.TextUnformatted(Gil(row.AcquisitionCost));
                ImGui.TableSetColumnIndex(5);
                ImGui.TextUnformatted(row.SuggestedExitUnitPrice is { } exit ? $"{exit:N0}g" : "—");
                ImGui.TableSetColumnIndex(6);
                ImGui.TextUnformatted(row.SuggestedExitStackSize.ToString("N0"));
                ImGui.TableSetColumnIndex(7);
                ImGui.TextUnformatted(Gil(row.PotentialProfit));
                ImGui.TableSetColumnIndex(8);
                ImGui.TextUnformatted(Gil(row.RiskAdjustedProfit));
                ImGui.TableSetColumnIndex(9);
                ImGui.TextUnformatted(Percent(row.Roi));
                ImGui.TableSetColumnIndex(10);
                ImGui.TextUnformatted(Days(row.EstimatedLiquidationDays));
            }
            ImGui.EndTable();
        }

        if (selectedBuyOpportunity is { } opportunity)
            DrawBuyDetails(opportunity);
    }

    private void DrawBuyDetails(BuyOpportunity opportunity)
    {
        ImGui.Separator();
        ImGui.TextUnformatted($"{opportunity.Item.Name}{(opportunity.IsHq ? " [HQ]" : string.Empty)} — {Stars(opportunity.Stars)} {opportunity.OpportunityScore:0.0}");
        ImGui.TextWrapped($"Recommended action: {opportunity.StrategyLabel}. Acquire {opportunity.AcquireQuantity:N0} for about {opportunity.AcquisitionCost:N0}g, then target {opportunity.SuggestedExitStackSize:N0}-unit listing(s) around {(opportunity.SuggestedExitUnitPrice is { } p ? p.ToString("N0") + "g" : "the modeled exit price")}.");

        if (ImGui.BeginTable("##buy-detail-metrics", 4, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.TextDisabled("Potential profit"); ImGui.TextUnformatted(Gil(opportunity.PotentialProfit));
            ImGui.TableSetColumnIndex(1); ImGui.TextDisabled("Risk-adjusted"); ImGui.TextUnformatted(Gil(opportunity.RiskAdjustedProfit));
            ImGui.TableSetColumnIndex(2); ImGui.TextDisabled("ROI"); ImGui.TextUnformatted(Percent(opportunity.Roi));
            ImGui.TableSetColumnIndex(3); ImGui.TextDisabled("Full liquidation"); ImGui.TextUnformatted(Days(opportunity.EstimatedLiquidationDays));
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.TextDisabled("Already owned"); ImGui.TextUnformatted(opportunity.ExistingQuantity.ToString("N0"));
            ImGui.TableSetColumnIndex(1); ImGui.TextDisabled("Max suggested buy"); ImGui.TextUnformatted(opportunity.MaximumRecommendedBuyPrice is { } max ? $"{max:N0}g/unit" : "—");
            ImGui.TableSetColumnIndex(2); ImGui.TextDisabled("First sale estimate"); ImGui.TextUnformatted(Days(opportunity.EstimatedFirstSaleDays));
            ImGui.TableSetColumnIndex(3); ImGui.TextDisabled("Confidence"); ImGui.TextUnformatted(opportunity.Confidence.ToString("P0"));
            ImGui.EndTable();
        }

        if (opportunity.AcquisitionLots.Count > 0)
        {
            ImGui.TextUnformatted("Buy these Market Board listings:");
            foreach (var lot in opportunity.AcquisitionLots)
                ImGui.BulletText($"{lot.Quantity:N0} × {lot.UnitPrice:N0}g = {lot.TotalCost:N0}g incl. {lot.BuyerTax:N0}g reported tax");
        }
        else if (opportunity.Kind == BuyOpportunityKind.VendorToMarket)
        {
            ImGui.TextUnformatted("Source: normal gil NPC vendor (no Market Board acquisition listing required).");
        }

        ImGui.TextUnformatted("Why:");
        foreach (var note in opportunity.Notes)
            ImGui.BulletText(note);

        ImGui.TextDisabled("Should I Buy? never purchases automatically. Execute the purchase yourself on the normal Market Board; Tycoon records a successful purchase after the server confirms it.");
    }
}
