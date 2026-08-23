namespace ShouldISell.Windows;

public sealed partial class SuiteWindow
{
    private void RefreshBuyLiveRatings()
    {
        if (plugin.RefreshEngine.IsRunning || plugin.BuyScanner.IsScanning)
            return;

        plugin.BuyScanner.ApplyLiveSnapshots();
        if (selectedBuyOpportunity is not { } selected)
            return;

        var updated = plugin.BuyScanner.GetOpportunities().FirstOrDefault(x =>
            x.WorldId == selected.WorldId &&
            x.Item.ItemId == selected.Item.ItemId &&
            x.IsHq == selected.IsHq &&
            x.Kind == selected.Kind);
        if (updated is not null)
        {
            selectedBuyOpportunity = updated;
            return;
        }

        // The recommendation may disappear after the player fills it (especially Vendor -> Market)
        // or when a live snapshot makes the package non-actionable.
        selectedBuyOpportunity = null;
        buyDetailsOpen = false;
        buyPortfolioPlan = null;
    }

    private static int OneListingUnits(BuyOpportunity opportunity)
        => Math.Max(1, Math.Min(opportunity.AcquireQuantity, Math.Max(1, opportunity.SuggestedExitStackSize)));

    private static double OneListingNetRevenue(BuyOpportunity opportunity)
        => opportunity.NetExitUnitPrice is { } net
            ? net * (double)OneListingUnits(opportunity)
            : 0;

    private static double OneListingCapitalRecovery(BuyOpportunity opportunity)
        => opportunity.AcquisitionCost > 0
            ? OneListingNetRevenue(opportunity) / opportunity.AcquisitionCost
            : 1.0;

    private static int SequentialListingCycles(BuyOpportunity opportunity)
    {
        var stack = Math.Max(1, opportunity.SuggestedExitStackSize);
        var position = Math.Max(1, opportunity.ExistingQuantity + opportunity.AcquireQuantity);
        return (position + stack - 1) / stack;
    }
}
