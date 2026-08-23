namespace ShouldISell.Services;

public sealed record BuyPortfolioPlan(
    int BudgetGil,
    int MaxPositions,
    long InvestedGil,
    long ReserveGil,
    double PotentialProfit,
    double RiskAdjustedProfit,
    double WeightedOpportunityScore,
    IReadOnlyList<BuyOpportunity> Selections,
    DateTimeOffset CalculatedAtUtc);

/// <summary>
/// Builds a capital-allocation plan from the scanner's executable opportunity packages.
/// This is a multiple-choice knapsack: competing strategies for the same item/HQ variant are
/// alternatives, never simultaneous positions, and the objective is total risk-adjusted profit
/// under both the user's gil budget and explicit basket-size cap.
/// </summary>
public static class PortfolioAllocator
{
    private const int MaxBudgetBuckets = 3000;
    private const int MaxSupportedPositions = 20;

    public static BuyPortfolioPlan Build(
        IReadOnlyList<BuyOpportunity> opportunities,
        int budgetGil,
        int maxPositions)
    {
        var budget = Math.Max(0, budgetGil);
        var positionLimit = Math.Clamp(maxPositions, 1, MaxSupportedPositions);
        if (budget <= 0 || opportunities.Count == 0)
            return Empty(budget, positionLimit);

        var candidates = opportunities
            .Where(x => x.AcquisitionCost > 0 && x.AcquisitionCost <= budget && x.RiskAdjustedProfit > 0)
            .OrderByDescending(x => x.RiskAdjustedProfit)
            .ThenByDescending(x => x.OpportunityScore)
            .Take(500)
            .ToList();
        if (candidates.Count == 0)
            return Empty(budget, positionLimit);

        // Each group represents one economic exposure. A market flip and vendor strategy for the
        // same item/HQ are alternatives rather than additive positions because their exit models
        // would otherwise compete with each other and double-count the same demand.
        var groups = candidates
            .GroupBy(x => (x.Item.ItemId, x.IsHq))
            .Select(g => g
                .OrderByDescending(x => x.RiskAdjustedProfit)
                .ThenByDescending(x => x.OpportunityScore)
                .Take(6)
                .ToList())
            .OrderByDescending(g => g.Max(x => x.RiskAdjustedProfit))
            .ToList();

        // Scale gil into a bounded number of budget buckets. Candidate costs round UP, so the
        // discrete optimization can be slightly conservative but can never construct an actually
        // over-budget portfolio due to rounding.
        var bucketSize = Math.Max(1L, (long)Math.Ceiling(budget / (double)MaxBudgetBuckets));
        var capacity = (int)Math.Min(MaxBudgetBuckets, budget / bucketSize);
        if (capacity <= 0)
            return Empty(budget, positionLimit);

        var capacitiesPerCount = capacity + 1;
        var stateCount = (positionLimit + 1) * capacitiesPerCount;
        var previous = Enumerable.Repeat(double.NegativeInfinity, stateCount).ToArray();
        var next = Enumerable.Repeat(double.NegativeInfinity, stateCount).ToArray();
        previous[StateIndex(0, 0, capacitiesPerCount)] = 0;

        // One signed byte is enough because each exposure group keeps at most six alternatives.
        // -2 means unreachable, -1 means this group was skipped, 0..5 identifies the chosen package.
        var decisions = new sbyte[checked(groups.Count * stateCount)];
        Array.Fill(decisions, (sbyte)-2);

        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            Array.Fill(next, double.NegativeInfinity);
            var alternatives = groups[groupIndex];

            for (var count = 0; count <= positionLimit; count++)
            {
                for (var cap = 0; cap <= capacity; cap++)
                {
                    var sourceIndex = StateIndex(count, cap, capacitiesPerCount);
                    var sourceObjective = previous[sourceIndex];
                    if (double.IsNegativeInfinity(sourceObjective))
                        continue;

                    // Choose nothing from this exposure group.
                    if (sourceObjective > next[sourceIndex])
                    {
                        next[sourceIndex] = sourceObjective;
                        decisions[DecisionIndex(groupIndex, sourceIndex, stateCount)] = -1;
                    }

                    if (count >= positionLimit)
                        continue;

                    for (var alternativeIndex = 0; alternativeIndex < alternatives.Count; alternativeIndex++)
                    {
                        var candidate = alternatives[alternativeIndex];
                        var costBuckets = (int)Math.Ceiling(candidate.AcquisitionCost / (double)bucketSize);
                        var targetCap = cap + costBuckets;
                        if (costBuckets <= 0 || targetCap > capacity)
                            continue;

                        var targetCount = count + 1;
                        var targetIndex = StateIndex(targetCount, targetCap, capacitiesPerCount);
                        var qualityTieBreak = candidate.RiskAdjustedProfit * candidate.OpportunityScore * 1e-9;
                        var objective = sourceObjective + candidate.RiskAdjustedProfit + qualityTieBreak;
                        if (objective <= next[targetIndex])
                            continue;

                        next[targetIndex] = objective;
                        decisions[DecisionIndex(groupIndex, targetIndex, stateCount)] = (sbyte)alternativeIndex;
                    }
                }
            }

            (previous, next) = (next, previous);
        }

