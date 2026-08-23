using Dalamud.Configuration;

namespace ShouldISell;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 4;

    /// <summary>
    /// The expected after-tax gil value of one recommended sell listing that the user considers
    /// meaningfully worthwhile. This remains the subjective value anchor for Should I Sell?.
    /// </summary>
    public int ValueThresholdGil { get; set; } = 10_000;

    // Kept only so old v0.1 configs deserialize cleanly and can be migrated once.
    public int ValueSetting { get; set; } = 3;

    // Should I Buy? capital/risk preferences.
    public int BuyBudgetGil { get; set; } = 500_000;
    public int BuyMinimumProfitGil { get; set; } = 2_000;
    public double BuyMinimumRoi { get; set; } = 0.10;
    public double BuyMaximumHoldingDays { get; set; } = 7.0;
    public double BuyMaximumBudgetFractionPerItem { get; set; } = 0.25;
    public int BuyDeepCandidateLimit { get; set; } = 180;
    public double BuyEstimatedBuyerTaxRate { get; set; } = 0.05;
    public bool BuyEnableMarketToMarket { get; set; } = true;
    public bool BuyEnableVendorToMarket { get; set; } = true;
    public bool BuyEnableMarketToVendor { get; set; } = true;
    public bool BuyIncludeHq { get; set; } = true;

    // Buy scan scope. All-mode is explicit so an empty custom selection can really mean "scan none".
    public bool BuyUseAllSearchCategories { get; set; } = true;
    public List<uint> BuyEnabledSearchCategoryIds { get; set; } = new();

    // Technical controls. These do not express a player preference about which item is "better".
    public int UniversalisCurrentTtlMinutes { get; set; } = 15;
    public int UniversalisHistoryTtlMinutes { get; set; } = 60;
    public int ExperimentalRefreshStaleHours { get; set; } = 24;
    public int ExperimentalRequestSpacingMs { get; set; } = 2200;
    public int ExperimentalRequestTimeoutMs { get; set; } = 12000;
    public int ExperimentalMaxRetries { get; set; } = 3;

    public void MigrateIfNeeded()
    {
        var changed = false;

        if (Version < 2)
        {
            ValueThresholdGil = Math.Clamp(ValueSetting, 1, 5) switch
            {
                1 => 500,
                2 => 2_000,
                3 => 10_000,
                4 => 30_000,
                _ => 80_000,
            };
            Version = 2;
            changed = true;
        }

        if (Version < 3)
        {
            Version = 3;
            changed = true;
        }

        if (Version < 4)
        {
            BuyBudgetGil = Math.Max(10_000, BuyBudgetGil);
            BuyMinimumProfitGil = Math.Max(0, BuyMinimumProfitGil);
            BuyMinimumRoi = Math.Clamp(BuyMinimumRoi, 0.0, 10.0);
            BuyMaximumHoldingDays = Math.Clamp(BuyMaximumHoldingDays, 0.25, 90.0);
            BuyMaximumBudgetFractionPerItem = Math.Clamp(BuyMaximumBudgetFractionPerItem, 0.01, 1.0);
            BuyDeepCandidateLimit = Math.Clamp(BuyDeepCandidateLimit, 20, 500);
            BuyEstimatedBuyerTaxRate = Math.Clamp(BuyEstimatedBuyerTaxRate, 0.0, 0.25);
            BuyUseAllSearchCategories = true;
            BuyEnabledSearchCategoryIds ??= new List<uint>();
            Version = 4;
            changed = true;
        }

        BuyEnabledSearchCategoryIds ??= new List<uint>();

        if (changed)
            Save();
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
