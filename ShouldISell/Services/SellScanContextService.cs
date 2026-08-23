using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ShouldISell.Services;

public enum SellScanTarget
{
    None,
    PlayerInventory,
    ActiveRetainerInventory,
    ActiveRetainerMarket,
}

public sealed record SellScanContext(SellScanTarget Target, string Label, string Detail)
{
    public bool IsAvailable => Target != SellScanTarget.None;
}

/// <summary>
/// Detects which of FFXIV's native sell/inventory windows is currently visible so the
/// experimental live scanner can act on exactly the inventory the player is looking at.
/// No UI callbacks are fired here; this is detection only.
/// </summary>
public sealed unsafe class SellScanContextService
{
    private readonly IGameGui gameGui;

    private static readonly string[] RetainerGridAddons =
    [
        "RetainerGrid0",
        "RetainerGrid1",
        "RetainerGrid2",
        "RetainerGrid3",
        "RetainerGrid4",
        "RetainerCrystalGrid",
    ];

    public SellScanContextService(IGameGui gameGui)
    {
        this.gameGui = gameGui;
    }

    public SellScanContext Detect()
    {
        // Retainer grids take priority because the normal inventory may also remain visible
        // while interacting with a retainer.
        if (RetainerGridAddons.Any(IsVisible))
            return new SellScanContext(
                SellScanTarget.ActiveRetainerInventory,
                "Active retainer inventory",
                "Retainer inventory grid detected. All marketable items on the active retainer will be requested once.");

        if (IsVisible("Inventory"))
            return new SellScanContext(
                SellScanTarget.PlayerInventory,
                "Player inventory",
                "Inventory window detected. Inventory 1–4 marketable items will be requested once.");

        if (IsVisible("RetainerSellList"))
            return new SellScanContext(
                SellScanTarget.ActiveRetainerMarket,
                "Active retainer market listings",
                "Retainer sell list detected. Items already listed by the active retainer will be requested once.");

        return new SellScanContext(
            SellScanTarget.None,
            "No sell inventory detected",
            "Open 'Sell items in your inventory on the market' or 'Sell items in your retainer inventory on the market', then start the live scan.");
    }

    private bool IsVisible(string addonName)
    {
        var address = gameGui.GetAddonByName(addonName, 1).Address;
        if (address == nint.Zero)
            return false;
        var addon = (AtkUnitBase*)address;
        return addon->IsVisible && addon->UldManager.LoadedState == AtkLoadState.Loaded;
    }
}
