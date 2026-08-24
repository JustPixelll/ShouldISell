using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ShouldISell.Services;

namespace ShouldISell.Windows;

public enum ShouldIModule
{
    Sell,
    Buy,
    Craft,
    Gather,
    Opportunities,
    Tycoon,
}

public sealed partial class SuiteWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly MainWindow sellWindow;
    private ShouldIModule? selectOnNextDraw;
    private BuyOpportunity? selectedBuyOpportunity;
    private BuyPortfolioPlan? buyPortfolioPlan; // Legacy model cache compatibility; no portfolio UI remains.
    private bool buyDetailsOpen;
    private BuySortColumn buySortColumn = BuySortColumn.Rating;
    private bool buySortAscending;
    private string buySearch = string.Empty;
    private string vendorBuySearch = string.Empty;
    private string buyCategorySearch = string.Empty;
    private bool selectTycoonPurchases;

    public SuiteWindow(Plugin plugin)
        : base("Should I?##ShouldISuite")
    {
        this.plugin = plugin;
        sellWindow = new MainWindow(plugin);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(980, 620),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose() => sellWindow.Dispose();

    public void OpenModule(ShouldIModule module)
    {
        selectOnNextDraw = module;
        IsOpen = true;
    }

    public void OpenItemLookup(ShouldIModule module, uint itemId, bool isHq)
    {
        var item = plugin.Catalog.Get(itemId);
        if (item.ItemId == 0)
            return;

        switch (module)
        {
            case ShouldIModule.Sell:
                sellWindow.FocusItem(itemId, isHq);
                break;
            case ShouldIModule.Buy:
                marketBuyLane.FindingsSearch = item.Name;
                vendorBuyLane.FindingsSearch = item.Name;
                buyDetailsOpen = false;
                selectedBuyOpportunity = null;
                break;
            case ShouldIModule.Craft:
                craftSearch = item.Name;
                selectedCraftOpportunity = null;
                break;
            case ShouldIModule.Gather:
                gatherSearch = item.Name;
                selectedGatherOpportunity = null;
                break;
            case ShouldIModule.Opportunities:
                opportunitySearch = item.Name;
                break;
        }

        OpenModule(module);
    }

    public override void Draw()
    {
        ImGui.TextUnformatted("Should I?");
        ImGui.SameLine();
        ImGui.TextDisabled("One economy brain: buy, craft, gather, sell, then learn from what actually happened.");
        ImGui.Separator();

        if (!ImGui.BeginTabBar("##should-i-modules"))
            return;

        DrawModuleTab(ShouldIModule.Sell, "Should I Sell?", sellWindow.Draw);
        DrawModuleTab(ShouldIModule.Buy, "Should I Buy?", DrawBuyModule);
        DrawModuleTab(ShouldIModule.Craft, "Should I Craft?", DrawCraftModule);
        DrawModuleTab(ShouldIModule.Gather, "Should I Gather?", DrawGatherModule);
        DrawModuleTab(ShouldIModule.Opportunities, "Opportunities", DrawOpportunitiesModule);
        DrawModuleTab(ShouldIModule.Tycoon, "Should I Tycoon?", DrawTycoonModule);
        ImGui.EndTabBar();

        selectOnNextDraw = null;
    }

    private void DrawModuleTab(ShouldIModule module, string label, Action draw)
    {
        var flags = selectOnNextDraw == module ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        if (!ImGui.BeginTabItem(label, flags))
            return;
        draw();
        ImGui.EndTabItem();
    }

    private uint CurrentBuyWorldId
        => Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.CurrentWorld.RowId : 0;

    private string CurrentBuyWorldName
        => CurrentBuyWorldId == 0 ? "Unknown world" : plugin.Catalog.GetWorldName(CurrentBuyWorldId);

    private IReadOnlyList<BuyOpportunity> GetCurrentWorldBuyOpportunities()
    {
        var worldId = CurrentBuyWorldId;
        if (worldId == 0)
            return Array.Empty<BuyOpportunity>();
        return GetModelAdjustedBuyOpportunities(worldId)
            .Where(x => x.Kind != BuyOpportunityKind.VendorToMarket)
            .ToList();
    }

    private IReadOnlyList<BuyOpportunity> GetCurrentWorldVendorOpportunities()
    {
        var worldId = CurrentBuyWorldId;
        if (worldId == 0)
            return Array.Empty<BuyOpportunity>();
        return GetModelAdjustedBuyOpportunities(worldId)
            .Where(x => x.Kind == BuyOpportunityKind.VendorToMarket)
            .ToList();
    }

    private static void ItemNameContextMenu(string popupId, string itemName)
    {
        if (!ImGui.BeginPopupContextItem(popupId))
            return;
        if (ImGui.MenuItem("Copy item name"))
            ImGui.SetClipboardText(itemName);
        ImGui.EndPopup();
    }

    private static string Stars(int stars)
        => new string('★', Math.Clamp(stars, 1, 5)) + new string('☆', 5 - Math.Clamp(stars, 1, 5));

    private static string Gil(double? value)
        => value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)
            ? "—"
            : $"{value.Value:N0}g";

    private static string Gil(long value) => $"{value:N0}g";

    private static string Percent(double value) => $"{value:P1}";

    private static string Days(double? value)
        => value is null ? "—" : value.Value < 1 ? $"{value.Value * 24:0.#}h" : $"{value.Value:0.#}d";
}
