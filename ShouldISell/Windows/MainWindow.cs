using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using ShouldISell.Services;

namespace ShouldISell.Windows;

public sealed partial class MainWindow : Window, IDisposable
{
    private enum OwnedLocationFilter
    {
        AllLocations,
        PlayerInventory,
        AllRetainers,
        SpecificRetainer,
    }

    private readonly Plugin plugin;
    private string search = string.Empty;
    private string listingSearch = string.Empty;
    private DetailSelection? selected;

    // Owned-item filters. Net-value bounds apply to one recommended listing at the suggested stack size.
    private int ownedRatingMin;
    private int ownedRatingMax = 100;
    private int ownedStarsMin = 1;
    private int ownedStarsMax = 5;
    private long ownedNetMin;
    private long ownedNetMax = 999_999_999_999;
    private OwnedLocationFilter ownedLocationFilter = OwnedLocationFilter.AllLocations;
    private ulong ownedRetainerFilterId;

    // Current-listing filters. Payout bounds apply to the after-tax payout of the currently listed stack.
    private int listingRatingMin;
    private int listingRatingMax = 100;
    private int listingStarsMin = 1;
    private int listingStarsMax = 5;
    private long listingPayoutMin;
    private long listingPayoutMax = 999_999_999_999;
    private ulong listingRetainerFilterId;

    private sealed record DetailSelection(
        ItemInfo Item,
        bool IsHq,
        int Quantity,
        SellRating? Rating,
        IReadOnlyList<string> Locations,
        OwnMarketListing? Listing,
        int TotalOwnedQuantity);

    public MainWindow(Plugin plugin)
        : base("Should I Sell?##ShouldISell")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(900, 540),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        DrawInventoryCoverageWarning();
        DrawHeader();
        ImGui.Separator();

