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

public enum PurchaseSourceKind
{
    MarketBoard,
    VendorManual,
}

public sealed record BuyAcquisitionLot(
    ulong ListingId,
    uint Quantity,
    uint UnitPrice,
    uint BuyerTax,
    long TotalCost);

/// <summary>
/// One modeled acquisition/exit package. Vendor -> Market packages receive an additional
/// working-inventory guard here so renewable NPC stock cannot turn a permissive discovery horizon
/// into a multi-month or multi-year stockpile recommendation.
/// </summary>
public sealed record BuyOpportunity
{
    public BuyOpportunity(
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
        DateTimeOffset AnalysedAtUtc)
    {
        this.WorldId = WorldId;
        this.Item = Item;
        this.IsHq = IsHq;
        this.Kind = Kind;
        this.StrategyLabel = StrategyLabel;
        this.Confidence = Confidence;
        this.ExistingQuantity = ExistingQuantity;
        this.AverageAcquisitionUnitCost = AverageAcquisitionUnitCost;
        this.SuggestedExitUnitPrice = SuggestedExitUnitPrice;
        this.NetExitUnitPrice = NetExitUnitPrice;
        this.SuggestedExitStackSize = SuggestedExitStackSize;
        this.Roi = Roi;
        this.EstimatedFirstSaleDays = EstimatedFirstSaleDays;
        this.MaximumRecommendedBuyPrice = MaximumRecommendedBuyPrice;
        this.UnitsPerDay = UnitsPerDay;
        this.SalesSampleCount = SalesSampleCount;
        this.MarketFreshnessUtc = MarketFreshnessUtc;
        this.AcquisitionLots = AcquisitionLots;
        this.AnalysedAtUtc = AnalysedAtUtc;

        if (Kind != BuyOpportunityKind.VendorToMarket || AcquireQuantity <= 0)
        {
            this.Stars = Stars;
            this.OpportunityScore = OpportunityScore;
            this.AcquireQuantity = AcquireQuantity;
            this.AcquisitionCost = AcquisitionCost;
            this.SuggestedExitListingCount = SuggestedExitListingCount;
            this.PotentialProfit = PotentialProfit;
            this.RiskAdjustedProfit = RiskAdjustedProfit;
            this.EstimatedLiquidationDays = EstimatedLiquidationDays;
            this.Notes = Notes;
            return;
        }

        ApplyVendorWorkingInventoryPolicy(
            Stars,
            OpportunityScore,
            AcquireQuantity,
            AcquisitionCost,
            SuggestedExitListingCount,
            PotentialProfit,
            RiskAdjustedProfit,
            EstimatedLiquidationDays,
            Notes);
    }

    public uint WorldId { get; init; }
    public ItemInfo Item { get; init; }
    public bool IsHq { get; init; }
    public BuyOpportunityKind Kind { get; init; }
    public string StrategyLabel { get; init; }
    public int Stars { get; set; }
    public double OpportunityScore { get; set; }
    public double Confidence { get; init; }
    public int ExistingQuantity { get; init; }
    public int AcquireQuantity { get; set; }
    public long AcquisitionCost { get; set; }
    public double AverageAcquisitionUnitCost { get; init; }
    public uint? SuggestedExitUnitPrice { get; init; }
    public uint? NetExitUnitPrice { get; init; }
    public int SuggestedExitStackSize { get; init; }
    public int SuggestedExitListingCount { get; set; }
    public double PotentialProfit { get; set; }
    public double RiskAdjustedProfit { get; set; }
    public double Roi { get; init; }
    public double? EstimatedFirstSaleDays { get; init; }
    public double? EstimatedLiquidationDays { get; set; }
    public uint? MaximumRecommendedBuyPrice { get; init; }
    public double UnitsPerDay { get; init; }
    public int SalesSampleCount { get; init; }
    public DateTimeOffset? MarketFreshnessUtc { get; init; }
    public IReadOnlyList<BuyAcquisitionLot> AcquisitionLots { get; init; }
    public IReadOnlyList<string> Notes { get; set; } = Array.Empty<string>();
    public DateTimeOffset AnalysedAtUtc { get; init; }

