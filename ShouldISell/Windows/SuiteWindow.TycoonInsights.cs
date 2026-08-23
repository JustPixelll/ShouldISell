using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace ShouldISell.Windows;

public sealed partial class SuiteWindow
{
    private void DrawTycoonInsightSummary(TraderSnapshot pnlSnapshot)
    {
        var insight = plugin.TycoonInsights.GetSnapshot();
        if (!ImGui.BeginTable("##tycoon-all-sales-summary", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
            return;

        MetricCell(0, "All captured sales", insight.SaleEvents.ToString("N0"));
        MetricCell(1, "All-sale net gil", Gil(insight.NetSalesGil));
        MetricCell(2, "Untracked-cost units", pnlSnapshot.UnmatchedSaleUnits.ToString("N0"));
        MetricCell(3, "Listing-traceable sales", insight.TraceableSaleEvents.ToString("N0"));
        ImGui.EndTable();
        ImGui.TextDisabled("All-sale metrics include gathered, crafted, dropped, gifted and pre-tracking stock. Unknown cost basis stays unknown; these numbers describe selling behavior, not invented profit.");
    }

    private void DrawTycoonSalesInsights()
    {
        var insight = plugin.TycoonInsights.GetSnapshot();
        ImGui.TextWrapped("Sales Insights uses every captured personal retainer sale, even when Tycoon never observed how the item entered your inventory. This is the right place to learn what actually sells for you without pretending gathered/dropped/untracked stock had a known purchase cost.");
        ImGui.Spacing();

        if (insight.SaleEvents == 0)
        {
            ImGui.TextDisabled("No personal sales have been captured yet. Opening retainer sale history imports exact historical rows; passive sale capture adds future sales while Should I? is running.");
            return;
        }

        if (ImGui.CollapsingHeader("Top items by captured net sales##tycoon-sales-items", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (ImGui.BeginTable("##tycoon-sales-items-table", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Sales", ImGuiTableColumnFlags.WidthFixed, 60 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Units", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Net gil", ImGuiTableColumnFlags.WidthFixed, 95 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Avg net/unit", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Last sale", ImGuiTableColumnFlags.WidthFixed, 125 * ImGuiHelpers.GlobalScale);
                ImGui.TableHeadersRow();
                foreach (var row in insight.TopSalesItems.Take(100))
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(row.ItemName + (row.IsHq ? " [HQ]" : string.Empty));
                    ItemNameContextMenu($"##copy-tycoon-sales-{row.ItemId}-{row.IsHq}", row.ItemName);
                    ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(row.SaleEvents.ToString("N0"));
                    ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(row.Units.ToString("N0"));
                    ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(Gil(row.NetGil));
                    ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(Gil(row.AverageNetUnitPrice));
                    ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(row.LastSaleUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                }
                ImGui.EndTable();
            }
        }

        if (ImGui.CollapsingHeader("Recent captured sales##tycoon-sales-recent", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
            if (ImGui.BeginTable("##tycoon-sales-recent-table", 9, flags, new Vector2(0, 280 * ImGuiHelpers.GlobalScale)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Sold", ImGuiTableColumnFlags.WidthFixed, 125 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthFixed, 100 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 50 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Net", ImGuiTableColumnFlags.WidthFixed, 85 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Net/unit", ImGuiTableColumnFlags.WidthFixed, 75 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Listing trace", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Time listed", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
                ImGui.TableHeadersRow();
                foreach (var row in insight.RecentSales.Take(500))
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(row.SoldAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                    ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(row.ItemName + (row.IsHq ? " [HQ]" : string.Empty));
                    ItemNameContextMenu($"##copy-tycoon-sale-{row.ItemId}-{row.SoldAtUtc.ToUnixTimeSeconds()}", row.ItemName);
                    ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(row.RetainerName);
                    ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(row.Quantity.ToString("N0"));
                    ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(Gil(row.NetGil));
                    ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(Gil(row.NetUnitPrice));
                    ImGui.TableSetColumnIndex(6); ImGui.TextUnformatted(row.Source.ToString());
                    ImGui.TableSetColumnIndex(7); ImGui.TextUnformatted(row.ListingTraceable ? $"Yes ({row.PriceChanges}P/{row.SizeChanges}S)" : "Not traceable");
                    ImGui.TableSetColumnIndex(8); ImGui.TextUnformatted(row.TimeToSellDays is { } days ? Days(days) : "—");
                }
                ImGui.EndTable();
            }
        }
    }

    private void DrawTycoonListingInsights()
    {
        var insight = plugin.TycoonInsights.GetSnapshot();
        ImGui.TextWrapped("Listing Insights is a forward-looking local history. Should I? records your own listing states as it sees retainers, then links a sale only when retainer + item + HQ + quantity + plausible sale price line up. Repricing, size changes and short-gap relists are shown only when traceable; missing observations are left unknown rather than guessed.");
        ImGui.TextDisabled("Tracking begins with this version. Old sale-history rows still appear in Sales Insights, but an old listing cannot be reconstructed if Should I? never observed it.");
        ImGui.Spacing();

        if (insight.ListingInsights.Count == 0)
        {
            ImGui.TextDisabled("No listing lifecycle has been observed yet. Open your retainers' sell lists while Should I? is running to start building listing history.");
            return;
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("##tycoon-listing-insights", 12, flags, new Vector2(0, -1)))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthFixed, 95 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Started", ImGuiTableColumnFlags.WidthFixed, 120 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, 100 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Price edits", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Size edits", ImGuiTableColumnFlags.WidthFixed, 60 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Relist", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Sold", ImGuiTableColumnFlags.WidthFixed, 115 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Sale net", ImGuiTableColumnFlags.WidthFixed, 85 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var row in insight.ListingInsights)
        {
            var end = row.SoldAtUtc ?? row.RemovedAtUtc ?? DateTimeOffset.UtcNow;
            var elapsed = Math.Max(0, (end - row.FirstSeenUtc).TotalDays);
            var status = row.SoldAtUtc is not null ? "Sold" : row.IsActive ? "Current" : "Removed";
            var relist = !row.IsRelist
                ? "—"
                : row.PreviousQuantity is { } previous
                    ? previous == row.InitialQuantity ? "same size" : $"{previous}→{row.InitialQuantity}"
                    : "yes";

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(status);
            ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(row.ItemName + (row.IsHq ? " [HQ]" : string.Empty));
            ItemNameContextMenu($"##copy-tycoon-listing-{row.LifecycleId}", row.ItemName);
            ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(row.RetainerName);
            ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(row.FirstSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(Days(elapsed));
            ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(row.InitialQuantity == row.LastQuantity ? row.LastQuantity.ToString("N0") : $"{row.InitialQuantity:N0}→{row.LastQuantity:N0}");
            ImGui.TableSetColumnIndex(6); ImGui.TextUnformatted(row.InitialUnitPrice == row.LastUnitPrice ? $"{row.LastUnitPrice:N0}g" : $"{row.InitialUnitPrice:N0}→{row.LastUnitPrice:N0}g");
            ImGui.TableSetColumnIndex(7); ImGui.TextUnformatted(row.PriceChanges.ToString("N0"));
            ImGui.TableSetColumnIndex(8); ImGui.TextUnformatted(row.SizeChanges.ToString("N0"));
            ImGui.TableSetColumnIndex(9); ImGui.TextUnformatted(relist);
            ImGui.TableSetColumnIndex(10); ImGui.TextUnformatted(row.SoldAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—");
            ImGui.TableSetColumnIndex(11); ImGui.TextUnformatted(row.SaleNetGil is { } gil ? Gil(gil) : "—");
        }
        ImGui.EndTable();
    }
}
