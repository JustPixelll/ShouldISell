using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace ShouldISell.Windows;

public sealed partial class SuiteWindow
{
    private static readonly GilFlowCategory[] EditableGilCategories =
    {
        GilFlowCategory.Unclassified,
        GilFlowCategory.MarketBoardPurchase,
        GilFlowCategory.Vendor,
        GilFlowCategory.Quest,
        GilFlowCategory.Duty,
        GilFlowCategory.Teleport,
        GilFlowCategory.Repair,
        GilFlowCategory.Crafting,
        GilFlowCategory.Glamour,
        GilFlowCategory.Housing,
        GilFlowCategory.PlayerTrade,
        GilFlowCategory.RetainerTransfer,
        GilFlowCategory.Other,
    };

    private void DrawTycoonCashflowSummary(TraderSnapshot tradeSnapshot)
    {
        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0)
            return;

        var contentId = Plugin.PlayerState.ContentId;
        var flows = plugin.TraderStore.GetGilFlows(contentId);
        var allPurchases = plugin.TraderStore.GetPurchases(contentId);
        var sales = plugin.Store.GetPersonalSales(contentId);
        var walletIn = flows.Where(x => x.Amount > 0).Sum(x => (double)x.Amount);
        var walletOut = -flows.Where(x => x.Amount < 0).Sum(x => (double)x.Amount);
        var mbSpend = allPurchases.Sum(x => (double)x.TotalCost);
        var mbSales = sales.Sum(x => (double)x.NetGil);

