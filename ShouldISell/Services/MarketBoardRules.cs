namespace ShouldISell.Services;

/// <summary>
/// FFXIV Market Board execution constraints shared by Sell, Buy and Tycoon.
/// Inventory stacks can be much larger, but a single Market Board listing can contain at most 99 units.
/// </summary>
public static class MarketBoardRules
{
    public const int MaxListingQuantity = 99;

    public static int ClampListingQuantity(int desired, int available)
    {
        var owned = Math.Max(1, available);
        return Math.Clamp(desired, 1, Math.Min(owned, MaxListingQuantity));
    }

    public static int ListingCycles(int totalQuantity, int listingQuantity)
    {
        var total = Math.Max(1, totalQuantity);
        var stack = ClampListingQuantity(Math.Max(1, listingQuantity), total);
        return (total + stack - 1) / stack;
    }
}
