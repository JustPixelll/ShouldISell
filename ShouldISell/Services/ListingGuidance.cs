namespace ShouldISell.Services;

/// <summary>
/// Shared current-listing guidance used by both the main table and the small
/// in-game retainer-list attention overlay.
/// </summary>
public static class ListingGuidance
{
    public static string PriceChangeText(RatedOwnListing row)
        => PriceChangeText(row.Listing.UnitPrice, row.Rating?.SuggestedPrice);

    public static bool NeedsPriceChange(RatedOwnListing row)
    {
        var change = PriceChangeText(row);
        return change != "Keep" && change != "—";
    }

    public static bool NeedsStackChange(RatedOwnListing row)
        => row.Rating?.StackRecommendation is { RecommendedStackSize: > 0 } stack &&
           row.Listing.Quantity != stack.RecommendedStackSize;

    public static bool NeedsAttention(RatedOwnListing row)
        => NeedsPriceChange(row) || NeedsStackChange(row);

    public static string PriceChangeText(uint current, uint? suggested)
    {
        if (suggested is null || current == 0)
            return "—";

        var delta = (long)suggested.Value - current;
        var ratio = delta / (double)current;
        if (Math.Abs(ratio) <= 0.02 || Math.Abs(delta) <= 1)
            return "Keep";

        return delta < 0
            ? $"↓ {Math.Abs(delta):N0}g ({ratio:P1})"
            : $"↑ {delta:N0}g (+{ratio:P1})";
    }
}
