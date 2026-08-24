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
    PotentialProfit,
    Roi,
    Liquidation,
    Confidence,
    Tracking,
}

public enum BuyTrackingFilter
{
    All,
    NotBought,
    Bought,
}

public sealed partial class SuiteWindow
{
    private sealed class BuyLaneUiState
    {
        public string DiscoverySearch = string.Empty;
        public string CategorySearch = string.Empty;
        public string FindingsSearch = string.Empty;
        public bool IncludeNq = true;
        public bool IncludeHq = true;
        public bool IncludeEquipment;
        public bool UseCategoryFilter;
        public readonly HashSet<uint> CategoryIds = new();
        public bool EnableMarketToMarket = true;
        public bool EnableMarketToVendor = true;
        public int DetailedLimit = 120;

        public int MinimumStars = 1;
        public double MinimumProfit;
        public float MinimumRoiPercent;
        public long MaximumCost = 999_999_999;
        public float MaximumLiquidationDays = 3650;
        public BuyTrackingFilter Tracking = BuyTrackingFilter.All;
        public bool FindingsNq = true;
        public bool FindingsHq = true;
        public bool FindingsMarketToMarket = true;
        public bool FindingsMarketToVendor = true;
    }

    private readonly BuyLaneUiState marketBuyLane = new();
    private readonly BuyLaneUiState vendorBuyLane = new() { IncludeHq = false, FindingsHq = false };

    private sealed record BuyTrackingSummary(bool IsBought, int Quantity, DateTimeOffset? LastPurchasedAtUtc, bool ExactListingMatch)
    {
        public string Label => !IsBought
            ? "Not bought"
            : ExactListingMatch ? $"Bought {Quantity:N0} ✓" : $"Bought {Quantity:N0}";
    }

