using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ShouldISell.Services;

public sealed class TradingLedgerStore
{
    private readonly object gate = new();
    private readonly string path;
    private readonly IPluginLog log;
    private LedgerDocument document;
    private bool dirty;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public TradingLedgerStore(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        Directory.CreateDirectory(pluginInterface.ConfigDirectory.FullName);
        path = Path.Combine(pluginInterface.ConfigDirectory.FullName, "buy-data.json");
        document = Load();
    }

    public IReadOnlyList<PersonalPurchase> GetPurchases(ulong characterContentId)
    {
        lock (gate)
            return document.Purchases
                .Where(x => x.CharacterContentId == characterContentId)
                .OrderBy(x => x.PurchasedAtUtc)
                .ToList();
    }

    public void AddPurchase(PersonalPurchase purchase, bool flush = true)
    {
        if (purchase.CharacterContentId == 0 || purchase.ItemId == 0 || purchase.Quantity <= 0)
            return;

        lock (gate)
        {
            var duplicate = document.Purchases.Any(x =>
                x.CharacterContentId == purchase.CharacterContentId &&
                x.ListingId != 0 &&
                x.ListingId == purchase.ListingId &&
                x.Quantity == purchase.Quantity &&
                Math.Abs((x.PurchasedAtUtc - purchase.PurchasedAtUtc).TotalSeconds) <= 30);
            if (duplicate)
                return;

            document.Purchases.Add(purchase);
            document.Purchases = document.Purchases
                .OrderByDescending(x => x.PurchasedAtUtc)
                .Take(20_000)
                .OrderBy(x => x.PurchasedAtUtc)
                .ToList();
            dirty = true;
        }

        if (flush)
            Flush();
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
            log.Warning(ex, "Could not persist Should I Buy? trading ledger.");
            lock (gate) dirty = true;
        }
    }

    private LedgerDocument Load()
    {
        try
        {
            if (!File.Exists(path))
                return new LedgerDocument();
            return JsonSerializer.Deserialize<LedgerDocument>(File.ReadAllText(path), JsonOptions) ?? new LedgerDocument();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not load Should I Buy? trading ledger; starting clean.");
            return new LedgerDocument();
        }
    }

    private sealed class LedgerDocument
    {
        public List<PersonalPurchase> Purchases { get; set; } = new();
    }
}
