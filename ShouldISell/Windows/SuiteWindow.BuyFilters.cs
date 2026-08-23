using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace ShouldISell.Windows;

public enum BuyQualityFilter
{
    All,
    NqOnly,
    HqOnly,
}

public enum BuyLiveFilter
{
    All,
    NotChecked,
    Verified,
    Changed,
    Refreshed,
}

public enum BuyLiveState
{
    NotChecked,
    Verified,
    Changed,
    Refreshed,
}

public sealed partial class SuiteWindow
{
    private readonly HashSet<BuyOpportunityKind> buyStrategyFilter = Enum.GetValues<BuyOpportunityKind>().ToHashSet();
    private BuyQualityFilter buyQualityFilter = BuyQualityFilter.All;
    private BuyLiveFilter buyLiveFilter = BuyLiveFilter.All;
    private int buyMinimumStars = 1;

    private void DrawBuyScreenerAndDeepScan()
    {
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Opportunity filters & native deep scan", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawBuyFilterBar();
            ImGui.Spacing();
            DrawBuyNativeDeepScanControls();
        }
    }

    private void DrawBuyFilterBar()
    {
        var changed = false;

        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputTextWithHint("##buy-result-search", "Filter item name...", ref buySearch, 128))
            changed = true;
        Tooltip("Filters the current-world Should I Buy? opportunities by item name. The same filter also applies to the budget portfolio and native Deep Scan candidate set.");

        ImGui.SameLine();
        changed |= DrawStrategyFilterCombo();

        ImGui.SameLine();
        changed |= DrawQualityFilterCombo();

        ImGui.SameLine();
        changed |= DrawRatingFilterCombo();

        ImGui.SameLine();
        changed |= DrawLiveFilterCombo();

        ImGui.SameLine();
        if (ImGui.Button("Reset filters##buy-reset-filters"))
        {
            buySearch = string.Empty;
            buyStrategyFilter.Clear();
            foreach (var kind in Enum.GetValues<BuyOpportunityKind>())
                buyStrategyFilter.Add(kind);
            buyQualityFilter = BuyQualityFilter.All;
            buyLiveFilter = BuyLiveFilter.All;
            buyMinimumStars = 1;
            changed = true;
        }
        Tooltip("Restore all Should I Buy? result filters. Scanner settings such as minimum ROI and budget are not changed.");

        if (changed)
            buyPortfolioPlan = null;

        var visible = GetFilteredBuyOpportunities();
        ImGui.TextDisabled($"{visible.Count:N0} current-world opportunity package(s) match the screener. Filters feed the results table, budget portfolio and native Deep Scan.");
    }

    private bool DrawStrategyFilterCombo()
    {
        var allCount = Enum.GetValues<BuyOpportunityKind>().Length;
        var summary = buyStrategyFilter.Count switch
        {
            0 => "Strategy: none",
            var n when n == allCount => "Strategy: all",
            1 => $"Strategy: {StrategyFilterLabel(buyStrategyFilter.First())}",
            _ => $"Strategy: {buyStrategyFilter.Count}/{allCount}",
        };

        var changed = false;
        ImGui.SetNextItemWidth(175 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##buy-strategy-filter", summary))
        {
            if (ImGui.SmallButton("All##buy-strategy-all"))
            {
                buyStrategyFilter.Clear();
                foreach (var kind in Enum.GetValues<BuyOpportunityKind>())
                    buyStrategyFilter.Add(kind);
                changed = true;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("None##buy-strategy-none"))
            {
                buyStrategyFilter.Clear();
                changed = true;
            }

            foreach (var kind in Enum.GetValues<BuyOpportunityKind>())
            {
                var enabled = buyStrategyFilter.Contains(kind);
                if (!ImGui.Checkbox($"{StrategyFilterLabel(kind)}##buy-strategy-{kind}", ref enabled))
                    continue;
                if (enabled)
                    buyStrategyFilter.Add(kind);
                else
                    buyStrategyFilter.Remove(kind);
                changed = true;
            }
            ImGui.EndCombo();
        }
        Tooltip("Filter by one or several recommendation strategies. Unlike sorting the Strategy column, this actually removes non-matching opportunities from the working set.");
        return changed;
    }

    private bool DrawQualityFilterCombo()
    {
        var changed = false;
        var label = buyQualityFilter switch
        {
            BuyQualityFilter.NqOnly => "Quality: NQ",
            BuyQualityFilter.HqOnly => "Quality: HQ",
            _ => "Quality: all",
        };
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##buy-quality-filter", label))
        {
            changed |= SelectQuality("All", BuyQualityFilter.All);
            changed |= SelectQuality("NQ only", BuyQualityFilter.NqOnly);
            changed |= SelectQuality("HQ only", BuyQualityFilter.HqOnly);
            ImGui.EndCombo();
        }
        Tooltip("Filter recommendations by item quality variant.");
        return changed;
    }

    private bool SelectQuality(string label, BuyQualityFilter value)
    {
        var selected = buyQualityFilter == value;
        if (!ImGui.Selectable($"{label}##buy-quality-{value}", selected))
            return false;
        buyQualityFilter = value;
        return true;
    }

    private bool DrawRatingFilterCombo()
    {
        var changed = false;
        ImGui.SetNextItemWidth(125 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##buy-rating-filter", buyMinimumStars <= 1 ? "Rating: all" : $"Rating: {buyMinimumStars}★+"))
        {
            for (var stars = 1; stars <= 5; stars++)
            {
                var text = stars == 1 ? "All ratings" : $"{stars}★ and above";
                if (!ImGui.Selectable($"{text}##buy-rating-{stars}", buyMinimumStars == stars))
                    continue;
                buyMinimumStars = stars;
                changed = true;
            }
            ImGui.EndCombo();
        }
        Tooltip("Filter the visible opportunity set by the broad 1–5 star quality band. The underlying 0–100 score remains sortable.");
        return changed;
    }

    private bool DrawLiveFilterCombo()
    {
        var changed = false;
        var label = buyLiveFilter switch
        {
            BuyLiveFilter.NotChecked => "Live: not checked",
            BuyLiveFilter.Verified => "Live: verified",
            BuyLiveFilter.Changed => "Live: changed",
            BuyLiveFilter.Refreshed => "Live: refreshed",
            _ => "Live: all",
        };
        ImGui.SetNextItemWidth(145 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##buy-live-filter", label))
        {
            foreach (var value in Enum.GetValues<BuyLiveFilter>())
            {
                var text = value switch
                {
                    BuyLiveFilter.NotChecked => "Not checked",
                    BuyLiveFilter.Verified => "Verified",
                    BuyLiveFilter.Changed => "Changed",
                    BuyLiveFilter.Refreshed => "Refreshed",
                    _ => "All live states",
                };
                if (!ImGui.Selectable($"{text}##buy-live-{value}", buyLiveFilter == value))
                    continue;
                buyLiveFilter = value;
                changed = true;
            }
            ImGui.EndCombo();
        }
        Tooltip("Filter by native FFXIV verification state. Verified means every recommended acquisition listing still matched exactly; Changed means at least one no longer did; Refreshed is used when only the exit side needed a live board refresh.");
        return changed;
    }

    private void DrawBuyNativeDeepScanControls()
    {
        var c = plugin.Configuration;
        var limit = c.BuyNativeDeepScanLimit;
        var candidates = GetBuyDeepScanCandidates(limit);
        var refresh = plugin.RefreshEngine;
        var retainerMarketReady = plugin.SellScanContext.IsRetainerMarketUiVisible();
        var busy = refresh.IsRunning || plugin.BuyScanner.IsScanning;
        var canStart = retainerMarketReady && !busy && candidates.Count > 0;

        if (!canStart)
            ImGui.BeginDisabled();
        if (ImGui.Button($"DEEP SCAN TOP {Math.Min(limit, candidates.Count):N0}##buy-native-deep"))
        {
            refresh.StartForItems(
                candidates.Select(x => x.Item.ItemId),
                $"Should I Buy deep scan of top {candidates.Count:N0} Universalis hit(s)");
        }
        if (!canStart)
            ImGui.EndDisabled();
        Tooltip("Native FFXIV deep scan: requests the top currently filtered Universalis-ranked unique items one-by-one through ItemSearch. It does not repeat the broad Universalis discovery pass and never buys anything.");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(190 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("##buy-native-deep-limit", ref limit, 1, 100, "%d items"))
        {
            c.BuyNativeDeepScanLimit = Math.Clamp(limit, 1, 100);
            c.Save();
        }
        Tooltip("How many top unique filtered Universalis hits to verify through FFXIV itself. Range: 1–100.");

        ImGui.SameLine();
        if (retainerMarketReady)
            ImGui.TextDisabled($"Retainer market ready on {CurrentBuyWorldName}.");
        else
            ImGui.TextDisabled("Open a retainer's market/sell interface to enable Deep Scan.");

        if (refresh.IsRunning)
        {
            var progress = refresh.InitialCount > 0
                ? Math.Clamp((refresh.CompletedCount + refresh.FailedCount) / (float)refresh.InitialCount, 0f, 1f)
                : 0f;
            ImGui.ProgressBar(progress, new Vector2(-1, 0), $"Native scan {refresh.CompletedCount + refresh.FailedCount:N0}/{refresh.InitialCount:N0} — {refresh.Status}");
        }
        else
        {
            ImGui.TextDisabled($"Deep Scan candidates: {candidates.Count:N0} unique item(s), ranked by Universalis opportunity score after the active filters. Duplicate strategies/HQ rows for the same item consume only one native request.");
        }
    }

    private IReadOnlyList<BuyOpportunity> GetFilteredBuyOpportunities()
    {
        IEnumerable<BuyOpportunity> rows = GetCurrentWorldBuyOpportunities();

        if (!string.IsNullOrWhiteSpace(buySearch))
            rows = rows.Where(x => x.Item.Name.Contains(buySearch, StringComparison.CurrentCultureIgnoreCase));

        rows = rows.Where(x => buyStrategyFilter.Contains(x.Kind));
        rows = buyQualityFilter switch
        {
            BuyQualityFilter.NqOnly => rows.Where(x => !x.IsHq),
            BuyQualityFilter.HqOnly => rows.Where(x => x.IsHq),
            _ => rows,
        };
        rows = rows.Where(x => x.Stars >= buyMinimumStars);

        if (buyLiveFilter != BuyLiveFilter.All)
        {
            var wanted = buyLiveFilter switch
            {
                BuyLiveFilter.Verified => BuyLiveState.Verified,
                BuyLiveFilter.Changed => BuyLiveState.Changed,
                BuyLiveFilter.Refreshed => BuyLiveState.Refreshed,
                _ => BuyLiveState.NotChecked,
            };
            rows = rows.Where(x => GetBuyLiveState(x) == wanted);
        }

        return rows.ToList();
    }

    private IReadOnlyList<BuyOpportunity> GetBuyDeepScanCandidates(int limit)
        => GetFilteredBuyOpportunities()
            .OrderByDescending(x => x.OpportunityScore)
            .ThenByDescending(x => x.RiskAdjustedProfit)
            .ThenByDescending(x => x.PotentialProfit)
            .GroupBy(x => x.Item.ItemId)
            .Select(g => g.First())
            .Take(Math.Clamp(limit, 1, 100))
            .ToList();

    private BuyLiveState GetBuyLiveState(BuyOpportunity opportunity)
    {
        var live = plugin.Store.GetMarket(opportunity.WorldId, opportunity.Item.ItemId);
        var liveAt = live?.CurrentSource == MarketDataSource.LiveGame ? live.ListingObservedAtUtc : null;
        if (liveAt is null || liveAt.Value < opportunity.AnalysedAtUtc)
            return BuyLiveState.NotChecked;

        if (opportunity.AcquisitionLots.Count == 0)
            return BuyLiveState.Refreshed;

        var liveVariantListings = live!.Listings
            .Where(x => x.IsHq == opportunity.IsHq && x.PricePerUnit > 0 && x.Quantity > 0)
            .ToList();
        var matched = opportunity.AcquisitionLots.Count(lot =>
            lot.ListingId != 0 && liveVariantListings.Any(x =>
                x.ListingId == lot.ListingId &&
                x.PricePerUnit == lot.UnitPrice &&
                x.Quantity == lot.Quantity));

        return matched == opportunity.AcquisitionLots.Count
            ? BuyLiveState.Verified
            : BuyLiveState.Changed;
    }

    private static string LiveStateLabel(BuyLiveState state)
        => state switch
        {
            BuyLiveState.Verified => "Verified",
            BuyLiveState.Changed => "Changed",
            BuyLiveState.Refreshed => "Refreshed",
            _ => "Not checked",
        };

    private static string StrategyFilterLabel(BuyOpportunityKind kind)
        => kind switch
        {
            BuyOpportunityKind.UndercutSweep => "Undercut sweep",
            BuyOpportunityKind.SplitStack => "Split stack",
            BuyOpportunityKind.ConsolidateStack => "Consolidate stack",
            BuyOpportunityKind.VendorToMarket => "Vendor → Market",
            BuyOpportunityKind.MarketToVendor => "Market → Vendor",
            _ => "Market flip",
        };
}