        if (!ImGui.BeginTable("##tycoon-cashflow-headline", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
            return;

        MetricCell(0, "Current wallet", plugin.GilLedger.CurrentBalance is { } b ? Gil(b) : "—");
        MetricCell(1, "Observed wallet in", Gil(walletIn));
        MetricCell(2, "Observed wallet out", Gil(walletOut));
        MetricCell(3, "Observed wallet net", Gil(walletIn - walletOut));
        MetricCell(0, "MB purchase spend", Gil(mbSpend));
        MetricCell(1, "Captured MB sale income", Gil(mbSales));
        MetricCell(2, "Trade realized P&L", Gil(tradeSnapshot.RealizedProfit));
        MetricCell(3, "Unclassified wallet events", flows.Count(x => x.Category == GilFlowCategory.Unclassified).ToString("N0"));
        ImGui.EndTable();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Wallet figures are direct player-gil changes observed while Should I? is running. Market-sale income is shown separately because a retainer earns the sale before you later withdraw that gil into your character wallet.");
    }

    private void DrawTycoonCashflow()
    {
        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0)
        {
            ImGui.TextDisabled("Log in to start the gil cashflow ledger.");
            return;
        }

        var contentId = Plugin.PlayerState.ContentId;
        var flows = plugin.TraderStore.GetGilFlows(contentId);
        ImGui.TextWrapped("Every direct change to your character's gil wallet is recorded while Should I? is running. Confirmed Market Board purchases are automatically attributed when the exact cost matches. For quest, duty, vendor, teleport, repair and other changes, the gil delta is exact but the source is not always exposed by FFXIV, so Tycoon leaves it Unclassified until you choose a category instead of guessing.");
        ImGui.TextDisabled("Retainer Market Board sale revenue is tracked separately by Should I Sell? because the economic sale happens on the retainer before the gil is withdrawn into your player wallet. Mark withdrawals as Retainer transfer/internal if you want them excluded conceptually from earned-income analysis.");
        ImGui.Spacing();

        if (flows.Count == 0)
        {
            ImGui.TextDisabled("No wallet changes captured yet. The current gil amount is used only as a baseline; login/offline changes are never fabricated into transactions.");
            return;
        }

        if (ImGui.CollapsingHeader("Cashflow by category##tycoon-cashflow-category", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (ImGui.BeginTable("##tycoon-category-table", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Events", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Income", ImGuiTableColumnFlags.WidthFixed, 100 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Spend", ImGuiTableColumnFlags.WidthFixed, 100 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Net", ImGuiTableColumnFlags.WidthFixed, 100 * ImGuiHelpers.GlobalScale);
                ImGui.TableHeadersRow();

                foreach (var group in flows.GroupBy(x => x.Category).OrderByDescending(g => g.Sum(x => Math.Abs(x.Amount))))
                {
                    var income = group.Where(x => x.Amount > 0).Sum(x => (double)x.Amount);
                    var spend = -group.Where(x => x.Amount < 0).Sum(x => (double)x.Amount);
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(GilCategoryLabel(group.Key));
                    ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(group.Count().ToString("N0"));
                    ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(Gil(income));
                    ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(Gil(spend));
                    ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(Gil(income - spend));
                }
                ImGui.EndTable();
            }
        }

        ImGui.Spacing();
        ImGui.TextDisabled("RECENT DIRECT WALLET CHANGES — change a category inline when Tycoon cannot prove the source automatically.");
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("##tycoon-cashflow-ledger", 7, flags, new Vector2(0, 330 * ImGuiHelpers.GlobalScale)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 120 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Delta", ImGuiTableColumnFlags.WidthFixed, 100 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Balance", ImGuiTableColumnFlags.WidthFixed, 105 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 145 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Class", ImGuiTableColumnFlags.WidthFixed, 58 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Note", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var flow in flows.Take(1000))
        {
            ImGui.PushID(flow.Id);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(flow.AtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted($"{flow.Amount:+#,##0;-#,##0;0}g");
            ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted($"{flow.BalanceAfter:N0}g");
            ImGui.TableSetColumnIndex(3);
            DrawGilCategoryEditor(flow);
            ImGui.TableSetColumnIndex(4); ImGui.TextWrapped(flow.Source);
            ImGui.TableSetColumnIndex(5); ImGui.TextDisabled(flow.AutoClassified ? "Auto" : "User");
            ImGui.TableSetColumnIndex(6); ImGui.TextWrapped(flow.Note);
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawGilCategoryEditor(GilFlowEntry flow)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##gil-category", GilCategoryLabel(flow.Category)))
            return;

        foreach (var category in EditableGilCategories)
        {
            if (!ImGui.Selectable(GilCategoryLabel(category), category == flow.Category))
                continue;
            plugin.TraderStore.UpdateGilFlowClassification(
                flow.Id,
                category,
                GilCategoryLabel(category),
                autoClassified: false,
                note: flow.Note);
        }
        ImGui.EndCombo();
    }

    private void DrawTycoonPurchases()
    {
        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0)
        {
            ImGui.TextDisabled("Log in to inspect Market Board purchases.");
            return;
        }

        var contentId = Plugin.PlayerState.ContentId;
        var purchases = plugin.TraderStore.GetPurchases(contentId);
        ImGui.TextWrapped("Every confirmed Market Board purchase remains part of your spending history. Use the Trading position toggle to decide whether that purchase lot should participate in FIFO trade P&L/open positions. This is ideal for crafting, glamour, housing or personal-use buys: excluding a lot is reversible and does not erase the real gil spend.");
        ImGui.Spacing();

        if (purchases.Count == 0)
        {
            ImGui.TextDisabled("No captured Market Board purchases yet.");
            return;
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("##tycoon-purchase-ledger", 7, flags, new Vector2(0, -1)))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Bought", ImGuiTableColumnFlags.WidthFixed, 120 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Total cost", ImGuiTableColumnFlags.WidthFixed, 95 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Strategy / source", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Position", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 120 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var purchase in purchases.Take(5000))
        {
            var key = plugin.TraderStore.GetPurchaseKey(purchase);
            var excluded = plugin.TraderStore.IsPurchaseExcluded(purchase);
            ImGui.PushID(key);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(purchase.PurchasedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(plugin.Catalog.Get(purchase.ItemId).Name + (purchase.IsHq ? " [HQ]" : string.Empty));
            ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(purchase.Quantity.ToString("N0"));
            ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(Gil(purchase.TotalCost));
            ImGui.TableSetColumnIndex(4); ImGui.TextWrapped(purchase.Strategy);
            ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(excluded ? "Personal" : "Trade");
            ImGui.TableSetColumnIndex(6);
            if (ImGui.SmallButton(excluded ? "Track as trade" : "Mark personal"))
            {
                plugin.TraderStore.SetPurchaseExcluded(purchase, !excluded);
                plugin.TraderAnalyzer.GetSnapshot(force: true);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(excluded
                    ? "Put this purchase lot back into FIFO trading positions/P&L."
                    : "Exclude this purchase lot from trading positions/P&L while keeping its real Market Board spending in the cashflow ledger.");
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private static string GilCategoryLabel(GilFlowCategory category) => category switch
    {
        GilFlowCategory.MarketBoardPurchase => "Market Board purchase",
        GilFlowCategory.Vendor => "Vendor",
        GilFlowCategory.Quest => "Quest",
        GilFlowCategory.Duty => "Duty / roulette",
        GilFlowCategory.Teleport => "Teleport",
        GilFlowCategory.Repair => "Repair",
        GilFlowCategory.Crafting => "Crafting / materials",
        GilFlowCategory.Glamour => "Glamour",
        GilFlowCategory.Housing => "Housing",
        GilFlowCategory.PlayerTrade => "Player trade",
        GilFlowCategory.RetainerTransfer => "Retainer transfer/internal",
        GilFlowCategory.Other => "Other",
        _ => "Unclassified",
    };
}