        var bestObjective = double.NegativeInfinity;
        var bestCount = 0;
        var bestCapacity = 0;
        for (var count = 0; count <= positionLimit; count++)
        {
            for (var cap = 0; cap <= capacity; cap++)
            {
                var objective = previous[StateIndex(count, cap, capacitiesPerCount)];
                if (objective <= bestObjective)
                    continue;
                bestObjective = objective;
                bestCount = count;
                bestCapacity = cap;
            }
        }

        var selected = new List<BuyOpportunity>();
        var cursorCount = bestCount;
        var cursorCapacity = bestCapacity;
        for (var groupIndex = groups.Count - 1; groupIndex >= 0; groupIndex--)
        {
            var stateIndex = StateIndex(cursorCount, cursorCapacity, capacitiesPerCount);
            var selectedAlternative = decisions[DecisionIndex(groupIndex, stateIndex, stateCount)];
            if (selectedAlternative < 0)
                continue;

            var candidate = groups[groupIndex][selectedAlternative];
            selected.Add(candidate);
            cursorCapacity -= (int)Math.Ceiling(candidate.AcquisitionCost / (double)bucketSize);
            cursorCount--;
        }

        selected = selected
            .OrderByDescending(x => x.OpportunityScore)
            .ThenByDescending(x => x.RiskAdjustedProfit)
            .ToList();

        var invested = selected.Sum(x => x.AcquisitionCost);
        if (invested > budget || selected.Count > positionLimit)
        {
            // Defensive fallback for future changes to the rounding/reconstruction model.
            selected = selected
                .OrderByDescending(x => x.RiskAdjustedProfit)
                .ThenByDescending(x => x.OpportunityScore)
                .Take(positionLimit)
                .ToList();
            while (selected.Sum(x => x.AcquisitionCost) > budget && selected.Count > 0)
                selected.RemoveAt(selected.Count - 1);
            invested = selected.Sum(x => x.AcquisitionCost);
        }

        var potential = selected.Sum(x => x.PotentialProfit);
        var riskAdjusted = selected.Sum(x => x.RiskAdjustedProfit);
        var scoreWeight = selected.Sum(x => Math.Max(1.0, x.AcquisitionCost));
        var weightedScore = scoreWeight <= 0
            ? 0
            : selected.Sum(x => x.OpportunityScore * Math.Max(1.0, x.AcquisitionCost)) / scoreWeight;

        return new BuyPortfolioPlan(
            budget,
            positionLimit,
            invested,
            Math.Max(0, (long)budget - invested),
            potential,
            riskAdjusted,
            weightedScore,
            selected,
            DateTimeOffset.UtcNow);
    }

    private static int StateIndex(int count, int capacity, int capacitiesPerCount)
        => count * capacitiesPerCount + capacity;

    private static int DecisionIndex(int groupIndex, int stateIndex, int stateCount)
        => checked(groupIndex * stateCount + stateIndex);

    private static BuyPortfolioPlan Empty(int budget, int maxPositions)
        => new(
            budget,
            maxPositions,
            0,
            budget,
            0,
            0,
            0,
            Array.Empty<BuyOpportunity>(),
            DateTimeOffset.UtcNow);
}
