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
        int Units,
        long NetGil,
        double NetPerUnit,
        double AverageTransaction,
        long BestTransaction,
        DateTimeOffset FirstSaleUtc,
        DateTimeOffset LastSaleUtc,
        IReadOnlyList<PersonalSale> Sales);

    private void DrawSalesHistory()
    {
        ImGui.TextWrapped("Your personal retainer sales ledger. Open each retainer's ‘View sale history’ window and Should I Sell? will capture the game's exact recent sale rows — including date/time, buyer, quantity and the gil actually deposited after tax — then keep them locally so your history can grow over time.");
        if (plugin.SaleHistory.IsDegraded)
        {
            ImGui.Spacing();
            ImGui.TextColored(AttentionTextColor, "Sale-history capture is currently unavailable (game signature mismatch).");
            ImGui.TextDisabled("The rest of Should I Sell? still works. This capture hook may need an update after a game patch.");
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
            ImGui.TextDisabled("No personal sales captured yet.");
            ImGui.BulletText("Visit a Summoning Bell and select a retainer.");
            ImGui.BulletText("Open ‘View sale history’ for that retainer.");
            ImGui.BulletText("Repeat for each retainer. The game normally exposes up to 20 recent history rows per retainer at a time.");
            ImGui.TextWrapped("After the first capture, revisit sale history occasionally. Already-seen rows are deduplicated, while new sales are added to your persistent local ledger.");
            return;
        }

        ImGui.Spacing();
        DrawSalesSummary(sales);
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##sales-search", "Search sold item, retainer or buyer...", ref saleSearch, 128);
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        var sorts = new[] { "Net earned", "Last sold", "Transactions", "Units sold", "Item name" };
        ImGui.Combo("Sort by##sales", ref saleSortMode, sorts, sorts.Length);

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
                DrawSmallSaleIcon(group.Item, group.IsHq);
                ImGui.SameLine();
                ImGui.TextUnformatted(group.Item.Name + (group.IsHq ? " [HQ]" : string.Empty));
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(group.Transactions.ToString("N0"));
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(group.Units.ToString("N0"));
                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(Gil((double)group.NetGil));
                ImGui.TableSetColumnIndex(4);
                ImGui.TextUnformatted(Gil(group.NetPerUnit));
                ImGui.TableSetColumnIndex(5);
                ImGui.TextUnformatted(Gil(group.AverageTransaction));
                ImGui.TableSetColumnIndex(6);
                ImGui.TextUnformatted(Gil((double)group.BestTransaction));
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
        var totalNet = sales.Sum(x => x.NetGil);
        var totalUnits = sales.Sum(x => (long)x.Quantity);
        var unique = sales.Select(x => (x.ItemId, x.IsHq)).Distinct().Count();
        var bestSale = sales.MaxBy(x => x.NetGil)!;
        var topGroup = sales.GroupBy(x => (x.ItemId, x.IsHq))
            .Select(g => new { g.Key, Net = g.Sum(x => x.NetGil) })
            .OrderByDescending(x => x.Net)
            .First();
        var topItem = plugin.Catalog.Get(topGroup.Key.ItemId);
        var bestDay = sales.GroupBy(x => x.SoldAtUtc.ToLocalTime().Date)
            .Select(g => new { Date = g.Key, Net = g.Sum(x => x.NetGil), Count = g.Count() })
            .OrderByDescending(x => x.Net)
            .First();

        if (ImGui.BeginTable("sales-summary", 4, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
        {
            SummaryCell("Net earned", Gil((double)totalNet));
            SummaryCell("Transactions", sales.Count.ToString("N0"));
            SummaryCell("Units sold", totalUnits.ToString("N0"));
            SummaryCell("Unique items", unique.ToString("N0"));
            ImGui.TableNextRow();
            SummaryCell("Top earner", $"{topItem.Name}{(topGroup.Key.IsHq ? " [HQ]" : "")} • {Gil((double)topGroup.Net)}");
            SummaryCell("Biggest sale", $"{plugin.Catalog.Get(bestSale.ItemId).Name} • {Gil((double)bestSale.NetGil)}");
            SummaryCell("Best day", $"{bestDay.Date:yyyy-MM-dd} • {Gil((double)bestDay.Net)}");
            SummaryCell("Avg transaction", Gil((double)totalNet / Math.Max(1, sales.Count)));
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
               sale.RetainerName.Contains(saleSearch, StringComparison.CurrentCultureIgnoreCase) ||
               sale.BuyerName.Contains(saleSearch, StringComparison.CurrentCultureIgnoreCase);
    }

    private List<SaleGroup> BuildSaleGroups(IEnumerable<PersonalSale> sales)
        => sales.GroupBy(x => (x.ItemId, x.IsHq))
            .Select(g =>
            {
                var list = g.OrderByDescending(x => x.SoldAtUtc).ToList();
                var net = list.Sum(x => x.NetGil);
                var units = list.Sum(x => x.Quantity);
                return new SaleGroup(
                    plugin.Catalog.Get(g.Key.ItemId),
                    g.Key.IsHq,
                    list.Count,
                    units,
                    net,
                    units == 0 ? 0 : net / (double)units,
                    list.Count == 0 ? 0 : net / (double)list.Count,
                    list.Max(x => x.NetGil),
                    list.Min(x => x.SoldAtUtc),
                    list.Max(x => x.SoldAtUtc),
                    list);
            })
            .ToList();

    private static void DrawSmallSaleIcon(ItemInfo item, bool isHq)
    {
        if (item.IconId == 0)
            return;
        var shared = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.IconId, isHq));
        if (!shared.TryGetWrap(out var texture, out _))
            return;
        var size = 24 * ImGuiHelpers.GlobalScale;
        ImGui.Image(texture.Handle, new Vector2(size, size));
    }

    private void DrawSaleGroupDetails(SaleGroup group)
    {
        ImGui.Separator();
        ImGui.Spacing();
        DrawSmallSaleIcon(group.Item, group.IsHq);
        ImGui.SameLine();
        ImGui.TextUnformatted($"{group.Item.Name}{(group.IsHq ? " [HQ]" : "")} — sale history");
        ImGui.TextDisabled($"{group.Transactions:N0} transaction(s) • {group.Units:N0} unit(s) • {Gil((double)group.NetGil)} net earned • first captured sale {group.FirstSaleUtc.ToLocalTime():yyyy-MM-dd}");

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
        if (ImGui.BeginTable("personal-sales-detail", 6, flags, new Vector2(0, 230 * ImGuiHelpers.GlobalScale)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Sold", ImGuiTableColumnFlags.WidthFixed, 125 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Net earned", ImGuiTableColumnFlags.WidthFixed, 95 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Net / unit", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthFixed, 130 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Buyer", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            foreach (var sale in group.Sales)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(sale.SoldAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(sale.Quantity.ToString("N0"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Gil((double)sale.NetGil));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Gil(sale.NetGil / (double)Math.Max(1, sale.Quantity)));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(sale.RetainerName) ? "Unknown" : sale.RetainerName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(sale.BuyerName) ? "Unknown" : sale.BuyerName);
            }

            ImGui.EndTable();
        }
    }
}
