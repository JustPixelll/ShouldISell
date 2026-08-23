$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Get-Content $Path -Raw
    if (-not $text.Contains($Old)) { throw "Expected patch text not found in $Path`n--- OLD ---`n$Old" }
    $text = $text.Replace($Old, $New)
    Set-Content $Path $text -Encoding UTF8
}

$score = 'ShouldISell/Services/ScoreCalculator.cs'

# Final shared execution rule for v1.1.5: FFXIV Market Board listings contain at most 99 units.
Replace-Exact $score @'
        var stackLimit = (int)Math.Clamp(itemStackSize == 0 ? 999u : itemStackSize, 1u, (uint)int.MaxValue);
'@ @'
        var stackLimit = Math.Min(
            MarketBoardRules.MaxListingQuantity,
            (int)Math.Clamp(itemStackSize == 0 ? (uint)MarketBoardRules.MaxListingQuantity : itemStackSize, 1u, (uint)int.MaxValue));
'@

Replace-Exact $score @'
        var owned = Math.Max(1, quantityForSale);
        if (recommendation is { RecommendedStackSize: > 0 })
            return Math.Clamp(recommendation.RecommendedStackSize, 1, owned);

        var stackLimit = itemStackSize == 0
            ? owned
            : (int)Math.Min((uint)owned, itemStackSize);
        return Math.Max(1, stackLimit);
'@ @'
        var owned = Math.Max(1, quantityForSale);
        var maxListable = Math.Min(owned, MarketBoardRules.MaxListingQuantity);
        if (recommendation is { RecommendedStackSize: > 0 })
            return Math.Clamp(recommendation.RecommendedStackSize, 1, maxListable);

        var stackLimit = itemStackSize == 0
            ? maxListable
            : (int)Math.Min((uint)maxListable, itemStackSize);
        return Math.Max(1, Math.Min(stackLimit, MarketBoardRules.MaxListingQuantity));
'@

Write-Host 'v1.1.5 shared 99-unit Market Board rule applied.'
