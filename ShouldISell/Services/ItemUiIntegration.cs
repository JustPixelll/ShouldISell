using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using ShouldISell.Windows;

namespace ShouldISell.Services;

/// <summary>
/// Adds Should I? lookup actions to the normal inventory context menu through Dalamud's IContextMenu
/// service. Native item-tooltip content is handled separately by ItemTooltipAugmenter.
/// </summary>
public sealed class ItemUiIntegration : IDisposable
{
    private readonly Plugin plugin;
    private readonly IContextMenu contextMenu;
    private readonly SuiteWindow suiteWindow;

    public ItemUiIntegration(Plugin plugin, IContextMenu contextMenu, SuiteWindow suiteWindow)
    {
        this.plugin = plugin;
        this.contextMenu = contextMenu;
        this.suiteWindow = suiteWindow;
        contextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose()
        => contextMenu.OnMenuOpened -= OnMenuOpened;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!plugin.Configuration.ShowItemContextMenu ||
            args.MenuType != ContextMenuType.Inventory ||
            args.Target is not MenuTargetInventory target ||
            target.TargetItem is not { } targetItem)
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
            LookupMenuItem("Should I Do?", ShouldIModule.Do, itemId, isHq, item.IsMarketable),
        };
    }

    private MenuItem LookupMenuItem(string name, ShouldIModule module, uint itemId, bool isHq, bool enabled)
        => new()
        {
            Name = name,
            IsEnabled = enabled,
            OnClicked = _ => suiteWindow.OpenItemLookup(module, itemId, isHq),
        };
}
