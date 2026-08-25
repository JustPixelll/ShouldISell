using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace ShouldISell.Windows;

public sealed partial class SuiteWindow
{
    private void DrawTycoonModule()
    {
        var snapshot = plugin.TraderAnalyzer.GetSnapshot();
        ImGui.TextWrapped("Tycoon is both a gil/capital ledger and a trading laboratory. It records direct player-wallet changes while Should I? is running, keeps item-level Market Board purchase/sale economics separate, and uses FIFO only for purchase lots you consider trading positions. Unknown wallet sources stay visibly unattributed; category analytics are deferred until attribution can be reliable.");
        ImGui.Spacing();

        DrawTycoonCashflowSummary(snapshot);
        ImGui.Spacing();
        DrawTraderProfile(snapshot);
        ImGui.Separator();
        DrawTraderMetrics(snapshot);
        DrawTycoonInsightSummary(snapshot);
        ImGui.Separator();

        if (ImGui.BeginTabBar("##tycoon-tabs"))
        {
            if (ImGui.BeginTabItem("Cashflow"))
            {
                DrawTycoonCashflow();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Trade Positions"))
            {
                DrawOpenPositions(snapshot);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Closed Trades"))
            {
                DrawClosedTrades(snapshot);
                ImGui.EndTabItem();
            }
            var purchasesFlags = selectTycoonPurchases ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (ImGui.BeginTabItem("Purchases", purchasesFlags))
            {
                selectTycoonPurchases = false;
                DrawTycoonPurchases();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Best Items"))
            {
                DrawTopTraderItems(snapshot);
                ImGui.EndTabItem();
            }
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
            {
                DrawPredictionAccuracy(snapshot);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private static void DrawTraderProfile(TraderSnapshot snapshot)
    {
        ImGui.TextUnformatted($"Trader profile: {snapshot.ProfileName}");
        ImGui.TextWrapped(snapshot.ProfileDescription);
        if (snapshot.PurchaseCount == 0)
            ImGui.TextDisabled("Successful Market Board buys are captured automatically. Normal-gil vendor acquisitions can be entered manually under Purchases when you want their cost basis included in trading P&L.");
    }

    private static void DrawTraderMetrics(TraderSnapshot snapshot)
    {
        if (ImGui.BeginTable("##tycoon-metrics", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            MetricCell(0, "Tracked trade spend", Gil(snapshot.CapitalInvested));
            MetricCell(1, "Realized profit", Gil(snapshot.RealizedProfit));
            MetricCell(2, "Realized return / spend", Percent(snapshot.RealizedReturnOnTrackedSpend));
            MetricCell(3, "Win rate (sale events)", Percent(snapshot.WinRate));
            ImGui.TableNextRow();
            MetricCell(0, "Closed cost basis", Gil(snapshot.RealizedCostBasis));
            MetricCell(1, "Closed net revenue", Gil(snapshot.RealizedRevenue));
            MetricCell(2, "Closed-trade ROI", Percent(snapshot.RealizedRoi));
            MetricCell(3, "Median holding", Days(snapshot.MedianHoldingDays));
            ImGui.TableNextRow();
            MetricCell(0, "Open cost basis", Gil(snapshot.OpenCostBasis));
            MetricCell(1, "Open est. net value", Gil(snapshot.OpenEstimatedNetValue));
            MetricCell(2, "Unrealized est.", Gil(snapshot.UnrealizedProfit));
            MetricCell(3, "Open tracked units", snapshot.OpenUnits.ToString("N0"));
            ImGui.TableNextRow();
            MetricCell(0, "Trade purchases", snapshot.PurchaseCount.ToString("N0"));
            MetricCell(1, "Matched sale events", snapshot.TrackedSaleCount.ToString("N0"));
            MetricCell(2, "Closed units", snapshot.ClosedUnits.ToString("N0"));
            MetricCell(3, "Unmatched-cost units", snapshot.UnmatchedSaleUnits.ToString("N0"));
            ImGui.EndTable();
        }

        ImGui.TextDisabled("Realized return / spend = realized profit ÷ all tracked Trade purchase cost. Closed-trade ROI = realized profit ÷ the cost basis of sold tracked units only.");
        ImGui.TextDisabled("Closed-trade ROI is intentionally not capped: a 100g lot sold for 3,500g really is ~3,400% ROI on that closed lot, even if thousands of gil remain tied up in other open positions.");
        if (snapshot.RealizedRoi >= 10.0 && snapshot.RealizedCostBasis > 0)
            ImGui.TextWrapped("Very high closed-trade ROI detected. Check Closed Trades to verify the FIFO attribution. Tycoon consumes the known inventory that existed before the first tracked purchase before assigning later sales to costed lots; if a sale still came from an unobserved crafted/gathered/gifted unit, mark the purchase Personal.");

        if (snapshot.UnmatchedSaleUnits > 0)
            ImGui.TextDisabled($"{snapshot.UnmatchedSaleUnits:N0} sold unit(s) could not be assigned a tracked purchase cost basis. This is expected for sales from before Tycoon purchase tracking began, crafted/gathered stock, gifts, or offline purchase history that the game never exposed.");
    }

    private static void MetricCell(int column, string label, string value)
    {
        if (column == 0)
            ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(column);
        ImGui.TextDisabled(label);
        ImGui.TextUnformatted(value);
    }

    private static void DrawOpenPositions(TraderSnapshot snapshot)
    {
        ImGui.TextDisabled("Trade positions are remaining FIFO purchase lots currently marked as Trade. Purchases marked Personal remain in your real spending/cashflow history but are excluded from trading P&L and open positions. Suggested exit/value comes from the current Should I Sell? model when the item is present in known inventory snapshots.");
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable;
        if (!ImGui.BeginTable("##tycoon-open", 9, flags, new Vector2(0, -1)))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Listed", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Avg cost", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Cost basis", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Suggested", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Est. net", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Unrealized", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Strategy", ImGuiTableColumnFlags.WidthFixed, 130 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        var rows = TableSort.Apply(snapshot.OpenPositions, ImGui.TableGetSortSpecs(),
            x => x.ItemName,
            x => x.Quantity,
            x => x.ListedQuantity,
            x => x.AverageCost,
            x => x.CostBasis,
            x => x.SuggestedExitUnitPrice,
            x => x.EstimatedNetMarketValue,
            x => x.UnrealizedProfit,
            x => x.PrimaryStrategy);
        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(row.ItemName + (row.IsHq ? " [HQ]" : string.Empty));
            ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(row.Quantity.ToString("N0"));
            ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(row.ListedQuantity.ToString("N0"));
            ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(Gil(row.AverageCost));
            ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(Gil(row.CostBasis));
            ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(row.SuggestedExitUnitPrice is { } p ? $"{p:N0}g" : "—");
            ImGui.TableSetColumnIndex(6); ImGui.TextUnformatted(Gil(row.EstimatedNetMarketValue));
            ImGui.TableSetColumnIndex(7); ImGui.TextUnformatted(Gil(row.UnrealizedProfit));
            ImGui.TableSetColumnIndex(8); ImGui.TextUnformatted(row.PrimaryStrategy);
        }
        ImGui.EndTable();
    }

    private static void DrawClosedTrades(TraderSnapshot snapshot)
    {
        ImGui.TextDisabled("A row represents the tracked portion of one captured retainer-sale event. ROI on cost = (net revenue - matched FIFO cost basis) ÷ matched FIFO cost basis. If a sale consumed several purchase lots, their cost basis and predictions are quantity-weighted.");
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable;
        if (!ImGui.BeginTable("##tycoon-closed", 9, flags, new Vector2(0, -1)))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Sold", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending, 120 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 85 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Net revenue", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Profit", ImGuiTableColumnFlags.WidthFixed, 85 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("ROI on cost", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Held", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Strategy", ImGuiTableColumnFlags.WidthFixed, 130 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        var rows = TableSort.Apply(snapshot.RecentClosedTrades, ImGui.TableGetSortSpecs(),
            x => x.SoldAtUtc,
            x => x.ItemName,
            x => x.Quantity,
            x => x.CostBasis,
            x => x.NetRevenue,
            x => x.Profit,
            x => x.Roi,
            x => x.HoldingDays,
            x => x.Strategy);
        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(row.SoldAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(row.ItemName + (row.IsHq ? " [HQ]" : string.Empty));
            ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(row.Quantity.ToString("N0"));
            ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(Gil(row.CostBasis));
            ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(Gil(row.NetRevenue));
            ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(Gil(row.Profit));
            ImGui.TableSetColumnIndex(6); ImGui.TextUnformatted(Percent(row.Roi));
            ImGui.TableSetColumnIndex(7); ImGui.TextUnformatted(Days(row.HoldingDays));
            ImGui.TableSetColumnIndex(8); ImGui.TextUnformatted(row.Strategy);
        }
        ImGui.EndTable();
    }

    private static void DrawTopTraderItems(TraderSnapshot snapshot)
    {
        if (snapshot.TopItems.Count == 0)
        {
            ImGui.TextDisabled("No matched closed trades yet.");
            return;
        }

        if (!ImGui.BeginTable("##tycoon-items", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable))
            return;
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Closed units", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Revenue", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Profit", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Closed ROI", ImGuiTableColumnFlags.WidthFixed, 75 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Avg hold", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();
        var rows = TableSort.Apply(snapshot.TopItems, ImGui.TableGetSortSpecs(),
            x => x.ItemName,
            x => x.ClosedUnits,
            x => x.CostBasis,
            x => x.NetRevenue,
            x => x.Profit,
            x => x.Roi,
            x => x.AverageHoldingDays);
        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(row.ItemName + (row.IsHq ? " [HQ]" : string.Empty));
            ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(row.ClosedUnits.ToString("N0"));
            ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(Gil(row.CostBasis));
            ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(Gil(row.NetRevenue));
            ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(Gil(row.Profit));
            ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(Percent(row.Roi));
            ImGui.TableSetColumnIndex(6); ImGui.TextUnformatted(Days(row.AverageHoldingDays));
        }
        ImGui.EndTable();
    }

    private static void DrawTraderStrategies(TraderSnapshot snapshot)
    {
        if (snapshot.Strategies.Count == 0)
        {
            ImGui.TextDisabled("No matched strategy history yet. Market Board purchases inherit a current Should I Buy? strategy when matched; manually recorded vendor acquisitions inherit the current Vendor -> Market recommendation when available.");
            return;
        }

        if (!ImGui.BeginTable("##tycoon-strategies", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable))
            return;
        ImGui.TableSetupColumn("Strategy", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Sale events", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Units", ImGuiTableColumnFlags.WidthFixed, 60 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Profit", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Closed ROI", ImGuiTableColumnFlags.WidthFixed, 75 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Avg hold", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();
        var rows = TableSort.Apply(snapshot.Strategies, ImGui.TableGetSortSpecs(),
            x => x.Strategy,
            x => x.SaleEvents,
            x => x.ClosedUnits,
            x => x.CostBasis,
            x => x.Profit,
            x => x.Roi,
            x => x.AverageHoldingDays);
        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(row.Strategy);
            ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(row.SaleEvents.ToString("N0"));
            ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(row.ClosedUnits.ToString("N0"));
            ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(Gil(row.CostBasis));
            ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(Gil(row.Profit));
            ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(Percent(row.Roi));
            ImGui.TableSetColumnIndex(6); ImGui.TextUnformatted(Days(row.AverageHoldingDays));
        }
        ImGui.EndTable();
    }

    private static void DrawPredictionAccuracy(TraderSnapshot snapshot)
    {
        ImGui.TextWrapped("For an exact Market Board recommendation — and for a manually recorded vendor acquisition tied to the current Vendor -> Market model — Tycoon stores available exit/score predictions with the real cost basis. When Should I Sell? later captures the retainer sale, those predictions can be compared with reality. Vendor liquidation-time calibration is only stored when the entered quantity matches the recommendation.");
        ImGui.Spacing();

        if (snapshot.MeanAbsoluteExitPriceError is { } priceError)
        {
            ImGui.TextUnformatted($"Mean absolute exit-price error: {priceError:P1}");
            ImGui.TextDisabled("Actual after-fees gil/unit is compared with the predicted exit after applying the standard seller-tax assumption.");
        }
        else
        {
            ImGui.TextDisabled("No closed scanner-tagged trade has enough data for exit-price accuracy yet.");
        }

        if (snapshot.MeanAbsoluteHoldingTimeError is { } holdingError)
            ImGui.TextUnformatted($"Mean absolute liquidation-time error: {holdingError:P1}");
        else
            ImGui.TextDisabled("No closed scanner-tagged trade has enough data for liquidation-time accuracy yet.");

        ImGui.Spacing();
        ImGui.TextDisabled("These errors are descriptive calibration statistics, not guarantees. As your personal sample grows, they tell us whether the generic market model is systematically too optimistic or too conservative for the way you actually trade.");
    }
}
