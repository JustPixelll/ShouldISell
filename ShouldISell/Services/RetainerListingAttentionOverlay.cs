using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ShouldISell.Services;

/// <summary>
/// A non-invasive companion strip beside FFXIV's RetainerSellList. It deliberately does not
/// rewrite native nodes: flagged listings are shown with an amber ! while the market list is open.
/// </summary>
public sealed unsafe class RetainerListingAttentionOverlay
{
    private static readonly Vector4 Amber = new(1.00f, 0.62f, 0.24f, 1.00f);

    private readonly IGameGui gameGui;
    private readonly IPlayerState playerState;
    private readonly MarketDataCoordinator coordinator;
    private readonly IPluginLog log;

    private DateTimeOffset nextRefresh = DateTimeOffset.MinValue;
    private ulong cachedRetainerId;
    private IReadOnlyList<RatedOwnListing> cachedRows = Array.Empty<RatedOwnListing>();

    public RetainerListingAttentionOverlay(
        IGameGui gameGui,
        IPlayerState playerState,
        MarketDataCoordinator coordinator,
        IPluginLog log)
    {
        this.gameGui = gameGui;
        this.playerState = playerState;
        this.coordinator = coordinator;
        this.log = log;
    }

    public void Draw()
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0)
            return;

        try
        {
            var address = gameGui.GetAddonByName("RetainerSellList", 1).Address;
            if (address == nint.Zero)
                return;

            var addon = (AtkUnitBase*)address;
            if (!addon->IsVisible || addon->UldManager.LoadedState != AtkLoadState.Loaded)
                return;

            var retainerId = GetActiveRetainerId();
            if (retainerId == 0)
                return;

            var now = DateTimeOffset.UtcNow;
            if (retainerId != cachedRetainerId || now >= nextRefresh)
            {
                cachedRows = coordinator.GetRatedOwnListings()
                    .Where(x => x.Listing.RetainerId == retainerId && ListingGuidance.NeedsAttention(x))
                    .OrderBy(x => x.Listing.MarketSlot)
                    .ToList();
                cachedRetainerId = retainerId;
                nextRefresh = now.AddMilliseconds(1500);
            }

            if (cachedRows.Count == 0)
                return;

            DrawPanel(addon, cachedRows);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not draw retainer listing attention overlay.");
        }
    }

    private static void DrawPanel(AtkUnitBase* addon, IReadOnlyList<RatedOwnListing> rows)
    {
        const float panelWidth = 310f;
        var scale = addon->Scale <= 0 ? 1f : addon->Scale;
        var addonWidth = addon->RootNode == null ? 420f : addon->RootNode->Width * scale;
        var display = ImGui.GetIO().DisplaySize;
        var x = addon->X + addonWidth + 8f;
        if (x + panelWidth > display.X - 8f)
            x = Math.Max(8f, addon->X - panelWidth - 8f);
        var y = Math.Max(8f, addon->Y + 34f * scale);

        ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(panelWidth, 0), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.92f);

        var flags = ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoFocusOnAppearing |
                    ImGuiWindowFlags.NoNav;

        if (!ImGui.Begin("##ShouldISellRetainerAttention", flags))
        {
            ImGui.End();
            return;
        }

        ImGui.TextColored(Amber, $"!  {rows.Count} listing{(rows.Count == 1 ? "" : "s")} need attention");
        ImGui.Separator();
        foreach (var row in rows.Take(20))
        {
            ImGui.TextColored(Amber, "!");
            ImGui.SameLine();
            ImGui.TextDisabled($"#{row.Listing.MarketSlot + 1}");
            ImGui.SameLine();
            ImGui.TextUnformatted(row.Item.Name + (row.Listing.IsHq ? " [HQ]" : string.Empty));

            var guidance = new List<string>(2);
            if (ListingGuidance.NeedsPriceChange(row))
                guidance.Add(ListingGuidance.PriceChangeText(row));
            if (ListingGuidance.NeedsStackChange(row) && row.Rating?.StackRecommendation is { } stack)
                guidance.Add($"stack {row.Listing.Quantity:N0} → {stack.RecommendedStackSize:N0}");

            if (guidance.Count > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(string.Join(" • ", guidance));
            }
        }

        ImGui.End();
    }

    private static ulong GetActiveRetainerId()
    {
        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady)
            return 0;
        var active = manager->GetActiveRetainer();
        return active == null ? 0 : active->RetainerId;
    }
}
