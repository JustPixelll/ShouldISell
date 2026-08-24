using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
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

    // Render these glyphs through ImGuiComponents.IconButton, which explicitly pushes Dalamud's
    // icon font. Keep common high-frequency categories one click away; the full popup remains.
    private static readonly (GilFlowCategory Category, string Icon, string Label)[] QuickSpendGilCategories =
    {
        (GilFlowCategory.MarketBoardPurchase, "\uf07a", "Market Board purchase"),
        (GilFlowCategory.Vendor, "\uf54e", "Vendor"),
        (GilFlowCategory.Crafting, "\uf6e3", "Crafting / materials"),
        (GilFlowCategory.Glamour, "\uf553", "Glamour"),
        (GilFlowCategory.Housing, "\uf015", "Housing"),
        (GilFlowCategory.Teleport, "\uf14e", "Teleport"),
        (GilFlowCategory.Repair, "\uf0ad", "Repair"),
    };

    private static readonly (GilFlowCategory Category, string Icon, string Label)[] QuickIncomeGilCategories =
    {
        (GilFlowCategory.Vendor, "\uf54e", "Vendor"),
        (GilFlowCategory.Quest, "\uf005", "Quest"),
        (GilFlowCategory.Duty, "\uf091", "Duty / roulette"),
        (GilFlowCategory.PlayerTrade, "\uf362", "Player trade"),
        (GilFlowCategory.RetainerTransfer, "\uf51e", "Retainer transfer / internal"),
    };

    private string vendorPurchaseSearch = string.Empty;
    private uint vendorPurchaseItemId;
    private int vendorPurchaseQuantity = 1;
    private int vendorPurchaseUnitPrice;
    private bool vendorPurchaseTrackAsTrade = true;
    private string vendorPurchaseStatus = string.Empty;

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
        var mbSpend = allPurchases.Where(x => x.SourceKind == PurchaseSourceKind.MarketBoard).Sum(x => (double)x.TotalCost);
        var vendorSpend = allPurchases.Where(x => x.SourceKind == PurchaseSourceKind.VendorManual).Sum(x => (double)x.TotalCost);
        var mbSales = sales.Sum(x => (double)x.NetGil);

        if (!ImGui.BeginTable("##tycoon-cashflow-headline", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
            return;

        MetricCell(0, "Current wallet", plugin.GilLedger.CurrentBalance is { } b ? Gil(b) : "—");
        MetricCell(1, "Observed wallet in", Gil(walletIn));
        MetricCell(2, "Observed wallet out", Gil(walletOut));
        MetricCell(3, "Observed wallet net", Gil(walletIn - walletOut));
        MetricCell(0, "MB purchase spend", Gil(mbSpend));
        MetricCell(1, "Manual vendor spend", Gil(vendorSpend));
        MetricCell(2, "Captured MB sale income", Gil(mbSales));
        MetricCell(3, "Trade realized P&L", Gil(tradeSnapshot.RealizedProfit));
        MetricCell(0, "Unclassified wallet events", flows.Count(x => x.Category == GilFlowCategory.Unclassified).ToString("N0"));
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
        ImGui.TextWrapped("Cashflow records direct changes to your character's gil wallet while Should I? is running. The amount and resulting balance are the important evidence. Category analytics are intentionally deferred until Should I? can attribute categories reliably instead of asking you to classify every wallet delta manually.");
        ImGui.TextDisabled("Market Board purchase records and retainer-sale economics remain tracked separately with item-level evidence in Purchases / Trade Positions / Closed Trades.");
        ImGui.Spacing();

        if (flows.Count == 0)
        {
            ImGui.TextDisabled("No wallet changes captured yet. The current gil amount is used only as a baseline; login/offline changes are never fabricated into transactions.");
            return;
        }

        ImGui.TextDisabled("RECENT DIRECT WALLET CHANGES");
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("##tycoon-cashflow-ledger", 5, flags, new Vector2(0, 430 * ImGuiHelpers.GlobalScale)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 120 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Delta", ImGuiTableColumnFlags.WidthFixed, 100 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Balance", ImGuiTableColumnFlags.WidthFixed, 105 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Source evidence", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Note", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var flow in flows.Take(1000))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(flow.AtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted($"{flow.Amount:+#,##0;-#,##0;0}g");
            ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted($"{flow.BalanceAfter:N0}g");
            ImGui.TableSetColumnIndex(3); ImGui.TextWrapped(string.IsNullOrWhiteSpace(flow.Source) ? "Unattributed wallet change" : flow.Source);
            ImGui.TableSetColumnIndex(4); ImGui.TextWrapped(flow.Note);
        }

        ImGui.EndTable();
    }

    private void DrawTycoonPurchases()
    {
        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0)
        {
            ImGui.TextDisabled("Log in to inspect Market Board purchases.");
            return;
        }

        var contentId = Plugin.PlayerState.ContentId;
        DrawManualVendorPurchaseEntry(contentId);
        ImGui.Spacing();
        var purchases = plugin.TraderStore.GetPurchases(contentId);
        ImGui.TextWrapped("Purchases are now a unified acquisition ledger. Market Board buys are captured automatically; normal-gil vendor buys can be entered manually. Use the Trade/Personal toggle to decide whether a lot participates in FIFO trading P&L without erasing the real acquisition record.");
        ImGui.Spacing();

        if (purchases.Count == 0)
        {
            ImGui.TextDisabled("No captured Market Board or manually entered vendor purchases yet.");
            return;
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("##tycoon-purchase-ledger", 8, flags, new Vector2(0, -1)))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Bought", ImGuiTableColumnFlags.WidthFixed, 120 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Total cost", ImGuiTableColumnFlags.WidthFixed, 95 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 100 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Strategy", ImGuiTableColumnFlags.WidthStretch);
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
            ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(purchase.SourceKind == PurchaseSourceKind.VendorManual ? "Vendor (manual)" : "Market Board");
            ImGui.TableSetColumnIndex(5); ImGui.TextWrapped(purchase.Strategy);
            ImGui.TableSetColumnIndex(6); ImGui.TextUnformatted(excluded ? "Personal" : "Trade");
            ImGui.TableSetColumnIndex(7);
            if (ImGui.SmallButton(excluded ? "Track as trade" : "Mark personal"))
            {
                plugin.TraderStore.SetPurchaseExcluded(purchase, !excluded);
                plugin.TraderAnalyzer.GetSnapshot(force: true);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(excluded
                    ? "Put this purchase lot back into FIFO trading positions/P&L."
                    : "Exclude this purchase lot from trading positions/P&L while keeping the real acquisition in your purchase/cashflow history.");
            ImGui.PopID();
        }

        ImGui.EndTable();
    }


    private void DrawManualVendorPurchaseEntry(ulong contentId)
    {
        if (!ImGui.CollapsingHeader("Record vendor purchase manually##vendor-purchase-entry", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextWrapped("FFXIV does not expose a reliable item-level vendor-purchase event to Dalamud. Enter a normal-gil NPC purchase here when you want its real cost basis tracked. Should I? never invents the purchase: you choose the item, quantity and unit cost, then confirm it.");
        ImGui.TextDisabled("If the exact wallet decrease was captured as an Unclassified event in the last five minutes, Should I? will relabel that existing event as Vendor. If no exact match exists, no synthetic wallet transaction is created.");

        ImGui.SetNextItemWidth(360 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputTextWithHint("##vendor-purchase-search", "Search normal-gil vendor item...", ref vendorPurchaseSearch, 128) &&
            vendorPurchaseItemId != 0 &&
            !string.Equals(plugin.Catalog.Get(vendorPurchaseItemId).Name, vendorPurchaseSearch, StringComparison.CurrentCultureIgnoreCase))
        {
            vendorPurchaseItemId = 0;
        }
        var matches = string.IsNullOrWhiteSpace(vendorPurchaseSearch)
            ? new List<MarketCatalogEntry>()
            : plugin.Catalog.GetAllMarketableEntries()
                .Where(x => x.Item.VendorGilShopPrice is > 0 && x.Item.Name.Contains(vendorPurchaseSearch, StringComparison.CurrentCultureIgnoreCase))
                .Take(8)
                .ToList();
        if (matches.Count > 0 && (vendorPurchaseItemId == 0 || !string.Equals(plugin.Catalog.Get(vendorPurchaseItemId).Name, vendorPurchaseSearch, StringComparison.CurrentCultureIgnoreCase)))
        {
            if (ImGui.BeginChild("##vendor-purchase-matches", new Vector2(360 * ImGuiHelpers.GlobalScale, Math.Min(150, matches.Count * 24 + 8) * ImGuiHelpers.GlobalScale), true))
            {
                foreach (var match in matches)
                {
                    if (!ImGui.Selectable($"{match.Item.Name} — {match.Item.VendorGilShopPrice:N0}g##vendor-pick-{match.Item.ItemId}"))
                        continue;
                    vendorPurchaseItemId = match.Item.ItemId;
                    vendorPurchaseSearch = match.Item.Name;
                    vendorPurchaseUnitPrice = checked((int)Math.Min((uint)int.MaxValue, match.Item.VendorGilShopPrice!.Value));
                }
                ImGui.EndChild();
            }
        }

        if (vendorPurchaseItemId == 0)
        {
            if (!string.IsNullOrWhiteSpace(vendorPurchaseStatus))
                ImGui.TextDisabled(vendorPurchaseStatus);
            return;
        }

        var item = plugin.Catalog.Get(vendorPurchaseItemId);
        if (item.VendorGilShopPrice is null)
        {
            vendorPurchaseItemId = 0;
            vendorPurchaseStatus = "That item is no longer recognized as a normal-gil vendor item in current game data.";
            return;
        }

        ImGui.TextUnformatted($"Selected: {item.Name} • game-data vendor price {item.VendorGilShopPrice.Value:N0}g/unit");
        ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Quantity##vendor-purchase", ref vendorPurchaseQuantity, 1, 10))
            vendorPurchaseQuantity = Math.Clamp(vendorPurchaseQuantity, 1, 999_999);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Unit cost##vendor-purchase", ref vendorPurchaseUnitPrice, 1, 100))
            vendorPurchaseUnitPrice = Math.Clamp(vendorPurchaseUnitPrice, 1, 999_999_999);
        ImGui.SameLine();
        ImGui.Checkbox("Track as trade position##vendor-purchase", ref vendorPurchaseTrackAsTrade);

        var total = (long)Math.Max(1, vendorPurchaseQuantity) * Math.Max(1, vendorPurchaseUnitPrice);
        ImGui.TextDisabled($"Manual acquisition total: {total:N0}g. Vendor items are recorded as NQ with zero buyer tax.");

        if (ImGui.Button("RECORD VENDOR PURCHASE"))
        {
            var prediction = GetCurrentWorldVendorOpportunities()
                .Where(x => x.Item.ItemId == vendorPurchaseItemId && !x.IsHq)
                .OrderByDescending(x => x.OpportunityScore)
                .FirstOrDefault();
            var predictionQuantityMatches = prediction is not null && prediction.AcquireQuantity == vendorPurchaseQuantity;
            var predictedProfit = prediction?.NetExitUnitPrice is { } predictedNet
                ? predictedNet * (double)vendorPurchaseQuantity - total
                : (double?)null;
            var now = DateTimeOffset.UtcNow;
            var purchase = new PersonalPurchase(
                contentId,
                Plugin.PlayerState.CurrentWorld.RowId,
                vendorPurchaseItemId,
                false,
                vendorPurchaseQuantity,
                checked((uint)vendorPurchaseUnitPrice),
                0,
                total,
                0,
                now,
                prediction?.StrategyLabel ?? "Vendor -> Market (manual)",
                prediction?.OpportunityScore,
                prediction?.SuggestedExitUnitPrice,
                predictionQuantityMatches ? prediction?.EstimatedLiquidationDays : null,
                predictedProfit,
                prediction?.AnalysedAtUtc,
                PurchaseSourceKind.VendorManual);

            if (plugin.TraderStore.AddPurchase(purchase, flush: false))
            {
                if (!vendorPurchaseTrackAsTrade)
                    plugin.TraderStore.SetPurchaseExcluded(purchase, true, flush: false);
                var linked = plugin.TraderStore.TryClassifyRecentMatchingOutflow(
                    contentId,
                    total,
                    GilFlowCategory.Vendor,
                    $"Vendor purchase: {item.Name}",
                    $"Manual vendor acquisition: {vendorPurchaseQuantity:N0} × {vendorPurchaseUnitPrice:N0}g",
                    flush: false);
                plugin.TraderStore.Flush();
                plugin.TraderAnalyzer.GetSnapshot(force: true);
                vendorPurchaseStatus = linked
                    ? $"Recorded {vendorPurchaseQuantity:N0} × {item.Name} for {total:N0}g and linked the exact recent wallet outflow."
                    : $"Recorded {vendorPurchaseQuantity:N0} × {item.Name} for {total:N0}g. No exact recent Unclassified wallet outflow was changed.";
            }
            else
            {
                vendorPurchaseStatus = "That vendor purchase looks like a duplicate of an entry recorded moments ago; nothing was added.";
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Adds real vendor cost basis to Tycoon. If marked Trade, later captured sales can close it through the same FIFO accounting used for Market Board buys.");
        if (!string.IsNullOrWhiteSpace(vendorPurchaseStatus))
            ImGui.TextWrapped(vendorPurchaseStatus);
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