        if (ImGui.BeginTabBar("##sell-tabs"))
        {
            if (ImGui.BeginTabItem("Owned Items"))
            {
                DrawItems();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Current Listings"))
            {
                DrawCurrentListings();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Sales History"))
            {
                DrawSalesHistory();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Market Refresh"))
            {
                DrawUniversalisRefresh();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Settings"))
            {
                DrawSettings();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("About the Score"))
            {
                DrawAboutScore();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawHeader()
    {
        ImGui.TextWrapped("Every known marketable item is rated independently. Stars answer ‘is this an excellent selling situation?’, while the 0–100 opportunity score is deliberately stricter so 100 is reserved for an unusually complete fit across value, price, demand, liquidity and market health. Est. net is the expected payout of ONE recommended listing, not the whole stockpile.");
        ImGui.Spacing();

        if (!plugin.Coordinator.IsFetching)
        {
            if (ImGui.Button("Update from Universalis"))
                _ = plugin.Coordinator.RefreshOwnedFromUniversalisAsync();
        }
        else
        {
            ImGui.TextDisabled("Universalis update running...");
        }
        ImGui.TextDisabled(plugin.Coordinator.FetchStatus);
    }

    private void DrawItems()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##item-search", "Search item name...", ref search, 128);

        var ratedItems = plugin.Coordinator.GetRatedOwnedItems().ToList();
        DrawOwnedFilters(ratedItems);

        var listedVariants = plugin.Coordinator.GetKnownOwnListedVariants();
        var allItems = ratedItems
            .Where(x => string.IsNullOrWhiteSpace(search) || x.Item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        var items = allItems
            .Where(PassesOwnedFilters)
            .Where(PassesOwnedLocationFilter)
            .ToList();

        ImGui.TextDisabled($"Showing {items.Count:N0} of {allItems.Count:N0} matching item/HQ variants. Hover a rating for the score breakdown; click anywhere on a row for full details.");
        if (listedVariants.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(ListedRowTextColor, "Gold = already listed");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Gold-tinted rows have at least one cached current retainer Market Board listing for this item/HQ variant.");
        }

        var ownedDetailOpen = selected is { Listing: null };
        var tableHeight = ownedDetailOpen ? 300 * ImGuiHelpers.GlobalScale : -1;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable;
        if (ImGui.BeginTable("owned-items-table", 11, flags, new Vector2(0, tableHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Rating", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending, 130 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Suggested", ImGuiTableColumnFlags.WidthFixed, 88 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Stack", ImGuiTableColumnFlags.WidthFixed, 62 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Est. net", ImGuiTableColumnFlags.WidthFixed, 98 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Current ask", ImGuiTableColumnFlags.WidthFixed, 92 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Median", ImGuiTableColumnFlags.WidthFixed, 82 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Units/day", ImGuiTableColumnFlags.WidthFixed, 72 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Confidence", ImGuiTableColumnFlags.WidthFixed, 85 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Freshness", ImGuiTableColumnFlags.WidthFixed, 92 * ImGuiHelpers.GlobalScale);
            DrawHeaderRow(OwnedHeaderHelp);

            items = SortOwnedItems(items, ImGui.TableGetSortSpecs());
            foreach (var row in items)
            {
                var selection = FromOwned(row);
                var isSelected = IsSameSelection(selection);
                var isListed = listedVariants.Contains((row.Item.ItemId, row.IsHq));
                var clicked = BeginClickableRow($"owned-{row.Item.ItemId}-{row.IsHq}", isSelected, isListed);
                if (clicked)
                    selected = isSelected ? null : selection;

                ImGui.TableSetColumnIndex(0);
                DrawRatingCell(row.Rating);

                ImGui.TableSetColumnIndex(1);
                if (isListed)
                    ImGui.TextColored(ListedRowTextColor, row.Item.Name + (row.IsHq ? " [HQ]" : string.Empty));
                else
                    ImGui.TextUnformatted(row.Item.Name + (row.IsHq ? " [HQ]" : string.Empty));
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(VisibleOwnedQuantity(row).ToString("N0"));
                ImGui.TableSetColumnIndex(3);
                DrawSuggestedPriceCell(row.Rating);
                ImGui.TableSetColumnIndex(4);
                DrawStackCell(row.Rating);
                ImGui.TableSetColumnIndex(5);
                ImGui.TextUnformatted(Gil(EstimatedRecommendedListingValue(row)));
                ImGui.TableSetColumnIndex(6);
                ImGui.TextUnformatted(Gil(row.Rating?.RealisticCurrentPrice));
                ImGui.TableSetColumnIndex(7);
                ImGui.TextUnformatted(Gil(row.Rating?.HistoricalMedian));
                ImGui.TableSetColumnIndex(8);
                ImGui.TextUnformatted(row.Rating is null ? "—" : row.Rating.UnitsPerDay.ToString("0.##"));
                ImGui.TableSetColumnIndex(9);
                ImGui.TextUnformatted(row.Rating?.ConfidenceLabel ?? "No data");
                ImGui.TableSetColumnIndex(10);
                ImGui.TextUnformatted(Age(row.Rating?.ListingFreshnessUtc));
            }

            ImGui.EndTable();
        }

        if (selected is { Listing: null } ownedSelection)
            DrawSelectedDetails(ownedSelection);
    }

    private void DrawOwnedFilters(IReadOnlyList<RatedOwnedItem> ratedItems)
    {
        if (!ImGui.CollapsingHeader("Filters##owned", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawOwnedLocationFilter(ratedItems);
        ImGui.Spacing();

        var width = 165 * ImGuiHelpers.GlobalScale;
        ImGui.SetNextItemWidth(width);
        if (ImGui.SliderInt("Rating min##owned", ref ownedRatingMin, 0, 100))
            ownedRatingMax = Math.Max(ownedRatingMax, ownedRatingMin);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(width);
        if (ImGui.SliderInt("Rating max##owned", ref ownedRatingMax, 0, 100))
            ownedRatingMin = Math.Min(ownedRatingMin, ownedRatingMax);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(width);
        if (ImGui.SliderInt("Stars min##owned", ref ownedStarsMin, 1, 5))
            ownedStarsMax = Math.Max(ownedStarsMax, ownedStarsMin);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(width);
        if (ImGui.SliderInt("Stars max##owned", ref ownedStarsMax, 1, 5))
            ownedStarsMin = Math.Min(ownedStarsMin, ownedStarsMax);

        ImGui.SetNextItemWidth(210 * ImGuiHelpers.GlobalScale);
        if (ImGui.DragLong("Est. net min##owned", ref ownedNetMin, 100f, 0, 999_999_999_999))
            ownedNetMax = Math.Max(ownedNetMax, ownedNetMin);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(210 * ImGuiHelpers.GlobalScale);
        if (ImGui.DragLong("Est. net max##owned", ref ownedNetMax, 100f, 0, 999_999_999_999))
            ownedNetMin = Math.Min(ownedNetMin, ownedNetMax);
        ImGui.SameLine();
        if (ImGui.SmallButton("Reset##owned-filters"))
        {
            ownedRatingMin = 0;
            ownedRatingMax = 100;
            ownedStarsMin = 1;
            ownedStarsMax = 5;
            ownedNetMin = 0;
            ownedNetMax = 999_999_999_999;
            ownedLocationFilter = OwnedLocationFilter.AllLocations;
            ownedRetainerFilterId = 0;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reset location, rating, star and expected-net filters.");
        ImGui.TextDisabled("Est. net = expected after-tax payout of ONE recommended listing: suggested stack × net suggested unit price.");
        ImGui.Spacing();
    }

    private void DrawOwnedLocationFilter(IReadOnlyList<RatedOwnedItem> ratedItems)
    {
        var retainers = ratedItems
            .SelectMany(x => x.Ownership)
            .Where(x => x.OwnerKind == InventoryOwnerKind.Retainer && x.OwnerId != 0)
            .GroupBy(x => x.OwnerId)
            .Select(g => new
            {
                Id = g.Key,
                Name = g.Select(x => x.OwnerName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "Unnamed retainer",
            })
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (ownedLocationFilter == OwnedLocationFilter.SpecificRetainer &&
            retainers.All(x => x.Id != ownedRetainerFilterId))
        {
            ownedLocationFilter = OwnedLocationFilter.AllRetainers;
            ownedRetainerFilterId = 0;
        }

        var preview = ownedLocationFilter switch
        {
            OwnedLocationFilter.PlayerInventory => "Location: Player inventory",
            OwnedLocationFilter.AllRetainers => "Location: All retainers",
            OwnedLocationFilter.SpecificRetainer =>
                $"Location: {retainers.FirstOrDefault(x => x.Id == ownedRetainerFilterId)?.Name ?? "Retainer"}",
            _ => "Location: All",
        };

        ImGui.SetNextItemWidth(240 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##owned-location-filter", preview))
        {
            if (ImGui.Selectable("All locations", ownedLocationFilter == OwnedLocationFilter.AllLocations))
            {
                ownedLocationFilter = OwnedLocationFilter.AllLocations;
                ownedRetainerFilterId = 0;
            }
            if (ImGui.Selectable("Player inventory", ownedLocationFilter == OwnedLocationFilter.PlayerInventory))
            {
                ownedLocationFilter = OwnedLocationFilter.PlayerInventory;
                ownedRetainerFilterId = 0;
            }
            if (ImGui.Selectable("All retainers", ownedLocationFilter == OwnedLocationFilter.AllRetainers))
            {
                ownedLocationFilter = OwnedLocationFilter.AllRetainers;
                ownedRetainerFilterId = 0;
            }

            if (retainers.Count > 0)
            {
                ImGui.Separator();
                foreach (var retainer in retainers)
                {
                    if (!ImGui.Selectable($"Retainer: {retainer.Name}##owned-retainer-{retainer.Id}",
                            ownedLocationFilter == OwnedLocationFilter.SpecificRetainer && ownedRetainerFilterId == retainer.Id))
                        continue;
                    ownedLocationFilter = OwnedLocationFilter.SpecificRetainer;
                    ownedRetainerFilterId = retainer.Id;
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show items present in all known locations, your player-owned inventory/saddlebags, all retainers, or one specific cached retainer. When scoped, Qty shows the amount in that selected location; sell guidance still models your full known position.");
    }

    private bool PassesOwnedLocationFilter(RatedOwnedItem row)
        => ownedLocationFilter switch
        {
            OwnedLocationFilter.PlayerInventory => row.Ownership.Any(x => x.OwnerKind == InventoryOwnerKind.Player),
            OwnedLocationFilter.AllRetainers => row.Ownership.Any(x => x.OwnerKind == InventoryOwnerKind.Retainer),
            OwnedLocationFilter.SpecificRetainer => row.Ownership.Any(x =>
                x.OwnerKind == InventoryOwnerKind.Retainer && x.OwnerId == ownedRetainerFilterId),
            _ => true,
        };

    private int VisibleOwnedQuantity(RatedOwnedItem row)
        => ownedLocationFilter switch
        {
            OwnedLocationFilter.PlayerInventory => row.Ownership
                .Where(x => x.OwnerKind == InventoryOwnerKind.Player)
                .Sum(x => x.Quantity),
            OwnedLocationFilter.AllRetainers => row.Ownership
                .Where(x => x.OwnerKind == InventoryOwnerKind.Retainer)
                .Sum(x => x.Quantity),
            OwnedLocationFilter.SpecificRetainer => row.Ownership
                .Where(x => x.OwnerKind == InventoryOwnerKind.Retainer && x.OwnerId == ownedRetainerFilterId)
                .Sum(x => x.Quantity),
            _ => row.Quantity,
        };

    private bool PassesOwnedFilters(RatedOwnedItem row)
    {
        var rating = row.Rating;
        if (rating is null)
            return ownedRatingMin == 0 && ownedStarsMin <= 1 && ownedNetMin == 0;

        var score = Score100(rating);
        var net = EstimatedRecommendedListingValue(row) ?? 0;
        return score >= ownedRatingMin && score <= ownedRatingMax &&
               rating.Stars >= ownedStarsMin && rating.Stars <= ownedStarsMax &&
               net >= ownedNetMin && net <= ownedNetMax;
    }

    private static readonly (string Label, string Help)[] OwnedHeaderHelp =
    {
        ("Rating", "Stars are the broad sell-opportunity band. The colored 0–100 number is stricter: it uses the non-contrast opportunity score, so 100 is reserved for a near-perfect fit rather than every 5★ item."),
        ("Item", "The item/HQ variant represented by this row."),
        ("Qty", "Quantity in the active location filter. With All locations selected, this is the total known quantity across player, saddlebag, retainer and market-listing snapshots. Sell guidance still models the full known position."),
        ("Suggested", "Recommended gross market-board price per unit. It combines sold-price history, current listing depth, demand/supply, your quantity, and supported stack-size premiums or discounts. Right-click a value to copy the raw gil number."),
        ("Stack", "Recommended quantity per listing. It learns historical buyer stack-size preferences and convenience premiums, then balances those against liquidity and the manual cost of splitting into too many listings."),
        ("Est. net", "Expected after-tax payout of one recommended listing: suggested stack size × net suggested unit price. This intentionally does not pretend your whole stockpile sells in one transaction."),
        ("Current ask", "A realistic current board ask after ignoring clearly isolated/shallow anomalous undercuts when the surrounding depth and sales history support doing so."),
        ("Median", "Recency-weighted median of actual sold prices, primarily from the recent 30-day window with older fallback when needed."),
        ("Units/day", "Estimated sold units per day from recent sale history. This is distinct from transactions/day because stack sizes matter."),
        ("Confidence", "Confidence in the underlying market evidence based on sale sample count, listing freshness and last-sale recency. Confidence does not directly change the star rating."),
        ("Freshness", "Age of the most recent current-listing observation. This is different from the age of the last actual sale."),
    };

    private static void DrawRatingCell(SellRating? rating)
    {
        if (rating is null)
        {
            ImGui.TextDisabled("—");
            return;
        }

        var score = Score100(rating);
        ImGui.TextUnformatted(Stars(rating.Stars));
        var hovered = ImGui.IsItemHovered();
        ImGui.SameLine(0, 5 * ImGuiHelpers.GlobalScale);
        ImGui.TextColored(ScoreColor(score), ScoreText(score));
        hovered |= ImGui.IsItemHovered();
        if (hovered)
            DrawScoreTooltip(rating);
    }

    private static void DrawSuggestedPriceCell(SellRating? rating)
    {
        if (rating?.SuggestedPrice is not { } price)
        {
            ImGui.TextDisabled("—");
            return;
        }

        ImGui.TextUnformatted(Gil(price));
        var hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ImGui.SetClipboardText(price.ToString());

        if (!hovered)
            return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted($"Suggested gross listing price: {price:N0}g/unit");
        ImGui.TextDisabled($"After 5% seller tax: {Gil(rating.NetSuggestedPriceAfterTax)} net/unit");
        ImGui.Separator();
        ImGui.TextWrapped(rating.SuggestedPriceReason);
        ImGui.TextDisabled($"Pricing confidence: {rating.SuggestedPriceConfidence:P0}");
        ImGui.Separator();
        ImGui.TextDisabled("Right-click this table value to copy the raw price to your clipboard.");
        ImGui.EndTooltip();
    }

    private static void DrawScoreTooltip(SellRating r)
    {
        ImGui.BeginTooltip();
        var score = Score100(r);
        var starCalibration = StarCalibration100(r);
        ImGui.TextUnformatted($"{Stars(r.Stars)}  {r.Label}");
        ImGui.SameLine();
        ImGui.TextColored(ScoreColor(score), $"{ScoreText(score)}/100");
        ImGui.Separator();
        ImGui.TextDisabled("Component score × weight = contribution");

        var b = r.Breakdown;
        DrawComponent("Price", b.PriceAttractiveness, ScoreCalculator.PriceWeight);
        DrawComponent("Demand", b.Demand, ScoreCalculator.DemandWeight);
        DrawComponent("Supply", b.Supply, ScoreCalculator.SupplyWeight);
        DrawComponent("Liquidity", b.Liquidity, ScoreCalculator.LiquidityWeight);
        DrawComponent("Stability", b.Stability, ScoreCalculator.StabilityWeight);
        DrawComponent("Trend", b.Trend, ScoreCalculator.TrendWeight);
        DrawComponent("Value", b.AbsoluteValue, ScoreCalculator.ValueWeight);
        DrawComponent("Vendor", b.VendorEconomics, ScoreCalculator.VendorEconomicsWeight);

        var baseScore = BaseWeightedScore(r) * 100.0;
        ImGui.Separator();
        ImGui.TextUnformatted($"Weighted base: {baseScore:0.0}/100");
        ImGui.TextUnformatted($"Strict opportunity score: {score:0.0}/100");
        ImGui.TextUnformatted($"Star calibration: {starCalibration:0.0}/100 → {r.Stars}★");
        ImGui.TextDisabled("Stars use contrast expansion; the numeric score deliberately does not. This is why excellent 5★ items do not automatically become 100/100.");
        ImGui.TextDisabled("Vendor safeguards/bonuses and execution-friction adjustments may apply after the weighted-base calculation.");
        ImGui.TextDisabled($"Confidence: {r.Confidence:P0} ({r.ConfidenceLabel}) — confidence does not change the stars.");
        ImGui.Separator();
        ImGui.TextUnformatted($"Suggested listing price: {Gil(r.SuggestedPrice)} ({r.SuggestedPriceConfidence:P0} pricing confidence)");
        ImGui.TextUnformatted($"After 5% seller tax: {Gil(r.NetSuggestedPriceAfterTax)} net/unit");
        ImGui.TextUnformatted($"Current board ask: {Gil(r.RealisticCurrentPrice)} | sold median: {Gil(r.HistoricalMedian)}");
        ImGui.TextWrapped(r.SuggestedPriceReason);
        ImGui.Separator();
        ImGui.TextUnformatted($"NPC buyback (NPC pays you): {Gil(VendorBuyback(r))}");
        ImGui.TextUnformatted($"NPC gil-shop (you pay NPC): {Gil(r.VendorGilShopPrice)}");
        if (r.VendorFloorMargin is { } floorMargin)
            ImGui.TextDisabled($"After-tax MB margin over buyback: {floorMargin:+0.0%;-0.0%;0.0%}");
        if (r.VendorArbitrageMargin is { } arbMargin)
            ImGui.TextDisabled($"After-tax MB margin over NPC purchase price: {arbMargin:+0.0%;-0.0%;0.0%}");
        if (!string.IsNullOrWhiteSpace(r.VendorEconomicsReason))
            ImGui.TextWrapped(r.VendorEconomicsReason);
        if (r.StackRecommendation is { } stack)
        {
            ImGui.Separator();
            ImGui.TextUnformatted($"Suggested stack: {stack.RecommendedStackSize:N0} ({stack.RecommendedListingCount:N0} listing(s))");
            ImGui.TextUnformatted($"Low-maintenance: {stack.LowMaintenanceStackSize:N0} ({stack.LowMaintenanceListingCount:N0} listing(s))");
            ImGui.TextDisabled($"Stack confidence: {stack.Confidence:P0} | typical buyer spend: {Gil(stack.TypicalBuyerSpend)}");
        }

        if (r.Notes.Count > 0)
        {
            ImGui.Separator();
            foreach (var note in r.Notes.Take(3))
                ImGui.BulletText(note);
        }
        ImGui.EndTooltip();
    }

    private static void DrawComponent(string name, double score, double weight)
    {
        var component = Math.Clamp(score, 0.0, 1.0) * 100.0;
        var contribution = component * weight;
        ImGui.TextUnformatted($"{name,-10} {component,5:0.0} × {weight * 100,2:0}% = {contribution,5:0.0}");
    }

    private void DrawSelectedDetails(DetailSelection detail)
    {
        ImGui.Separator();

        if (ImGui.Button("← Back to full table##close-details"))
        {
            selected = null;
            return;
        }
        ImGui.SameLine();
        ImGui.TextDisabled("Click the selected row again to close this panel too.");
        ImGui.Spacing();

        DrawDetailIdentity(detail);
        ImGui.Spacing();

        if (detail.Rating is null)
        {
            ImGui.TextWrapped("No rating yet. Fetch market data or use the live market refresh/audit queue.");
            DrawKnownLocations(detail.Locations);
            return;
        }

        var r = detail.Rating;
        DrawDetailRecommendation(detail, r);
        ImGui.Spacing();
        DrawDetailMarketEvidence(r);
        ImGui.Spacing();
        DrawDetailScore(r, detail.Quantity);
        ImGui.Spacing();
        DrawDetailDiagnostics(detail, r);
    }

    private static void DrawDetailIdentity(DetailSelection detail)
    {
        var iconSize = 58 * ImGuiHelpers.GlobalScale;
        if (detail.Item.IconId != 0)
        {
            var shared = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(detail.Item.IconId, detail.IsHq));
            if (shared.TryGetWrap(out var texture, out _))
            {
                ImGui.Image(texture.Handle, new Vector2(iconSize, iconSize));
                ImGui.SameLine(0, 12 * ImGuiHelpers.GlobalScale);
            }
        }

        ImGui.BeginGroup();
        ImGui.TextUnformatted(detail.Item.Name + (detail.IsHq ? " [HQ]" : string.Empty));
        ImGui.TextDisabled($"Item #{detail.Item.ItemId:N0} • {detail.Quantity:N0} unit(s) in this analysis");
        if (detail.Rating is { } r)
        {
            var score = Score100(r);
            ImGui.TextUnformatted(Stars(r.Stars));
            ImGui.SameLine();
            ImGui.TextColored(ScoreColor(score), $"{ScoreText(score)}/100");
            ImGui.SameLine();
            ImGui.TextUnformatted($"{r.Label} • confidence {r.Confidence:P0} ({r.ConfidenceLabel})");
            if (ImGui.IsItemHovered())
                DrawScoreTooltip(r);
        }
        ImGui.EndGroup();
    }

    private void DrawDetailRecommendation(DetailSelection detail, SellRating r)
    {
        ImGui.TextDisabled("SALE RECOMMENDATION");
        if (ImGui.BeginTable("##detail-recommendation", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Recommendation", ImGuiTableColumnFlags.WidthStretch, 1.0f);
            ImGui.TableSetupColumn("Execution / value", ImGuiTableColumnFlags.WidthStretch, 1.0f);

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted($"Suggested price: {Gil(r.SuggestedPrice)} per unit");
            if (r.SuggestedPrice is not null && ImGui.IsItemClicked(ImGuiMouseButton.Right))
                ImGui.SetClipboardText(r.SuggestedPrice.Value.ToString());
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Right-click to copy the raw suggested gil price.");
            ImGui.TextDisabled($"Pricing confidence: {r.SuggestedPriceConfidence:P0}");
            ImGui.TextWrapped(r.SuggestedPriceReason);

            if (r.StackRecommendation is { } stack)
            {
                ImGui.Spacing();
                ImGui.TextUnformatted($"Suggested stack: {stack.RecommendedStackSize:N0} per listing");
                ImGui.TextDisabled($"≈ {stack.RecommendedListingCount:N0} listing(s) for the analysed quantity");
                ImGui.TextWrapped(stack.Reason);
                if (stack.LowMaintenanceStackSize != stack.RecommendedStackSize)
                {
                    ImGui.TextUnformatted($"Low-maintenance alternative: {stack.LowMaintenanceStackSize:N0} per listing");
                    ImGui.TextDisabled(stack.LowMaintenanceReason);
                }
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted($"Net after 5% tax: {Gil(r.NetSuggestedPriceAfterTax)} per unit");
            ImGui.TextUnformatted($"Est. net / recommended listing: {Gil(EstimatedRecommendedListingValue(detail))}");
            ImGui.TextDisabled($"Whole known position at the same unit price: {Gil(EstimatedWholePositionValue(detail))}");
            if (detail.Listing is { } listing)
            {
                ImGui.Separator();
                ImGui.TextUnformatted($"Current listing: {listing.Quantity:N0} × {listing.UnitPrice:N0}g");
                ImGui.TextUnformatted($"Expected current payout: {Gil(ExpectedListingPayout(listing))}");
                ImGui.TextUnformatted($"Suggested change: {PriceChangeText(listing.UnitPrice, r.SuggestedPrice)}");
                ImGui.TextDisabled($"Retainer: {listing.RetainerName} • known listed {Elapsed(listing.FirstSeenUtc)} • current price age {Elapsed(listing.PriceChangedUtc)}");
                var timing = plugin.ListingHistory.GetTiming(listing.CharacterContentId, listing);
                if (timing is not null)
                    ImGui.TextDisabled($"Exact price + quantity state: {Elapsed(timing.StateSinceUtc)} • current quantity age {Elapsed(timing.QuantitySinceUtc)} • full observed lifetime {Elapsed(timing.FirstSeenUtc)}");
            }

            ImGui.EndTable();
        }
    }

    private static void DrawDetailMarketEvidence(SellRating r)
    {
        ImGui.TextDisabled("MARKET EVIDENCE");
        if (!ImGui.BeginTable("##detail-market", 4, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        DetailCell("Current ask", Gil(r.RealisticCurrentPrice));
        DetailCell("Weighted median", Gil(r.HistoricalMedian));
        DetailCell("Q1 – Q3", $"{Gil(r.LowerQuartile)} – {Gil(r.UpperQuartile)}");
        DetailCell("Sales sample", $"{r.SalesSampleCount:N0} / 30d");

        DetailCell("Units/day", r.UnitsPerDay.ToString("0.##"));
        DetailCell("Transactions/day", r.TransactionsPerDay.ToString("0.##"));
        DetailCell("Days of supply", Days(r.DaysOfSupply));
        DetailCell("Estimated queue", Days(r.EstimatedQueueDays));

        DetailCell("7d median", Gil(r.SevenDayMedian));
        DetailCell("30d median", Gil(r.ThirtyDayMedian));
        DetailCell("Listing freshness", Age(r.ListingFreshnessUtc));
        DetailCell("Last actual sale", Age(r.LastSaleUtc));

        ImGui.EndTable();

        ImGui.Spacing();
        ImGui.TextDisabled("VENDOR ECONOMICS");
        ImGui.TextWrapped($"NPC buyback (NPC pays you): {Gil(VendorBuyback(r))}  •  NQ gil-shop price (you pay NPC): {Gil(r.VendorGilShopPrice)}  •  suggested MB net: {Gil(r.NetSuggestedPriceAfterTax)}");
        if (r.VendorFloorMargin is { } floorMargin)
            ImGui.TextDisabled($"After-tax margin over NPC buyback: {floorMargin:+0.0%;-0.0%;0.0%}");
        if (r.VendorArbitrageMargin is { } arbMargin)
            ImGui.TextDisabled($"After-tax margin over NPC purchase price: {arbMargin:+0.0%;-0.0%;0.0%}");
        if (!string.IsNullOrWhiteSpace(r.VendorEconomicsReason))
            ImGui.TextWrapped(r.VendorEconomicsReason);
    }

    private static void DetailCell(string label, string value)
    {
        ImGui.TableNextColumn();
        ImGui.TextDisabled(label);
        ImGui.TextUnformatted(value);
    }

    private void DrawDetailScore(SellRating r, int quantity)
    {
        ImGui.TextDisabled("WHY IT SCORED THIS WAY");
        var strict = Score100(r);
        var stars = StarCalibration100(r);
        ImGui.TextWrapped($"Strict opportunity score: {strict:0.0}/100. Star calibration: {stars:0.0}/100 → {r.Stars}★. The star calibration is intentionally more generous; the numeric score is meant to preserve a meaningful top end.");

        if (ImGui.BeginTable("##detail-score", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Component");
            ImGui.TableSetupColumn("Score");
            ImGui.TableSetupColumn("Weight");
            ImGui.TableSetupColumn("Contribution");
            ImGui.TableHeadersRow();
            var b = r.Breakdown;
            DrawScoreRow("Price", b.PriceAttractiveness, ScoreCalculator.PriceWeight);
            DrawScoreRow("Demand", b.Demand, ScoreCalculator.DemandWeight);
            DrawScoreRow("Supply", b.Supply, ScoreCalculator.SupplyWeight);
            DrawScoreRow("Liquidity", b.Liquidity, ScoreCalculator.LiquidityWeight);
            DrawScoreRow("Stability", b.Stability, ScoreCalculator.StabilityWeight);
            DrawScoreRow("Trend", b.Trend, ScoreCalculator.TrendWeight);
            DrawScoreRow("Value", b.AbsoluteValue, ScoreCalculator.ValueWeight);
            DrawScoreRow("Vendor", b.VendorEconomics, ScoreCalculator.VendorEconomicsWeight);
            ImGui.EndTable();
        }

        var reference = plugin.Configuration.ValueThresholdGil;
        var unitNet = EstimatedUnitValue(r) ?? 0.0;
        var recommendedQuantity = RecommendedListingQuantity(r, quantity);
        var estimatedListingNet = unitNet * recommendedQuantity;
        var estimatedPositionNet = unitNet * Math.Max(1, quantity);
        ImGui.TextDisabled(
            $"Value reference context: one recommended listing is ~{recommendedQuantity:N0} × {unitNet:N0}g net = {estimatedListingNet:N0}g " +
            $"versus your configured {reference:N0}g meaningful-listing reference. Whole known stock at the same unit price is ~{estimatedPositionNet:N0}g.");
    }


    private static void DrawScoreRow(string name, double score, double weight)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(name);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{score * 100:0.0}");
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{weight:P0}");
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{score * weight * 100:0.0}");
    }

    private static void DrawDetailDiagnostics(DetailSelection detail, SellRating r)
    {
        if (r.StackRecommendation is { } stack && ImGui.TreeNode("Stack analysis details"))
        {
            ImGui.TextDisabled($"Confidence {stack.Confidence:P0} • burst-adjusted typical buyer spend {Gil(stack.TypicalBuyerSpend)}");
            if (Math.Abs(stack.ConveniencePremium) >= 0.01)
                ImGui.TextDisabled(stack.ConveniencePremium > 0
                    ? $"Normalized convenience premium near recommendation: +{stack.ConveniencePremium:P0}"
                    : $"Normalized bulk discount near recommendation: {stack.ConveniencePremium:P0}");
            if (stack.TopCandidates.Count > 0)
            {
                foreach (var candidate in stack.TopCandidates.Take(8))
                    ImGui.BulletText($"×{candidate.StackSize:N0} @ {Gil(candidate.SuggestedUnitPrice)} • {candidate.ListingCount:N0} listing(s) • utility {candidate.Utility:0.00} • demand {candidate.DemandFit:0.00} • affordability {candidate.Affordability:0.00} • fragmentation −{candidate.FragmentationPenalty:0.00}");
            }
            ImGui.TreePop();
        }

        if (r.Notes.Count > 0 && ImGui.TreeNode("Market notes / warnings"))
        {
            foreach (var note in r.Notes)
                ImGui.BulletText(note);
            ImGui.TreePop();
        }

        DrawKnownLocations(detail.Locations);
    }

    private static void DrawKnownLocations(IReadOnlyList<string> locations)
    {
        if (locations.Count > 0 && ImGui.TreeNode("Known owned locations"))
        {
            foreach (var location in locations)
                ImGui.BulletText(location);
            ImGui.TreePop();
        }
    }

    private void DrawCurrentListings()
    {
        ImGui.TextDisabled("Live market snapshots update passively from normal Market Board use or Should I Deep Mine?.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##listing-search", "Search current listings...", ref listingSearch, 128);

        var searchedRows = plugin.Coordinator.GetRatedOwnListings()
            .Where(x => string.IsNullOrWhiteSpace(listingSearch) ||
                        x.Item.Name.Contains(listingSearch, StringComparison.CurrentCultureIgnoreCase) ||
                        x.Listing.RetainerName.Contains(listingSearch, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        DrawListingFilters(searchedRows);

        var allRows = listingRetainerFilterId == 0
            ? searchedRows
            : searchedRows.Where(x => x.Listing.RetainerId == listingRetainerFilterId).ToList();
        var rows = allRows.Where(PassesListingFilters).ToList();

        ImGui.TextDisabled($"Showing {rows.Count:N0} of {allRows.Count:N0} cached current retainer listing(s). Open each retainer once to populate/refresh this tab.");
        ImGui.TextColored(AttentionTextColor, "Amber = listing differs from recommendation");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A row turns amber when the listed price needs changing OR the listed quantity differs from the recommended stack size. KEEP price recommendations are not flagged.");
        ImGui.TextWrapped("‘Known listed’ starts when Should I Sell? first observes that item in that market slot. FFXIV does not expose the original server-side listing timestamp, so listings that predate the addon may actually be older.");
        ImGui.Spacing();

        var listingDetailOpen = selected is { Listing: not null };
        var tableHeight = listingDetailOpen ? 300 * ImGuiHelpers.GlobalScale : -1;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.ScrollX | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable;
        if (ImGui.BeginTable("current-listings-table", 14, flags, new Vector2(0, tableHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Rating", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending, 125 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthFixed, 110 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, 180 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Listed qty", ImGuiTableColumnFlags.WidthFixed, 72 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Listed price", ImGuiTableColumnFlags.WidthFixed, 88 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Exp. payout", ImGuiTableColumnFlags.WidthFixed, 98 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Suggested", ImGuiTableColumnFlags.WidthFixed, 88 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Stack rec.", ImGuiTableColumnFlags.WidthFixed, 72 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Change", ImGuiTableColumnFlags.WidthFixed, 135 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Known listed", ImGuiTableColumnFlags.WidthFixed, 85 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Price age", ImGuiTableColumnFlags.WidthFixed, 78 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Units/day", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Last seen", ImGuiTableColumnFlags.WidthFixed, 78 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("As-is", ImGuiTableColumnFlags.WidthFixed, 78 * ImGuiHelpers.GlobalScale);
            DrawHeaderRow(ListingHeaderHelp);

            rows = SortOwnListings(rows, ImGui.TableGetSortSpecs());
            foreach (var row in rows)
            {
                var selection = FromListing(row);
                var isSelected = IsSameSelection(selection);
                var needsPriceChange = NeedsPriceChange(row);
                var needsStackChange = NeedsStackChange(row);
                var needsAttention = needsPriceChange || needsStackChange;
                var clicked = BeginClickableRow($"listing-{row.Listing.RetainerId}-{row.Listing.MarketSlot}", isSelected, needsAttention: needsAttention);
                if (clicked)
                    selected = isSelected ? null : selection;

                ImGui.TableSetColumnIndex(0);
                DrawRatingCell(row.Rating);
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(row.Listing.RetainerName);
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(row.Item.Name + (row.Listing.IsHq ? " [HQ]" : string.Empty));
                ImGui.TableSetColumnIndex(3);
                if (needsStackChange)
                    ImGui.TextColored(AttentionTextColor, row.Listing.Quantity.ToString("N0"));
                else
                    ImGui.TextUnformatted(row.Listing.Quantity.ToString("N0"));
                ImGui.TableSetColumnIndex(4);
                if (needsPriceChange)
                    ImGui.TextColored(AttentionTextColor, Gil(row.Listing.UnitPrice));
                else
                    ImGui.TextUnformatted(Gil(row.Listing.UnitPrice));
                ImGui.TableSetColumnIndex(5);
                ImGui.TextUnformatted(Gil(ExpectedListingPayout(row.Listing)));
                ImGui.TableSetColumnIndex(6);
                DrawSuggestedPriceCell(row.Rating);
                ImGui.TableSetColumnIndex(7);
                DrawStackCell(row.Rating, row.TotalOwnedQuantity > 0 ? $"Plan considers {row.TotalOwnedQuantity:N0} total known owned unit(s), not only this listing." : null);
                ImGui.TableSetColumnIndex(8);
                if (needsPriceChange)
                    ImGui.TextColored(AttentionTextColor, PriceChangeText(row));
                else
                    ImGui.TextUnformatted(PriceChangeText(row));
                ImGui.TableSetColumnIndex(9);
                ImGui.TextUnformatted(Elapsed(row.Listing.FirstSeenUtc));
                ImGui.TableSetColumnIndex(10);
                ImGui.TextUnformatted(Elapsed(row.Listing.PriceChangedUtc));
                ImGui.TableSetColumnIndex(11);
                ImGui.TextUnformatted(row.Rating is null ? "—" : row.Rating.UnitsPerDay.ToString("0.##"));
                ImGui.TableSetColumnIndex(12);
                ImGui.TextUnformatted(Age(row.Listing.LastSeenUtc));
                ImGui.TableSetColumnIndex(13);
                DrawListingStateAge(row.Listing);
            }

            ImGui.EndTable();
        }

        if (selected is { Listing: not null } listingSelection)
            DrawSelectedDetails(listingSelection);
    }

    private void DrawListingStateAge(OwnMarketListing listing)
    {
        var timing = plugin.ListingHistory.GetTiming(listing.CharacterContentId, listing);
        if (timing is null)
        {
            ImGui.TextDisabled("—");
            return;
        }

        ImGui.TextUnformatted(Elapsed(timing.StateSinceUtc));
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted($"Exact price + quantity unchanged: {Elapsed(timing.StateSinceUtc)}");
        ImGui.TextUnformatted($"Observed listing lifetime: {Elapsed(timing.FirstSeenUtc)}");
        ImGui.TextDisabled("Listing lifetime includes the time before observed quantity changes.");
        ImGui.TextUnformatted($"Current price unchanged: {Elapsed(timing.PriceSinceUtc)}");
        ImGui.TextUnformatted($"Current quantity unchanged: {Elapsed(timing.QuantitySinceUtc)}");
        ImGui.Separator();
        ImGui.TextWrapped("These are observation ages. FFXIV does not provide the original server-side listing timestamp, and a change made while Should I? cannot observe that retainer is timestamped when the listing is next seen.");
        ImGui.EndTooltip();
    }

    private void DrawListingFilters(IReadOnlyList<RatedOwnListing> listings)
    {
        if (!ImGui.CollapsingHeader("Filters##listings"))
            return;

        var retainers = listings
            .Where(x => x.Listing.RetainerId != 0)
            .GroupBy(x => x.Listing.RetainerId)
            .Select(g => new
            {
                Id = g.Key,
                Name = g.Select(x => x.Listing.RetainerName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "Unnamed retainer",
                Listings = g.Count(),
            })
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (listingRetainerFilterId != 0 && retainers.All(x => x.Id != listingRetainerFilterId))
            listingRetainerFilterId = 0;

        var selectedRetainer = retainers.FirstOrDefault(x => x.Id == listingRetainerFilterId);
        var retainerPreview = selectedRetainer is null
            ? $"Retainer: All ({listings.Count:N0})"
            : $"Retainer: {selectedRetainer.Name} ({selectedRetainer.Listings:N0})";
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##listing-retainer-filter", retainerPreview))
        {
            if (ImGui.Selectable($"All retainers ({listings.Count:N0})", listingRetainerFilterId == 0))
                listingRetainerFilterId = 0;
            if (retainers.Count > 0)
                ImGui.Separator();
            foreach (var retainer in retainers)
            {
                if (ImGui.Selectable($"{retainer.Name} ({retainer.Listings:N0})##listing-retainer-{retainer.Id}", listingRetainerFilterId == retainer.Id))
                    listingRetainerFilterId = retainer.Id;
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Limit Current Listings to one cached retainer. Search, rating, stars and payout filters are applied on top of this selection.");
        ImGui.Spacing();

        var width = 165 * ImGuiHelpers.GlobalScale;
        ImGui.SetNextItemWidth(width);
        if (ImGui.SliderInt("Rating min##listings", ref listingRatingMin, 0, 100))
            listingRatingMax = Math.Max(listingRatingMax, listingRatingMin);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(width);
        if (ImGui.SliderInt("Rating max##listings", ref listingRatingMax, 0, 100))
            listingRatingMin = Math.Min(listingRatingMin, listingRatingMax);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(width);
        if (ImGui.SliderInt("Stars min##listings", ref listingStarsMin, 1, 5))
            listingStarsMax = Math.Max(listingStarsMax, listingStarsMin);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(width);
        if (ImGui.SliderInt("Stars max##listings", ref listingStarsMax, 1, 5))
            listingStarsMin = Math.Min(listingStarsMin, listingStarsMax);

        ImGui.SetNextItemWidth(210 * ImGuiHelpers.GlobalScale);
        if (ImGui.DragLong("Payout min##listings", ref listingPayoutMin, 100f, 0, 999_999_999_999))
            listingPayoutMax = Math.Max(listingPayoutMax, listingPayoutMin);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(210 * ImGuiHelpers.GlobalScale);
        if (ImGui.DragLong("Payout max##listings", ref listingPayoutMax, 100f, 0, 999_999_999_999))
            listingPayoutMin = Math.Min(listingPayoutMin, listingPayoutMax);
        ImGui.SameLine();
        if (ImGui.SmallButton("Reset##listing-filters"))
        {
            listingRatingMin = 0;
            listingRatingMax = 100;
            listingStarsMin = 1;
            listingStarsMax = 5;
            listingPayoutMin = 0;
            listingPayoutMax = 999_999_999_999;
            listingRetainerFilterId = 0;
        }
        ImGui.TextDisabled("Payout filters use the expected after-tax payout of the currently listed stack at its current listed price.");
        ImGui.Spacing();
    }

    private bool PassesListingFilters(RatedOwnListing row)
    {
        var payout = ExpectedListingPayout(row.Listing);
        if (payout < listingPayoutMin || payout > listingPayoutMax)
            return false;

        if (row.Rating is null)
            return listingRatingMin == 0 && listingStarsMin <= 1;

        var score = Score100(row.Rating);
        return score >= listingRatingMin && score <= listingRatingMax &&
               row.Rating.Stars >= listingStarsMin && row.Rating.Stars <= listingStarsMax;
    }

    private static readonly (string Label, string Help)[] ListingHeaderHelp =
    {
        ("Rating", "Sell-opportunity rating for this item after removing the matching own listing from the comparison market so your own price does not compete against itself."),
        ("Retainer", "Retainer currently holding the market listing."),
        ("Item", "The listed item/HQ variant."),
        ("Listed qty", "Quantity currently posted in this specific market-board listing."),
        ("Listed price", "Your current gross listing price per unit."),
        ("Exp. payout", "Expected gil received if this exact listing sells at its current price, after a conservative 5% seller tax: listed quantity × listed price × 95%."),
        ("Suggested", "Recommended gross price per unit if you were pricing/repricing now. Your own matching listing is excluded from the comparison depth. Right-click to copy the raw price."),
        ("Stack rec.", "Recommended quantity per listing based on historical buyer stack behavior, convenience premiums, affordability, sell-through and fragmentation cost. The plan considers your total known stock."),
        ("Change", "Suggested price movement relative to your current listed unit price. ‘Keep’ means the difference is negligible."),
        ("Known listed", "How long this addon has continuously observed this item in the same retainer market slot. Pre-existing listings may be older than this value."),
        ("Price age", "How long the currently observed price has remained unchanged in this market slot."),
        ("Units/day", "Recent estimated units sold per day for this item/HQ variant."),
        ("Last seen", "How recently the addon saw this listing in the loaded retainer-market container."),
        ("As-is", "How long the exact currently observed price + quantity combination has remained unchanged. Hover for full observed listing lifetime, current-price age and current-quantity age. The full listing lifetime does not reset when quantity changes."),
    };

    private static void DrawStackCell(SellRating? rating, string? context = null)
    {
        if (rating?.StackRecommendation is not { } stack)
        {
            ImGui.TextDisabled("—");
            return;
        }

        ImGui.TextUnformatted(stack.RecommendedStackSize.ToString("N0"));
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        if (!string.IsNullOrWhiteSpace(context))
        {
            ImGui.TextDisabled(context);
            ImGui.Separator();
        }
        ImGui.TextUnformatted($"Recommended: {stack.RecommendedStackSize:N0} per listing");
        ImGui.TextUnformatted($"~{stack.RecommendedListingCount:N0} listing(s) at {Gil(stack.RecommendedUnitPrice)} each");
        ImGui.TextWrapped(stack.Reason);
        ImGui.Separator();
        ImGui.TextUnformatted($"Low-maintenance: {stack.LowMaintenanceStackSize:N0} per listing");
        ImGui.TextUnformatted($"~{stack.LowMaintenanceListingCount:N0} listing(s) at {Gil(stack.LowMaintenanceUnitPrice)} each");
        ImGui.TextWrapped(stack.LowMaintenanceReason);
        ImGui.Separator();
        ImGui.TextDisabled($"Stack confidence: {stack.Confidence:P0}");
        if (stack.TypicalBuyerSpend > 0)
            ImGui.TextDisabled($"Typical burst-adjusted buyer spend: {stack.TypicalBuyerSpend:N0}g");
        if (Math.Abs(stack.ConveniencePremium) >= 0.01)
            ImGui.TextDisabled(stack.ConveniencePremium > 0
                ? $"Normalized convenience premium: +{stack.ConveniencePremium:P0}"
                : $"Normalized bulk discount: {stack.ConveniencePremium:P0}");

        if (stack.TopCandidates.Count > 1)
        {
            ImGui.Separator();
            ImGui.TextDisabled("Top candidate sizes:");
            foreach (var candidate in stack.TopCandidates.Take(4))
                ImGui.TextDisabled($"{candidate.StackSize,4:N0}  {Gil(candidate.SuggestedUnitPrice),10}  {candidate.ListingCount,2} listing(s)  utility {candidate.Utility:0.00}");
        }
        ImGui.EndTooltip();
    }

    private void DrawSettings()
    {
        var cfg = plugin.Configuration;

        var valueGil = cfg.ValueThresholdGil;
        ImGui.TextWrapped("Meaningful value is the only subjective input to the rating model. It represents the expected AFTER-TAX GIL PAYOUT OF ONE RECOMMENDED LISTING that you consider meaningfully worth the selling effort — not a per-unit minimum and not the value of your entire stockpile. At this value the Value component is neutral (50%); roughly 10× scores strongly positive and 0.1× scores strongly negative.");
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Meaningful listing value (gil)", ref valueGil, 1_000, 10_000))
        {
            cfg.ValueThresholdGil = Math.Clamp(valueGil, 1, 999_999_999);
            cfg.Save();
        }
        ImGui.TextDisabled($"Current reference: {cfg.ValueThresholdGil:N0}g expected net for one recommended listing. Example: a recommended stack of 100 × 100g net ≈ 10,000g and sits near a 10,000g reference; a recommended stack of 1 × 100g does not, even if you own 100.");

    }

    private static void DrawAboutScore()
    {
        ImGui.TextWrapped("v0.8 score weights: Price attractiveness 25%, demand 17%, supply 12%, liquidity 11%, price stability 9%, market trend 5%, expected recommended-listing value 11%, and vendor economics 10%. The value component is the only part affected by your gil reference.");
        ImGui.BulletText("The numeric 0–100 opportunity score now uses the strict weighted market signal rather than the contrast-expanded star calibration. Five stars can therefore be reasonably common among excellent opportunities while 100/100 remains rare.");
        ImGui.BulletText("The gil reference represents expected after-tax payout of one recommended listing. This prevents a 105-unit stockpile that should be sold one-at-a-time from being valued as one giant 105-unit transaction, while still rewarding cheap commodities when the recommended stack itself has meaningful value.");
        ImGui.BulletText("Very fragmented recommendations (more than about a dozen separate listings) receive a mild logarithmic execution-friction penalty. This never overrides market evidence, but it stops a 100+ click sales plan from ranking like a frictionless one-listing sale.");
        ImGui.BulletText("Suggested listing prices are gross because that is what you enter on the board, but value, estimated portfolio value and vendor comparisons use the conservative net after the standard 5% seller tax.");
        ImGui.BulletText("NPC buyback (Item.PriceLow) is treated as a guaranteed floor. If after-tax MB proceeds do not beat it, the rating is hard-capped into poor territory; near-floor MB sales are heavily penalized.");
        ImGui.BulletText("For NQ items actually present in GilShopItem, Item.PriceMid is the NPC purchase price. A supported after-tax MB premium over that price adds a bounded convenience-arbitrage boost gated by real demand and sale samples.");
        ImGui.BulletText("Price attractiveness and expected value use the suggested executable sale price, not an unsupported current ask. A fantasy 200,000g listing over a ~300g sales history does not grant free score points.");
        ImGui.BulletText("Suggested price combines recent sold-price distribution, trend, current supply depth, units/day, the quantity being sold, and the recommended stack-size premium/discount when history supports one.");
        ImGui.BulletText("Suggested stack size learns from historical sold quantities, buyer transaction totals, normalized price premiums/discounts and sell-through speed, then subtracts a fragmentation/manual-management penalty.");
        ImGui.BulletText("Sales arriving in the same short purchase burst are down-weighted so one buyer sweeping many stacks does not look like many independent votes for that stack size.");
        ImGui.BulletText("Historical price premiums are normalized to the local price level around each sale date before comparing small vs. large stacks.");
        ImGui.BulletText("A low-maintenance alternative intentionally prefers fewer listings even when it gives up some historical buyer/price fit.");
        ImGui.BulletText("Shallow low-price stacks can be ignored when current demand implies they should clear quickly; larger positions relative to demand are priced more conservatively.");
        ImGui.BulletText("Price attractiveness compares the suggested executable price against a recency-weighted median of actual sold prices.");
        ImGui.BulletText("Q1/Q3 and IQR measure volatility/stability; demand separates units/day from transactions/day; supply uses current units / units sold per day; liquidity estimates queue depth at the executable price.");
        ImGui.BulletText("Confidence is separate from stars and depends on sample count plus market-data freshness.");
        ImGui.TextWrapped("The model is intentionally empirical. The table tooltips and expanded detail inspector expose the inputs so odd ratings can be diagnosed instead of hidden behind a single number.");
    }

    private static readonly Vector4 ListedRowBackground = new(0.52f, 0.36f, 0.06f, 0.24f);
    private static readonly Vector4 ListedRowTextColor = new(1.00f, 0.82f, 0.34f, 1.00f);
    private static readonly Vector4 AttentionRowBackground = new(0.62f, 0.28f, 0.03f, 0.30f);
    private static readonly Vector4 AttentionTextColor = new(1.00f, 0.62f, 0.24f, 1.00f);

    private bool BeginClickableRow(string id, bool isSelected, bool isListed = false, bool needsAttention = false)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        var cursorY = ImGui.GetCursorPosY();
        ImGui.PushID(id);
        var clicked = ImGui.Selectable("##row-hit", isSelected,
            ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap,
            new Vector2(0, ImGui.GetTextLineHeightWithSpacing()));
        var hovered = ImGui.IsItemHovered();

        if (needsAttention)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(AttentionRowBackground));
        else if (isListed)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ListedRowBackground));
        if (hovered)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ImGuiCol.HeaderHovered));
        if (isSelected)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(ImGuiCol.Header));

        ImGui.SetCursorPosY(cursorY);
        ImGui.PopID();
        return clicked;
    }

    private static void DrawHeaderRow(IReadOnlyList<(string Label, string Help)> columns)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        for (var i = 0; i < columns.Count; i++)
        {
            ImGui.TableSetColumnIndex(i);
            ImGui.TableHeader(columns[i].Label);
            if (!ImGui.IsItemHovered())
                continue;
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(420 * ImGuiHelpers.GlobalScale);
            ImGui.TextWrapped(columns[i].Help);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    private DetailSelection FromOwned(RatedOwnedItem row) =>
        new(row.Item, row.IsHq, row.Quantity, row.Rating, row.Locations, null, row.Quantity);

    private DetailSelection FromListing(RatedOwnListing row) =>
        new(
            row.Item,
            row.Listing.IsHq,
            row.TotalOwnedQuantity > 0 ? row.TotalOwnedQuantity : row.Listing.Quantity,
            row.Rating,
            new[] { $"{row.Listing.RetainerName}: current market listing ×{row.Listing.Quantity:N0}" },
            row.Listing,
            row.TotalOwnedQuantity);

    private bool IsSameSelection(DetailSelection other)
    {
        if (selected is null)
            return false;
        if (selected.Item.ItemId != other.Item.ItemId || selected.IsHq != other.IsHq)
            return false;
        if (selected.Listing is null && other.Listing is null)
            return true;
        return selected.Listing is not null && other.Listing is not null &&
               selected.Listing.RetainerId == other.Listing.RetainerId &&
               selected.Listing.MarketSlot == other.Listing.MarketSlot;
    }

    private List<RatedOwnedItem> SortOwnedItems(List<RatedOwnedItem> rows, ImGuiTableSortSpecsPtr sortSpecs)
    {
        if (sortSpecs.IsNull || sortSpecs.SpecsCount == 0)
            return rows;

        var spec = sortSpecs.Specs;
        var descending = spec.SortDirection == ImGuiSortDirection.Descending;
        return (OwnedItemColumn)spec.ColumnIndex switch
        {
            OwnedItemColumn.Rating => OrderRows(rows, x => x.Rating?.OpportunityScore ?? double.MinValue, descending),
            OwnedItemColumn.Item => OrderRows(rows, x => x.Item.Name, descending, StringComparer.CurrentCultureIgnoreCase),
            OwnedItemColumn.Quantity => OrderRows(rows, VisibleOwnedQuantity, descending),
            OwnedItemColumn.Suggested => OrderRows(rows, x => x.Rating?.SuggestedPrice ?? 0u, descending),
            OwnedItemColumn.Stack => OrderRows(rows, x => x.Rating?.StackRecommendation?.RecommendedStackSize ?? 0, descending),
            OwnedItemColumn.EstimatedTotal => OrderRows(rows, x => EstimatedRecommendedListingValue(x) ?? -1.0, descending),
            OwnedItemColumn.CurrentAsk => OrderRows(rows, x => x.Rating?.RealisticCurrentPrice ?? 0u, descending),
            OwnedItemColumn.Median => OrderRows(rows, x => x.Rating?.HistoricalMedian ?? 0.0, descending),
            OwnedItemColumn.UnitsPerDay => OrderRows(rows, x => x.Rating?.UnitsPerDay ?? -1.0, descending),
            OwnedItemColumn.Confidence => OrderRows(rows, x => x.Rating?.Confidence ?? -1.0, descending),
            OwnedItemColumn.Freshness => OrderRows(rows, x => x.Rating?.ListingFreshnessUtc ?? DateTimeOffset.MinValue, descending),
            _ => rows,
        };
    }

    private List<RatedOwnListing> SortOwnListings(List<RatedOwnListing> rows, ImGuiTableSortSpecsPtr sortSpecs)
    {
        if (sortSpecs.IsNull || sortSpecs.SpecsCount == 0)
            return rows;

        var spec = sortSpecs.Specs;
        var descending = spec.SortDirection == ImGuiSortDirection.Descending;
        return (OwnListingColumn)spec.ColumnIndex switch
        {
            OwnListingColumn.Rating => OrderRows(rows, x => x.Rating?.OpportunityScore ?? double.MinValue, descending),
            OwnListingColumn.Retainer => OrderRows(rows, x => x.Listing.RetainerName, descending, StringComparer.CurrentCultureIgnoreCase),
            OwnListingColumn.Item => OrderRows(rows, x => x.Item.Name, descending, StringComparer.CurrentCultureIgnoreCase),
            OwnListingColumn.ListedQuantity => OrderRows(rows, x => x.Listing.Quantity, descending),
            OwnListingColumn.ListedPrice => OrderRows(rows, x => x.Listing.UnitPrice, descending),
            OwnListingColumn.ExpectedPayout => OrderRows(rows, x => ExpectedListingPayout(x.Listing), descending),
            OwnListingColumn.SuggestedPrice => OrderRows(rows, x => x.Rating?.SuggestedPrice ?? 0u, descending),
            OwnListingColumn.SuggestedStack => OrderRows(rows, x => x.Rating?.StackRecommendation?.RecommendedStackSize ?? 0, descending),
            OwnListingColumn.Change => OrderRows(rows, PriceChangeRatio, descending),
            OwnListingColumn.KnownListed => OrderRows(rows, x => x.Listing.FirstSeenUtc, descending),
            OwnListingColumn.PriceAge => OrderRows(rows, x => x.Listing.PriceChangedUtc, descending),
            OwnListingColumn.UnitsPerDay => OrderRows(rows, x => x.Rating?.UnitsPerDay ?? -1.0, descending),
            OwnListingColumn.LastSeen => OrderRows(rows, x => x.Listing.LastSeenUtc, descending),
            OwnListingColumn.AsIs => OrderRows(rows, x => ListingStateAgeSeconds(x.Listing), descending),
            _ => rows,
        };
    }

    private static List<TRow> OrderRows<TRow, TKey>(List<TRow> rows, Func<TRow, TKey> key, bool descending, IComparer<TKey>? comparer = null)
        => (descending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer)).ToList();

    private enum OwnedItemColumn
    {
        Rating = 0,
        Item = 1,
        Quantity = 2,
        Suggested = 3,
        Stack = 4,
        EstimatedTotal = 5,
        CurrentAsk = 6,
        Median = 7,
        UnitsPerDay = 8,
        Confidence = 9,
        Freshness = 10,
    }

    private enum OwnListingColumn
    {
        Rating = 0,
        Retainer = 1,
        Item = 2,
        ListedQuantity = 3,
        ListedPrice = 4,
        ExpectedPayout = 5,
        SuggestedPrice = 6,
        SuggestedStack = 7,
        Change = 8,
        KnownListed = 9,
        PriceAge = 10,
        UnitsPerDay = 11,
        LastSeen = 12,
        AsIs = 13,
    }

    private double ListingStateAgeSeconds(OwnMarketListing listing)
    {
        var timing = plugin.ListingHistory.GetTiming(listing.CharacterContentId, listing);
        var since = timing?.StateSinceUtc ?? listing.FirstSeenUtc;
        return Math.Max(0, (DateTimeOffset.UtcNow - since).TotalSeconds);
    }

    private static double PriceChangeRatio(RatedOwnListing row)
    {
        if (row.Rating?.SuggestedPrice is not { } suggested || row.Listing.UnitPrice == 0)
            return 0.0;
        return ((double)suggested - row.Listing.UnitPrice) / row.Listing.UnitPrice;
    }

    private static string PriceChangeText(RatedOwnListing row)
        => ListingGuidance.PriceChangeText(row);

    private static bool NeedsPriceChange(RatedOwnListing row)
        => ListingGuidance.NeedsPriceChange(row);

    private static bool NeedsStackChange(RatedOwnListing row)
        => ListingGuidance.NeedsStackChange(row);

    private static string PriceChangeText(uint current, uint? suggested)
        => ListingGuidance.PriceChangeText(current, suggested);

    private static double Score100(SellRating rating)
        => Math.Clamp(rating.OpportunityScore, 0.0, 100.0);

    private static double StarCalibration100(SellRating rating)
        => Math.Clamp((rating.RawScore - 1.0) / 4.0 * 100.0, 0.0, 100.0);

    private static string ScoreText(double score)
    {
        if (score >= 99.999)
            return "100";
        return score.ToString("0.#");
    }

    private static double BaseWeightedScore(SellRating rating)
    {
        var b = rating.Breakdown;
        return
            ScoreCalculator.PriceWeight * b.PriceAttractiveness +
            ScoreCalculator.DemandWeight * b.Demand +
            ScoreCalculator.SupplyWeight * b.Supply +
            ScoreCalculator.LiquidityWeight * b.Liquidity +
            ScoreCalculator.StabilityWeight * b.Stability +
            ScoreCalculator.TrendWeight * b.Trend +
            ScoreCalculator.ValueWeight * b.AbsoluteValue +
            ScoreCalculator.VendorEconomicsWeight * b.VendorEconomics;
    }

    private static Vector4 ScoreColor(double score)
    {
        var t = (float)Math.Clamp(score / 100.0, 0.0, 1.0);
        var red = t < 0.5f ? 1.0f : 2.0f * (1.0f - t);
        var green = t < 0.5f ? 2.0f * t : 1.0f;
        return new Vector4(red, green, 0.08f, 1.0f);
    }

    private static double? EstimatedUnitValue(SellRating? rating)
    {
        if (rating is null)
            return null;
        if (rating.NetSuggestedPriceAfterTax is { } netSuggested)
            return netSuggested;
        return rating.HistoricalMedian is { } median
            ? median * (1.0 - ScoreCalculator.MarketSellerTaxRate)
            : null;
    }

    private static int RecommendedListingQuantity(SellRating? rating, int ownedQuantity)
    {
        var owned = Math.Max(1, ownedQuantity);
        if (rating?.StackRecommendation is { RecommendedStackSize: > 0 } stack)
            return Math.Clamp(stack.RecommendedStackSize, 1, owned);
        return owned;
    }

    private static double? EstimatedRecommendedListingValue(RatedOwnedItem row)
    {
        var unit = EstimatedUnitValue(row.Rating);
        var quantity = RecommendedListingQuantity(row.Rating, row.Quantity);
        return unit is null ? null : unit.Value * quantity;
    }

    private static double? EstimatedRecommendedListingValue(DetailSelection detail)
    {
        var unit = EstimatedUnitValue(detail.Rating);
        var quantity = RecommendedListingQuantity(detail.Rating, detail.Quantity);
        return unit is null ? null : unit.Value * quantity;
    }

    private static double? EstimatedWholePositionValue(DetailSelection detail)
    {
        var unit = EstimatedUnitValue(detail.Rating);
        return unit is null ? null : unit.Value * Math.Max(1, detail.Quantity);
    }

    private static double ExpectedListingPayout(OwnMarketListing listing)
    {
        var netUnit = ScoreCalculator.NetAfterSellerTax(listing.UnitPrice) ?? 0u;
        return netUnit * (double)listing.Quantity;
    }

    private static uint? VendorBuyback(SellRating rating) => rating.VendorBuybackPrice == 0 ? null : rating.VendorBuybackPrice;
    private static string Stars(int n) => new string('★', Math.Clamp(n, 0, 5)) + new string('☆', 5 - Math.Clamp(n, 0, 5));
    private static string Gil(uint? value) => value is null ? "—" : $"{value.Value:N0}g";
    private static string Gil(double? value) => value is null ? "—" : $"{value.Value:N0}g";
    private static string Days(double? value) => value is null ? "—" : value < 1 ? $"{value * 24:0.#}h" : $"{value:0.#}d";

    private static string Age(DateTimeOffset? timestamp)
    {
        if (timestamp is null)
            return "unknown";
        var age = DateTimeOffset.UtcNow - timestamp.Value;
        if (age.TotalMinutes < 1) return "now";
        if (age.TotalHours < 1) return $"{age.TotalMinutes:0}m ago";
        if (age.TotalDays < 1) return $"{age.TotalHours:0.#}h ago";
        return $"{age.TotalDays:0.#}d ago";
    }

    private static string Elapsed(DateTimeOffset timestamp)
    {
        var age = DateTimeOffset.UtcNow - timestamp;
        if (age.TotalMinutes < 1) return "<1m";
        if (age.TotalHours < 1) return $"{age.TotalMinutes:0}m";
        if (age.TotalDays < 1) return $"{age.TotalHours:0.#}h";
        return $"{age.TotalDays:0.#}d";
    }
}










