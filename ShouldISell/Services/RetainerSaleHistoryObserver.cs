using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ShouldISell.Services;

/// <summary>
/// Captures the game's exact retainer sale-history rows when the player opens
/// "View sale history". The packet contains seller after-tax net gil, exact
/// timestamps and buyer names. A signature miss after a game patch degrades
/// this feature without preventing the rest of Should I Sell? from loading.
/// </summary>
public sealed unsafe class RetainerSaleHistoryObserver : IDisposable
{
    private const string ProcessRetainerHistorySignature = "40 53 56 57 41 57 48 83 EC 38 48 8B F1";
    private const int MaxHistoryEntries = 20;
    private const int EntriesOffset = 8;
    private const int EntrySize = 52;
    private const int BuyerNameLength = 32;
    private const int MaxQuantity = 999_999;
    private const long MaxUnitPrice = 999_999_999L;
    private const long MinPlausibleUnixTime = 1_262_304_000L; // 2010-01-01 UTC

    private readonly IPlayerState playerState;
    private readonly LocalStore store;
    private readonly IPluginLog log;
    private readonly Hook<ProcessRetainerHistoryDelegate>? hook;

    private delegate nint ProcessRetainerHistoryDelegate(nint agent, nint packetData);

    public RetainerSaleHistoryObserver(
        IGameInteropProvider gameInterop,
        IPlayerState playerState,
        LocalStore store,
        IPluginLog log)
    {
        this.playerState = playerState;
        this.store = store;
        this.log = log;

        try
        {
            hook = gameInterop.HookFromSignature<ProcessRetainerHistoryDelegate>(
                ProcessRetainerHistorySignature,
                OnProcessRetainerHistory);
            hook.Enable();
        }
        catch (Exception ex)
        {
            IsDegraded = true;
            log.Warning(ex, "Retainer sale-history signature was not found; personal Sales History capture is unavailable until the signature is updated.");
        }
    }

    public bool IsDegraded { get; }

    public void Dispose() => hook?.Dispose();

    private nint OnProcessRetainerHistory(nint agent, nint packetData)
    {
        var result = hook!.Original(agent, packetData);
        try
        {
            Capture(packetData);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not parse retainer sale history.");
        }

        return result;
    }

    private void Capture(nint packetData)
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0 || packetData == 0)
            return;

        var manager = RetainerManager.Instance();
        var active = manager == null ? null : manager->GetActiveRetainer();
        if (active == null || active->RetainerId == 0)
        {
            log.Warning("Retainer sale history arrived without an active retainer; skipping unattributed rows.");
            return;
        }

        var retainerId = active->RetainerId;
        var retainerName = ReadFixedUtf8((byte*)active + 0x08, 32);
        var capturedAt = DateTimeOffset.UtcNow;
        var maxUnix = capturedAt.AddDays(1).ToUnixTimeSeconds();
        var sales = new List<PersonalSale>(MaxHistoryEntries);

        for (var index = 0; index < MaxHistoryEntries; index++)
        {
            var entry = (RetainerHistoryEntry*)((byte*)packetData + EntriesOffset + (index * EntrySize));
            if (entry->ItemId == 0)
                break;
            if (entry->IsMannequin != 0)
                continue;

            var quantity = (int)entry->Quantity;
            var netGil = (long)entry->Price;
            var unix = (long)entry->UnixTimeSeconds;
            if (quantity < 1 || quantity > MaxQuantity || netGil < 1 ||
                netGil > MaxUnitPrice * quantity || unix < MinPlausibleUnixTime || unix > maxUnix)
            {
                log.Warning("Skipping malformed retainer sale-history row {Index}.", index);
                continue;
            }

            var buyer = ReadFixedUtf8(entry->BuyerNameBytes, BuyerNameLength);
            sales.Add(new PersonalSale(
                playerState.ContentId,
                retainerId,
                retainerName,
                entry->ItemId,
                quantity,
                entry->IsHq != 0,
                netGil,
                DateTimeOffset.FromUnixTimeSeconds(unix),
                buyer,
                capturedAt));
        }

        if (sales.Count == 0)
            return;

        var added = store.MergePersonalSaleHistory(playerState.ContentId, retainerId, retainerName, sales);
        if (added > 0)
        {
            store.Flush();
            log.Debug("Captured {Added} new exact personal sale(s) for retainer {RetainerId}.", added, retainerId);
        }
    }

    private static string ReadFixedUtf8(byte* ptr, int maxLength)
    {
        if (ptr == null || maxLength <= 0)
            return string.Empty;

        var length = 0;
        while (length < maxLength && ptr[length] != 0)
            length++;
        return length == 0 ? string.Empty : Encoding.UTF8.GetString(new ReadOnlySpan<byte>(ptr, length)).Trim();
    }

    [StructLayout(LayoutKind.Explicit, Size = EntrySize)]
    private struct RetainerHistoryEntry
    {
        [FieldOffset(0)] public uint ItemId;
        [FieldOffset(4)] public uint Price;
        [FieldOffset(8)] public uint UnixTimeSeconds;
        [FieldOffset(12)] public uint Quantity;
        [FieldOffset(16)] public byte IsHq;
        [FieldOffset(18)] public byte IsMannequin;
        [FieldOffset(19)] public fixed byte BuyerNameBytes[BuyerNameLength];
    }
}
