using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ShouldISell.Services;

public enum ListingTraceEventKind
{
    Listed,
    PriceChanged,
    QuantityChanged,
    Removed,
}

public sealed record ListingTraceEvent(
    ListingTraceEventKind Kind,
    DateTimeOffset AtUtc,
    uint UnitPrice,
    int Quantity,
    short MarketSlot,
    uint? PreviousUnitPrice = null,
    int? PreviousQuantity = null);

public sealed class ListingTraceLifecycle
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ulong CharacterContentId { get; set; }
    public ulong RetainerId { get; set; }
    public string RetainerName { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public bool IsHq { get; set; }
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastObservedUtc { get; set; }
    public DateTimeOffset? RemovedAtUtc { get; set; }
    public bool IsActive { get; set; }
    public short LastMarketSlot { get; set; }
    public uint InitialUnitPrice { get; set; }
    public uint LastUnitPrice { get; set; }
    public int InitialQuantity { get; set; }
    public int LastQuantity { get; set; }
    public List<ListingTraceEvent> Events { get; set; } = new();
}

/// <summary>
/// Long-lived local history of the player's own retainer listings. LocalStore intentionally keeps
/// only the current listing state; this tracker records state transitions so Tycoon can later study
/// repricing, size changes, relists and time-to-sale when a sale can be correlated safely.
/// </summary>
public sealed class ListingHistoryTracker : IDisposable
{
    private readonly object gate = new();
    private readonly string path;
    private readonly IPlayerState playerState;
    private readonly LocalStore store;
    private readonly IPluginLog log;
    private ListingTraceDocument document;
    private bool dirty;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public ListingHistoryTracker(
        IDalamudPluginInterface pluginInterface,
        IPlayerState playerState,
        LocalStore store,
        IPluginLog log)
    {
        this.playerState = playerState;
        this.store = store;
        this.log = log;
        Directory.CreateDirectory(pluginInterface.ConfigDirectory.FullName);
        path = Path.Combine(pluginInterface.ConfigDirectory.FullName, "listing-insights.json");
        document = Load();
    }