    private void DrawBuyModule()
    {
        var currentWorldId = CurrentBuyWorldId;
        if (buyDetailsOpen && selectedBuyOpportunity is { } selected)
        {
            if (selected.WorldId != currentWorldId)
            {
                selectedBuyOpportunity = null;
                buyDetailsOpen = false;
            }
            else
            {
                DrawBuyDetailPage(selected);
                return;
            }
        }

        ImGui.TextWrapped("Should I Buy? separates Market Board acquisitions from renewable Vendor → Market opportunities. Each lane is discovered from Universalis independently, then filtered as findings. No hidden budget/ROI/holding rule silently removes discoveries, and Should I? never performs native queued Market Board searches.");
        if (currentWorldId != 0)
            ImGui.TextDisabled($"Current-world scope: {CurrentBuyWorldName}. Purchases you actually make are tracked separately and shown directly on opportunity rows.");
        ImGui.Spacing();

        if (!ImGui.BeginTabBar("##buy-lanes"))
            return;

        if (ImGui.BeginTabItem("Market Board Opportunities"))
        {
            DrawBuyLane(BuyScanLane.MarketBoard, marketBuyLane);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Vendor Opportunities"))
        {
            DrawBuyLane(BuyScanLane.Vendor, vendorBuyLane);
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawBuyLane(BuyScanLane lane, BuyLaneUiState state)
    {
        var vendor = lane == BuyScanLane.Vendor;
        ImGui.TextWrapped(vendor
            ? "Vendor → Market looks for normal-gil NPC items whose realistic Market Board exit creates a worthwhile spread. Vendor supply is treated as renewable; recommendations target a working listing rather than speculative stockpiles."
            : "Market Board opportunities look for listing packages that can be acquired manually and exited through the shared Should I Sell? model, including guaranteed Market → Vendor cases where appropriate.");
        ImGui.Spacing();

        DrawBuyDiscoveryFilters(lane, state);
        ImGui.Spacing();
        DrawBuyUniversalisUpdate(lane, state);
        ImGui.Separator();
        DrawBuyFindingsFilters(lane, state);
        ImGui.Spacing();
        DrawBuyLaneTable(lane, state);
    }

    private void DrawBuyDiscoveryFilters(BuyScanLane lane, BuyLaneUiState state)
    {
        if (!ImGui.CollapsingHeader($"Discovery filters##buy-discovery-{lane}", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextDisabled("These filters decide which catalog items Universalis should inspect. Profit, ROI, cost and holding-time filters are intentionally applied only after discovery.");

        ImGui.SetNextItemWidth(330 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint($"##buy-discovery-name-{lane}", "Item name contains...", ref state.DiscoverySearch, 128);
        Tooltip("Restrict Universalis discovery to item names containing this text. Leave blank for the full eligible catalog.");

        ImGui.SameLine();
        var equipment = state.IncludeEquipment;
        if (ImGui.Checkbox($"Include equipment##buy-equipment-{lane}", ref equipment))
            state.IncludeEquipment = equipment;
        Tooltip("Include equippable items such as gear and glamour-capable equipment in discovery.");

        if (lane == BuyScanLane.MarketBoard)
        {
            var nq = state.IncludeNq;
            if (ImGui.Checkbox("NQ##buy-discovery-nq", ref nq))
                state.IncludeNq = nq;
            ImGui.SameLine();
            var hq = state.IncludeHq;
            if (ImGui.Checkbox("HQ##buy-discovery-hq", ref hq))
                state.IncludeHq = hq;

            ImGui.SameLine();
            var market = state.EnableMarketToMarket;
            if (ImGui.Checkbox("Market → Market##buy-discovery-m2m", ref market))
                state.EnableMarketToMarket = market;
            ImGui.SameLine();
            var vendorExit = state.EnableMarketToVendor;
            if (ImGui.Checkbox("Market → Vendor##buy-discovery-m2v", ref vendorExit))
                state.EnableMarketToVendor = vendorExit;
        }
        else
        {
            ImGui.TextDisabled("Vendor discovery is NQ-only because normal NPC gil shops do not sell HQ variants.");
        }

        var useCategories = state.UseCategoryFilter;
        if (ImGui.Checkbox($"Filter by FFXIV item categories##buy-category-toggle-{lane}", ref useCategories))
        {
            state.UseCategoryFilter = useCategories;
            if (useCategories && state.CategoryIds.Count == 0)
            {
                foreach (var category in plugin.Catalog.GetCategories())
                    state.CategoryIds.Add(category.CategoryId);
            }
        }
        if (state.UseCategoryFilter)
            DrawBuyDiscoveryCategories(lane, state);

        var detailed = state.DetailedLimit;
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt($"Detailed Universalis candidates##buy-detailed-{lane}", ref detailed, 20, 500, "%d items"))
            state.DetailedLimit = Math.Clamp(detailed, 20, 500);
        Tooltip("After the cheap aggregated discovery pass, at most this many unique item IDs receive detailed current listings plus 90-day history from Universalis.");
    }

    private void DrawBuyDiscoveryCategories(BuyScanLane lane, BuyLaneUiState state)
    {
        var categories = plugin.Catalog.GetCategories();
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint($"##buy-category-search-{lane}", "Filter category names...", ref state.CategorySearch, 96);
        ImGui.SameLine();
        if (ImGui.SmallButton($"All##buy-category-all-{lane}"))
        {
            state.CategoryIds.Clear();
            foreach (var category in categories)
                state.CategoryIds.Add(category.CategoryId);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton($"None##buy-category-none-{lane}"))
            state.CategoryIds.Clear();

        if (ImGui.BeginChild($"##buy-category-list-{lane}", new Vector2(0, 135 * ImGuiHelpers.GlobalScale), true))
        {
            foreach (var category in categories.Where(x =>
                         string.IsNullOrWhiteSpace(state.CategorySearch) ||
                         x.Name.Contains(state.CategorySearch, StringComparison.CurrentCultureIgnoreCase)))
            {
                var enabled = state.CategoryIds.Contains(category.CategoryId);
                if (!ImGui.Checkbox($"{category.Name}##buy-cat-{lane}-{category.CategoryId}", ref enabled))
                    continue;
                if (enabled) state.CategoryIds.Add(category.CategoryId);
                else state.CategoryIds.Remove(category.CategoryId);
            }
            ImGui.EndChild();
        }
        ImGui.TextDisabled($"{state.CategoryIds.Count:N0} of {categories.Count:N0} categories selected.");
    }

    private void DrawBuyUniversalisUpdate(BuyScanLane lane, BuyLaneUiState state)
    {
        var scanner = plugin.BuyScanner;
        var thisLaneRunning = scanner.IsScanning && scanner.ActiveLane == lane;
        var otherLaneRunning = scanner.IsScanning && scanner.ActiveLane != lane;
        var laneLabel = lane == BuyScanLane.Vendor ? "VENDOR OPPORTUNITIES" : "MARKET BOARD OPPORTUNITIES";

        if (otherLaneRunning)
            ImGui.BeginDisabled();
        if (!scanner.IsScanning)
        {
            if (ImGui.Button($"UPDATE {laneLabel} FROM UNIVERSALIS##buy-update-{lane}"))
            {
                ApplyDiscoverySettings(lane, state);
                selectedBuyOpportunity = null;
                buyDetailsOpen = false;
                if (lane == BuyScanLane.Vendor)
                    _ = scanner.ScanVendorAsync();
                else
                    _ = scanner.ScanMarketAsync();
            }
            Tooltip("Run aggregated Universalis discovery followed by detailed Universalis current-listing + 90-day-history analysis for this lane. This does not make native FFXIV Market Board requests.");
        }
        else if (thisLaneRunning)
        {
            if (ImGui.Button($"STOP UNIVERSALIS UPDATE##buy-stop-{lane}"))
                scanner.CancelScan();
        }
        else
        {
            ImGui.TextDisabled("The other Buy lane is currently updating from Universalis.");
        }
        if (otherLaneRunning)
            ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled(scanner.Status);

        if (thisLaneRunning && scanner.BroadItemsTotal > 0 && scanner.BroadItemsScanned < scanner.BroadItemsTotal)
        {
            var fraction = scanner.BroadItemsScanned / (float)Math.Max(1, scanner.BroadItemsTotal);
            ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"Universalis discovery {scanner.BroadItemsScanned:N0}/{scanner.BroadItemsTotal:N0}");
        }
        else if (thisLaneRunning && scanner.DeepItemsTotal > 0)
        {
            var fraction = scanner.DeepItemsScanned / (float)Math.Max(1, scanner.DeepItemsTotal);
            ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"Detailed Universalis {scanner.DeepItemsScanned:N0}/{scanner.DeepItemsTotal:N0}");
        }
    }

    private void ApplyDiscoverySettings(BuyScanLane lane, BuyLaneUiState state)
    {
        var c = plugin.Configuration;
        c.BuyDeepCandidateLimit = Math.Clamp(state.DetailedLimit, 20, 500);
        c.BuyIncludeEquipment = state.IncludeEquipment;
        c.BuyUseCategoryFilter = state.UseCategoryFilter;
        c.BuyIncludedCategoryIds = state.CategoryIds.Order().ToList();
        c.BuyDiscoveryNameFilter = state.DiscoverySearch.Trim();
        c.BuyDiscoveryIncludeNq = lane == BuyScanLane.Vendor || state.IncludeNq;
        c.BuyDiscoveryIncludeHq = lane == BuyScanLane.MarketBoard && state.IncludeHq;
        c.BuyEnableMarketToMarket = lane == BuyScanLane.MarketBoard && state.EnableMarketToMarket;
        c.BuyEnableMarketToVendor = lane == BuyScanLane.MarketBoard && state.EnableMarketToVendor;
        c.BuyEnableVendorToMarket = lane == BuyScanLane.Vendor;
        // These are runtime discovery inputs. Do not persist the temporary lane copy over the other
        // tab's independent UI state; the scanner snapshots them immediately when the run starts.
    }

    private void DrawBuyFindingsFilters(BuyScanLane lane, BuyLaneUiState state)
    {
        if (!ImGui.CollapsingHeader($"Findings filters##buy-findings-{lane}", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint($"##buy-findings-search-{lane}", "Filter found item names...", ref state.FindingsSearch, 128);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo($"##buy-tracking-filter-{lane}", state.Tracking switch
            {
                BuyTrackingFilter.Bought => "Bought",
                BuyTrackingFilter.NotBought => "Not bought",
                _ => "All tracking",
            }))
        {
            foreach (var filter in Enum.GetValues<BuyTrackingFilter>())
            {
                var label = filter switch
                {
                    BuyTrackingFilter.Bought => "Bought / tracked",
                    BuyTrackingFilter.NotBought => "Not bought",
                    _ => "All",
                };
                if (ImGui.Selectable(label, state.Tracking == filter))
                    state.Tracking = filter;
            }
            ImGui.EndCombo();
        }

        var stars = state.MinimumStars;
        ImGui.SetNextItemWidth(155 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt($"Minimum stars##buy-findings-stars-{lane}", ref stars, 1, 5, "%d★+"))
            state.MinimumStars = stars;

        var minProfit = state.MinimumProfit;
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputDouble($"Minimum profit##buy-findings-profit-{lane}", ref minProfit, 1000, 10000, "%.0f g"))
            state.MinimumProfit = Math.Max(0, minProfit);
        ImGui.SameLine();
        var minRoi = state.MinimumRoiPercent;
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputFloat($"Minimum ROI %##buy-findings-roi-{lane}", ref minRoi, 1, 5, "%.1f"))
            state.MinimumRoiPercent = Math.Max(0, minRoi);

        var maxCost = state.MaximumCost;
        ImGui.SetNextItemWidth(210 * ImGuiHelpers.GlobalScale);
        if (ImGui.DragLong($"Maximum acquisition cost##buy-findings-cost-{lane}", ref maxCost, 1000, 0, 999_999_999))
            state.MaximumCost = Math.Max(0, maxCost);
        ImGui.SameLine();
        var maxDays = state.MaximumLiquidationDays;
        ImGui.SetNextItemWidth(190 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputFloat($"Maximum liquidation days##buy-findings-days-{lane}", ref maxDays, 0.5f, 5, "%.1f"))
            state.MaximumLiquidationDays = Math.Max(0.05f, maxDays);

        if (lane == BuyScanLane.MarketBoard)
        {
            var nq = state.FindingsNq;
            if (ImGui.Checkbox("Show NQ##buy-findings-nq", ref nq)) state.FindingsNq = nq;
            ImGui.SameLine();
            var hq = state.FindingsHq;
            if (ImGui.Checkbox("Show HQ##buy-findings-hq", ref hq)) state.FindingsHq = hq;
            ImGui.SameLine();
            var m2m = state.FindingsMarketToMarket;
            if (ImGui.Checkbox("Market exits##buy-findings-m2m", ref m2m)) state.FindingsMarketToMarket = m2m;
            ImGui.SameLine();
            var m2v = state.FindingsMarketToVendor;
            if (ImGui.Checkbox("Vendor exits##buy-findings-m2v", ref m2v)) state.FindingsMarketToVendor = m2v;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton($"Reset findings##buy-findings-reset-{lane}"))
            ResetFindings(state, lane);
    }

    private static void ResetFindings(BuyLaneUiState state, BuyScanLane lane)
    {
        state.FindingsSearch = string.Empty;
        state.MinimumStars = 1;
        state.MinimumProfit = 0;
        state.MinimumRoiPercent = 0;
        state.MaximumCost = 999_999_999;
        state.MaximumLiquidationDays = 3650;
        state.Tracking = BuyTrackingFilter.All;
        state.FindingsNq = true;
        state.FindingsHq = lane == BuyScanLane.MarketBoard;
        state.FindingsMarketToMarket = true;
        state.FindingsMarketToVendor = true;
    }

    private void DrawBuyLaneTable(BuyScanLane lane, BuyLaneUiState state)
    {
        var raw = (lane == BuyScanLane.Vendor
                ? plugin.BuyScanner.GetVendorOpportunities()
                : plugin.BuyScanner.GetMarketOpportunities())
            .Where(x => CurrentBuyWorldId == 0 || x.WorldId == CurrentBuyWorldId)
            .ToList();

        var rows = raw
            .Where(x => PassesBuyFindings(x, lane, state))
            .ToList();
        rows = SortBuyRows(rows);

        var completedAt = lane == BuyScanLane.Vendor
            ? plugin.BuyScanner.LastVendorCompletedUtc
            : plugin.BuyScanner.LastMarketCompletedUtc;
        ImGui.TextDisabled(completedAt is null
            ? "No Universalis update has completed for this lane yet."
            : $"Showing {rows.Count:N0} of {raw.Count:N0} finding(s). Last lane update {FormatBuyAge(DateTimeOffset.UtcNow - completedAt.Value)} ago.");

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable($"##buy-lane-table-{lane}", 11, flags, new Vector2(0, -1)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Rating", ImGuiTableColumnFlags.WidthFixed, 116 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Strategy", ImGuiTableColumnFlags.WidthFixed, 118 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Acquire", ImGuiTableColumnFlags.WidthFixed, 58 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 88 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Exit @", ImGuiTableColumnFlags.WidthFixed, 82 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Profit", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("ROI", ImGuiTableColumnFlags.WidthFixed, 65 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Liquidate", ImGuiTableColumnFlags.WidthFixed, 72 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Confidence", ImGuiTableColumnFlags.WidthFixed, 78 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Tracking", ImGuiTableColumnFlags.WidthFixed, 105 * ImGuiHelpers.GlobalScale);
        DrawBuyLaneHeaders();

        foreach (var row in rows)
            DrawBuyLaneRow(row);
        ImGui.EndTable();
    }

    private bool PassesBuyFindings(BuyOpportunity row, BuyScanLane lane, BuyLaneUiState state)
    {
        if (!string.IsNullOrWhiteSpace(state.FindingsSearch) &&
            !row.Item.Name.Contains(state.FindingsSearch, StringComparison.CurrentCultureIgnoreCase))
            return false;
        if (row.Stars < state.MinimumStars || row.PotentialProfit < state.MinimumProfit)
            return false;
        if (row.Roi * 100.0 < state.MinimumRoiPercent || row.AcquisitionCost > state.MaximumCost)
            return false;
        if (row.EstimatedLiquidationDays is { } days && days > state.MaximumLiquidationDays)
            return false;

        if (lane == BuyScanLane.MarketBoard)
        {
            if (row.IsHq && !state.FindingsHq) return false;
            if (!row.IsHq && !state.FindingsNq) return false;
            if (row.Kind == BuyOpportunityKind.MarketToVendor && !state.FindingsMarketToVendor) return false;
            if (row.Kind != BuyOpportunityKind.MarketToVendor && !state.FindingsMarketToMarket) return false;
        }

        var tracking = GetBuyTracking(row);
        return state.Tracking switch
        {
            BuyTrackingFilter.Bought => tracking.IsBought,
            BuyTrackingFilter.NotBought => !tracking.IsBought,
            _ => true,
        };
    }

    private void DrawBuyLaneHeaders()
    {
        ImGui.TableNextRow();
        SortableHeader(0, "Rating", BuySortColumn.Rating, "Opportunity score and broad star band.");
        SortableHeader(1, "Item", BuySortColumn.Item, "Item and quality variant.");
        SortableHeader(2, "Strategy", BuySortColumn.Strategy, "Acquisition/exit route modeled for this finding.");
        SortableHeader(3, "Acquire", BuySortColumn.BuyQuantity, "Recommended new units to acquire for the modeled package.");
        SortableHeader(4, "Cost", BuySortColumn.Cost, "Total modeled acquisition cost.");
        SortableHeader(5, "Exit @", BuySortColumn.ExitPrice, "Modeled gross Market Board exit price or guaranteed vendor payout.");
        SortableHeader(6, "Profit", BuySortColumn.PotentialProfit, "Modeled profit on the new acquisition only.");
        SortableHeader(7, "ROI", BuySortColumn.Roi, "Modeled profit divided by acquisition cost.");
        SortableHeader(8, "Liquidate", BuySortColumn.Liquidation, "Estimated time to liquidate the resulting position.");
        SortableHeader(9, "Confidence", BuySortColumn.Confidence, "Evidence confidence from market history and exit modeling.");
        SortableHeader(10, "Tracking", BuySortColumn.Tracking, "Whether Should I Tycoon? has recorded that you actually bought this item/listing. Exact Market Board listing-ID matches get a check mark.");
    }

    private void DrawBuyLaneRow(BuyOpportunity row)
    {
        var tracking = GetBuyTracking(row);
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (ImGui.Selectable($"{Stars(row.Stars)} {row.OpportunityScore:0}##buy-{row.Item.ItemId}-{row.IsHq}-{row.Kind}-{row.AcquisitionCost}", false, ImGuiSelectableFlags.SpanAllColumns))
        {
            selectedBuyOpportunity = row;
            buyDetailsOpen = true;
        }
        Tooltip($"Click for full analysis.\nConfidence: {row.Confidence:P0}\nRecent sales: {row.SalesSampleCount:N0}\nVelocity: {row.UnitsPerDay:0.##}/day");

        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(row.Item.Name + (row.IsHq ? " [HQ]" : string.Empty));
        ItemNameContextMenu($"##copy-buy-name-{row.Item.ItemId}-{row.IsHq}-{row.Kind}-{row.AcquisitionCost}", row.Item.Name);
        ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(row.StrategyLabel);
        ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(row.AcquireQuantity.ToString("N0"));
        ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(Gil(row.AcquisitionCost));
        ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(row.SuggestedExitUnitPrice is { } exit ? $"{exit:N0}g" : "—");
        ImGui.TableSetColumnIndex(6); ImGui.TextUnformatted(Gil(row.PotentialProfit));
        ImGui.TableSetColumnIndex(7); ImGui.TextUnformatted(Percent(row.Roi));
        ImGui.TableSetColumnIndex(8); ImGui.TextUnformatted(Days(row.EstimatedLiquidationDays));
        ImGui.TableSetColumnIndex(9); ImGui.TextUnformatted(row.Confidence.ToString("P0"));
        ImGui.TableSetColumnIndex(10); ImGui.TextUnformatted(tracking.Label);
        if (ImGui.IsItemHovered() && tracking.IsBought)
            ImGui.SetTooltip(tracking.ExactListingMatch
                ? $"Should I Tycoon? recorded {tracking.Quantity:N0} unit(s) from the exact listing ID in this recommendation."
                : $"Should I Tycoon? has {tracking.Quantity:N0} tracked unit(s) of this item from this acquisition lane.");
    }

    private BuyTrackingSummary GetBuyTracking(BuyOpportunity opportunity)
    {
        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0)
            return new BuyTrackingSummary(false, 0, null, false);

        var purchases = plugin.TraderStore.GetPurchases(Plugin.PlayerState.ContentId)
            .Where(x => x.ItemId == opportunity.Item.ItemId && x.IsHq == opportunity.IsHq)
            .ToList();
        if (opportunity.Kind == BuyOpportunityKind.VendorToMarket)
        {
            var vendor = purchases
                .Where(x => x.SourceKind == PurchaseSourceKind.VendorManual)
                .OrderByDescending(x => x.PurchasedAtUtc)
                .ToList();
            return vendor.Count == 0
                ? new BuyTrackingSummary(false, 0, null, false)
                : new BuyTrackingSummary(true, vendor.Sum(x => x.Quantity), vendor.Max(x => x.PurchasedAtUtc), false);
        }

        var market = purchases.Where(x => x.SourceKind == PurchaseSourceKind.MarketBoard).ToList();
        if (market.Count == 0)
            return new BuyTrackingSummary(false, 0, null, false);

        var listingIds = opportunity.AcquisitionLots.Where(x => x.ListingId != 0).Select(x => x.ListingId).ToHashSet();
        var exact = listingIds.Count == 0 ? new List<PersonalPurchase>() : market.Where(x => x.ListingId != 0 && listingIds.Contains(x.ListingId)).ToList();
        if (exact.Count > 0)
            return new BuyTrackingSummary(true, exact.Sum(x => x.Quantity), exact.Max(x => x.PurchasedAtUtc), true);

        // Keep item-level history visible even when Universalis did not expose a stable listing ID.
        var recent = market.Where(x => x.PurchasedAtUtc >= opportunity.AnalysedAtUtc.AddHours(-1)).ToList();
        return recent.Count == 0
            ? new BuyTrackingSummary(false, 0, null, false)
            : new BuyTrackingSummary(true, recent.Sum(x => x.Quantity), recent.Max(x => x.PurchasedAtUtc), false);
    }

    private List<BuyOpportunity> SortBuyRows(IEnumerable<BuyOpportunity> source)
    {
        IOrderedEnumerable<BuyOpportunity> ordered = buySortColumn switch
        {
            BuySortColumn.Item => buySortAscending ? source.OrderBy(x => x.Item.Name, StringComparer.CurrentCultureIgnoreCase) : source.OrderByDescending(x => x.Item.Name, StringComparer.CurrentCultureIgnoreCase),
            BuySortColumn.Strategy => buySortAscending ? source.OrderBy(x => x.StrategyLabel, StringComparer.CurrentCultureIgnoreCase) : source.OrderByDescending(x => x.StrategyLabel, StringComparer.CurrentCultureIgnoreCase),
            BuySortColumn.BuyQuantity => buySortAscending ? source.OrderBy(x => x.AcquireQuantity) : source.OrderByDescending(x => x.AcquireQuantity),
            BuySortColumn.Cost => buySortAscending ? source.OrderBy(x => x.AcquisitionCost) : source.OrderByDescending(x => x.AcquisitionCost),
            BuySortColumn.ExitPrice => buySortAscending ? source.OrderBy(x => x.SuggestedExitUnitPrice ?? uint.MaxValue) : source.OrderByDescending(x => x.SuggestedExitUnitPrice ?? 0),
            BuySortColumn.PotentialProfit => buySortAscending ? source.OrderBy(x => x.PotentialProfit) : source.OrderByDescending(x => x.PotentialProfit),
            BuySortColumn.Roi => buySortAscending ? source.OrderBy(x => x.Roi) : source.OrderByDescending(x => x.Roi),
            BuySortColumn.Liquidation => buySortAscending ? source.OrderBy(x => x.EstimatedLiquidationDays ?? double.MaxValue) : source.OrderByDescending(x => x.EstimatedLiquidationDays ?? double.MinValue),
            BuySortColumn.Confidence => buySortAscending ? source.OrderBy(x => x.Confidence) : source.OrderByDescending(x => x.Confidence),
            BuySortColumn.Tracking => buySortAscending ? source.OrderBy(x => GetBuyTracking(x).IsBought) : source.OrderByDescending(x => GetBuyTracking(x).IsBought),
            _ => buySortAscending ? source.OrderBy(x => x.OpportunityScore) : source.OrderByDescending(x => x.OpportunityScore),
        };
        return ordered.ThenByDescending(x => x.OpportunityScore).ThenByDescending(x => x.PotentialProfit).ToList();
    }

    private void SortableHeader(int column, string label, BuySortColumn sortColumn, string explanation)
    {
        ImGui.TableSetColumnIndex(column);
        var suffix = buySortColumn == sortColumn ? (buySortAscending ? " ▲" : " ▼") : string.Empty;
        if (ImGui.Selectable($"{label}{suffix}##buy-header-{sortColumn}"))
        {
            if (buySortColumn == sortColumn) buySortAscending = !buySortAscending;
            else
            {
                buySortColumn = sortColumn;
                buySortAscending = sortColumn is BuySortColumn.Item or BuySortColumn.Strategy;
            }
        }
        Tooltip(explanation + "\nClick to sort; click again to reverse direction.");
    }

    private void DrawBuyDetailPage(BuyOpportunity opportunity)
    {
        if (ImGui.Button("← BACK TO FINDINGS"))
        {
            buyDetailsOpen = false;
            return;
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"Analysed {FormatBuyAge(DateTimeOffset.UtcNow - opportunity.AnalysedAtUtc)} ago");
        ImGui.Separator();

        var tracking = GetBuyTracking(opportunity);
        ImGui.TextUnformatted($"{opportunity.Item.Name}{(opportunity.IsHq ? " [HQ]" : string.Empty)}");
        ItemNameContextMenu($"##copy-detail-name-{opportunity.Item.ItemId}-{opportunity.IsHq}", opportunity.Item.Name);
        ImGui.TextDisabled($"{plugin.Catalog.GetWorldName(opportunity.WorldId)} • {opportunity.StrategyLabel}");
        ImGui.TextUnformatted($"{Stars(opportunity.Stars)}  {opportunity.OpportunityScore:0.0}/100  ·  Confidence {opportunity.Confidence:P0}");
        ImGui.TextWrapped($"Acquire {opportunity.AcquireQuantity:N0} new unit(s) for about {opportunity.AcquisitionCost:N0}g. Modeled exit: {opportunity.SuggestedExitStackSize:N0}-unit listing(s) around {(opportunity.SuggestedExitUnitPrice is { } p ? p.ToString("N0") + "g/unit" : "the calculated exit value")}.");
        ImGui.TextDisabled($"Tracking: {tracking.Label}{(tracking.LastPurchasedAtUtc is { } bought ? $" • last recorded {bought.ToLocalTime():yyyy-MM-dd HH:mm}" : string.Empty)}");

        if (opportunity.Kind == BuyOpportunityKind.VendorToMarket)
        {
            ImGui.Spacing();
            if (ImGui.Button("RECORD THIS VENDOR BUY IN TYCOON"))
                PrepareVendorPurchaseFromOpportunity(opportunity);
            Tooltip("Prefill Tycoon's vendor purchase form with this exact opportunity. You still confirm the real quantity/cost yourself; Should I? never invents a vendor purchase.");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Trade overview");
        if (ImGui.BeginTable("##buy-detail-overview", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextRow();
            MetricCell(0, "Potential profit", Gil(opportunity.PotentialProfit), "Modeled profit on the new acquisition if the exit succeeds.");
            MetricCell(1, "ROI", Percent(opportunity.Roi), "Potential profit divided by acquisition cost.");
            MetricCell(2, "Investment", Gil(opportunity.AcquisitionCost), "Total modeled acquisition cost.");
            MetricCell(3, "Average buy", $"{opportunity.AverageAcquisitionUnitCost:N0}g/unit", "Average modeled acquisition cost per unit.");
            ImGui.TableNextRow();
            MetricCell(0, "First sale", Days(opportunity.EstimatedFirstSaleDays), "Estimated time before the first modeled sale.");
            MetricCell(1, "Full liquidation", Days(opportunity.EstimatedLiquidationDays), "Estimated time to sell the full resulting position.");
            MetricCell(2, "Units/day", $"{opportunity.UnitsPerDay:0.##}", "Recent market velocity used by the model.");
            MetricCell(3, "Sale samples", opportunity.SalesSampleCount.ToString("N0"), "Recent sale records available to the detailed Universalis analysis.");
            ImGui.TableNextRow();
            MetricCell(0, "Already owned", opportunity.ExistingQuantity.ToString("N0"), "Known stock at analysis time. It affects liquidation planning but not acquisition profit.");
            MetricCell(1, "Resulting position", (opportunity.ExistingQuantity + opportunity.AcquireQuantity).ToString("N0"), "Known stock plus the modeled acquisition.");
            MetricCell(2, "Recommended stack", opportunity.SuggestedExitStackSize.ToString("N0"), "Recommended units per exit listing.");
            MetricCell(3, "Market freshness", opportunity.MarketFreshnessUtc is { } fresh ? FormatBuyAge(DateTimeOffset.UtcNow - fresh) + " ago" : "unknown", "Age of the listing snapshot used for this finding.");
            ImGui.EndTable();
        }

        DrawBuyLiveVerification(opportunity);

        ImGui.Spacing();
        ImGui.TextUnformatted("Acquisition package");
        if (opportunity.AcquisitionLots.Count > 0)
        {
            if (ImGui.BeginTable("##buy-acquisition-lots", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("Listing ID");
                ImGui.TableSetupColumn("Quantity");
                ImGui.TableSetupColumn("Unit price");
                ImGui.TableSetupColumn("Buyer tax");
                ImGui.TableSetupColumn("Total cost");
                ImGui.TableHeadersRow();
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
            ImGui.TextWrapped($"Source the recommended {opportunity.AcquireQuantity:N0} unit(s) from a normal gil NPC vendor at about {opportunity.AverageAcquisitionUnitCost:N0}g/unit.");
        }
        else
        {
            ImGui.TextDisabled("This finding does not require a Market Board acquisition package.");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Model reasoning & cautions");
        foreach (var note in opportunity.Notes)
            ImGui.BulletText(note);

        ImGui.Spacing();
        ImGui.TextDisabled("Should I Buy? never purchases automatically. Execute the trade yourself through normal game UI; confirmed Market Board purchases are captured by Tycoon, while vendor acquisitions are recorded only when you explicitly confirm them.");
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
        if (age.TotalSeconds < 60) return $"{Math.Max(0, age.TotalSeconds):0}s";
        if (age.TotalMinutes < 60) return $"{age.TotalMinutes:0.#}m";
        if (age.TotalHours < 24) return $"{age.TotalHours:0.#}h";
        return $"{age.TotalDays:0.#}d";
    }
}
