using Dalamud.Bindings.ImGui;

namespace ShouldISell.Windows;

public sealed partial class SuiteWindow
{
    private void DrawBuyLiveVerification(BuyOpportunity opportunity)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Live verification");

        var refresh = plugin.RefreshEngine;
        var thisItemRunning = refresh.IsRunning && refresh.Current?.ItemId == opportunity.Item.ItemId;
        if (refresh.IsRunning)
            ImGui.BeginDisabled();
        if (ImGui.Button($"LIVE VERIFY##buy-live-{opportunity.Item.ItemId}-{opportunity.IsHq}"))
            refresh.StartForItem(opportunity.Item.ItemId, $"live verification for {opportunity.Item.Name}");
        if (refresh.IsRunning)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Asks FFXIV itself for a fresh Market Board observation of this item. This never buys anything.");

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

        var live = plugin.Store.GetMarket(opportunity.WorldId, opportunity.Item.ItemId);
        var liveAt = live?.CurrentSource == MarketDataSource.LiveGame ? live.ListingObservedAtUtc : null;
        if (liveAt is null)
        {
            ImGui.TextDisabled("No FFXIV-live listing snapshot is stored for this opportunity yet.");
            return;
        }

        var age = DateTimeOffset.UtcNow - liveAt.Value;
        ImGui.TextDisabled($"Latest FFXIV-live board observation: {liveAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} ({FormatAge(age)} ago).");

        if (liveAt.Value < opportunity.AnalysedAtUtc)
        {
            ImGui.TextDisabled("That live snapshot predates this scanner result. Press LIVE VERIFY before relying on it for this trade.");
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
                ImGui.TextUnformatted($"LIVE VERIFIED — all {matched:N0} recommended acquisition listing(s) are still present at the scanned price and quantity.");
            }
            else
            {
                ImGui.TextWrapped($"MARKET CHANGED — only {matched:N0} of {opportunity.AcquisitionLots.Count:N0} recommended acquisition listing(s) still match exactly. Rerun the Buy scan before committing gil; the previous package economics may no longer be valid.");
            }
        }
        else
        {
            var lowest = liveVariantListings.FirstOrDefault();
            if (lowest is not null)
                ImGui.TextUnformatted($"Exit market refreshed live. Current lowest matching ask: {lowest.PricePerUnit:N0}g/unit ({lowest.Quantity:N0} unit(s)).");
            else
                ImGui.TextDisabled("Exit market refreshed live, but no matching current listing is visible.");

            ImGui.TextDisabled("Vendor → Market has no acquisition listing to verify; the live check validates the exit-side board. Rerun the scanner if the refreshed market materially differs from the recommendation.");
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
