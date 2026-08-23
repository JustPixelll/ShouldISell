using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;

namespace ShouldISell.Services;

/// <summary>
/// Observation-based player-wallet ledger. Dalamud exposes the Currency inventory; Gil is item #1.
/// This gives us complete direct wallet deltas while the plugin is running without packet signatures
/// or native memory reads. The delta alone does not reveal whether arbitrary income came from a
/// quest, duty, vendor, trade, etc., so unknown sources remain explicitly unclassified.
/// </summary>
public sealed class GilLedgerTracker
{
    private const uint GilItemId = 1;

    private readonly IGameInventory gameInventory;
    private readonly IPlayerState playerState;
    private readonly TraderStore store;
    private readonly IPluginLog log;

    private ulong activeCharacterId;
    private long? lastBalance;
    private DateTimeOffset lastObservedUtc;

    public GilLedgerTracker(
        IGameInventory gameInventory,
        IPlayerState playerState,
        TraderStore store,
        IPluginLog log)
    {
        this.gameInventory = gameInventory;
        this.playerState = playerState;
        this.store = store;
        this.log = log;
    }

    public long? CurrentBalance { get; private set; }
    public DateTimeOffset? CurrentBalanceObservedAtUtc { get; private set; }

    public void Capture()
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0)
        {
            activeCharacterId = 0;
            lastBalance = null;
            return;
        }

        var balance = ReadPlayerGil();
        if (balance is null)
            return;

        var now = DateTimeOffset.UtcNow;
        var contentId = playerState.ContentId;
        CurrentBalance = balance;
        CurrentBalanceObservedAtUtc = now;

        if (activeCharacterId != contentId || lastBalance is null)
        {
            // Login/character switch is a baseline, never an invented offline cashflow event.
            activeCharacterId = contentId;
            lastBalance = balance;
            lastObservedUtc = now;
            return;
        }

        var delta = balance.Value - lastBalance.Value;
        if (delta != 0)
        {
            try
            {
                var classification = ClassifyDelta(contentId, delta, lastObservedUtc, now);
                store.AddGilFlow(new GilFlowEntry(
                    Guid.NewGuid().ToString("N"),
                    contentId,
                    delta,
                    balance.Value,
                    now,
                    classification.Category,
                    classification.Source,
                    classification.AutoClassified,
                    classification.Note));
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Could not persist a Should I Tycoon? gil-balance change.");
            }
        }

        lastBalance = balance;
        lastObservedUtc = now;

        // Purchase acknowledgement and Currency updates can land a frame apart. Revisit only recent
        // unknown spending so an exact confirmed MB purchase can still become an exact classification.
        ReconcileRecentMarketPurchases(contentId, now);
    }

    private long? ReadPlayerGil()
    {
        try
        {
            var currency = gameInventory.GetInventoryItems(GameInventoryType.Currency);
            foreach (var item in currency)
            {
                if (!item.IsEmpty && (item.BaseItemId == GilItemId || item.ItemId == GilItemId))
                    return Math.Max(0, (long)item.Quantity);
            }
        }
        catch (Exception ex)
        {
            log.Verbose(ex, "Player Currency inventory was not available for gil tracking yet.");
        }

        return null;
    }

    private (GilFlowCategory Category, string Source, bool AutoClassified, string Note) ClassifyDelta(
        ulong contentId,
        long delta,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        if (delta >= 0)
            return (GilFlowCategory.Unclassified, "Direct gil income", false,
                "The wallet increase is exact, but FFXIV does not encode quest/duty/vendor/etc. source identity in the balance itself. Categorize it here when useful.");

        var purchases = store.GetPurchases(contentId)
            .Where(x => x.PurchasedAtUtc >= fromUtc.AddSeconds(-3) && x.PurchasedAtUtc <= toUtc.AddSeconds(6))
            .OrderBy(x => x.PurchasedAtUtc)
            .ToList();
        var spending = -delta;
        var exactSingle = purchases.FirstOrDefault(x => x.TotalCost == spending);
        if (exactSingle is not null)
        {
            return (
                GilFlowCategory.MarketBoardPurchase,
                exactSingle.Strategy,
                true,
                $"Matched exact confirmed Market Board cost: {exactSingle.Quantity:N0} unit(s), {exactSingle.TotalCost:N0}g including buyer tax.");
        }

        var exactTotal = purchases.Sum(x => x.TotalCost);
        if (purchases.Count > 1 && exactTotal == spending)
        {
            return (
                GilFlowCategory.MarketBoardPurchase,
                $"{purchases.Count:N0} Market Board purchases",
                true,
                $"Matched the wallet decrease to {purchases.Count:N0} confirmed Market Board purchases totaling {exactTotal:N0}g.");
        }

        return (GilFlowCategory.Unclassified, "Direct gil spending", false,
            "The wallet decrease is exact, but its gameplay source could not be proven automatically. Categorize it here when useful.");
    }

    private void ReconcileRecentMarketPurchases(ulong contentId, DateTimeOffset now)
    {
        var unknown = store.GetGilFlows(contentId)
            .Where(x => x.Category == GilFlowCategory.Unclassified && x.Amount < 0 && now - x.AtUtc <= TimeSpan.FromSeconds(30))
            .Take(12)
            .ToList();
        if (unknown.Count == 0)
            return;

        var purchases = store.GetPurchases(contentId)
            .Where(x => now - x.PurchasedAtUtc <= TimeSpan.FromSeconds(40))
            .ToList();

        foreach (var flow in unknown)
        {
            var exact = purchases
                .Where(x => x.TotalCost == -flow.Amount)
                .OrderBy(x => Math.Abs((x.PurchasedAtUtc - flow.AtUtc).TotalSeconds))
                .FirstOrDefault(x => Math.Abs((x.PurchasedAtUtc - flow.AtUtc).TotalSeconds) <= 10);
            if (exact is null)
                continue;

            store.UpdateGilFlowClassification(
                flow.Id,
                GilFlowCategory.MarketBoardPurchase,
                exact.Strategy,
                autoClassified: true,
                note: $"Reconciled to exact confirmed Market Board purchase: {exact.Quantity:N0} unit(s), {exact.TotalCost:N0}g including buyer tax.",
                flush: false);
        }

        store.Flush();
    }
}
