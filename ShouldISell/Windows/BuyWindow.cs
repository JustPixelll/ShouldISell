using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace ShouldISell.Windows;

public sealed class BuyWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string search = string.Empty;
    private BuyOpportunity? selected;

    public BuyWindow(Plugin plugin)
        : base("Should I Buy?##ShouldIBuy")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(960, 580),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        ImGui.TextWrapped("Should I Buy? scans the market as a capital-allocation problem. It ranks executable purchase packages by potential profit, ROI, liquidity, estimated time to exit, market evidence, stack behavior and execution friction. Purchases remain manual; successful Market Board buys are passively recorded for your personal trader profile.");
        ImGui.Separator();

        if (ImGui.BeginTabBar("##buy-tabs"))
        {
            if (ImGui.BeginTabItem("Opportunities"))
            {
                DrawOpportunities();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Portfolio"))
            {
                DrawPortfolio();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Trader Profile"))
            {
                DrawTraderProfile();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Buy Settings"))
            {
                DrawSettings();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawOpportunities()
    {
        var engine = plugin.BuyEngine;
        if (!engine.IsScanning)
        {
            if (ImGui.Button("SCAN FOR BUYS", new Vector2(180 * ImGuiHelpers.GlobalScale, 0)))
                _ = engine.ScanAsync();
        }
        else
        {
            if (ImGui.Button("STOP SCAN", new Vector2(180 * ImGuiHelpers.GlobalScale, 0)))
                engine.Stop();
        }
        ImGui.SameLine();
        ImGui.TextWrapped(engine.Status);

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##buy-search", "Search opportunities...", ref search, 128);

        var rows = engine.Opportunities
            .Where(x => string.IsNullOrWhiteSpace(search) ||
                        x.Item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                        StrategyName(x.Strategy).Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        ImGui.TextDisabled($"{rows.Count:N0} opportunity packages. Rating combines return with realistic exit quality; confidence remains separate.");

        var tableHeight = selected is null ? -1 : 310 * ImGuiHelpers.GlobalScale;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.Resizable;
        if (ImGui.BeginTable("buy-opportunities", 12, flags, new Vector2(0, tableHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Rating", ImGuiTableColumnFlags.WidthFixed, 112 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Strategy", ImGuiTableColumnFlags.WidthFixed, 110 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Buy", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 82 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Exit @", ImGuiTableColumnFlags.WidthFixed, 78 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Stack", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Profit", ImGuiTableColumnFlags.WidthFixed, 85 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("ROI", ImGuiTableColumnFlags.WidthFixed, 62 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Risk adj.", ImGuiTableColumnFlags.WidthFixed, 88 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Exit days", ImGuiTableColumnFlags.WidthFixed, 68 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Max buy", ImGuiTableColumnFlags.WidthFixed, 78 * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                var chosen = selected == row;
                if (ImGui.Selectable($"{Stars(row.Stars)} {row.Score:0}##buy-{row.Item.ItemId}-{row.IsHq}-{row.Strategy}", chosen,
                        ImGuiSelectableFlags.SpanAllColumns))
                    selected = chosen ? null : row;
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(row.Item.Name + (row.IsHq ? " [HQ]" : string.Empty));
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(StrategyName(row.Strategy));
                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(row.BuyQuantity.ToString("N0"));
                ImGui.TableSetColumnIndex(4);
                ImGui.TextUnformatted(Gil(row.AcquisitionCost));
                ImGui.TableSetColumnIndex(5);
                ImGui.TextUnformatted(row.GuaranteedExit ? "Vendor" : Gil(row.SuggestedSellUnitPrice));
                ImGui.TableSetColumnIndex(6);
                ImGui.TextUnformatted(row.SuggestedSellStackSize.ToString("N0"));
                ImGui.TableSetColumnIndex(7);
                ImGui.TextUnformatted("+" + Gil(row.PotentialProfit));
                ImGui.TableSetColumnIndex(8);
                ImGui.TextUnformatted(row.Roi.ToString("P0"));
                ImGui.TableSetColumnIndex(9);
                ImGui.TextUnformatted("+" + Gil(row.RiskAdjustedProfit));
                ImGui.TableSetColumnIndex(10);
                ImGui.TextUnformatted(row.EstimatedLiquidationDays is { } d ? d.ToString("0.##") : "—");
                ImGui.TableSetColumnIndex(11);
                ImGui.TextUnformatted(Gil(row.MaximumAcceptableBuyPrice));
            }

            ImGui.EndTable();
        }

        if (selected is not null)
            DrawOpportunityDetails(selected);
    }

    private void DrawOpportunityDetails(BuyOpportunity row)
    {
        ImGui.Separator();
        ImGui.Text($"{row.Item.Name}{(row.IsHq ? " [HQ]" : string.Empty)} — {Stars(row.Stars)} {row.Score:0}/100");
        ImGui.TextWrapped($"{StrategyName(row.Strategy)} | Buy {row.BuyQuantity:N0} | Cost {Gil(row.AcquisitionCost)} | Potential profit +{Gil(row.PotentialProfit)} | Risk-adjusted +{Gil(row.RiskAdjustedProfit)} | ROI {row.Roi:P1}");
        ImGui.TextWrapped($"Existing {row.ExistingQuantity:N0} → modeled position {row.PositionAfterBuy:N0}. Daily volume {row.DailyVolume:0.##}. Confidence {row.Confidence:P0}. " +
                          (row.GuaranteedExit
                              ? "Exit is guaranteed NPC buyback."
                              : $"Suggested exit {Gil(row.SuggestedSellUnitPrice)}/unit in stacks of {row.SuggestedSellStackSize:N0}; estimated full liquidation {FormatDays(row.EstimatedLiquidationDays)}."));

        if (row.Lots.Count > 0)
        {
            ImGui.Text("Purchase package:");
            foreach (var lot in row.Lots)
                ImGui.BulletText($"{lot.Quantity:N0} × {lot.UnitPrice:N0}g | estimated tax {lot.EstimatedTax:N0}g | total {lot.TotalAcquisitionCost:N0}g");
        }
        else if (row.Strategy == BuyStrategy.VendorToMarket)
        {
            ImGui.TextWrapped("Acquisition source: normal gil vendor. The recommended quantity is demand-capped rather than automatically buying a full stack.");
        }

        ImGui.Text("Why / caveats:");
        foreach (var note in row.Notes)
            ImGui.BulletText(note);
    }

    private void DrawPortfolio()
    {
        var portfolio = plugin.BuyEngine.BuildPortfolio();
        ImGui.TextWrapped("The portfolio allocator greedily deploys your budget into the strongest non-overlapping item/HQ packages by risk-adjusted return efficiency. It deliberately leaves gil unspent when the remaining trades are weaker or indivisible.");
        ImGui.Separator();
        ImGui.Text($"Budget: {Gil(portfolio.Budget)}");
        ImGui.SameLine();
        ImGui.Text($"Invested: {Gil(portfolio.Invested)}");
        ImGui.SameLine();
        ImGui.Text($"Reserve: {Gil(portfolio.Reserve)}");
        ImGui.Text($"Potential profit: +{Gil(portfolio.PotentialProfit)} | Risk-adjusted: +{Gil(portfolio.RiskAdjustedProfit)}");

        if (ImGui.BeginTable("buy-portfolio", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Strategy", ImGuiTableColumnFlags.WidthFixed, 110 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Buy", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Allocated", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Potential", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Risk adj.", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Rating", ImGuiTableColumnFlags.WidthFixed, 100 * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();
            foreach (var line in portfolio.Lines)
            {
                var row = line.Opportunity;
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(row.Item.Name + (row.IsHq ? " [HQ]" : string.Empty));
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(StrategyName(row.Strategy));
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(row.BuyQuantity.ToString("N0"));
                ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(Gil(line.AllocatedGil));
                ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted("+" + Gil(line.PotentialProfit));
                ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted("+" + Gil(line.RiskAdjustedProfit));
                ImGui.TableSetColumnIndex(6); ImGui.TextUnformatted($"{Stars(row.Stars)} {row.Score:0}");
            }
            ImGui.EndTable();
        }
    }

    private void DrawTraderProfile()
    {
        var profile = plugin.TraderProfiles.Build();
        ImGui.TextWrapped("Your trader profile joins passively captured Market Board purchase cost basis with Should I Sell?'s personal retainer sales. FIFO matching only attributes realized P&L where the plugin can connect sold units to prior recorded buys; unmatched crafted/pre-existing stock is excluded from trading ROI instead of being guessed.");
        ImGui.Separator();
        ImGui.Text($"Purchases: {profile.PurchaseTransactions:N0} | Capital deployed: {Gil(profile.TotalCapitalDeployed)} | Open cost basis: {Gil(profile.OpenCostBasis)} ({profile.OpenUnits:N0} units)");
        ImGui.Text($"Matched realized profit: {SignedGil(profile.RealizedProfit)} | Realized ROI: {profile.RealizedRoi:P1} | Win rate: {profile.WinRate:P0} | Avg hold: {profile.AverageHoldingDays:0.##}d");
        ImGui.Text($"Sales coverage: {profile.SaleCoverage:P0} | Matched units: {profile.MatchedUnitsSold:N0} | Matched net revenue: {Gil(profile.MatchedRevenue)}");
        if (profile.AverageSellTimePredictionErrorDays is { } timeError)
            ImGui.Text($"Avg sell-time prediction error: {timeError:0.##}d");
        if (profile.AverageExitPricePredictionErrorPercent is { } priceError)
        {
            ImGui.SameLine();
            ImGui.Text($"| Avg exit-price prediction error: {priceError:0.##}%");
        }
        if (profile.BestStrategy is { } best)
            ImGui.Text($"Best realized strategy so far: {StrategyName(best)}");

        ImGui.Separator();
        ImGui.Text("Strategy analysis");
        if (ImGui.BeginTable("trader-strategies", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Strategy");
            ImGui.TableSetupColumn("Matched units");
            ImGui.TableSetupColumn("Profit");
            ImGui.TableSetupColumn("ROI");
            ImGui.TableSetupColumn("Avg hold");
            ImGui.TableSetupColumn("Win rate");
            ImGui.TableHeadersRow();
            foreach (var row in profile.Strategies)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(StrategyName(row.Strategy));
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(row.MatchedUnits.ToString("N0"));
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(SignedGil(row.RealizedProfit));
                ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(row.Roi.ToString("P1"));
                ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(row.AverageHoldingDays.ToString("0.##") + "d");
                ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(row.WinRate.ToString("P0"));
            }
            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.Text("Open trading positions");
        if (ImGui.BeginTable("trader-open", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Qty");
            ImGui.TableSetupColumn("Cost basis");
            ImGui.TableSetupColumn("Avg cost");
            ImGui.TableSetupColumn("Strategy");
            ImGui.TableSetupColumn("Oldest lot");
            ImGui.TableHeadersRow();
            foreach (var row in profile.OpenPositions.Take(100))
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(row.ItemName + (row.IsHq ? " [HQ]" : string.Empty));
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(row.Quantity.ToString("N0"));
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(Gil(row.RemainingCostBasis));
                ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(row.AverageCostPerUnit.ToString("N0") + "g");
                ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(row.DominantStrategy is { } s ? StrategyName(s) : "Unclassified");
                ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(row.OldestPurchaseUtc.ToLocalTime().ToString("yyyy-MM-dd"));
            }
            ImGui.EndTable();
        }
    }

    private void DrawSettings()
    {
        var cfg = plugin.Configuration;
        ImGui.TextWrapped("These settings control capital exposure and risk tolerance. The scanner can look at the whole market, but only deep-fetches the strongest aggregate candidates before running the expensive counterfactual listing/history model.");

        var budget = cfg.BuyBudgetGil;
        if (ImGui.InputInt("Budget (gil)", ref budget, 10_000, 100_000))
        {
            cfg.BuyBudgetGil = Math.Clamp(budget, 1_000, 2_000_000_000);
            cfg.Save();
        }
        var minProfit = cfg.BuyMinimumProfitGil;
        if (ImGui.InputInt("Minimum potential profit", ref minProfit, 500, 5_000))
        {
            cfg.BuyMinimumProfitGil = Math.Clamp(minProfit, 0, 2_000_000_000);
            cfg.Save();
        }

        var minRoi = (float)(cfg.BuyMinimumRoi * 100.0);
        if (ImGui.SliderFloat("Minimum ROI (%)", ref minRoi, 0, 200, "%.0f%%"))
        {
            cfg.BuyMinimumRoi = minRoi / 100.0;
            cfg.Save();
        }
        var holding = (float)cfg.BuyMaximumHoldingDays;
        if (ImGui.SliderFloat("Target max holding (days)", ref holding, 0.25f, 30f, "%.2f"))
        {
            cfg.BuyMaximumHoldingDays = holding;
            cfg.Save();
        }
        var exposure = (float)(cfg.BuyMaximumBudgetFractionPerItem * 100.0);
        if (ImGui.SliderFloat("Max budget per item (%)", ref exposure, 1, 100, "%.0f%%"))
        {
            cfg.BuyMaximumBudgetFractionPerItem = exposure / 100.0;
            cfg.Save();
        }
        var deep = cfg.BuyDeepCandidateLimit;
        if (ImGui.SliderInt("Deep-analysis candidate limit", ref deep, 20, 500))
        {
            cfg.BuyDeepCandidateLimit = deep;
            cfg.Save();
        }
        var buyerTax = (float)(cfg.BuyEstimatedBuyerTaxRate * 100.0);
        if (ImGui.SliderFloat("Estimated buyer tax (%)", ref buyerTax, 0, 10, "%.1f%%"))
        {
            cfg.BuyEstimatedBuyerTaxRate = buyerTax / 100.0;
            cfg.Save();
        }

        var marketMarket = cfg.BuyEnableMarketToMarket;
        if (ImGui.Checkbox("Market → Market", ref marketMarket))
        {
            cfg.BuyEnableMarketToMarket = marketMarket;
            cfg.Save();
        }
        var vendorMarket = cfg.BuyEnableVendorToMarket;
        if (ImGui.Checkbox("Vendor → Market", ref vendorMarket))
        {
            cfg.BuyEnableVendorToMarket = vendorMarket;
            cfg.Save();
        }
        var marketVendor = cfg.BuyEnableMarketToVendor;
        if (ImGui.Checkbox("Market → Vendor", ref marketVendor))
        {
            cfg.BuyEnableMarketToVendor = marketVendor;
            cfg.Save();
        }
        var includeHq = cfg.BuyIncludeHq;
        if (ImGui.Checkbox("Include HQ market opportunities", ref includeHq))
        {
            cfg.BuyIncludeHq = includeHq;
            cfg.Save();
        }

        ImGui.Separator();
        ImGui.TextWrapped("Market → Vendor is restricted to NQ in this first implementation so the guaranteed-exit calculation never relies on an assumed HQ vendor multiplier. Market purchases are recorded with the actual tax exposed by Dalamud; the configured buyer-tax value is only the conservative estimate used while scanning unseen listings.");
    }

    private static string StrategyName(BuyStrategy strategy) => strategy switch
    {
        BuyStrategy.MarketSweep => "Sweep",
        BuyStrategy.SplitStack => "Buy & split",
        BuyStrategy.ConsolidateStack => "Consolidate",
        BuyStrategy.VendorToMarket => "Vendor → MB",
        BuyStrategy.MarketToVendor => "MB → Vendor",
        _ => strategy.ToString(),
    };

    private static string Stars(int stars) => new('★', Math.Clamp(stars, 1, 5));
    private static string Gil(long value) => $"{value:N0}g";
    private static string Gil(uint? value) => value is { } v ? $"{v:N0}g" : "—";
    private static string SignedGil(long value) => value >= 0 ? $"+{value:N0}g" : $"-{Math.Abs(value):N0}g";
    private static string FormatDays(double? days) => days is { } d ? $"{d:0.##}d" : "—";
}
