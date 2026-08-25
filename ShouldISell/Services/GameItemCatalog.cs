using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace ShouldISell.Services;

public sealed class GameItemCatalog
{
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, ItemInfo> cache = new();
    private HashSet<uint>? gilShopItems;
    private IReadOnlyList<ItemCategoryInfo>? categories;
    private IReadOnlyList<MarketCatalogEntry>? marketableEntries;

    public GameItemCatalog(IDataManager data, IPluginLog log)
    {
        this.data = data;
        this.log = log;
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

    public string GetWorldName(uint worldId)
    {
        if (worldId == 0)
            return "Unknown world";

        var sheet = data.GetExcelSheet<World>();
        return sheet.TryGetRow(worldId, out var world) && !string.IsNullOrWhiteSpace(world.Name.ToString())
            ? world.Name.ToString()
            : $"World #{worldId}";
    }

    /// <summary>
    /// Returns FFXIV's normal item UI categories. Should I Buy? uses these as its discovery
    /// scope rather than maintaining a brittle hand-written list of item IDs.
    /// </summary>
    public IReadOnlyList<ItemCategoryInfo> GetCategories()
    {
        if (categories is not null)
            return categories;

        categories = data.GetExcelSheet<ItemUICategory>()
            .Where(x => x.RowId != 0 && !string.IsNullOrWhiteSpace(x.Name.ToString()))
            .Select(x => new ItemCategoryInfo(x.RowId, x.Name.ToString()))
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return categories;
    }

    /// <summary>
    /// Builds the local discovery universe for Should I Buy?. This is deliberately derived from
    /// Lumina/game data: discovery never needs to walk the in-game Market Board to learn which
    /// items exist. Universalis is queried only after this local scope has been selected.
    /// </summary>
    public IReadOnlyList<MarketCatalogEntry> GetAllMarketableEntries()
    {
        if (marketableEntries is not null)
            return marketableEntries;

        var categorySheet = data.GetExcelSheet<ItemUICategory>();
        var result = new List<MarketCatalogEntry>();
        foreach (var row in data.GetExcelSheet<Item>())
        {
            if (row.RowId == 0 || row.ItemSearchCategory.RowId == 0)
                continue;

            var name = row.Name.ToString();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var categoryId = row.ItemUICategory.RowId;
            var categoryName = categorySheet.TryGetRow(categoryId, out var category)
                ? category.Name.ToString()
                : "Uncategorized";

            result.Add(new MarketCatalogEntry(
                Get(row.RowId),
                categoryId,
                categoryName,
                row.EquipSlotCategory.RowId > 0));
        }

        marketableEntries = result
            .OrderBy(x => x.Item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return marketableEntries;
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
        catch (Exception ex)
        {
            // Vendor-price enrichment is useful but must never prevent the core market plugin
            // from loading after a game-data/schema change. An empty index simply disables the
            // vendor-purchase arbitrage signal until the sheet path is updated.
            log.Warning(ex, "Could not build the normal-gil vendor item index; vendor-purchase comparisons are disabled.");
        }

        gilShopItems = result;
    }
}
