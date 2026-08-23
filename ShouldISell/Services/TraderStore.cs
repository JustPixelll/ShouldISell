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
                ((purchase.ListingId != 0 && x.ListingId == purchase.ListingId) ||
                 Math.Abs((x.PurchasedAtUtc - purchase.PurchasedAtUtc).TotalSeconds) <= 5));
            if (duplicate)
                return false;

            document.Purchases.Add(purchase);
            document.Purchases = document.Purchases
                .OrderByDescending(x => x.PurchasedAtUtc)
                .Take(25_000)
                .ToList();
            dirty = true;
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
        public int Version { get; set; } = 1;
        public List<PersonalPurchase> Purchases { get; set; } = new();
    }
}
