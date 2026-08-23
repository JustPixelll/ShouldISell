namespace ShouldISell;

public enum BuyStrategy
{
    MarketSweep,
    SplitStack,
    ConsolidateStack,
    VendorToMarket,
    MarketToVendor,
}

public sealed record AggregatedVariant(
    uint MinListingPrice,
    uint MedianListingPrice,
    double AverageSalePrice,
    double DailySaleVelocity,
    uint RecentPurchasePrice,
    DateTimeOffset? RecentPurchaseUtc);

public sealed record AggregatedMarketItem(
    uint ItemId,
    AggregatedVariant Nq,
    AggregatedVariant Hq,
    DateTimeOffset? FreshestWorldUploadUtc);

public sealed record BuyListingLot(
    ulong ListingId,
    uint UnitPrice,
    int Quantity,
    long EstimatedTax,
    long TotalAcquisitionCost);

public sealed record BuyScoreBreakdown(
    double Roi,
    double Profit,
    double Liquidity,
    double PriceAdvantage,
    double Demand,
    double Stability,
    double Confidence,
    double Execution);

public sealed record BuyOpportunity(
    ItemInfo Item,
    bool IsHq,
    BuyStrategy Strategy,
    int Stars,
    double Score,
    double Confidence,
    int BuyQuantity,
    int ExistingQuantity,
    int PositionAfterBuy,
    long AcquisitionCost,
    double AverageBuyUnitPrice,
    uint? SuggestedSellUnitPrice,
    int SuggestedSellStackSize,
    long PotentialProceeds,
    long PotentialProfit,
    long RiskAdjustedProfit,
    double Roi,
    double? EstimatedFirstSaleDays,
    double? EstimatedLiquidationDays,
    uint? MaximumAcceptableBuyPrice,
    double DailyVolume,
    bool GuaranteedExit,
    DateTimeOffset? MarketFreshnessUtc,
    BuyScoreBreakdown Breakdown,
    IReadOnlyList<BuyListingLot> Lots,
    IReadOnlyList<string> Notes);

public sealed record BuyPortfolioLine(
    BuyOpportunity Opportunity,
    long AllocatedGil,
    long PotentialProfit,
    long RiskAdjustedProfit);

public sealed record BuyPortfolio(
    long Budget,
    long Invested,
    long Reserve,
    long PotentialProfit,
    long RiskAdjustedProfit,
    IReadOnlyList<BuyPortfolioLine> Lines);

public sealed record PersonalPurchase(
    Guid PurchaseId,
    ulong CharacterContentId,
    uint WorldId,
    uint ItemId,
    bool IsHq,
    int Quantity,
    uint UnitPrice,
    long TaxPaid,
    long TotalCost,
    ulong ListingId,
    ulong RetainerId,
    int RetainerCityId,
    DateTimeOffset PurchasedAtUtc,
    BuyStrategy? MatchedStrategy,
    uint? PredictedSellUnitPrice,
    double? PredictedLiquidationDays,
    long? PredictedProfit,
    double? PredictedRoi);

public sealed record ClosedTradeAllocation(
    Guid PurchaseId,
    uint ItemId,
    bool IsHq,
    BuyStrategy? Strategy,
    int Quantity,
    long AllocatedCost,
    long AllocatedNetRevenue,
    long RealizedProfit,
    double Roi,
    double HoldingDays,
    double? PredictedLiquidationDays,
    double? PredictedNetUnitPrice,
    double ActualNetUnitPrice);

public sealed record OpenTradePosition(
    uint ItemId,
    bool IsHq,
    string ItemName,
    int Quantity,
    long RemainingCostBasis,
    double AverageCostPerUnit,
    BuyStrategy? DominantStrategy,
    DateTimeOffset OldestPurchaseUtc);

public sealed record TraderStrategyStats(
    BuyStrategy Strategy,
    int MatchedUnits,
    long RealizedProfit,
    double Roi,
    double AverageHoldingDays,
    double WinRate);

public sealed record TraderProfile(
    int PurchaseTransactions,
    long TotalCapitalDeployed,
    int MatchedSaleTransactions,
    int MatchedUnitsSold,
    long MatchedRevenue,
    long RealizedProfit,
    double RealizedRoi,
    double WinRate,
    double AverageHoldingDays,
    long OpenCostBasis,
    int OpenUnits,
    double SaleCoverage,
    double? AverageSellTimePredictionErrorDays,
    double? AverageExitPricePredictionErrorPercent,
    BuyStrategy? BestStrategy,
    IReadOnlyList<TraderStrategyStats> Strategies,
    IReadOnlyList<OpenTradePosition> OpenPositions,
    IReadOnlyList<ClosedTradeAllocation> ClosedAllocations);
