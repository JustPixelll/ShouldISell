using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using ShouldISell.Windows;

namespace ShouldISell.Services;

/// <summary>
/// Official Dalamud item-UI integration. It never mutates the native ItemDetail addon or context-menu
/// node tree: hover insight is a separate no-input ImGui window, and right-click integration uses
/// IContextMenu. That keeps it additive alongside other plugins using the same game UI.
/// </summary>
public sealed class ItemUiIntegration : IDisposable
{
    private readonly Plugin plugin;
    private readonly IGameGui gameGui;
    private readonly IContextMenu contextMenu;
    private readonly SuiteWindow suiteWindow;
    private ulong lastHoveredRawId;
    private HoverInsight? hoverInsight;

    public ItemUiIntegration(Plugin plugin, IGameGui gameGui, IContextMenu contextMenu, SuiteWindow suiteWindow)
    {
        this.plugin = plugin;
        this.gameGui = gameGui;
        this.contextMenu = contextMenu;
        this.suiteWindow = suiteWindow;
        contextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose()
        => contextMenu.OnMenuOpened -= OnMenuOpened;

    public void Draw()
    {
        if (!Plugin.PlayerState.IsLoaded || gameGui.GameUiHidden)
            return;

        var raw = gameGui.HoveredItem;
        if (raw == 0)
        {
            lastHoveredRawId = 0;
            hoverInsight = null;
            return;
        }

        if (raw != lastHoveredRawId)
        {
            lastHoveredRawId = raw;
            hoverInsight = BuildHoverInsight(raw);
        }

        if (hoverInsight is not { } insight)
            return;

        var viewport = ImGui.GetMainViewport();
        var width = 350f;
        var pos = new Vector2(
            viewport.WorkPos.X + Math.Max(8, viewport.WorkSize.X - width - 18),
            viewport.WorkPos.Y + 72);
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, 0), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.94f);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav |
                    ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoMove;
        if (!ImGui.Begin("Should I? item insight##ShouldIItemHover", flags))
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted(insight.ItemName + (insight.IsHq ? " [HQ]" : string.Empty));
        ImGui.Separator();
        if (insight.SellText is not null) ImGui.TextUnformatted(insight.SellText);
        if (insight.ValueText is not null) ImGui.TextDisabled(insight.ValueText);
        if (insight.BuyText is not null) ImGui.TextUnformatted(insight.BuyText);
        if (insight.CraftText is not null) ImGui.TextUnformatted(insight.CraftText);
        if (insight.GatherText is not null) ImGui.TextUnformatted(insight.GatherText);
        ImGui.TextDisabled("Right-click the item for Look up in Should I…");
        ImGui.End();
    }

    private HoverInsight? BuildHoverInsight(ulong rawId)
    {
        var isHq = rawId > 1_000_000;
        var itemId64 = isHq ? rawId - 1_000_000 : rawId;
        if (itemId64 == 0 || itemId64 > uint.MaxValue)
            return null;
        var itemId = (uint)itemId64;
        var item = plugin.Catalog.Get(itemId);
        if (item.ItemId == 0 || !item.IsMarketable)
            return null;

        var owned = plugin.Coordinator.GetRatedOwnedItems()
            .FirstOrDefault(x => x.Item.ItemId == itemId && x.IsHq == isHq);
        var rating = owned?.Rating;
        string? sellText = null;
        string? valueText = null;
        if (rating is not null)
        {
            sellText = $"Sell  {Stars(rating.Stars)}  {rating.OpportunityScore:0}/100  ·  {rating.Confidence:P0} confidence";
            if (rating.NetSuggestedPriceAfterTax is { } net)
            {
                var stack = Math.Max(1, rating.StackRecommendation?.RecommendedStackSize ?? 1);
                var stackNet = (double)net * stack;
                var knownTotal = owned is null ? (double?)null : (double)net * owned.Quantity;
                valueText = $"Est. net/unit {net:N0}g  ·  recommended stack {stack:N0} ≈ {stackNet:N0}g" +
                            (knownTotal is null ? string.Empty : $"  ·  known total ≈ {knownTotal:N0}g");
            }
        }
        else
        {
            sellText = "Sell  not rated yet — refresh this item's market data from Universalis.";
        }

        var buy = plugin.BuyScanner.GetOpportunities()
            .Where(x => x.Item.ItemId == itemId && x.IsHq == isHq)
            .OrderByDescending(x => x.OpportunityScore)
            .FirstOrDefault();
        var buyText = buy is null
            ? null
            : $"Buy   {Stars(buy.Stars)}  {buy.OpportunityScore:0}/100  ·  {buy.PotentialProfit:N0}g modeled profit";

        var craft = plugin.ProductionScanner.GetCraftOpportunities()
            .Where(x => x.Item.ItemId == itemId)
            .OrderByDescending(x => x.OpportunityScore)
            .FirstOrDefault();
        var craftText = craft is null
            ? null
            : $"Craft {Stars(craft.Stars)}  {craft.OpportunityScore:0}/100  ·  {craft.EconomicProfit:N0}g economic profit";

        var gather = plugin.ProductionScanner.GetGatherOpportunities()
            .Where(x => x.Item.ItemId == itemId)
            .OrderByDescending(x => x.OpportunityScore)
            .FirstOrDefault();
        var gatherText = gather is null
            ? null
            : $"Gather {Stars(gather.Stars)}  {gather.OpportunityScore:0}/100  ·  ~{gather.EstimatedGilPerActiveMinute:N0}g/active min";

        return new HoverInsight(item.Name, isHq, sellText, valueText, buyText, craftText, gatherText);
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Inventory || args.Target is not MenuTargetInventory target || target.TargetItem is not { } targetItem)
            return;

        var itemId = targetItem.BaseItemId;
        if (itemId == 0)
            return;
        var item = plugin.Catalog.Get(itemId);
        if (item.ItemId == 0 || !item.IsMarketable)
            return;

        var isHq = targetItem.IsHq;
        args.AddMenuItem(new MenuItem
        {
            Name = "Look up in Should I…",
            IsSubmenu = true,
            OnClicked = clicked => clicked.OpenSubmenu(BuildLookupSubmenu(itemId, isHq)),
        });
    }

    private IReadOnlyList<IMenuItem> BuildLookupSubmenu(uint itemId, bool isHq)
    {
        var item = plugin.Catalog.Get(itemId);
        var hasRecipe = plugin.ProductionScanner.GetCraftOpportunities().Any(x => x.Item.ItemId == itemId);
        var hasGather = plugin.ProductionScanner.GetGatherOpportunities().Any(x => x.Item.ItemId == itemId);

        return new List<IMenuItem>
        {
            LookupMenuItem("Should I Sell?", ShouldIModule.Sell, itemId, isHq, item.IsMarketable),
            LookupMenuItem("Should I Buy?", ShouldIModule.Buy, itemId, isHq, item.IsMarketable),
            LookupMenuItem("Should I Craft?", ShouldIModule.Craft, itemId, isHq, hasRecipe),
            LookupMenuItem("Should I Gather?", ShouldIModule.Gather, itemId, isHq, hasGather),
            LookupMenuItem("Opportunities", ShouldIModule.Opportunities, itemId, isHq, item.IsMarketable),
        };
    }

    private MenuItem LookupMenuItem(string name, ShouldIModule module, uint itemId, bool isHq, bool enabled)
        => new()
        {
            Name = name,
            IsEnabled = enabled,
            OnClicked = _ => suiteWindow.OpenItemLookup(module, itemId, isHq),
        };

    private static string Stars(int stars)
        => new string('★', Math.Clamp(stars, 1, 5)) + new string('☆', 5 - Math.Clamp(stars, 1, 5));

    private sealed record HoverInsight(
        string ItemName,
        bool IsHq,
        string? SellText,
        string? ValueText,
        string? BuyText,
        string? CraftText,
        string? GatherText);
}
