using FFXIVClientStructs.FFXIV.Client.Game;

namespace ShouldISell;

public enum InventoryOwnerKind
{
    Player,
    Retainer,
}

public sealed record OwnedStack(
    ulong CharacterContentId,
    InventoryOwnerKind OwnerKind,
    ulong OwnerId,
    string OwnerName,
    InventoryType Container,
    ushort Slot,
    uint ItemId,
    int Quantity,
    bool IsHq,
    DateTimeOffset ObservedAtUtc);

public sealed record InventoryContainerSnapshot(
    ulong CharacterContentId,
    InventoryOwnerKind OwnerKind,
    ulong OwnerId,
    string OwnerName,
    InventoryType Container,
    DateTimeOffset ObservedAtUtc,
    List<OwnedStack> Items);

public sealed record OwnMarketListing(
    ulong CharacterContentId,
    ulong RetainerId,
    string RetainerName,
    short MarketSlot,
    uint ItemId,
    int Quantity,
    bool IsHq,
    uint UnitPrice,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset PriceChangedUtc,
    DateTimeOffset LastSeenUtc);

public enum PersonalSaleSource
{
    History,
    Announcement,
    Reconciled,
}

public sealed record PersonalSale(
    ulong CharacterContentId,
    ulong RetainerId,
    string RetainerName,
    uint ItemId,
    int Quantity,
    bool IsHq,
    long NetGil,
    DateTimeOffset SoldAtUtc,
    string BuyerName,
    DateTimeOffset CapturedAtUtc,
    PersonalSaleSource Source = PersonalSaleSource.History,
    bool NetGilEstimated = false,
    bool QuantityEstimated = false);

public sealed record ItemInfo(
    uint ItemId,
    string Name,
    bool IsMarketable,
    bool CanBeHq,
    uint VendorBuybackPrice,
    uint? VendorGilShopPrice,
    uint StackSize,
    uint IconId);

public sealed record MarketListing(
    uint ItemId,
    uint PricePerUnit,
    uint Quantity,
    bool IsHq,
    ulong ListingId,
    ulong RetainerId,
    string RetainerName,
    DateTimeOffset ObservedAtUtc,
    MarketDataSource Source);

public sealed record MarketSale(
    uint ItemId,
    uint PricePerUnit,
    uint Quantity,
    bool IsHq,
    DateTimeOffset SoldAtUtc,
    MarketDataSource Source);

public enum MarketDataSource
{
    Unknown,
    Universalis,
    LiveGame,
}

public sealed class MarketSnapshot
{
    public uint WorldId { get; set; }
    public uint ItemId { get; set; }
    public DateTimeOffset? ListingObservedAtUtc { get; set; }
    public DateTimeOffset? HistoryObservedAtUtc { get; set; }
    public DateTimeOffset? UniversalisLastUploadUtc { get; set; }
    public MarketDataSource CurrentSource { get; set; } = MarketDataSource.Unknown;
    public List<MarketListing> Listings { get; set; } = new();
    public List<MarketSale> Sales { get; set; } = new();
}

public sealed record ScoreBreakdown(
    double PriceAttractiveness,
    double Demand,
    double Supply,
    double Liquidity,
    double Stability,
    double Trend,
    double AbsoluteValue,
    double VendorEconomics);


public sealed record StackCandidateScore(
    int StackSize,
    uint? SuggestedUnitPrice,
    int ListingCount,
    double Utility,
    double DemandFit,
    double ConveniencePremium,
    double Affordability,
    double SpeedFit,
    double FragmentationPenalty);

public sealed record StackRecommendation(
    int RecommendedStackSize,
    int RecommendedListingCount,
    uint? RecommendedUnitPrice,
    int LowMaintenanceStackSize,
    int LowMaintenanceListingCount,
    uint? LowMaintenanceUnitPrice,
    double ConveniencePremium,
    double TypicalBuyerSpend,
    double Confidence,
    string Reason,
    string LowMaintenanceReason,
    IReadOnlyList<StackCandidateScore> TopCandidates);

public sealed record SellRating(
    uint ItemId,
    bool IsHq,
    double RawScore,
    double OpportunityScore,
    int Stars,
    string Label,
    double Confidence,
    string ConfidenceLabel,
    uint? RealisticCurrentPrice,
    uint? SuggestedPrice,
    string SuggestedPriceReason,
    double SuggestedPriceConfidence,
    uint? NetSuggestedPriceAfterTax,
    uint VendorBuybackPrice,
    uint? VendorGilShopPrice,
    double? VendorFloorMargin,
    double? VendorArbitrageMargin,
    string VendorEconomicsReason,
    StackRecommendation? StackRecommendation,
    double? HistoricalMedian,
    double? LowerQuartile,
    double? UpperQuartile,
    double UnitsPerDay,
    double TransactionsPerDay,
    double? DaysOfSupply,
    double? EstimatedQueueDays,
    double? SevenDayMedian,
    double? ThirtyDayMedian,
    DateTimeOffset? ListingFreshnessUtc,
    DateTimeOffset? LastSaleUtc,
    int SalesSampleCount,
    ScoreBreakdown Breakdown,
    IReadOnlyList<string> Notes);

public sealed record OwnedLocationSummary(
    InventoryOwnerKind OwnerKind,
    ulong OwnerId,
    string OwnerName,
    int Quantity);

public sealed record RatedOwnedItem(
    ItemInfo Item,
    bool IsHq,
    int Quantity,
    IReadOnlyList<string> Locations,
    IReadOnlyList<OwnedLocationSummary> Ownership,
    SellRating? Rating,
    DateTimeOffset? InventoryObservedAtUtc);

public sealed record RatedOwnListing(
    OwnMarketListing Listing,
    ItemInfo Item,
    SellRating? Rating,
    int TotalOwnedQuantity);

public enum RefreshState
{
    Idle,
    WaitingToRequest,
    WaitingForPackets,
    Cooldown,
    Completed,
    Stopped,
}

public sealed record RefreshQueueEntry(
    uint ItemId,
    string ItemName,
    DateTimeOffset? LastUploadUtc,
    int Attempts = 0);

