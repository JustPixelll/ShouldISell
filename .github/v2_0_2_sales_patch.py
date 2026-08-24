from pathlib import Path


def replace_once(path: str, old: str, new: str):
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    if old not in text:
        raise SystemExit(f"pattern not found in {path}: {old[:120]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


# Current Listings: add a retainer filter without disturbing existing rating/payout filters.
replace_once(
    "ShouldISell/Windows/MainWindow.cs",
    "    private long listingPayoutMax = 999_999_999_999;\n",
    "    private long listingPayoutMax = 999_999_999_999;\n    private ulong listingRetainerFilterId;\n",
)

replace_once(
    "ShouldISell/Windows/MainWindow.cs",
    '''        ImGui.SetNextItemWidth(-1);\n        ImGui.InputTextWithHint("##listing-search", "Search current listings...", ref listingSearch, 128);\n        DrawListingFilters();\n\n        var allRows = plugin.Coordinator.GetRatedOwnListings()\n            .Where(x => string.IsNullOrWhiteSpace(listingSearch) ||\n                        x.Item.Name.Contains(listingSearch, StringComparison.CurrentCultureIgnoreCase) ||\n                        x.Listing.RetainerName.Contains(listingSearch, StringComparison.CurrentCultureIgnoreCase))\n            .ToList();\n        var rows = allRows.Where(PassesListingFilters).ToList();\n''',
    '''        ImGui.SetNextItemWidth(-1);\n        ImGui.InputTextWithHint("##listing-search", "Search current listings...", ref listingSearch, 128);\n\n        var searchedRows = plugin.Coordinator.GetRatedOwnListings()\n            .Where(x => string.IsNullOrWhiteSpace(listingSearch) ||\n                        x.Item.Name.Contains(listingSearch, StringComparison.CurrentCultureIgnoreCase) ||\n                        x.Listing.RetainerName.Contains(listingSearch, StringComparison.CurrentCultureIgnoreCase))\n            .ToList();\n        DrawListingFilters(searchedRows);\n\n        var allRows = listingRetainerFilterId == 0\n            ? searchedRows\n            : searchedRows.Where(x => x.Listing.RetainerId == listingRetainerFilterId).ToList();\n        var rows = allRows.Where(PassesListingFilters).ToList();\n''',
)

replace_once(
    "ShouldISell/Windows/MainWindow.cs",
    '''    private void DrawListingFilters()\n    {\n        if (!ImGui.CollapsingHeader("Filters##listings"))\n            return;\n\n        var width = 165 * ImGuiHelpers.GlobalScale;\n''',
    '''    private void DrawListingFilters(IReadOnlyList<RatedOwnListing> listings)\n    {\n        if (!ImGui.CollapsingHeader("Filters##listings"))\n            return;\n\n        var retainers = listings\n            .Where(x => x.Listing.RetainerId != 0)\n            .GroupBy(x => x.Listing.RetainerId)\n            .Select(g => new\n            {\n                Id = g.Key,\n                Name = g.Select(x => x.Listing.RetainerName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "Unnamed retainer",\n                Listings = g.Count(),\n            })\n            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)\n            .ToList();\n\n        if (listingRetainerFilterId != 0 && retainers.All(x => x.Id != listingRetainerFilterId))\n            listingRetainerFilterId = 0;\n\n        var selectedRetainer = retainers.FirstOrDefault(x => x.Id == listingRetainerFilterId);\n        var retainerPreview = selectedRetainer is null\n            ? $"Retainer: All ({listings.Count:N0})"\n            : $"Retainer: {selectedRetainer.Name} ({selectedRetainer.Listings:N0})";\n        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);\n        if (ImGui.BeginCombo("##listing-retainer-filter", retainerPreview))\n        {\n            if (ImGui.Selectable($"All retainers ({listings.Count:N0})", listingRetainerFilterId == 0))\n                listingRetainerFilterId = 0;\n            if (retainers.Count > 0)\n                ImGui.Separator();\n            foreach (var retainer in retainers)\n            {\n                if (ImGui.Selectable($"{retainer.Name} ({retainer.Listings:N0})##listing-retainer-{retainer.Id}", listingRetainerFilterId == retainer.Id))\n                    listingRetainerFilterId = retainer.Id;\n            }\n            ImGui.EndCombo();\n        }\n        if (ImGui.IsItemHovered())\n            ImGui.SetTooltip("Limit Current Listings to one cached retainer. Search, rating, stars and payout filters are applied on top of this selection.");\n        ImGui.Spacing();\n\n        var width = 165 * ImGuiHelpers.GlobalScale;\n''',
)

replace_once(
    "ShouldISell/Windows/MainWindow.cs",
    '''            listingPayoutMin = 0;\n            listingPayoutMax = 999_999_999_999;\n''',
    '''            listingPayoutMin = 0;\n            listingPayoutMax = 999_999_999_999;\n            listingRetainerFilterId = 0;\n''',
)

# Sales History: insert the new market benchmark overview after the existing personal-sales summary.
replace_once(
    "ShouldISell/Windows/MainWindow.SalesHistory.cs",
    '''        DrawSalesSummary(sales);\n        ImGui.Spacing();\n\n        ImGui.SetNextItemWidth(-1);\n''',
    '''        DrawSalesSummary(sales);\n        ImGui.Spacing();\n        DrawSalesMarketBenchmark(sales);\n        ImGui.Spacing();\n\n        ImGui.SetNextItemWidth(-1);\n''',
)

# Dedicated Universalis history refresh for sold items, including items no longer owned.
coordinator_insert = r'''
    public async Task RefreshSalesHistoryBenchmarksAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0)
            return;
        if (!await refreshGate.WaitAsync(0, cancellationToken))
            return;

        try
        {
            IsFetching = true;
            var worldId = playerState.CurrentWorld.RowId;
            acceptingWorldId = worldId;
            var ids = store.GetPersonalSales(playerState.ContentId)
                .Select(x => x.ItemId)
                .Distinct()
                .Where(id => catalog.Get(id).IsMarketable)
                .ToList();
            if (ids.Count == 0)
            {
                FetchStatus = "No captured marketable sales to benchmark.";
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var historyNeeded = ids
                .Where(itemId =>
                {
                    var observed = store.GetMarket(worldId, itemId)?.HistoryObservedAtUtc;
                    return force || observed is null ||
                           now - observed.Value > TimeSpan.FromMinutes(configuration.UniversalisHistoryTtlMinutes);
                })
                .ToList();

            FetchStatus = historyNeeded.Count == 0
                ? $"Sales benchmarks ready: {ids.Count:N0} sold item(s) already fresh."
                : $"Sales benchmarks: refreshing 90-day history for {historyNeeded.Count:N0} sold item(s)...";

            if (historyNeeded.Count > 0)
                await universalis.FetchHistoryAsync(worldId, historyNeeded, cancellationToken);

            store.Flush();
            LastFetchCompletedUtc = DateTimeOffset.UtcNow;
            FetchStatus = $"Sales benchmarks ready: {ids.Count:N0} sold item(s).";
        }
        catch (OperationCanceledException)
        {
            FetchStatus = "Sales benchmark refresh cancelled.";
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Universalis sales-history benchmark refresh failed.");
            FetchStatus = $"Sales benchmark error: {ex.Message}";
        }
        finally
        {
            acceptingWorldId = 0;
            IsFetching = false;
            refreshGate.Release();
        }
    }

'''
replace_once(
    "ShouldISell/Services/MarketDataCoordinator.cs",
    "    public IReadOnlyList<RatedOwnedItem> GetRatedOwnedItems()\n",
    coordinator_insert + "    public IReadOnlyList<RatedOwnedItem> GetRatedOwnedItems()\n",
)

# New partial UI file keeps the benchmark logic separate from the personal ledger itself.
sales_benchmark = r'''using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using ShouldISell.Services;

namespace ShouldISell.Windows;

public sealed partial class MainWindow
{
    private sealed record SaleMarketBenchmark(
        ItemInfo Item,
        bool IsHq,
        long PersonalUnits,
        double PersonalNetGil,
        double PersonalAverageNetUnit,
        double HistoricalAverageNetUnit,
        double HistoricalMedianNetUnit,
        double HistoricalAverageBenchmarkTotal,
        double HistoricalMedianBenchmarkTotal,
        double DeltaVsAverage,
        double DeltaVsMedian,
        int MarketTransactions,
        long MarketUnits);

    private void DrawSalesMarketBenchmark(IReadOnlyList<PersonalSale> sales)
    {
        if (!ImGui.CollapsingHeader("Should I? realized-price benchmark##sales-market-benchmark", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextWrapped("Compare the net prices you actually realized with Phoenix's cached 90-day Market Board history for the same item/HQ variants. The historical average is unit-weighted; the median is unit-weighted too. Both market prices are converted to a conservative after-5%-seller-tax net so they are comparable with your captured net gil.");
        ImGui.TextDisabled("This is a benchmark, not causal proof that the addon created the entire difference. Market prices move over time, your own sales may be part of Universalis history, and only variants with usable 90-day history are included.");

        if (!plugin.Coordinator.IsFetching)
        {
            if (ImGui.SmallButton("Refresh 90-day sales benchmarks"))
                _ = plugin.Coordinator.RefreshSalesHistoryBenchmarksAsync(force: true);
        }
        else
        {
            ImGui.TextDisabled("Market-data refresh running...");
        }
        ImGui.SameLine();
        ImGui.TextDisabled(plugin.Coordinator.FetchStatus);

        var benchmarks = BuildSaleMarketBenchmarks(sales);
        var soldVariants = sales
            .Where(x => x.NetGil > 0 && x.Quantity > 0)
            .Select(x => (x.ItemId, x.IsHq))
            .Distinct()
            .Count();

        if (benchmarks.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("No comparable 90-day market history is cached for your sold variants yet. Use the refresh button above; sold items are fetched even if you no longer own them.");
            return;
        }

        var actualNet = benchmarks.Sum(x => x.PersonalNetGil);
        var averageBenchmark = benchmarks.Sum(x => x.HistoricalAverageBenchmarkTotal);
        var medianBenchmark = benchmarks.Sum(x => x.HistoricalMedianBenchmarkTotal);
        var deltaAverage = actualNet - averageBenchmark;
        var deltaMedian = actualNet - medianBenchmark;
        var coveredUnits = benchmarks.Sum(x => x.PersonalUnits);

        ImGui.Spacing();
        if (ImGui.BeginTable("##sales-market-benchmark-summary", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            SummaryCell("Your covered net", Gil(actualNet));
            SummaryCell("90d avg benchmark", Gil(averageBenchmark));
            SummaryCell("Difference vs avg", SignedGil(deltaAverage) + RelativeDifference(deltaAverage, averageBenchmark));
            SummaryCell("Coverage", $"{benchmarks.Count:N0}/{soldVariants:N0} variants • {coveredUnits:N0} units");
            ImGui.TableNextRow();
            SummaryCell("Your covered net", Gil(actualNet));
            SummaryCell("90d median benchmark", Gil(medianBenchmark));
            SummaryCell("Difference vs median", SignedGil(deltaMedian) + RelativeDifference(deltaMedian, medianBenchmark));
            SummaryCell("Market basis", "Phoenix • trailing 90d");
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("PER ITEM / HQ VARIANT — total difference applies the historical net/unit benchmark to the same number of units you actually sold.");
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
        var height = Math.Min(300, 42 + benchmarks.Count * 25) * ImGuiHelpers.GlobalScale;
        if (!ImGui.BeginTable("##sales-market-benchmark-items", 9, flags, new Vector2(0, height)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Units", ImGuiTableColumnFlags.WidthFixed, 58 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Your net/u", ImGuiTableColumnFlags.WidthFixed, 88 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("90d avg/u", ImGuiTableColumnFlags.WidthFixed, 88 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("vs avg/u", ImGuiTableColumnFlags.WidthFixed, 82 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Total vs avg", ImGuiTableColumnFlags.WidthFixed, 96 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("90d median/u", ImGuiTableColumnFlags.WidthFixed, 94 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Total vs median", ImGuiTableColumnFlags.WidthFixed, 105 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Market sample", ImGuiTableColumnFlags.WidthFixed, 105 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var row in benchmarks.OrderByDescending(x => x.DeltaVsAverage))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(row.Item.Name + (row.IsHq ? " [HQ]" : string.Empty));
            ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(row.PersonalUnits.ToString("N0"));
            ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(Gil(row.PersonalAverageNetUnit));
            ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(Gil(row.HistoricalAverageNetUnit));
            ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(SignedGil(row.PersonalAverageNetUnit - row.HistoricalAverageNetUnit));
            ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(SignedGil(row.DeltaVsAverage));
            ImGui.TableSetColumnIndex(6); ImGui.TextUnformatted(Gil(row.HistoricalMedianNetUnit));
            ImGui.TableSetColumnIndex(7); ImGui.TextUnformatted(SignedGil(row.DeltaVsMedian));
            ImGui.TableSetColumnIndex(8); ImGui.TextUnformatted($"{row.MarketTransactions:N0} sales / {row.MarketUnits:N0}u");
        }

        ImGui.EndTable();
    }

    private List<SaleMarketBenchmark> BuildSaleMarketBenchmarks(IReadOnlyList<PersonalSale> sales)
    {
        if (!Plugin.PlayerState.IsLoaded)
            return new List<SaleMarketBenchmark>();

        var worldId = Plugin.PlayerState.CurrentWorld.RowId;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-90);
        var netFactor = 1.0 - ScoreCalculator.MarketSellerTaxRate;
        var result = new List<SaleMarketBenchmark>();

        foreach (var personalGroup in sales.GroupBy(x => (x.ItemId, x.IsHq)))
        {
            var personal = personalGroup.Where(x => x.NetGil > 0 && x.Quantity > 0).ToList();
            if (personal.Count == 0)
                continue;

            var market = plugin.Store.GetMarket(worldId, personalGroup.Key.ItemId);
            var marketSales = market?.Sales
                .Where(x => x.IsHq == personalGroup.Key.IsHq && x.PricePerUnit > 0 && x.Quantity > 0 && x.SoldAtUtc >= cutoff)
                .OrderBy(x => x.PricePerUnit)
                .ToList() ?? new List<MarketSale>();
            if (marketSales.Count == 0)
                continue;

            var personalUnits = personal.Sum(x => (long)x.Quantity);
            var personalNet = personal.Sum(x => (double)x.NetGil);
            if (personalUnits <= 0 || personalNet <= 0)
                continue;

            var marketUnits = marketSales.Sum(x => (long)x.Quantity);
            if (marketUnits <= 0)
                continue;

            var historicalAverageGross = marketSales.Sum(x => (double)x.PricePerUnit * x.Quantity) / marketUnits;
            var historicalMedianGross = UnitWeightedMedian(marketSales, marketUnits);
            var historicalAverageNet = historicalAverageGross * netFactor;
            var historicalMedianNet = historicalMedianGross * netFactor;
            var averageBenchmarkTotal = historicalAverageNet * personalUnits;
            var medianBenchmarkTotal = historicalMedianNet * personalUnits;

            result.Add(new SaleMarketBenchmark(
                plugin.Catalog.Get(personalGroup.Key.ItemId),
                personalGroup.Key.IsHq,
                personalUnits,
                personalNet,
                personalNet / personalUnits,
                historicalAverageNet,
                historicalMedianNet,
                averageBenchmarkTotal,
                medianBenchmarkTotal,
                personalNet - averageBenchmarkTotal,
                personalNet - medianBenchmarkTotal,
                marketSales.Count,
                marketUnits));
        }

        return result;
    }

    private static double UnitWeightedMedian(IReadOnlyList<MarketSale> sortedSales, long totalUnits)
    {
        var target = (totalUnits + 1) / 2;
        long cumulative = 0;
        foreach (var sale in sortedSales)
        {
            cumulative += sale.Quantity;
            if (cumulative >= target)
                return sale.PricePerUnit;
        }
        return sortedSales.Count == 0 ? 0 : sortedSales[^1].PricePerUnit;
    }

    private static string SignedGil(double value)
        => $"{value:+#,##0;-#,##0;0}g";

    private static string RelativeDifference(double delta, double baseline)
        => baseline <= 0 ? string.Empty : $" ({delta / baseline:+0.0%;-0.0%;0.0%})";
}
'''
Path("ShouldISell/Windows/MainWindow.SalesBenchmark.cs").write_text(sales_benchmark, encoding="utf-8")

# Version/release notes.
replace_once(
    "ShouldISell/ShouldISell.csproj",
    "    <Version>2.0.1.0</Version>\n",
    "    <Version>2.0.2.0</Version>\n",
)

Path("RELEASE_NOTES_v2.0.2.md").write_text(r'''# Should I? v2.0.2

## Sales History — realized-price benchmark

- Adds a new benchmark overview comparing your captured net sale prices with Phoenix 90-day historical Market Board prices for the same item/HQ variant.
- Shows your average realized net/unit, the historical unit-weighted average and unit-weighted median, per-unit differences, and total gil difference across the units you actually sold.
- Adds portfolio-level totals: actual covered net gil, the equivalent historical-average/median benchmark, and the aggregate difference in gil and percent.
- Historical market prices are converted to a conservative after-5%-seller-tax net so they are comparable with the personal sales ledger.
- Includes explicit coverage/sample counts and avoids claiming causal profit: this is an observed market benchmark, not proof that every gil of difference was created by Should I?.
- Adds a dedicated Universalis history refresh for sold items, including items that are no longer in your inventory.

## Current Listings — retainer filter

- Current Listings can now be filtered to All retainers or one individual cached retainer.
- The retainer filter composes with item search, rating, stars and payout filters.
- Reset restores All retainers.
''', encoding="utf-8")

print("v2.0.2 sales benchmark + retainer filter patch applied")