    public void Capture()
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0)
            return;

        var contentId = playerState.ContentId;
        var now = DateTimeOffset.UtcNow;
        var current = store.GetOwnListings(contentId).ToList();

        lock (gate)
        {
            var active = document.Lifecycles
                .Where(x => x.CharacterContentId == contentId && x.IsActive)
                .ToList();
            var matched = new HashSet<string>(StringComparer.Ordinal);

            foreach (var listing in current)
            {
                var lifecycle = active.FirstOrDefault(x =>
                    !matched.Contains(x.Id) &&
                    x.RetainerId == listing.RetainerId &&
                    x.ItemId == listing.ItemId &&
                    x.IsHq == listing.IsHq &&
                    Math.Abs((x.FirstSeenUtc - listing.FirstSeenUtc).TotalSeconds) <= 5);

                lifecycle ??= active.FirstOrDefault(x =>
                    !matched.Contains(x.Id) &&
                    x.RetainerId == listing.RetainerId &&
                    x.LastMarketSlot == listing.MarketSlot &&
                    x.ItemId == listing.ItemId &&
                    x.IsHq == listing.IsHq);

                if (lifecycle is null)
                {
                    lifecycle = new ListingTraceLifecycle
                    {
                        CharacterContentId = contentId,
                        RetainerId = listing.RetainerId,
                        RetainerName = listing.RetainerName,
                        ItemId = listing.ItemId,
                        IsHq = listing.IsHq,
                        FirstSeenUtc = listing.FirstSeenUtc,
                        LastObservedUtc = listing.LastSeenUtc,
                        IsActive = true,
                        LastMarketSlot = listing.MarketSlot,
                        InitialUnitPrice = listing.UnitPrice,
                        LastUnitPrice = listing.UnitPrice,
                        InitialQuantity = Math.Clamp(listing.Quantity, 1, MarketBoardRules.MaxListingQuantity),
                        LastQuantity = Math.Clamp(listing.Quantity, 1, MarketBoardRules.MaxListingQuantity),
                    };
                    lifecycle.Events.Add(new ListingTraceEvent(
                        ListingTraceEventKind.Listed,
                        listing.FirstSeenUtc,
                        listing.UnitPrice,
                        lifecycle.InitialQuantity,
                        listing.MarketSlot));
                    document.Lifecycles.Add(lifecycle);
                    dirty = true;
                }
                else
                {
                    var quantity = Math.Clamp(listing.Quantity, 1, MarketBoardRules.MaxListingQuantity);
                    if (lifecycle.LastUnitPrice != listing.UnitPrice)
                    {
                        lifecycle.Events.Add(new ListingTraceEvent(
                            ListingTraceEventKind.PriceChanged,
                            listing.PriceChangedUtc > lifecycle.LastObservedUtc ? listing.PriceChangedUtc : now,
                            listing.UnitPrice,
                            quantity,
                            listing.MarketSlot,
                            lifecycle.LastUnitPrice,
                            lifecycle.LastQuantity));
                        lifecycle.LastUnitPrice = listing.UnitPrice;
                        dirty = true;
                    }

                    if (lifecycle.LastQuantity != quantity)
                    {
                        lifecycle.Events.Add(new ListingTraceEvent(
                            ListingTraceEventKind.QuantityChanged,
                            now,
                            listing.UnitPrice,
                            quantity,
                            listing.MarketSlot,
                            lifecycle.LastUnitPrice,
                            lifecycle.LastQuantity));
                        lifecycle.LastQuantity = quantity;
                        dirty = true;
                    }

                    lifecycle.RetainerName = listing.RetainerName;
                    lifecycle.LastMarketSlot = listing.MarketSlot;
                    lifecycle.IsActive = true;
                    lifecycle.RemovedAtUtc = null;
                    if (listing.LastSeenUtc > lifecycle.LastObservedUtc)
                        lifecycle.LastObservedUtc = listing.LastSeenUtc;
                }

                matched.Add(lifecycle.Id);
            }

            foreach (var lifecycle in active.Where(x => !matched.Contains(x.Id)))
            {
                lifecycle.IsActive = false;
                lifecycle.RemovedAtUtc = now;
                lifecycle.LastObservedUtc = now;
                lifecycle.Events.Add(new ListingTraceEvent(
                    ListingTraceEventKind.Removed,
                    now,
                    lifecycle.LastUnitPrice,
                    lifecycle.LastQuantity,
                    lifecycle.LastMarketSlot));
                dirty = true;
            }

            if (document.Lifecycles.Count > 20_000)
            {
                document.Lifecycles = document.Lifecycles
                    .OrderByDescending(x => x.IsActive)
                    .ThenByDescending(x => x.RemovedAtUtc ?? x.LastObservedUtc)
                    .Take(20_000)
                    .ToList();
                dirty = true;
            }
        }

        if (dirty)
            Flush();
    }

    public IReadOnlyList<ListingTraceLifecycle> GetLifecycles(ulong characterContentId)
    {
        lock (gate)
            return document.Lifecycles
                .Where(x => x.CharacterContentId == characterContentId)
                .Select(Clone)
                .ToList();
    }

    public void Flush()
    {
        string? json = null;
        lock (gate)
        {
            if (!dirty)
                return;
            json = JsonSerializer.Serialize(document, JsonOptions);
            dirty = false;
        }

        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, true);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not persist Tycoon listing-insight history.");
            lock (gate) dirty = true;
        }
    }

    public void Dispose() => Flush();

    private ListingTraceDocument Load()
    {
        try
        {
            if (!File.Exists(path))
                return new ListingTraceDocument();
            return JsonSerializer.Deserialize<ListingTraceDocument>(File.ReadAllText(path), JsonOptions)
                   ?? new ListingTraceDocument();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not load Tycoon listing-insight history; starting a new history file.");
            return new ListingTraceDocument();
        }
    }

    private static ListingTraceLifecycle Clone(ListingTraceLifecycle source) => new()
    {
        Id = source.Id,
        CharacterContentId = source.CharacterContentId,
        RetainerId = source.RetainerId,
        RetainerName = source.RetainerName,
        ItemId = source.ItemId,
        IsHq = source.IsHq,
        FirstSeenUtc = source.FirstSeenUtc,
        LastObservedUtc = source.LastObservedUtc,
        RemovedAtUtc = source.RemovedAtUtc,
        IsActive = source.IsActive,
        LastMarketSlot = source.LastMarketSlot,
        InitialUnitPrice = source.InitialUnitPrice,
        LastUnitPrice = source.LastUnitPrice,
        InitialQuantity = source.InitialQuantity,
        LastQuantity = source.LastQuantity,
        Events = source.Events.ToList(),
    };

    private sealed class ListingTraceDocument
    {
        public List<ListingTraceLifecycle> Lifecycles { get; set; } = new();
    }
}
