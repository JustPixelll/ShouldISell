using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;

namespace ShouldISell.Windows;

public sealed partial class MainWindow
{
    private string saleSearch = string.Empty;
    private SaleGroupKey? selectedSaleGroup;
    private int saleSortMode;

    private readonly record struct SaleGroupKey(uint ItemId, bool IsHq);

    private sealed record SaleGroup(
        ItemInfo Item,
        bool IsHq,
        int Transactions,
        long Units,
        long NetGil,
        double NetPerUnit,
        double AverageTransaction,
        long BestTransaction,
        DateTimeOffset FirstSaleUtc,
        DateTimeOffset LastSaleUtc,
        IReadOnlyList<PersonalSale> Sales);

    private void DrawSalesHistory()
    {
        ImGui.TextWrapped("Your personal sales ledger now grows automatically while you are online: Should I Sell? listens for FFXIV's retainer-sale announcement and records the linked item, sold quantity and after-fees gil immediately. Opening a retainer's ‘View sale history’ later backfills offline sales and reconciles live entries with the exact retainer, buyer and server sale time.");
        ImGui.TextDisabled("Live capture does not suppress or change the game's notification. Reopening sale history is still useful as an exact reconciliation/backfill, but is no longer required just to keep recording online sales.");

        if (plugin.SaleHistory.IsDegraded)
        {
            ImGui.Spacing();
            ImGui.TextColored(AttentionTextColor, "Exact sale-history reconciliation is currently unavailable (game signature mismatch).");
            ImGui.TextDisabled("Passive retainer-sale announcement capture still works; exact retainer/buyer backfill may need an update after a game patch.");
        }

        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Log into a character to view its sales history.");
            return;
        }

        var sales = plugin.Store.GetPersonalSales(Plugin.PlayerState.ContentId);
        if (sales.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("No personal sales captured yet. Passive capture is armed for new retainer-sale notifications.");
            ImGui.BulletText("New sales announced while you are online are recorded automatically.");
            ImGui.BulletText("Open ‘View sale history’ on each retainer once to backfill its recent exact rows.");
            ImGui.BulletText("The game normally exposes up to 20 recent history rows per retainer at a time.");
            return;
        }

