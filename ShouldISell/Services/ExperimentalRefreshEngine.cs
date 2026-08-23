using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace ShouldISell.Services;

/// <summary>
/// Experimental queue that asks the native ItemSearch info proxy for selected owned items.
/// The resulting normal market packets are observed by Dalamud's IMarketBoard service; when
/// Dalamud market-board collection is enabled, Dalamud's existing uploader can contribute those
/// observations to Universalis. This class deliberately does NOT implement a Universalis uploader.
/// </summary>
public sealed unsafe class ExperimentalRefreshEngine : IDisposable
{
    private readonly Configuration configuration;
    private readonly IFramework framework;
    private readonly IPlayerState playerState;
    private readonly GameItemCatalog catalog;
    private readonly InventoryScanner inventory;
    private readonly LocalStore store;
    private readonly MarketBoardObserver observer;
    private readonly SellScanContextService sellContext;
    private readonly IPluginLog log;

    private readonly Queue<RefreshQueueEntry> queue = new();
    private RefreshQueueEntry? current;
    private DateTimeOffset stateSince;
    private DateTimeOffset lastPacketAt;
    private DateTimeOffset nextRequestAt;
    private bool currentHasHistory;
    private int completed;
    private int failed;
    private int initialCount;
    private string currentScope = "owned items";

    public ExperimentalRefreshEngine(
        Configuration configuration,
        IFramework framework,
        IPlayerState playerState,
        GameItemCatalog catalog,
        InventoryScanner inventory,
        LocalStore store,
        MarketBoardObserver observer,
        SellScanContextService sellContext,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.framework = framework;
        this.playerState = playerState;
        this.catalog = catalog;
        this.inventory = inventory;
        this.store = store;
        this.observer = observer;
        this.sellContext = sellContext;
        this.log = log;
        observer.PacketObserved += OnPacketObserved;
        framework.Update += OnFrameworkUpdate;
    }

    public RefreshState State { get; private set; } = RefreshState.Idle;
    public string Status { get; private set; } = "Idle";
    public int Remaining => queue.Count + (current is null ? 0 : 1);
    public int CompletedCount => completed;
    public int FailedCount => failed;
    public int InitialCount => initialCount;
    public RefreshQueueEntry? Current => current;
    public bool IsRunning => State is RefreshState.WaitingToRequest or RefreshState.WaitingForPackets or RefreshState.Cooldown;
    public SellScanContext CurrentSellContext => sellContext.Detect();

    public void Dispose()
    {
        observer.PacketObserved -= OnPacketObserved;
        framework.Update -= OnFrameworkUpdate;
    }

    public void StartForStaleOwnedItems()
    {
        if (IsRunning || !playerState.IsLoaded)
            return;

        inventory.ScanLoadedContainers(forceFlush: true);
        var worldId = playerState.CurrentWorld.RowId;
        var staleBefore = DateTimeOffset.UtcNow.AddHours(-Math.Max(1, configuration.ExperimentalRefreshStaleHours));
        var ids = inventory.GetUniqueMarketableItemIds().Where(itemId =>
        {
            var market = store.GetMarket(worldId, itemId);
            var freshest = Max(market?.ListingObservedAtUtc, market?.UniversalisLastUploadUtc);
            return freshest is null || freshest < staleBefore;
        });

        StartQueue(ids, "stale owned items", onlyStale: false);
    }

    public void StartForAllOwnedItems()
    {
        if (IsRunning || !playerState.IsLoaded)
            return;
        inventory.ScanLoadedContainers(forceFlush: true);
        StartQueue(inventory.GetUniqueMarketableItemIds(), "all known owned items", onlyStale: false);
    }

    /// <summary>
    /// Force-refreshes only the unique items that are currently listed across the player's
    /// cached retainers. This is intentionally much smaller than a full owned-item audit.
    /// </summary>
    public void StartForCurrentListings()
    {
        if (IsRunning || !playerState.IsLoaded || playerState.ContentId == 0)
            return;

        inventory.ScanLoadedContainers(forceFlush: true);
        var ids = store.GetOwnListings(playerState.ContentId)
            .Select(x => x.ItemId)
            .Distinct()
            .Order();
        StartQueue(ids, "current retainer listings", onlyStale: false);
    }

    /// <summary>
    /// Force-refreshes every unique marketable item represented by whichever native sell/inventory
    /// window the player currently has open. Fresh Universalis data does not cause a skip here: this
    /// action explicitly asks FFXIV for a new observation of every item in the selected scope.
    /// </summary>
    public void StartForCurrentSellWindow()
    {
        if (IsRunning || !playerState.IsLoaded)
            return;

        inventory.ScanLoadedContainers(forceFlush: true);
        var context = sellContext.Detect();
        IReadOnlyList<uint> ids = context.Target switch
        {
            SellScanTarget.PlayerInventory => inventory.GetUniqueMarketablePlayerInventoryItemIds(),
            SellScanTarget.ActiveRetainerInventory => inventory.GetUniqueMarketableActiveRetainerInventoryItemIds(),
            SellScanTarget.ActiveRetainerMarket => inventory.GetUniqueMarketableActiveRetainerMarketItemIds(),
            _ => Array.Empty<uint>(),
        };

        if (!context.IsAvailable)
        {
            State = RefreshState.Idle;
            Status = context.Detail;
            return;
        }

        StartQueue(ids, context.Label, onlyStale: false);
    }

    public void Stop(string reason = "Stopped.")
    {
        queue.Clear();
        current = null;
        State = RefreshState.Stopped;
        Status = reason;
    }

