namespace ShouldISell.Windows;

public sealed partial class MainWindow
{
    public void FocusItem(uint itemId, bool isHq)
    {
        var item = plugin.Catalog.Get(itemId);
        if (item.ItemId == 0)
            return;
        search = item.Name;
        selected = plugin.Coordinator.GetRatedOwnedItems()
            .Where(x => x.Item.ItemId == itemId && x.IsHq == isHq)
            .Select(FromOwned)
            .FirstOrDefault();
    }
}
