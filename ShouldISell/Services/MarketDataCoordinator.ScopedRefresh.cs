namespace ShouldISell.Services;

public sealed partial class MarketDataCoordinator
{
    /// <summary>
    /// Refreshes a caller-selected safe item scope from Universalis only. No native FFXIV market
    /// request is issued by this path.
    /// </summary>
    public async Task RefreshScopeFromUniversalisAsync(
        IEnumerable<uint> itemIds,
        string scopeLabel,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (!playerState.IsLoaded)
            return;
        if (!await refreshGate.WaitAsync(0, cancellationToken))
            return;

        try
        {
            IsFetching = true;
            var worldId = playerState.CurrentWorld.RowId;
            acceptingWorldId = worldId;
            var ids = itemIds
                .Where(x => x != 0 && catalog.IsMarketable(x))
                .Distinct()
                .Order()
                .ToList();
            if (ids.Count == 0)
            {
                FetchStatus = $"{scopeLabel}: no known marketable items.";
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var currentNeeded = new List<uint>();
            var historyNeeded = new List<uint>();
            foreach (var itemId in ids)
            {
                var market = store.GetMarket(worldId, itemId);
                if (force || market?.ListingObservedAtUtc is null ||
                    now - market.ListingObservedAtUtc.Value > TimeSpan.FromMinutes(configuration.UniversalisCurrentTtlMinutes))
                    currentNeeded.Add(itemId);

                if (force || market?.HistoryObservedAtUtc is null ||
                    now - market.HistoryObservedAtUtc.Value > TimeSpan.FromMinutes(configuration.UniversalisHistoryTtlMinutes))
                    historyNeeded.Add(itemId);
            }

            FetchStatus = $"{scopeLabel}: Universalis {currentNeeded.Count:N0} current + {historyNeeded.Count:N0} history item(s)...";
            if (currentNeeded.Count > 0)
                await universalis.FetchCurrentAsync(worldId, currentNeeded, cancellationToken);
            if (historyNeeded.Count > 0)
                await universalis.FetchHistoryAsync(worldId, historyNeeded, cancellationToken);

            store.Flush();
            LastFetchCompletedUtc = DateTimeOffset.UtcNow;
            FetchStatus = $"{scopeLabel}: ready for {ids.Count:N0} marketable item(s).";
        }
        catch (OperationCanceledException)
        {
            FetchStatus = $"{scopeLabel}: Universalis refresh cancelled.";
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Universalis scoped refresh failed for {Scope}.", scopeLabel);
            FetchStatus = $"{scopeLabel}: Universalis error: {ex.Message}";
        }
        finally
        {
            acceptingWorldId = 0;
            IsFetching = false;
            refreshGate.Release();
        }
    }
}
