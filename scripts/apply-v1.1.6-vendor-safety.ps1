$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Get-Content $Path -Raw
    if (-not $text.Contains($Old)) { throw "Expected patch text not found in $Path`n--- OLD ---`n$Old" }
    $text = $text.Replace($Old, $New)
    Set-Content $Path $text -Encoding UTF8
}

$scanner = 'ShouldISell/Services/BuyOpportunityScanner.cs'
$buyUi = 'ShouldISell/Windows/SuiteWindow.Buy.cs'
$project = 'ShouldISell/ShouldISell.csproj'

Replace-Exact $scanner 'new ProductInfoHeaderValue("ShouldI", "1.1.3")' 'new ProductInfoHeaderValue("ShouldI", "1.1.6")'

Replace-Exact $scanner @'
                if (settings.EnableMarketToMarket)
                    TryAddBestMarketFlip(final, worldId, candidate, deep, existingQuantity, settings);
'@ @'
                if (settings.EnableMarketToMarket && !HasRenewableVendorSupply(candidate.Entry.Item, candidate.IsHq))
                    TryAddBestMarketFlip(final, worldId, candidate, deep, existingQuantity, settings);
'@

Replace-Exact $scanner @'
    private void TryAddBestMarketFlip(
        List<BuyOpportunity> output,
        uint worldId,
        RoughCandidate candidate,
        DeepMarketData deep,
        int existingQuantity,
        ScanSettings settings)
    {
        var variantListings = deep.Listings
'@ @'
    private static bool HasRenewableVendorSupply(ItemInfo item, bool isHq)
        => !isHq && item.VendorGilShopPrice is > 0;

    private void TryAddBestMarketFlip(
        List<BuyOpportunity> output,
        uint worldId,
        RoughCandidate candidate,
        DeepMarketData deep,
        int existingQuantity,
        ScanSettings settings)
    {
        // A normal-gil vendor is effectively renewable external supply. Buying out player listings
        // does not create durable scarcity because any player can immediately restock at the fixed
        // NPC price. Never model those NQ items as Market → Market buyouts/undercut sweeps.
        if (HasRenewableVendorSupply(candidate.Entry.Item, candidate.IsHq))
            return;

        var variantListings = deep.Listings
'@

Replace-Exact $scanner @'
        var netAverage = variant.AverageSalePrice * (1.0 - ScoreCalculator.MarketSellerTaxRate);
        var marketMargin = variant.MinListing > 0
            ? (netAverage - variant.MinListing) / variant.MinListing
            : 0;
        var marketSignal = settings.EnableMarketToMarket && variant.MinListing > 0 && variant.DailyVelocity > 0.001 &&
                           marketMargin >= settings.MinimumRoi * 0.60;
'@ @'
        var netAverage = variant.AverageSalePrice * (1.0 - ScoreCalculator.MarketSellerTaxRate);
        var marketMargin = variant.MinListing > 0
            ? (netAverage - variant.MinListing) / variant.MinListing
            : 0;
        var vendorContested = HasRenewableVendorSupply(item, isHq);
        var marketSignal = settings.EnableMarketToMarket && !vendorContested &&
                           variant.MinListing > 0 && variant.DailyVelocity > 0.001 &&
                           marketMargin >= settings.MinimumRoi * 0.60;
'@

Replace-Exact $scanner @'
        var bestMargin = Math.Max(0, Math.Max(marketMargin, double.IsFinite(vendorMarketMargin) ? vendorMarketMargin : 0));
'@ @'
        var scoredMarketMargin = marketSignal ? marketMargin : 0;
        var scoredVendorMarketMargin = vendorMarketSignal && double.IsFinite(vendorMarketMargin) ? vendorMarketMargin : 0;
        var bestMargin = Math.Max(0, Math.Max(scoredMarketMargin, scoredVendorMarketMargin));
'@

Replace-Exact $scanner @'
        var stackBound = (int)Math.Clamp(candidate.Entry.Item.StackSize == 0 ? 999u : candidate.Entry.Item.StackSize, 1u, int.MaxValue);
'@ @'
        var stackBound = Math.Min(
            MarketBoardRules.MaxListingQuantity,
            (int)Math.Clamp(candidate.Entry.Item.StackSize == 0 ? (uint)MarketBoardRules.MaxListingQuantity : candidate.Entry.Item.StackSize, 1u, int.MaxValue));
'@

Replace-Exact $scanner @'
            $"Normal gil vendor price is {vendorPrice.Value:N0}g/unit; this route does not depend on finding a cheap Market Board listing.",
            $"Quantity is demand-capped to about {settings.MaximumHoldingDays:0.#} day(s) of recent velocity rather than blindly buying a full stack.",
'@ @'
            $"Normal gil vendor price is {vendorPrice.Value:N0}g/unit; this route does not depend on finding a cheap Market Board listing.",
            "Normal-gil vendor supply is renewable for every player, so this strategy never assumes that buying out competing Market Board listings will create durable scarcity.",
            $"Quantity is demand-capped and hard-capped to one listable working stack (maximum 99 units) rather than stockpiling renewable vendor supply.",
'@

Replace-Exact $buyUi 'Tooltip("Buy one or more Market Board listings and resell the acquired units on the Market Board using the shared Should I Sell? exit model.");' 'Tooltip("Buy one or more Market Board listings and resell them using the shared Should I Sell? exit model. NQ items sold by a normal gil vendor are deliberately excluded: their supply is renewable, so buying out cheap player listings cannot be assumed to create scarcity.");'

Replace-Exact $buyUi 'Tooltip("Buy from a verified normal gil NPC vendor and resell on the Market Board. Quantity is capped by recent demand rather than blindly recommending a full stack.");' 'Tooltip("Buy from a verified normal gil NPC vendor and resell on the Market Board. Because this supply is renewable, the model targets only one working listing (maximum 99 units) and never relies on buying out competing player listings.");'

Replace-Exact $project '<Version>1.1.5.0</Version>' '<Version>1.1.6.0</Version>'

Write-Host 'v1.1.6 vendor-contestable buyout safety applied.'
