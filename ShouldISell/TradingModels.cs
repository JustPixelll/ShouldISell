namespace ShouldISell;

public sealed record ItemCategoryInfo(uint CategoryId, string Name);

public sealed record MarketCatalogEntry(
    ItemInfo Item,
    uint UiCategoryId,
    string UiCategoryName,
    bool IsEquipment);

public enum BuyOpportunityKind
{
    MarketFlip,
    UndercutSweep,
    SplitStack,
    ConsolidateStack,
    VendorToMarket,
    MarketToVendor,
}

public sealed record BuyAcquisitionLot(
    ulong ListingId,
    uint Quantity,
    uint UnitPrice,
    uint BuyerTax,
    long TotalCost);

public sealed record BuyOpportunity(
    uint WorldId,
    ItemInfo Item,
    bool IsHq,
    BuyOpportunityKind Kind,
    string StrategyLabel,
    int Stars,
    double OpportunityScore,
    double Confidence,
    int ExistingQuantity,
    int AcquireQuantity,
    long AcquisitionCost,
    double AverageAcquisitionUnitCost,
    uint? SuggestedExitUnitPrice,
    uint? NetExitUnitPrice,
    int SuggestedExitStackSize,
    int SuggestedExitListingCount,
    double PotentialProfit,
    double RiskAdjustedProfit,
    double Roi,
    double? EstimatedFirstSaleDays,
    double? EstimatedLiquidationDays,
    uint? MaximumRecommendedBuyPrice,
    double UnitsPerDay,
    int SalesSampleCount,
    DateTimeOffset? MarketFreshnessUtc,
    IReadOnlyList<BuyAcquisitionLot> AcquisitionLots,
    IReadOnlyList<string> Notes,
    DateTimeOffset AnalysedAtUtc);

public sealed record PurchasePredictionContext(
    BuyOpportunityKind Kind,
    string StrategyLabel,
    double OpportunityScore,
    uint? SuggestedExitUnitPrice,
    double? EstimatedLiquidationDays,
    double PotentialPackageProfit,
    DateTimeOffset AnalysedAtUtc);

public sealed record PersonalPurchase(
    ulong CharacterContentId,
    uint WorldId,
    uint ItemId,
    bool IsHq,
    int Quantity,
    uint UnitPrice,
    uint BuyerTax,
    long TotalCost,
    ulong ListingId,
    DateTimeOffset PurchasedAtUtc,
    string Strategy,
    double? OpportunityScore,
    uint? PredictedExitUnitPrice,
    double? PredictedLiquidationDays,
    double? PredictedPackageProfit,
    DateTimeOffset? PredictionObservedAtUtc);

public sealed record ClosedTrade(
    uint ItemId,
    bool IsHq,
    string ItemName,
    int Quantity,
    double CostBasis,
    double NetRevenue,
    double Profit,
    double Roi,
    double HoldingDays,
    string Strategy,
    DateTimeOffset SoldAtUtc,
    double? PredictedExitUnitPrice,
    double? PredictedLiquidationDays);

public sealed record OpenTraderPosition(
    uint ItemId,
    bool IsHq,
    string ItemName,
    int Quantity,
    double CostBasis,
    double AverageCost,
    int ListedQuantity,
    uint? SuggestedExitUnitPrice,
    double? EstimatedNetMarketValue,
    double? UnrealizedProfit,
    string PrimaryStrategy,
    DateTimeOffset OldestPurchaseUtc);

public sealed record TraderItemPerformance(
    uint ItemId,
    bool IsHq,
    string ItemName,
    int ClosedUnits,
    double CostBasis,
    double NetRevenue,
    double Profit,
    double Roi,
    double AverageHoldingDays);

public sealed record TraderStrategyPerformance(
    string Strategy,
    int ClosedUnits,
    int SaleEvents,
    double CostBasis,
    double NetRevenue,
    double Profit,
    double Roi,
    double AverageHoldingDays);

public sealed record TraderSnapshot(
    string ProfileName,
    string ProfileDescription,
    int PurchaseCount,
    int TrackedSaleCount,
    int ClosedUnits,
    int OpenUnits,
    double CapitalInvested,
    double RealizedRevenue,
    double RealizedProfit,
    double RealizedRoi,
    double WinRate,
    double MedianHoldingDays,
    double OpenCostBasis,
    double? OpenEstimatedNetValue,
    double? UnrealizedProfit,
    int UnmatchedSaleUnits,
    double? MeanAbsoluteExitPriceError,
    double? MeanAbsoluteHoldingTimeError,
    IReadOnlyList<ClosedTrade> RecentClosedTrades,
    IReadOnlyList<OpenTraderPosition> OpenPositions,
    IReadOnlyList<TraderItemPerformance> TopItems,
    IReadOnlyList<TraderStrategyPerformance> Strategies,
    DateTimeOffset CalculatedAtUtc);
