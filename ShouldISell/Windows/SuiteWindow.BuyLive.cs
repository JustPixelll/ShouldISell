using Dalamud.Bindings.ImGui;

namespace ShouldISell.Windows;

public sealed partial class SuiteWindow
{
    private void DrawBuyLiveVerification(BuyOpportunity opportunity)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Live verification");
        ImGui.TextDisabled("Single-item FFXIV check only. This does not restart the broad Universalis discovery pass.");

        var currentWorldId = CurrentBuyWorldId;
        if (currentWorldId == 0)
        {
            ImGui.TextDisabled("Player/world state is not loaded, so live verification is unavailable.");
            return;
        }

        if (opportunity.WorldId != currentWorldId)
        {
            ImGui.TextWrapped($"WORLD CHANGED — this recommendation belongs to {plugin.Catalog.GetWorldName(opportunity.WorldId)}, but you are currently on {CurrentBuyWorldName}. Rerun discovery on the current world before verifying or buying it.");
            return;
        }

        if (!plugin.SellScanContext.IsMarketUiVisible())
            ImGui.TextWrapped("For the most reliable native request, open the Market Board search/results window or a retainer market window before pressing LIVE VERIFY. The request can still be attempted, but FFXIV may refuse it when the market UI/proxy is not ready.");

        var refresh = plugin.RefreshEngine;
        var thisItemRunning = refresh.IsRunning && refresh.Current?.ItemId == opportunity.Item.ItemId;
        if (refresh.IsRunning)
            ImGui.BeginDisabled();
        if (ImGui.Button($"LIVE VERIFY THIS ITEM ONLY##buy-live-{opportunity.Item.ItemId}-{opportunity.IsHq}"))
            refresh.StartForItem(opportunity.Item.ItemId, $"live verification for {opportunity.Item.Name}");
        if (refresh.IsRunning)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Ask FFXIV itself for one fresh Market Board observation of {opportunity.Item.Name} on {CurrentBuyWorldName}. No broad Universalis scan is started and nothing is purchased.");

        if (thisItemRunning)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(refresh.Status);
        }
        else if (refresh.IsRunning)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"Another live refresh is active: {refresh.Status}");
        }

        var live = plugin.Store.GetMarket(currentWorldId, opportunity.Item.ItemId);
        var liveAt = live?.CurrentSource == MarketDataSource.LiveGame ? live.ListingObservedAtUtc : null;
        if (liveAt is null)
        {
            ImGui.TextDisabled($"No FFXIV-live listing snapshot is stored for {CurrentBuyWorldName} yet.");
            return;
        }

        var age = DateTimeOffset.UtcNow - liveAt.Value;
        ImGui.TextDisabled($"Latest FFXIV-live board observation on {CurrentBuyWorldName}: {liveAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} ({FormatAge(age)} ago).");

        if (liveAt.Value < opportunity.AnalysedAtUtc)
        {
            ImGui.TextDisabled("That live snapshot predates this recommendation. Press LIVE VERIFY before relying on it for this trade.");
            return;
        }

        var liveVariantListings = live!.Listings
            .Where(x => x.IsHq == opportunity.IsHq && x.PricePerUnit > 0 && x.Quantity > 0)
            .OrderBy(x => x.PricePerUnit)
            .ToList();

        if (opportunity.AcquisitionLots.Count > 0)
        {
            var matched = 0;
            foreach (var lot in opportunity.AcquisitionLots)
            {
                var exact = liveVariantListings.Any(x =>
                    lot.ListingId != 0 &&
                    x.ListingId == lot.ListingId &&
                    x.PricePerUnit == lot.UnitPrice &&
                    x.Quantity == lot.Quantity);
                if (exact)
                    matched++;
            }

            if (matched == opportunity.AcquisitionLots.Count)
            {
                ImGui.TextUnformatted($"LIVE VERIFIED — all {matched:N0} recommended acquisition listing(s) are still present on {CurrentBuyWorldName} at the scanned price and quantity.");
            }
            else
            {
                ImGui.TextWrapped($"MARKET CHANGED — only {matched:N0} of {opportunity.AcquisitionLots.Count:N0} recommended acquisition listing(s) still match exactly on {CurrentBuyWorldName}. Rerun discovery before committing gil; the previous package economics may no longer be valid.");
            }
        }
        else
        {
            var lowest = liveVariantListings.FirstOrDefault();
            if (lowest is not null)
                ImGui.TextUnformatted($"Exit market refreshed live on {CurrentBuyWorldName}. Current lowest matching ask: {lowest.PricePerUnit:N0}g/unit ({lowest.Quantity:N0} unit(s)).");
            else
                ImGui.TextDisabled("Exit market refreshed live, but no matching current listing is visible.");

            ImGui.TextDisabled("Vendor → Market has no acquisition listing to verify; the live check validates the exit-side board. Rerun discovery if the refreshed market materially differs from the recommendation.");
        }
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalSeconds < 60)
            return $"{Math.Max(0, age.TotalSeconds):0}s";
        if (age.TotalMinutes < 60)
            return $"{age.TotalMinutes:0.#}m";
        if (age.TotalHours < 24)
            return $"{age.TotalHours:0.#}h";
        return $"{age.TotalDays:0.#}d";
    }
}