        ImGui.Spacing();
        DrawSalesSummary(sales);
        ImGui.Spacing();
        DrawSalesMarketBenchmark(sales);
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##sales-search", "Search sold item, retainer or buyer...", ref saleSearch, 128);
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        var sorts = new[] { "Net earned", "Last sold", "Transactions", "Units sold", "Item name" };
        var currentSort = sorts[Math.Clamp(saleSortMode, 0, sorts.Length - 1)];
        if (ImGui.BeginCombo("Sort by##sales", currentSort))
        {
            for (var i = 0; i < sorts.Length; i++)
            {
                var isSelected = saleSortMode == i;
                if (ImGui.Selectable(sorts[i], isSelected))
                    saleSortMode = i;
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        var filtered = sales.Where(SaleMatchesSearch).ToList();
        var groups = BuildSaleGroups(filtered);
        groups = saleSortMode switch
        {
            1 => groups.OrderByDescending(x => x.LastSaleUtc).ThenBy(x => x.Item.Name).ToList(),
            2 => groups.OrderByDescending(x => x.Transactions).ThenByDescending(x => x.NetGil).ToList(),
            3 => groups.OrderByDescending(x => x.Units).ThenByDescending(x => x.NetGil).ToList(),
            4 => groups.OrderBy(x => x.Item.Name, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(x => x.IsHq).ToList(),
            _ => groups.OrderByDescending(x => x.NetGil).ThenByDescending(x => x.LastSaleUtc).ToList(),
        };

        ImGui.TextDisabled($"{filtered.Count:N0} sale(s) across {groups.Count:N0} item/HQ variant(s). Click an item for its transaction history.");

        var detailsOpen = selectedSaleGroup is not null;
        var tableHeight = detailsOpen ? 300 * ImGuiHelpers.GlobalScale : -1;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.Resizable;
        if (ImGui.BeginTable("personal-sales-groups", 8, flags, new Vector2(0, tableHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Sales", ImGuiTableColumnFlags.WidthFixed, 58 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Units", ImGuiTableColumnFlags.WidthFixed, 58 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Net earned", ImGuiTableColumnFlags.WidthFixed, 100 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Net / unit", ImGuiTableColumnFlags.WidthFixed, 88 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Avg sale", ImGuiTableColumnFlags.WidthFixed, 88 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Best sale", ImGuiTableColumnFlags.WidthFixed, 92 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Last sold", ImGuiTableColumnFlags.WidthFixed, 118 * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var group in groups)
            {
                var key = new SaleGroupKey(group.Item.ItemId, group.IsHq);
                var isSelected = selectedSaleGroup == key;
                var clicked = BeginClickableRow($"sale-group-{group.Item.ItemId}-{group.IsHq}", isSelected);
                if (clicked)
                    selectedSaleGroup = isSelected ? null : key;

                ImGui.TableSetColumnIndex(0);
                var drewIcon = DrawSmallSaleIcon(group.Item, group.IsHq);
                if (drewIcon)
                    ImGui.SameLine();
                ImGui.TextUnformatted(group.Item.Name + (group.IsHq ? " [HQ]" : string.Empty));
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(group.Transactions.ToString("N0"));
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(group.Units.ToString("N0"));
                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(group.NetGil > 0 ? Gil((double)group.NetGil) : "—");
                ImGui.TableSetColumnIndex(4);
                ImGui.TextUnformatted(group.NetPerUnit > 0 ? Gil(group.NetPerUnit) : "—");
                ImGui.TableSetColumnIndex(5);
                ImGui.TextUnformatted(group.AverageTransaction > 0 ? Gil(group.AverageTransaction) : "—");
                ImGui.TableSetColumnIndex(6);
                ImGui.TextUnformatted(group.BestTransaction > 0 ? Gil((double)group.BestTransaction) : "—");
                ImGui.TableSetColumnIndex(7);
                ImGui.TextUnformatted(group.LastSaleUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            }

            ImGui.EndTable();
        }

        if (selectedSaleGroup is { } selectedKey)
        {
            var group = groups.FirstOrDefault(x => x.Item.ItemId == selectedKey.ItemId && x.IsHq == selectedKey.IsHq)
                        ?? BuildSaleGroups(sales).FirstOrDefault(x => x.Item.ItemId == selectedKey.ItemId && x.IsHq == selectedKey.IsHq);
            if (group is not null)
                DrawSaleGroupDetails(group);
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Privacy note: buyer character names from the in-game sale-history window are stored only in your local Should I Sell? data file.");
    }

    private void DrawSalesSummary(IReadOnlyList<PersonalSale> sales)
    {
        var totalNet = sales.Where(x => x.NetGil > 0).Sum(x => x.NetGil);
        var totalUnits = sales.Where(x => x.Quantity > 0).Sum(x => (long)x.Quantity);
        var unique = sales.Select(x => (x.ItemId, x.IsHq)).Distinct().Count();
        var liveCount = sales.Count(x => x.Source is PersonalSaleSource.Announcement or PersonalSaleSource.Reconciled);
        var confirmedCount = sales.Count(x => x.Source is PersonalSaleSource.History or PersonalSaleSource.Reconciled);
        var financialSales = sales.Where(x => x.NetGil > 0).ToList();
        var bestSale = financialSales.MaxBy(x => x.NetGil);
        var topGroup = financialSales.GroupBy(x => (x.ItemId, x.IsHq))
            .Select(g => new { g.Key, Net = g.Sum(x => x.NetGil) })
            .OrderByDescending(x => x.Net)
            .FirstOrDefault();
        var bestDay = financialSales.GroupBy(x => x.SoldAtUtc.ToLocalTime().Date)
            .Select(g => new { Date = g.Key, Net = g.Sum(x => x.NetGil) })
            .OrderByDescending(x => x.Net)
            .FirstOrDefault();

        if (ImGui.BeginTable("sales-summary", 4, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
        {
            ImGui.TableNextRow();
            SummaryCell("Net earned", totalNet > 0 ? Gil((double)totalNet) : "—");
            SummaryCell("Transactions", sales.Count.ToString("N0"));
            SummaryCell("Units sold", totalUnits.ToString("N0"));
            SummaryCell("Unique items", unique.ToString("N0"));
            ImGui.TableNextRow();
            SummaryCell("Top earner", topGroup is null ? "—" : $"{plugin.Catalog.Get(topGroup.Key.ItemId).Name}{(topGroup.Key.IsHq ? " [HQ]" : "")} • {Gil((double)topGroup.Net)}");
            SummaryCell("Biggest sale", bestSale is null ? "—" : $"{plugin.Catalog.Get(bestSale.ItemId).Name} • {Gil((double)bestSale.NetGil)}");
            SummaryCell("Best day", bestDay is null ? "—" : $"{bestDay.Date:yyyy-MM-dd} • {Gil((double)bestDay.Net)}");
            SummaryCell("Live / confirmed", $"{liveCount:N0} / {confirmedCount:N0}");
            ImGui.EndTable();
        }
    }

    private static void SummaryCell(string label, string value)
    {
        ImGui.TableNextColumn();
        ImGui.TextDisabled(label);
        ImGui.TextUnformatted(value);
    }

    private bool SaleMatchesSearch(PersonalSale sale)
    {
        if (string.IsNullOrWhiteSpace(saleSearch))
            return true;
        var item = plugin.Catalog.Get(sale.ItemId);
        return item.Name.Contains(saleSearch, StringComparison.CurrentCultureIgnoreCase) ||
               (sale.RetainerName ?? string.Empty).Contains(saleSearch, StringComparison.CurrentCultureIgnoreCase) ||
               (sale.BuyerName ?? string.Empty).Contains(saleSearch, StringComparison.CurrentCultureIgnoreCase);
    }

    private List<SaleGroup> BuildSaleGroups(IEnumerable<PersonalSale> sales)
        => sales.GroupBy(x => (x.ItemId, x.IsHq))
            .Select(g =>
            {
                var list = g.OrderByDescending(x => x.SoldAtUtc).ToList();
                var knownFinancial = list.Where(x => x.NetGil > 0).ToList();
                var net = knownFinancial.Sum(x => x.NetGil);
                var units = list.Where(x => x.Quantity > 0).Sum(x => (long)x.Quantity);
                var financialUnits = knownFinancial.Where(x => x.Quantity > 0).Sum(x => (long)x.Quantity);
                return new SaleGroup(
                    plugin.Catalog.Get(g.Key.ItemId),
                    g.Key.IsHq,
                    list.Count,
                    units,
                    net,
                    financialUnits == 0 ? 0 : net / (double)financialUnits,
                    knownFinancial.Count == 0 ? 0 : net / (double)knownFinancial.Count,
                    knownFinancial.Count == 0 ? 0 : knownFinancial.Max(x => x.NetGil),
                    list.Min(x => x.SoldAtUtc),
                    list.Max(x => x.SoldAtUtc),
                    list);
            })
            .ToList();

    private static bool DrawSmallSaleIcon(ItemInfo item, bool isHq)
    {
        if (item.IconId == 0)
            return false;
        var shared = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.IconId, isHq));
        if (!shared.TryGetWrap(out var texture, out _))
            return false;
        var size = 24 * ImGuiHelpers.GlobalScale;
        ImGui.Image(texture.Handle, new Vector2(size, size));
        return true;
    }

    private void DrawSaleGroupDetails(SaleGroup group)
    {
        ImGui.Separator();
        ImGui.Spacing();
        var drewIcon = DrawSmallSaleIcon(group.Item, group.IsHq);
        if (drewIcon)
            ImGui.SameLine();
        ImGui.TextUnformatted($"{group.Item.Name}{(group.IsHq ? " [HQ]" : "")} — sale history");
        ImGui.TextDisabled($"{group.Transactions:N0} transaction(s) • {group.Units:N0} unit(s) • {(group.NetGil > 0 ? Gil((double)group.NetGil) : "—")} net earned • first captured sale {group.FirstSaleUtc.ToLocalTime():yyyy-MM-dd}");

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
        if (ImGui.BeginTable("personal-sales-detail", 7, flags, new Vector2(0, 230 * ImGuiHelpers.GlobalScale)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Sold", ImGuiTableColumnFlags.WidthFixed, 125 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Net earned", ImGuiTableColumnFlags.WidthFixed, 95 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Net / unit", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthFixed, 130 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Buyer", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 115 * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var sale in group.Sales)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(sale.SoldAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(sale.Quantity > 0 ? sale.Quantity.ToString("N0") : "—");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(sale.NetGil > 0 ? Gil((double)sale.NetGil) : "—");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(sale.NetGil > 0 && sale.Quantity > 0 ? Gil(sale.NetGil / (double)sale.Quantity) : "—");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(sale.RetainerName) ? "Unknown" : sale.RetainerName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(sale.BuyerName) ? "Unknown" : sale.BuyerName);
                ImGui.TableNextColumn();
                if (sale.Source == PersonalSaleSource.Announcement)
                    ImGui.TextColored(AttentionTextColor, "Live");
                else
                    ImGui.TextUnformatted(sale.Source == PersonalSaleSource.Reconciled ? "Live + confirmed" : "History");
            }

            ImGui.EndTable();
        }
    }
}
