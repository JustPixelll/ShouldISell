using Dalamud.Configuration;

namespace ShouldISell;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;

    /// <summary>
    /// The only subjective rating input: the expected after-tax gil value of the whole known
    /// item position the user considers meaningfully worthwhile. At exactly this value, the
    /// absolute-value component is neutral (50%). Values one order of magnitude above/below it
    /// are strongly rewarded/penalized on a smooth logarithmic curve.
    /// </summary>
    public int ValueThresholdGil { get; set; } = 10_000;

    // Kept only so old v0.1 configs deserialize cleanly and can be migrated once.
    public int ValueSetting { get; set; } = 3;

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

        // v0.7 changes the meaning from per-unit reference to expected NET value of the whole
        // known position, but the existing gil number remains a useful personal reference and
        // therefore does not need a numeric conversion.
        if (Version < 3)
        {
            Version = 3;
            changed = true;
        }

        if (changed)
            Save();
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
