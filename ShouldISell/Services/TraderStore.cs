using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ShouldISell.Services;

public sealed class TraderStore
{
    private readonly object gate = new();
    private readonly string path;
    private readonly IPluginLog log;
    private TraderDocument document;
    private bool dirty;
    private long tradeRevision;

    public long TradeRevision => Interlocked.Read(ref tradeRevision);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public TraderStore(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        Directory.CreateDirectory(pluginInterface.ConfigDirectory.FullName);
        path = Path.Combine(pluginInterface.ConfigDirectory.FullName, "trader-data.json");
        document = Load();
    }

    public IReadOnlyList<PersonalPurchase> GetPurchases(ulong characterContentId)
    {
        lock (gate)
            return document.Purchases
                .Where(x => x.CharacterContentId == characterContentId)
                .OrderByDescending(x => x.PurchasedAtUtc)
                .ToList();
    }

    public bool AddPurchase(PersonalPurchase purchase, bool flush = true)
    {
        if (purchase.CharacterContentId == 0 || purchase.ItemId == 0 || purchase.Quantity <= 0 || purchase.TotalCost <= 0)
            return false;

        lock (gate)
        {
            var duplicate = document.Purchases.Any(x =>
                x.CharacterContentId == purchase.CharacterContentId &&
                x.ItemId == purchase.ItemId &&
                x.IsHq == purchase.IsHq &&
                x.Quantity == purchase.Quantity &&
                x.TotalCost == purchase.TotalCost &&
                x.SourceKind == purchase.SourceKind &&
                ((purchase.ListingId != 0 && x.ListingId == purchase.ListingId) ||
                 Math.Abs((x.PurchasedAtUtc - purchase.PurchasedAtUtc).TotalSeconds) <= 5));
            if (duplicate)
                return false;

            document.Purchases.Add(purchase);
            document.Purchases = document.Purchases
                .OrderByDescending(x => x.PurchasedAtUtc)
                .Take(25_000)
                .ToList();
            document.Version = Math.Max(document.Version, 4);
            MarkDirtyUnsafe(affectsTrades: true);
        }

        if (flush)
            Flush();
        return true;
    }

    public IReadOnlyList<GilFlowEntry> GetGilFlows(ulong characterContentId)
    {
        lock (gate)
            return document.GilFlows
                .Where(x => x.CharacterContentId == characterContentId)
                .OrderByDescending(x => x.AtUtc)
                .ToList();
    }

    public bool AddGilFlow(GilFlowEntry flow, bool flush = true)
    {
        if (string.IsNullOrWhiteSpace(flow.Id) || flow.CharacterContentId == 0 || flow.Amount == 0)
            return false;

        lock (gate)
        {
            if (document.GilFlows.Any(x => string.Equals(x.Id, flow.Id, StringComparison.Ordinal)))
                return false;

            document.GilFlows.Add(flow);
            document.GilFlows = document.GilFlows
                .OrderByDescending(x => x.AtUtc)
                .Take(50_000)
                .ToList();
            document.Version = Math.Max(document.Version, 2);
            MarkDirtyUnsafe();
        }

        if (flush)
            Flush();
        return true;
    }

    public bool UpdateGilFlowClassification(
        string id,
        GilFlowCategory category,
        string source,
        bool autoClassified,
        string note,
        bool flush = true)
    {
        lock (gate)
        {
            var index = document.GilFlows.FindIndex(x => string.Equals(x.Id, id, StringComparison.Ordinal));
            if (index < 0)
                return false;

            var current = document.GilFlows[index];
            document.GilFlows[index] = current with
            {
                Category = category,
                Source = string.IsNullOrWhiteSpace(source) ? current.Source : source,
                AutoClassified = autoClassified,
                Note = note,
            };
            MarkDirtyUnsafe();
        }

        if (flush)
            Flush();
        return true;
    }


    public bool TryClassifyRecentMatchingOutflow(
        ulong characterContentId,
        long totalCost,
        GilFlowCategory category,
        string source,
        string note,
        TimeSpan? window = null,
        bool flush = true)
    {
        if (characterContentId == 0 || totalCost <= 0)
            return false;

        var now = DateTimeOffset.UtcNow;
        var tolerance = window ?? TimeSpan.FromMinutes(5);
        var changed = false;
        lock (gate)
        {
            var match = document.GilFlows
                .Select((flow, index) => (flow, index))
                .Where(x => x.flow.CharacterContentId == characterContentId &&
                            x.flow.Amount == -totalCost &&
                            x.flow.Category == GilFlowCategory.Unclassified &&
                            (now - x.flow.AtUtc).Duration() <= tolerance)
                .OrderBy(x => (now - x.flow.AtUtc).Duration())
                .FirstOrDefault();

            if (match.flow is null)
                return false;

            document.GilFlows[match.index] = match.flow with
            {
                Category = category,
                Source = string.IsNullOrWhiteSpace(source) ? match.flow.Source : source,
                AutoClassified = false,
                Note = note,
            };
            document.Version = Math.Max(document.Version, 3);
            MarkDirtyUnsafe();
            changed = true;
        }

        if (changed && flush)
            Flush();
        return changed;
    }

    public string GetPurchaseKey(PersonalPurchase purchase)
        => $"{purchase.CharacterContentId}:{purchase.PurchasedAtUtc.ToUniversalTime().Ticks}:{purchase.ListingId}:{purchase.ItemId}:{(purchase.IsHq ? 1 : 0)}:{purchase.Quantity}:{purchase.TotalCost}";

    public bool IsPurchaseExcluded(PersonalPurchase purchase)
    {
        var key = GetPurchaseKey(purchase);
        lock (gate)
            return document.ExcludedPurchaseKeys.Any(x => string.Equals(x, key, StringComparison.Ordinal));
    }

    public bool SetPurchaseExcluded(PersonalPurchase purchase, bool excluded, bool flush = true)
    {
        var key = GetPurchaseKey(purchase);
        lock (gate)
        {
            var exists = document.ExcludedPurchaseKeys.Any(x => string.Equals(x, key, StringComparison.Ordinal));
            if (excluded == exists)
                return false;

            if (excluded)
                document.ExcludedPurchaseKeys.Add(key);
            else
                document.ExcludedPurchaseKeys.RemoveAll(x => string.Equals(x, key, StringComparison.Ordinal));

            document.Version = Math.Max(document.Version, 2);
            MarkDirtyUnsafe(affectsTrades: true);
        }

        if (flush)
            Flush();
        return true;
    }

    public void Flush()
    {
        string json;
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
            log.Warning(ex, "Could not persist Should I? trader ledger.");
            lock (gate) dirty = true;
        }
    }

    private void MarkDirtyUnsafe(bool affectsTrades = false)
    {
        dirty = true;
        if (affectsTrades)
            Interlocked.Increment(ref tradeRevision);
    }

    private TraderDocument Load()
    {
        try
        {
            if (!File.Exists(path))
                return new TraderDocument();
            return JsonSerializer.Deserialize<TraderDocument>(File.ReadAllText(path), JsonOptions) ?? new TraderDocument();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not load Should I? trader ledger; starting with an empty purchase history.");
            return new TraderDocument();
        }
    }

    public sealed class TraderDocument
    {
        public int Version { get; set; } = 4;
        public List<PersonalPurchase> Purchases { get; set; } = new();
        public List<GilFlowEntry> GilFlows { get; set; } = new();
        public List<string> ExcludedPurchaseKeys { get; set; } = new();
    }
}
