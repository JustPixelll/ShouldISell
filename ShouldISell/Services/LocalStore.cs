using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ShouldISell.Services;

public sealed class LocalStore
{
    private readonly object gate = new();
    private readonly string path;
    private readonly IPluginLog log;
    private StoreDocument document;
    private bool dirty;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public LocalStore(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        Directory.CreateDirectory(pluginInterface.ConfigDirectory.FullName);
        path = Path.Combine(pluginInterface.ConfigDirectory.FullName, "sell-data.json");
        document = Load();
    }

    public IReadOnlyList<InventoryContainerSnapshot> GetInventorySnapshots(ulong characterContentId)
    {
        lock (gate)
            return document.InventorySnapshots
                .Where(x => x.CharacterContentId == characterContentId)
                .ToList();
    }

    public void UpsertInventoryContainer(InventoryContainerSnapshot snapshot, bool flush = false)
    {
        lock (gate)
        {
            document.InventorySnapshots.RemoveAll(x =>
                x.CharacterContentId == snapshot.CharacterContentId &&
                x.OwnerKind == snapshot.OwnerKind &&
                x.OwnerId == snapshot.OwnerId &&
                x.Container == snapshot.Container);
            document.InventorySnapshots.Add(snapshot);
            dirty = true;
        }

        if (flush)
            Flush();
    }


