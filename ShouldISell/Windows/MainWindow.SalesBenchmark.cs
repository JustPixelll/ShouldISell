using System.Numerics;
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