    private void ApplyVendorWorkingInventoryPolicy(
        int originalStars,
        double originalScore,
        int originalQuantity,
        long originalCost,
        int originalExitListings,
        double originalProfit,
        double originalRiskAdjustedProfit,
        double? originalLiquidationDays,
        IReadOnlyList<string> originalNotes)
    {
        // Absolute profit buys patience, but only up to two months. A small convenience flip should
        // turn in roughly a week; a genuinely valuable position can justify progressively more time.
        var allowedHoldingDays = Math.Clamp(
            7.0 + 14.0 * Math.Max(0, Math.Log10(1 + Math.Max(0, originalProfit) / 5_000.0)),
            7.0,
            60.0);

        var queueDays = Math.Max(0, EstimatedFirstSaleDays ?? 0);
        var stackSize = Math.Max(1, SuggestedExitStackSize);
        var maxWorkingListings = originalProfit switch
        {
            < 25_000 => 1,
            < 100_000 => 2,
            < 500_000 => 3,
            < 2_000_000 => 4,
            _ => 5,
        };

        var maxByListings = checked(stackSize * maxWorkingListings);
        var maxByTime = 0;
        if (UnitsPerDay > 0.01 && allowedHoldingDays > queueDays)
        {
            var maxResultingPosition = (int)Math.Floor((allowedHoldingDays - queueDays) * UnitsPerDay);
            maxByTime = Math.Max(0, maxResultingPosition - Math.Max(0, ExistingQuantity));
        }

        var adjustedQuantity = Math.Min(originalQuantity, Math.Min(maxByListings, maxByTime));
        if (adjustedQuantity <= 0)
        {
            // The normal findings filter excludes negative-profit rows, so an opportunity where even
            // one additional vendor unit cannot meet the working-inventory horizon never becomes a
            // user-facing recommendation.
            Stars = 1;
            OpportunityScore = 0;
            AcquireQuantity = 0;
            AcquisitionCost = 0;
            SuggestedExitListingCount = originalExitListings;
            PotentialProfit = -1;
            RiskAdjustedProfit = -1;
            EstimatedLiquidationDays = double.PositiveInfinity;
            Notes = CleanVendorQuantityNotes(originalNotes)
                .Append($"Not recommended: even one additional unit cannot liquidate inside the {allowedHoldingDays:0.#}-day working-inventory window supported by this projected profit.")
                .ToList();
            return;
        }

        var ratio = adjustedQuantity / (double)originalQuantity;
        var adjustedCost = (long)Math.Round(AverageAcquisitionUnitCost * adjustedQuantity, MidpointRounding.AwayFromZero);
        var adjustedProfit = originalProfit * ratio;
        var adjustedRiskProfit = originalRiskAdjustedProfit * ratio;
        var adjustedLiquidation = UnitsPerDay > 0.01
            ? queueDays + (Math.Max(0, ExistingQuantity) + adjustedQuantity) / UnitsPerDay
            : originalLiquidationDays;
        var adjustedExitListings = (Math.Max(0, ExistingQuantity) + adjustedQuantity + stackSize - 1) / stackSize;

        // The original score contains package-profit magnitude. If the safety policy sharply reduces
        // the package, trim that score as well instead of letting a former 99-stack keep its rank.
        var scoreScale = 0.70 + 0.30 * Math.Sqrt(ratio);
        var adjustedScore = originalScore * scoreScale;

        Stars = StarsForScore(adjustedScore);
        OpportunityScore = adjustedScore;
        AcquireQuantity = adjustedQuantity;
        AcquisitionCost = adjustedCost;
        SuggestedExitListingCount = Math.Max(1, adjustedExitListings);
        PotentialProfit = adjustedProfit;
        RiskAdjustedProfit = adjustedRiskProfit;
        EstimatedLiquidationDays = adjustedLiquidation;

        var notes = CleanVendorQuantityNotes(originalNotes);
        if (adjustedQuantity < originalQuantity)
        {
            notes.Add(
                $"Working-inventory policy reduced the vendor purchase from {originalQuantity:N0} to {adjustedQuantity:N0} unit(s). " +
                $"Projected profit supports up to {allowedHoldingDays:0.#} holding days; larger profits may justify more time, but vendor recommendations never target more than 60 days.");
        }
        else
        {
            notes.Add(
                $"Vendor working-inventory check passed: the package fits its {allowedHoldingDays:0.#}-day profit-adjusted holding window (hard maximum 60 days).");
        }
        Notes = notes;
    }

    private static List<string> CleanVendorQuantityNotes(IReadOnlyList<string> notes)
        => notes
            .Where(x => !x.StartsWith("Quantity is demand-capped", StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static int StarsForScore(double score) => score switch
    {
        >= 80 => 5,
        >= 65 => 4,
        >= 50 => 3,
        >= 35 => 2,
        _ => 1,
    };
}

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
    DateTimeOffset? PredictionObservedAtUtc,
    PurchaseSourceKind SourceKind = PurchaseSourceKind.MarketBoard);

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
    double RealizedCostBasis,
    double RealizedRevenue,
    double RealizedProfit,
    double RealizedReturnOnTrackedSpend,
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
