using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

namespace ShouldISell.Services;

/// <summary>
/// Tracks whether the normal player inventory UI has been visibly opened this session. This is
/// informational only: Should I? never opens UI or requests data on the player's behalf.
/// </summary>
public sealed class InventoryCoverageMonitor : IDisposable
{
    private static readonly string[] InventoryAddons =
    [
        "Inventory",
        "InventoryLarge",
        "InventoryExpansion",
    ];

    private readonly IAddonLifecycle addonLifecycle;
    private readonly Configuration configuration;

    public InventoryCoverageMonitor(IAddonLifecycle addonLifecycle, Configuration configuration)
    {
        this.addonLifecycle = addonLifecycle;
        this.configuration = configuration;
        addonLifecycle.RegisterListener(AddonEvent.PostDraw, InventoryAddons, OnInventoryDrawn);
    }

    public bool InventoryOpenedThisSession { get; private set; }

    public bool ShouldWarn
        => !configuration.InventoryCoverageWarningDismissed && !InventoryOpenedThisSession;

    public void DismissPermanently()
    {
        configuration.InventoryCoverageWarningDismissed = true;
        configuration.Save();
    }

    public void Dispose()
        => addonLifecycle.UnregisterListener(AddonEvent.PostDraw, InventoryAddons, OnInventoryDrawn);

    private void OnInventoryDrawn(AddonEvent _, AddonArgs __)
        => InventoryOpenedThisSession = true;
}