    public IReadOnlyList<OwnMarketListing> GetOwnListings(ulong characterContentId)
    {
        lock (gate)
            return document.OwnListings
                .Where(x => x.CharacterContentId == characterContentId)
                .OrderBy(x => x.RetainerName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(x => x.MarketSlot)
                .ToList();
    }

    public void ReplaceOwnListingsForRetainer(
        ulong characterContentId,
        ulong retainerId,
        string retainerName,
        IReadOnlyList<OwnMarketListing> observed,
        DateTimeOffset observedAtUtc)
    {
        lock (gate)
        {
            var previous = document.OwnListings
                .Where(x => x.CharacterContentId == characterContentId && x.RetainerId == retainerId)
                .ToList();
            var previousBySlot = previous.ToDictionary(x => x.MarketSlot);
            var usedPreviousSlots = new HashSet<short>();

            var merged = new List<OwnMarketListing>();
            foreach (var current in observed)
            {
                OwnMarketListing? old = null;
                if (previousBySlot.TryGetValue(current.MarketSlot, out var sameSlot) &&
                    sameSlot.ItemId == current.ItemId &&
                    sameSlot.IsHq == current.IsHq)
                {
                    old = sameSlot;
                }
                else
                {
                    // The retainer sell list can be reordered. If the slot moved, preserve the
                    // oldest matching observation rather than pretending the listing is brand-new.
                    old = previous
                        .Where(x => !usedPreviousSlots.Contains(x.MarketSlot) &&
                                    x.ItemId == current.ItemId &&
                                    x.IsHq == current.IsHq &&
                                    x.Quantity == current.Quantity)
                        .OrderByDescending(x => x.UnitPrice == current.UnitPrice)
                        .ThenBy(x => x.FirstSeenUtc)
                        .FirstOrDefault();
                }

                if (old is not null)
                {
                    usedPreviousSlots.Add(old.MarketSlot);
                    var priceChanged = old.UnitPrice == current.UnitPrice
                        ? old.PriceChangedUtc
                        : observedAtUtc;
                    merged.Add(current with
                    {
                        RetainerName = retainerName,
                        FirstSeenUtc = old.FirstSeenUtc,
                        PriceChangedUtc = priceChanged,
                        LastSeenUtc = observedAtUtc,
                    });
                }
                else
                {
                    merged.Add(current with
                    {
                        RetainerName = retainerName,
                        FirstSeenUtc = observedAtUtc,
                        PriceChangedUtc = observedAtUtc,
                        LastSeenUtc = observedAtUtc,
                    });
                }
            }

            document.OwnListings.RemoveAll(x =>
                x.CharacterContentId == characterContentId && x.RetainerId == retainerId);
            document.OwnListings.AddRange(merged);
            dirty = true;
        }
    }

    public IReadOnlyList<PersonalSale> GetPersonalSales(ulong characterContentId)
    {
        lock (gate)
            return document.PersonalSales
                .Where(x => x.CharacterContentId == characterContentId)
                .OrderByDescending(x => x.SoldAtUtc)
                .ToList();
    }

    public bool AddPersonalSaleAnnouncement(PersonalSale sale)
    {
        if (sale.CharacterContentId == 0 || sale.ItemId == 0)
            return false;

        var normalized = sale with
        {
            RetainerName = string.IsNullOrWhiteSpace(sale.RetainerName) ? "Unknown retainer" : sale.RetainerName,
            BuyerName = sale.BuyerName ?? string.Empty,
            Source = PersonalSaleSource.Announcement,
        };

        lock (gate)
        {
            // Chat can occasionally be re-emitted during UI/log churn. Collapse only an extremely
            // tight identical window so two legitimate same-item sales are not accidentally merged.
            var duplicate = document.PersonalSales.Any(x =>
                x.CharacterContentId == normalized.CharacterContentId &&
                x.ItemId == normalized.ItemId &&
                x.IsHq == normalized.IsHq &&
                x.Quantity == normalized.Quantity &&
                x.NetGil == normalized.NetGil &&
                Math.Abs((x.SoldAtUtc - normalized.SoldAtUtc).TotalSeconds) <= 8);
            if (duplicate)
                return false;

            // If exact history was already captured first, do not append a late duplicate
            // notification for the same transaction.
            var exactAlreadyExists = document.PersonalSales.Any(x =>
                x.CharacterContentId == normalized.CharacterContentId &&
                (x.Source is PersonalSaleSource.History or PersonalSaleSource.Reconciled) &&
                x.ItemId == normalized.ItemId &&
                x.IsHq == normalized.IsHq &&
                (normalized.Quantity <= 0 || x.Quantity == normalized.Quantity) &&
                (normalized.NetGil <= 0 || x.NetGil == normalized.NetGil) &&
                Math.Abs((x.SoldAtUtc - normalized.SoldAtUtc).TotalMinutes) <= 10);
            if (exactAlreadyExists)
                return false;

            document.PersonalSales.Add(normalized);
            TrimPersonalSalesUnsafe();
            dirty = true;
            return true;
        }
    }

    public int MergePersonalSaleHistory(
        ulong characterContentId,
        ulong retainerId,
        string retainerName,
        IReadOnlyList<PersonalSale> observed)
    {
        if (observed.Count == 0)
            return 0;

        var changed = 0;
        lock (gate)
        {
            foreach (var sale in observed)
            {
                var normalized = sale with
                {
                    CharacterContentId = characterContentId,
                    RetainerId = retainerId,
                    RetainerName = retainerName,
                    BuyerName = sale.BuyerName ?? string.Empty,
                    Source = PersonalSaleSource.History,
                    NetGilEstimated = false,
                    QuantityEstimated = false,
                };

                // Exact packet rows are the authoritative representation. Reopening View sale
                // history should not create another copy of a transaction we already know.
                if (document.PersonalSales.Any(x => PersonalSaleKey(x) == PersonalSaleKey(normalized)))
                    continue;

                var announcementIndex = FindAnnouncementToReconcileUnsafe(normalized);
                if (announcementIndex >= 0)
                {
                    document.PersonalSales[announcementIndex] = normalized with
                    {
                        Source = PersonalSaleSource.Reconciled,
                        CapturedAtUtc = document.PersonalSales[announcementIndex].CapturedAtUtc,
                    };
                    changed++;
                    continue;
                }

                document.PersonalSales.Add(normalized);
                changed++;
            }

            if (changed > 0)
            {
                TrimPersonalSalesUnsafe();
                dirty = true;
            }
        }

        return changed;
    }

    private int FindAnnouncementToReconcileUnsafe(PersonalSale exact)
    {
        var candidates = document.PersonalSales
            .Select((sale, index) => (sale, index))
            .Where(x =>
                x.sale.CharacterContentId == exact.CharacterContentId &&
                x.sale.Source == PersonalSaleSource.Announcement &&
                x.sale.ItemId == exact.ItemId &&
                x.sale.IsHq == exact.IsHq &&
                (x.sale.Quantity <= 0 || x.sale.Quantity == exact.Quantity) &&
                (x.sale.NetGil <= 0 || x.sale.NetGil == exact.NetGil) &&
                (x.sale.RetainerId == 0 || x.sale.RetainerId == exact.RetainerId) &&
                Math.Abs((x.sale.SoldAtUtc - exact.SoldAtUtc).TotalMinutes) <= 10)
            .OrderBy(x => Math.Abs((x.sale.SoldAtUtc - exact.SoldAtUtc).TotalSeconds))
            .ToList();

        // For the normal passive path quantity + payout make the match effectively unique.
        // If a future localization prevents one of those fields being parsed, only reconcile an
        // incomplete announcement when there is exactly one plausible candidate in the tight window.
        if (candidates.Count == 0)
            return -1;
        var best = candidates[0];
        var complete = best.sale.Quantity > 0 && best.sale.NetGil > 0;
        return complete || candidates.Count == 1 ? best.index : -1;
    }

    private void TrimPersonalSalesUnsafe()
    {
        // This is a long-lived local ledger rather than a rolling market cache. Keep a generous
        // cap so passive notifications and repeated exact history visits can build useful stats.
        document.PersonalSales = document.PersonalSales
            .OrderByDescending(x => x.SoldAtUtc)
            .Take(20_000)
            .ToList();
    }

    private static string PersonalSaleKey(PersonalSale sale)
        => string.Join('|',
            sale.CharacterContentId,
            sale.RetainerId,
            sale.ItemId,
            sale.IsHq ? 1 : 0,
            sale.Quantity,
            sale.NetGil,
            sale.SoldAtUtc.ToUnixTimeSeconds(),
            (sale.BuyerName ?? string.Empty).Trim().ToUpperInvariant());

    public MarketSnapshot? GetMarket(uint worldId, uint itemId)
    {
        lock (gate)
        {
            var key = MarketKey(worldId, itemId);
            if (!document.Markets.TryGetValue(key, out var snapshot))
                return null;
            return Clone(snapshot);
        }
    }

    public IReadOnlyDictionary<uint, MarketSnapshot> GetMarkets(uint worldId, IEnumerable<uint> itemIds)
    {
        lock (gate)
        {
            var result = new Dictionary<uint, MarketSnapshot>();
            foreach (var id in itemIds.Distinct())
            {
                if (document.Markets.TryGetValue(MarketKey(worldId, id), out var snapshot))
                    result[id] = Clone(snapshot);
            }
            return result;
        }
    }

    public void MergeUniversalisCurrent(uint worldId, uint itemId, DateTimeOffset? uploadTime, List<MarketListing> listings)
    {
        lock (gate)
        {
            var snapshot = GetOrCreateUnsafe(worldId, itemId);
            // Never let an older API snapshot replace a newer direct game observation.
            if (snapshot.CurrentSource == MarketDataSource.LiveGame &&
                snapshot.ListingObservedAtUtc is { } liveAt &&
                (uploadTime is null || liveAt >= uploadTime.Value))
                return;

            snapshot.Listings = listings;
            // No upload timestamp means Universalis has no trustworthy observation time. Keep
            // this null so the experimental queue can deliberately refresh the obscure item.
            snapshot.ListingObservedAtUtc = uploadTime;
            snapshot.UniversalisLastUploadUtc = uploadTime;
            snapshot.CurrentSource = MarketDataSource.Universalis;
            dirty = true;
        }
    }

    public void MergeUniversalisHistory(uint worldId, uint itemId, DateTimeOffset? uploadTime, List<MarketSale> sales)
    {
        lock (gate)
        {
            var snapshot = GetOrCreateUnsafe(worldId, itemId);
            snapshot.Sales = MergeSales(snapshot.Sales, sales);
            snapshot.HistoryObservedAtUtc = DateTimeOffset.UtcNow;
            if (uploadTime is not null && (snapshot.UniversalisLastUploadUtc is null || uploadTime > snapshot.UniversalisLastUploadUtc))
                snapshot.UniversalisLastUploadUtc = uploadTime;
            dirty = true;
        }
    }

    public void BeginLiveObservation(uint worldId, uint itemId, DateTimeOffset at)
    {
        lock (gate)
        {
            var snapshot = GetOrCreateUnsafe(worldId, itemId);
            snapshot.Listings.Clear();
            snapshot.ListingObservedAtUtc = at;
            snapshot.CurrentSource = MarketDataSource.LiveGame;
            dirty = true;
        }
    }

    public void SetLiveHistory(uint worldId, uint itemId, IEnumerable<MarketSale> sales, DateTimeOffset at)
    {
        lock (gate)
        {
            var snapshot = GetOrCreateUnsafe(worldId, itemId);
            snapshot.Sales = MergeSales(snapshot.Sales, sales);
            snapshot.HistoryObservedAtUtc = at;
            dirty = true;
        }
    }

    public void AppendLiveListings(uint worldId, uint itemId, IEnumerable<MarketListing> listings, DateTimeOffset at)
    {
        lock (gate)
        {
            var snapshot = GetOrCreateUnsafe(worldId, itemId);
            var byId = snapshot.Listings.Where(x => x.ListingId != 0).ToDictionary(x => x.ListingId);
            foreach (var listing in listings)
            {
                if (listing.ListingId != 0)
                    byId[listing.ListingId] = listing;
                else
                    snapshot.Listings.Add(listing);
            }

            snapshot.Listings = snapshot.Listings.Where(x => x.ListingId == 0)
                .Concat(byId.Values)
                .OrderBy(x => x.PricePerUnit)
                .ToList();
            snapshot.ListingObservedAtUtc = at;
            snapshot.CurrentSource = MarketDataSource.LiveGame;
            dirty = true;
        }
    }

    public void MarkLiveUploadObservation(uint worldId, uint itemId, DateTimeOffset at)
    {
        lock (gate)
        {
            var snapshot = GetOrCreateUnsafe(worldId, itemId);
            if (snapshot.UniversalisLastUploadUtc is null || at > snapshot.UniversalisLastUploadUtc)
                snapshot.UniversalisLastUploadUtc = at;
            dirty = true;
        }
    }

    public void Flush()
    {
        string json;
        lock (gate)
        {
            if (!dirty)
                return;
            // Serialize while holding the gate so an asynchronous Universalis continuation cannot
            // mutate a list/dictionary halfway through System.Text.Json enumeration.
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
            log.Warning(ex, "Could not persist Should I Sell? cache.");
            lock (gate) dirty = true;
        }
    }

    private StoreDocument Load()
    {
        try
        {
            if (!File.Exists(path))
                return new StoreDocument();
            return JsonSerializer.Deserialize<StoreDocument>(File.ReadAllText(path), JsonOptions) ?? new StoreDocument();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not load Should I Sell? cache; starting clean.");
            return new StoreDocument();
        }
    }

    private MarketSnapshot GetOrCreateUnsafe(uint worldId, uint itemId)
    {
        var key = MarketKey(worldId, itemId);
        if (!document.Markets.TryGetValue(key, out var snapshot))
        {
            snapshot = new MarketSnapshot { WorldId = worldId, ItemId = itemId };
            document.Markets[key] = snapshot;
        }
        return snapshot;
    }

    private static string MarketKey(uint worldId, uint itemId) => $"{worldId}:{itemId}";

    private static MarketSnapshot Clone(MarketSnapshot source) => new()
    {
        WorldId = source.WorldId,
        ItemId = source.ItemId,
        ListingObservedAtUtc = source.ListingObservedAtUtc,
        HistoryObservedAtUtc = source.HistoryObservedAtUtc,
        UniversalisLastUploadUtc = source.UniversalisLastUploadUtc,
        CurrentSource = source.CurrentSource,
        Listings = source.Listings.ToList(),
        Sales = source.Sales.ToList(),
    };

    private static List<MarketSale> MergeSales(IEnumerable<MarketSale> a, IEnumerable<MarketSale> b)
    {
        // Universalis does not expose a stable sale ID in the endpoint we use. The tuple below is
        // intentionally conservative: duplicate observations of the same transaction collapse.
        return a.Concat(b)
            .GroupBy(x => (x.ItemId, x.IsHq, x.PricePerUnit, x.Quantity, x.SoldAtUtc.ToUnixTimeSeconds()))
            .Select(g => g.OrderByDescending(x => x.Source == MarketDataSource.LiveGame).First())
            .OrderByDescending(x => x.SoldAtUtc)
            .Take(5000)
            .ToList();
    }

    public sealed class StoreDocument
    {
        public List<InventoryContainerSnapshot> InventorySnapshots { get; set; } = new();
        public Dictionary<string, MarketSnapshot> Markets { get; set; } = new();
        public List<OwnMarketListing> OwnListings { get; set; } = new();
        public List<PersonalSale> PersonalSales { get; set; } = new();
    }
}
