using Dalamud.Configuration;

namespace ShouldISell;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 8;
    public int ValueThresholdGil { get; set; } = 10_000;
    public int ValueSetting { get; set; } = 3;

    // Legacy capital/risk fields remain only so existing configs deserialize without data loss.
    // They no longer silently filter Should I Buy? discovery; findings filters live in the UI.
    public int BuyBudgetGil { get; set; } = 500_000;
    public int BuyMinimumProfitGil { get; set; } = 2_000;
    public float BuyMinimumRoiPercent { get; set; } = 10f;
    public float BuyMaximumHoldingDays { get; set; } = 7f;
    public int BuyMaximumInvestmentPercentPerItem { get; set; } = 25;
    public int BuyPortfolioMaxPositions { get; set; } = 8;

    // Universalis discovery scope. The Market Board and Vendor tabs copy their own UI state into
    // these fields immediately before starting a discovery run.
    public int BuyDeepCandidateLimit { get; set; } = 120;
    public bool BuyIncludeEquipment { get; set; } = false;
    public bool BuyUseCategoryFilter { get; set; } = false;
    public List<uint> BuyIncludedCategoryIds { get; set; } = new();
    public bool BuyEnableMarketToMarket { get; set; } = true;
    public bool BuyEnableVendorToMarket { get; set; } = true;
    public bool BuyEnableMarketToVendor { get; set; } = true;
    public string BuyDiscoveryNameFilter { get; set; } = string.Empty;
    public bool BuyDiscoveryIncludeNq { get; set; } = true;
    public bool BuyDiscoveryIncludeHq { get; set; } = true;

    public int UniversalisCurrentTtlMinutes { get; set; } = 15;
    public int UniversalisHistoryTtlMinutes { get; set; } = 60;

    // The inventory-coverage reminder is informational. Once dismissed by the user, it stays gone.
    public bool InventoryCoverageWarningDismissed { get; set; }

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

        if (Version < 3) { Version = 3; changed = true; }
        if (Version < 4) { Version = 4; changed = true; }
        if (Version < 5)
        {
            BuyPortfolioMaxPositions = Math.Clamp(BuyPortfolioMaxPositions <= 0 ? 8 : BuyPortfolioMaxPositions, 1, 20);
            Version = 5;
            changed = true;
        }
        if (Version < 6) { Version = 6; changed = true; }
        if (Version < 7) { Version = 7; changed = true; }
        if (Version < 8)
        {
            BuyDiscoveryNameFilter ??= string.Empty;
            Version = 8;
            changed = true;
        }

        if (changed)
            Save();
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
