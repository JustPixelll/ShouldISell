using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ShouldISell.Services;

public sealed unsafe class InventoryScanner
{
    private static readonly InventoryType[] PlayerContainers =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.SaddleBag1,
        InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1,
        InventoryType.PremiumSaddleBag2,
    ];

    private static readonly InventoryType[] RetainerContainers =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
        InventoryType.RetainerMarket,
    ];

    private readonly IPlayerState playerState;
    private readonly GameItemCatalog catalog;
    private readonly LocalStore store;
    private readonly IPluginLog log;
    private DateTimeOffset lastFlush = DateTimeOffset.MinValue;

    public InventoryScanner(IPlayerState playerState, GameItemCatalog catalog, LocalStore store, IPluginLog log)
    {
        this.playerState = playerState;
        this.catalog = catalog;
        this.store = store;
        this.log = log;
    }

    public void ScanLoadedContainers(bool forceFlush = false)
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0)
            return;

        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null)
                return;

            var now = DateTimeOffset.UtcNow;
            foreach (var type in PlayerContainers)
                SnapshotIfLoaded(manager, type, InventoryOwnerKind.Player, playerState.ContentId, playerState.CharacterName, now);

            var retainerManager = RetainerManager.Instance();
            if (retainerManager != null && retainerManager->IsReady)
            {
                var active = retainerManager->GetActiveRetainer();
                if (active != null && active->RetainerId != 0)
                {
                    var name = ReadFixedUtf8((byte*)active + 0x08, 32);
                    foreach (var type in RetainerContainers)
                        SnapshotIfLoaded(manager, type, InventoryOwnerKind.Retainer, active->RetainerId, name, now);
                    SnapshotOwnMarketListings(manager, active->RetainerId, name, now);
                }
            }

            if (forceFlush || now - lastFlush >= TimeSpan.FromSeconds(15))
            {
                store.Flush();
                lastFlush = now;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Inventory scan failed.");
        }
    }

    public IReadOnlyList<OwnedStack> GetKnownOwnedStacks()
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0)
            return Array.Empty<OwnedStack>();

        ScanLoadedContainers();
        return store.GetInventorySnapshots(playerState.ContentId)
            .SelectMany(x => x.Items)
            .Where(x => x.ItemId != 0 && x.Quantity > 0 && catalog.IsMarketable(x.ItemId))
            .ToList();
    }

    public IReadOnlyList<uint> GetUniqueMarketableItemIds()
        => GetKnownOwnedStacks().Select(x => x.ItemId).Distinct().Order().ToList();

    public IReadOnlyList<uint> GetUniqueMarketablePlayerInventoryItemIds()
        => GetKnownOwnedStacks()
            .Where(x => x.OwnerKind == InventoryOwnerKind.Player && IsNormalPlayerInventory(x.Container))
            .Select(x => x.ItemId)
            .Distinct()
            .Order()
            .ToList();

    public IReadOnlyList<uint> GetUniqueMarketableSaddlebagItemIds()
        => GetKnownOwnedStacks()
            .Where(x => x.OwnerKind == InventoryOwnerKind.Player && IsSaddlebag(x.Container))
            .Select(x => x.ItemId)
            .Distinct()
            .Order()
            .ToList();

    public IReadOnlyList<uint> GetUniqueMarketablePlayerAndSaddlebagsItemIds()
        => GetKnownOwnedStacks()
            .Where(x => x.OwnerKind == InventoryOwnerKind.Player)
            .Select(x => x.ItemId)
            .Distinct()
            .Order()
            .ToList();

    public IReadOnlyList<uint> GetUniqueMarketableKnownRetainerInventoryItemIds()
        => GetKnownOwnedStacks()
            .Where(x => x.OwnerKind == InventoryOwnerKind.Retainer && IsRetainerInventoryPage(x.Container))
            .Select(x => x.ItemId)
            .Distinct()
            .Order()
            .ToList();

    public IReadOnlyList<uint> GetUniqueMarketableCurrentListingItemIds()
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0)
            return Array.Empty<uint>();
        ScanLoadedContainers();
        return store.GetOwnListings(playerState.ContentId)
            .Select(x => x.ItemId)
            .Where(catalog.IsMarketable)
            .Distinct()
            .Order()
            .ToList();
    }

    public IReadOnlyList<uint> GetUniqueMarketableActiveRetainerInventoryItemIds()
    {
        var activeRetainerId = GetActiveRetainerId();
        if (activeRetainerId == 0)
            return Array.Empty<uint>();

        return GetKnownOwnedStacks()
            .Where(x => x.OwnerKind == InventoryOwnerKind.Retainer &&
                        x.OwnerId == activeRetainerId &&
                        IsRetainerInventoryPage(x.Container))
            .Select(x => x.ItemId)
            .Distinct()
            .Order()
            .ToList();
    }

    public IReadOnlyList<uint> GetUniqueMarketableActiveRetainerMarketItemIds()
    {
        var activeRetainerId = GetActiveRetainerId();
        if (activeRetainerId == 0)
            return Array.Empty<uint>();

        return GetKnownOwnedStacks()
            .Where(x => x.OwnerKind == InventoryOwnerKind.Retainer &&
                        x.OwnerId == activeRetainerId &&
                        x.Container == InventoryType.RetainerMarket)
            .Select(x => x.ItemId)
            .Distinct()
            .Order()
            .ToList();
    }

    private static bool IsNormalPlayerInventory(InventoryType type)
        => type is InventoryType.Inventory1 or InventoryType.Inventory2 or InventoryType.Inventory3 or InventoryType.Inventory4;

    private static bool IsSaddlebag(InventoryType type)
        => type is InventoryType.SaddleBag1 or InventoryType.SaddleBag2 or
                   InventoryType.PremiumSaddleBag1 or InventoryType.PremiumSaddleBag2;

    private static bool IsRetainerInventoryPage(InventoryType type)
        => type is InventoryType.RetainerPage1 or InventoryType.RetainerPage2 or InventoryType.RetainerPage3 or
                   InventoryType.RetainerPage4 or InventoryType.RetainerPage5 or InventoryType.RetainerPage6 or
                   InventoryType.RetainerPage7;

    private static ulong GetActiveRetainerId()
    {
        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady)
            return 0;
        var active = manager->GetActiveRetainer();
        return active == null ? 0 : active->RetainerId;
    }

    private void SnapshotOwnMarketListings(
        InventoryManager* manager,
        ulong retainerId,
        string retainerName,
        DateTimeOffset now)
    {
        var container = manager->GetInventoryContainer(InventoryType.RetainerMarket);
        if (container == null || !container->IsLoaded)
            return;

        var observed = new List<OwnMarketListing>();
        for (var slot = 0; slot < container->Size; slot++)
        {
            var invItem = container->GetInventorySlot(slot);
            if (invItem == null || invItem->ItemId == 0 || invItem->Quantity <= 0 || invItem->IsSymbolic)
                continue;

            var itemId = invItem->GetBaseItemId();
            if (itemId == 0)
                itemId = invItem->ItemId;

            var marketSlot = checked((short)slot);
            var rawUnitPrice = manager->GetRetainerMarketPrice(marketSlot);
            var unitPrice = rawUnitPrice > uint.MaxValue ? uint.MaxValue : (uint)rawUnitPrice;
            observed.Add(new OwnMarketListing(
                playerState.ContentId,
                retainerId,
                retainerName,
                marketSlot,
                itemId,
                invItem->Quantity,
                invItem->IsHighQuality(),
                unitPrice,
                now,
                now,
                now));
        }

        store.ReplaceOwnListingsForRetainer(
            playerState.ContentId, retainerId, retainerName, observed, now);
    }

    private void SnapshotIfLoaded(
        InventoryManager* manager,
        InventoryType type,
        InventoryOwnerKind ownerKind,
        ulong ownerId,
        string ownerName,
        DateTimeOffset now)
    {
        var container = manager->GetInventoryContainer(type);
        if (container == null || !container->IsLoaded)
            return;

        var items = new List<OwnedStack>();
        for (var slot = 0; slot < container->Size; slot++)
        {
            var invItem = container->GetInventorySlot(slot);
            if (invItem == null || invItem->ItemId == 0 || invItem->Quantity <= 0 || invItem->IsSymbolic)
                continue;

            var itemId = invItem->GetBaseItemId();
            if (itemId == 0)
                itemId = invItem->ItemId;

            items.Add(new OwnedStack(
                playerState.ContentId,
                ownerKind,
                ownerId,
                ownerName,
                type,
                (ushort)slot,
                itemId,
                invItem->Quantity,
                invItem->IsHighQuality(),
                now));
        }

        store.UpsertInventoryContainer(new InventoryContainerSnapshot(
            playerState.ContentId,
            ownerKind,
            ownerId,
            ownerName,
            type,
            now,
            items));
    }

    private static string ReadFixedUtf8(byte* ptr, int maxBytes)
    {
        var length = 0;
        while (length < maxBytes && ptr[length] != 0)
            length++;
        return length == 0 ? string.Empty : Marshal.PtrToStringUTF8((nint)ptr, length) ?? string.Empty;
    }
}
