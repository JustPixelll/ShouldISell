using Dalamud.Configuration;

namespace ShouldISell;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 6;

    /// <summary>
    /// The only subjective sell-rating input: the expected after-tax gil value of one recommended
    /// listing the user considers meaningfully worthwhile. At exactly this value, the absolute-value
    /// component is neutral (50%). Values one order of magnitude above/below it are strongly
    /// rewarded/penalized on a smooth logarithmic curve.
    /// </summary>
    public int ValueThresholdGil { get; set; } = 10_000;

    // Kept only so old v0.1 configs deserialize cleanly and can be migrated once.
    public int ValueSetting { get; set; } = 3;

    // Should I Buy? capital/risk preferences.
    public int BuyBudgetGil { get; set; } = 500_000;
    public int BuyMinimumProfitGil { get; set; } = 2_000;
    public float BuyMinimumRoiPercent { get; set; } = 10f;
    public float BuyMaximumHoldingDays { get; set; } = 7f;
    public int BuyMaximumInvestmentPercentPerItem { get; set; } = 25;
    public int BuyDeepCandidateLimit { get; set; } = 120;
    public int BuyPortfolioMaxPositions { get; set; } = 8;
    public int BuyNativeDeepScanLimit { get; set; } = 20;
    public bool BuyIncludeEquipment { get; set; } = false;
    public bool BuyUseCategoryFilter { get; set; } = false;
    public List<uint> BuyIncludedCategoryIds { get; set; } = new();
    public bool BuyEnableMarketToMarket { get; set; } = true;
    public bool BuyEnableVendorToMarket { get; set; } = true;
    public bool BuyEnableMarketToVendor { get; set; } = true;

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

        // v0.7 changed the meaning from per-unit reference to expected NET value of a recommended
        // listing, but the existing gil number remains a useful personal reference and therefore
        // does not need a numeric conversion.
        if (Version < 3)
        {
            Version = 3;
            changed = true;
        }

        // v1.1 introduces the Should I? suite and Should I Buy?. Defaults are deliberately
        // conservative, so existing Should I Sell? installations can migrate without surprises.
        if (Version < 4)
        {
            Version = 4;
            changed = true;
        }

        // v1.1.1 adds an explicit portfolio basket-size cap. Existing configs get the field's
        // conservative default of eight positions.
        if (Version < 5)
        {
            BuyPortfolioMaxPositions = Math.Clamp(BuyPortfolioMaxPositions <= 0 ? 8 : BuyPortfolioMaxPositions, 1, 20);
            Version = 5;
            changed = true;
        }

        // v1.1.3 adds the user-selected native deep-scan size for Should I Buy?.
        if (Version < 6)
        {
            BuyNativeDeepScanLimit = Math.Clamp(BuyNativeDeepScanLimit <= 0 ? 20 : BuyNativeDeepScanLimit, 1, 100);
            Version = 6;
            changed = true;
        }

        if (changed)
            Save();
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
