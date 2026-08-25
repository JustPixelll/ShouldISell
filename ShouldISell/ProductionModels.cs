namespace ShouldISell;

public enum ProductionAcquisitionRoute
{
    MarketBoard,
    Vendor,
    Craft,
    Unavailable,
}

public sealed record CraftIngredientDecision(
    ItemInfo Item,
    int QuantityRequired,
    int OwnedQuantity,
    ProductionAcquisitionRoute Route,
    double? MarketUnitCost,
    double EconomicUnitCost,
    double EconomicCost,
    double CashCost,
    string Reason);

public sealed record CraftOpportunity(
    uint WorldId,
    uint RecipeId,
    ItemInfo Item,
    uint CrafterClassJobId,
    string CrafterName,
    int RequiredLevel,
    int PlayerLevel,
    int ResultQuantity,
    bool CanQuickSynth,
    int Stars,
    double OpportunityScore,
    double Confidence,
    double GrossSaleValue,
    double NetSaleValue,
    double EconomicMaterialCost,
    double CashMaterialCost,
    double EconomicProfit,
    double CashProfit,
    double Roi,
    double UnitsPerDay,
    double? EstimatedLiquidationDays,
    double EstimatedActiveMinutes,
    double? EstimatedProfitPerActiveMinute,
    double? SalePriceVolatility,
    int SalesSampleCount,
    DateTimeOffset? LastSaleUtc,
    IReadOnlyList<CraftIngredientDecision> Ingredients,
    IReadOnlyList<string> Notes,
    DateTimeOffset AnalysedAtUtc);

public sealed record GatherOpportunity(
    uint WorldId,
    ItemInfo Item,
    uint GathererClassJobId,
    string GathererName,
    int RequiredLevel,
    int PlayerLevel,
    string GatheringType,
    IReadOnlyList<string> Locations,
    bool IsTimed,
    bool IsHidden,
    int Stars,
    double OpportunityScore,
    double Confidence,
    double RealisticUnitSalePrice,
    double UnitsPerDay,
    double EstimatedUnitsPerActiveMinute,
    double EstimatedGilPerActiveMinute,
    double? SalePriceVolatility,
    int SalesSampleCount,
    DateTimeOffset? LastSaleUtc,
    IReadOnlyList<string> Notes,
    DateTimeOffset AnalysedAtUtc);

public enum UnifiedOpportunityKind
{
    Buy,
    Craft,
    Gather,
    CraftAndGather,
}

public sealed record UnifiedOpportunity(
    UnifiedOpportunityKind Kind,
    uint ItemId,
    string ItemName,
    int Stars,
    double OpportunityScore,
    double Confidence,
    double? ExpectedProfit,
    double? Roi,
    double? GilPerActiveMinute,
    double? EstimatedLiquidationDays,
    string Action,
    string Why,
    DateTimeOffset AnalysedAtUtc);