    private void StartQueue(IEnumerable<uint> itemIds, string scope, bool onlyStale)
    {
        queue.Clear();
        current = null;
        completed = 0;
        failed = 0;
        currentScope = scope;

        var worldId = playerState.CurrentWorld.RowId;
        var staleBefore = DateTimeOffset.UtcNow.AddHours(-Math.Max(1, configuration.ExperimentalRefreshStaleHours));
        foreach (var itemId in itemIds.Distinct().Order())
        {
            if (!catalog.IsMarketable(itemId))
                continue;

            var market = store.GetMarket(worldId, itemId);
            var freshest = Max(market?.ListingObservedAtUtc, market?.UniversalisLastUploadUtc);
            if (onlyStale && freshest is not null && freshest >= staleBefore)
                continue;

            queue.Enqueue(new RefreshQueueEntry(itemId, catalog.Get(itemId).Name, freshest));
        }

        initialCount = queue.Count;
        if (queue.Count == 0)
        {
            State = RefreshState.Completed;
            Status = $"No marketable items found for {scope}.";
            return;
        }

        State = RefreshState.WaitingToRequest;
        stateSince = DateTimeOffset.UtcNow;
        nextRequestAt = DateTimeOffset.UtcNow;
        Status = $"Queued {queue.Count} item(s) from {scope}.";
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!IsRunning)
            return;
        if (!playerState.IsLoaded)
        {
            Stop("Stopped: player state unloaded.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        switch (State)
        {
            case RefreshState.WaitingToRequest:
                if (now < nextRequestAt)
                    return;
                if (current is null)
                {
                    if (queue.Count == 0)
                    {
                        State = RefreshState.Completed;
                        Status = $"Done scanning {currentScope}. {completed} refreshed, {failed} failed.";
                        store.Flush();
                        return;
                    }
                    current = queue.Dequeue();
                }
                SendCurrentRequest(now);
                break;

            case RefreshState.WaitingForPackets:
                // History is always part of a normal successful item request. Offering pages may be
                // zero or many, so after history we wait until packet traffic has been quiet long
                // enough for the listing pages to finish before advancing the queue.
                if (currentHasHistory && now - lastPacketAt >= TimeSpan.FromMilliseconds(850))
                {
                    completed++;
                    Status = $"Refreshed {current!.ItemName}. {Remaining - 1} remaining.";
                    current = null;
                    State = RefreshState.Cooldown;
                    stateSince = now;
                    nextRequestAt = now.AddMilliseconds(Math.Max(1000, configuration.ExperimentalRequestSpacingMs));
                    store.Flush();
                    return;
                }

                if (now - stateSince >= TimeSpan.FromMilliseconds(Math.Max(4000, configuration.ExperimentalRequestTimeoutMs)))
                    HandleTimeout(now);
                break;

            case RefreshState.Cooldown:
                if (now >= nextRequestAt)
                {
                    State = RefreshState.WaitingToRequest;
                    stateSince = now;
                }
                break;
        }
    }

    private void SendCurrentRequest(DateTimeOffset now)
    {
        if (current is null)
            return;

        try
        {
            var proxy = InfoProxyItemSearch.Instance();
            if (proxy == null)
            {
                Status = "Waiting for ItemSearch info proxy...";
                nextRequestAt = now.AddSeconds(1);
                return;
            }

            current = current with { Attempts = current.Attempts + 1 };
            currentHasHistory = false;
            lastPacketAt = now;

            proxy->EntryCount = 0;
            proxy->SearchItemId = current.ItemId;
            if (!proxy->RequestData())
            {
                Status = $"Client refused request for {current.ItemName}; retrying.";
                HandleTimeout(now, immediate: true);
                return;
            }

            State = RefreshState.WaitingForPackets;
            stateSince = now;
            Status = $"Requesting {current.ItemName} ({completed + failed + 1}/{initialCount}), attempt {current.Attempts}...";
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Experimental market request failed for item {ItemId}.", current.ItemId);
            HandleTimeout(now, immediate: true);
        }
    }

    private void OnPacketObserved(uint itemId, MarketPacketKind kind)
    {
        if (State != RefreshState.WaitingForPackets || current is null || itemId != current.ItemId)
            return;

        lastPacketAt = DateTimeOffset.UtcNow;
        if (kind == MarketPacketKind.History)
            currentHasHistory = true;
    }

    private void HandleTimeout(DateTimeOffset now, bool immediate = false)
    {
        if (current is null)
            return;

        if (current.Attempts < Math.Max(1, configuration.ExperimentalMaxRetries))
        {
            Status = $"No complete response for {current.ItemName}; retry {current.Attempts + 1}/{configuration.ExperimentalMaxRetries}.";
            State = RefreshState.Cooldown;
            stateSince = now;
            nextRequestAt = now.AddMilliseconds(Math.Max(2000, configuration.ExperimentalRequestSpacingMs));
            return;
        }

        failed++;
        log.Warning("Giving up experimental market refresh for {ItemId} after {Attempts} attempts.", current.ItemId, current.Attempts);
        Status = $"Skipped {current.ItemName} after {current.Attempts} failed attempt(s).";
        current = null;
        State = RefreshState.Cooldown;
        stateSince = now;
        nextRequestAt = now.AddMilliseconds(Math.Max(2000, configuration.ExperimentalRequestSpacingMs));
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b)
        => a is null ? b : b is null ? a : a >= b ? a : b;
}
