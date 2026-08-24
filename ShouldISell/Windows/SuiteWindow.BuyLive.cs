using Dalamud.Bindings.ImGui;

namespace ShouldISell.Windows;

public sealed partial class SuiteWindow
{
    private void DrawBuyLiveVerification(BuyOpportunity opportunity)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Live data");
        ImGui.TextDisabled("Read-only in Should I?. Live snapshots can update from normal Market Board use or compatible local data providers.");

        var currentWorldId = CurrentBuyWorldId;
        if (currentWorldId == 0)
        {
            ImGui.TextDisabled("Player/world state is not loaded, so live-data comparison is unavailable.");
            return;
        }

        if (opportunity.WorldId != currentWorldId)
        {
            ImGui.TextWrapped($"WORLD CHANGED — this recommendation belongs to {plugin.Catalog.GetWorldName(opportunity.WorldId)}, but you are currently on {CurrentBuyWorldName}. Rerun discovery on the current world before relying on it.");
            return;
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
            ImGui.TextDisabled("That live snapshot predates this recommendation. Refresh it through normal Market Board use before relying on it for this trade.");
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
                ImGui.TextUnformatted($"LIVE VERIFIED — all {matched:N0} recommended acquisition listing(s) are still present on {CurrentBuyWorldName} at the scanned price and quantity.");
            else
                ImGui.TextWrapped($"MARKET CHANGED — only {matched:N0} of {opportunity.AcquisitionLots.Count:N0} recommended acquisition listing(s) still match exactly on {CurrentBuyWorldName}. Rerun Should I Buy? discovery before committing gil.");
        }
        else
        {
            var lowest = liveVariantListings.FirstOrDefault();
            if (lowest is not null)
                ImGui.TextUnformatted($"Exit market snapshot: current lowest matching ask {lowest.PricePerUnit:N0}g/unit ({lowest.Quantity:N0} unit(s)).");
            else
                ImGui.TextDisabled("The live snapshot contains no matching current listing for this quality variant.");
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
