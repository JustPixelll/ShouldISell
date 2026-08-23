using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace ShouldISell.Services;

public sealed class GameItemCatalog
{
    private readonly IDataManager data;
    private readonly Dictionary<uint, ItemInfo> cache = new();
    private HashSet<uint>? gilShopItems;
    private IReadOnlyList<(uint Id, string Name)>? marketCategories;

    public GameItemCatalog(IDataManager data)
    {
        this.data = data;
    }

    public ItemInfo Get(uint itemId)
    {
        if (cache.TryGetValue(itemId, out var cached))
            return cached;

        var sheet = data.GetExcelSheet<Item>();
        if (!sheet.TryGetRow(itemId, out var row))
            return cache[itemId] = new ItemInfo(itemId, $"Item #{itemId}", false, false, 0, null, 1, 0);

        // ItemSearchCategory > 0 is the game's own market-board search categorisation and is
        // a strong practical definition for an item that can participate in this addon.
        var marketable = row.ItemSearchCategory.RowId > 0;

        // PriceLow is what an NPC pays the player (the guaranteed vendor floor). PriceMid is
        // what a normal gil vendor charges, but it is meaningful only when the item actually
        // appears in GilShopItem. Many Item rows have a PriceMid without being a normal gil-shop
        // purchase, so membership is deliberately checked instead of trusting PriceMid alone.
        var vendorGilPrice = IsSoldByGilVendor(itemId) && row.PriceMid > 0 ? row.PriceMid : (uint?)null;
        var info = new ItemInfo(
            itemId,
            row.Name.ToString(),
            marketable,
            row.CanBeHq,
            row.PriceLow,
            vendorGilPrice,
            row.StackSize,
            row.Icon);

        cache[itemId] = info;
        return info;
    }

    public bool IsMarketable(uint itemId) => Get(itemId).IsMarketable;

    public uint GetMarketSearchCategoryId(uint itemId)
    {
        var sheet = data.GetExcelSheet<Item>();
        return sheet.TryGetRow(itemId, out var row) ? row.ItemSearchCategory.RowId : 0;
    }

    public IReadOnlyList<(uint Id, string Name)> GetMarketSearchCategories()
    {
        if (marketCategories is not null)
            return marketCategories;

        var result = new List<(uint Id, string Name)>();
        try
        {
            foreach (var row in data.GetExcelSheet<ItemSearchCategory>())
            {
                if (row.RowId == 0)
                    continue;
                var name = row.Name.ToString();
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                result.Add((row.RowId, name));
            }
        }
        catch
        {
            // Category scoping is optional. Returning an empty list makes the buy scanner behave
            // as "all marketable items" rather than preventing the plugin from loading after a sheet change.
        }

        marketCategories = result
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return marketCategories;
    }

    private bool IsSoldByGilVendor(uint itemId)
    {
        EnsureGilShopIndex();
        return gilShopItems!.Contains(itemId);
    }

    private void EnsureGilShopIndex()
    {
        if (gilShopItems is not null)
            return;

        var result = new HashSet<uint>();
        try
        {
            foreach (var shopRows in data.GetSubrowExcelSheet<GilShopItem>())
            {
                foreach (var row in shopRows)
                {
                    var id = row.Item.RowId;
                    if (id != 0)
                        result.Add(id);
                }
            }
        }
        catch
        {
            // Vendor-price enrichment is useful but must never prevent the core market plugin
            // from loading after a game-data/schema change. An empty index simply disables the
            // vendor-purchase arbitrage signal until the sheet path is updated.
        }

        gilShopItems = result;
    }
}
