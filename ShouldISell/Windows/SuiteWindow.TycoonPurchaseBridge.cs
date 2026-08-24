namespace ShouldISell.Windows;

public sealed partial class SuiteWindow
{
    private void PrepareVendorPurchaseFromOpportunity(BuyOpportunity opportunity)
    {
        vendorPurchaseSearch = opportunity.Item.Name;
        vendorPurchaseItemId = opportunity.Item.ItemId;
        vendorPurchaseQuantity = Math.Max(1, opportunity.AcquireQuantity);
        vendorPurchaseUnitPrice = Math.Max(1, (int)Math.Round(opportunity.AverageAcquisitionUnitCost));
        vendorPurchaseTrackAsTrade = true;
        vendorPurchaseStatus = $"Prepared from Should I Buy?: {opportunity.StrategyLabel}, {vendorPurchaseQuantity:N0} unit(s) at about {vendorPurchaseUnitPrice:N0}g/unit. Confirm the real purchase below before recording it.";
        selectTycoonPurchases = true;
        buyDetailsOpen = false;
        OpenModule(ShouldIModule.Tycoon);
    }
}
