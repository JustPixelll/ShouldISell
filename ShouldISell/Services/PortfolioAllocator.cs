namespace ShouldISell.Services;

public sealed record BuyPortfolioPlan(
    int BudgetGil,
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
/// under the user's actual gil budget.
/// </summary>
public static class PortfolioAllocator
{
    private const int MaxBudgetBuckets = 3000;

    public static BuyPortfolioPlan Build(IReadOnlyList<BuyOpportunity> opportunities, int budgetGil)
    {
        var budget = Math.Max(0, budgetGil);
        if (budget <= 0 || opportunities.Count == 0)
            return Empty(budget);

        var candidates = opportunities
            .Where(x => x.AcquisitionCost > 0 && x.AcquisitionCost <= budget && x.RiskAdjustedProfit > 0)
            .OrderByDescending(x => x.RiskAdjustedProfit)
            .ThenByDescending(x => x.OpportunityScore)
            .Take(500)
            .ToList();
        if (candidates.Count == 0)
            return Empty(budget);

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
            return Empty(budget);

        var groupCount = groups.Count;
        var choice = new int[groupCount, capacity + 1];
        var previousCapacity = new int[groupCount, capacity + 1];
        var previous = Enumerable.Repeat(double.NegativeInfinity, capacity + 1).ToArray();
        previous[0] = 0;

        // Objective is risk-adjusted gil, with a very small score-quality tie breaker. The score
        // cannot overpower real gil value; it only breaks near-equal portfolios toward stronger
        // evidence/liquidity/execution quality.
        for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
        {
            var next = Enumerable.Repeat(double.NegativeInfinity, capacity + 1).ToArray();
            for (var cap = 0; cap <= capacity; cap++)
            {
                choice[groupIndex, cap] = -1;
                previousCapacity[groupIndex, cap] = cap;
            }

            for (var cap = 0; cap <= capacity; cap++)
            {
                if (double.IsNegativeInfinity(previous[cap]))
                    continue;

                // Choose nothing from this exposure group. If this carry-forward state beats a
                // candidate that reached the same bucket earlier, its reconstruction metadata must
                // also become "no selection" rather than retaining the stale candidate choice.
                if (previous[cap] > next[cap])
                {
                    next[cap] = previous[cap];
                    choice[groupIndex, cap] = -1;
                    previousCapacity[groupIndex, cap] = cap;
                }

                var alternatives = groups[groupIndex];
                for (var alternativeIndex = 0; alternativeIndex < alternatives.Count; alternativeIndex++)
                {
                    var candidate = alternatives[alternativeIndex];
                    var costBuckets = (int)Math.Ceiling(candidate.AcquisitionCost / (double)bucketSize);
                    if (costBuckets <= 0 || cap + costBuckets > capacity)
                        continue;

                    var qualityTieBreak = candidate.RiskAdjustedProfit * candidate.OpportunityScore * 1e-9;
                    var objective = previous[cap] + candidate.RiskAdjustedProfit + qualityTieBreak;
                    var target = cap + costBuckets;
                    if (objective <= next[target])
                        continue;

                    next[target] = objective;
                    choice[groupIndex, target] = alternativeIndex;
                    previousCapacity[groupIndex, target] = cap;
                }
            }
            previous = next;
        }

        var bestCapacity = 0;
        var bestObjective = double.NegativeInfinity;
        for (var cap = 0; cap <= capacity; cap++)
        {
            if (previous[cap] <= bestObjective)
                continue;
            bestObjective = previous[cap];
            bestCapacity = cap;
        }

        var selected = new List<BuyOpportunity>();
        var cursor = bestCapacity;
        for (var groupIndex = groupCount - 1; groupIndex >= 0; groupIndex--)
        {
            var selectedAlternative = choice[groupIndex, cursor];
            var prior = previousCapacity[groupIndex, cursor];
            if (selectedAlternative >= 0 && selectedAlternative < groups[groupIndex].Count)
                selected.Add(groups[groupIndex][selectedAlternative]);
            cursor = prior;
        }

        // The bucket model is conservative, but calculate actual totals and keep the result honest.
        selected = selected
            .OrderByDescending(x => x.OpportunityScore)
            .ThenByDescending(x => x.RiskAdjustedProfit)
            .ToList();
        var invested = selected.Sum(x => x.AcquisitionCost);
        if (invested > budget)
        {
            // Defensive fallback for any future changes to the rounding model.
            selected = selected
                .OrderByDescending(x => x.RiskAdjustedProfit / Math.Max(1, x.AcquisitionCost))
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
            invested,
            Math.Max(0, (long)budget - invested),
            potential,
            riskAdjusted,
            weightedScore,
            selected,
            DateTimeOffset.UtcNow);
    }

    private static BuyPortfolioPlan Empty(int budget)
        => new(
            budget,
            0,
            budget,
            0,
            0,
            0,
            Array.Empty<BuyOpportunity>(),
            DateTimeOffset.UtcNow);
}
