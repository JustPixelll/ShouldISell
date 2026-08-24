using Dalamud.Bindings.ImGui;

namespace ShouldISell.Windows;

public sealed partial class MainWindow
{
    private bool forceUniversalisRefresh;

    private void DrawInventoryCoverageWarning()
    {
        if (!plugin.InventoryCoverage.ShouldWarn)
            return;

        ImGui.PushTextWrapPos();
        ImGui.TextWrapped("Inventory coverage notice: Should I? can only remember containers FFXIV has actually loaded while the plugin is running. Open your Inventory once so the current bags are observed. Retainers and saddlebags are remembered when you open them normally; Should I? does not open or automate those interfaces for you.");
        ImGui.PopTextWrapPos();
        ImGui.SameLine();
        if (ImGui.SmallButton("Dismiss permanently##inventory-coverage"))
            plugin.InventoryCoverage.DismissPermanently();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hide this reminder permanently. It will not reappear in future sessions.");
        ImGui.Separator();
    }

    private void DrawUniversalisRefresh()
    {
        ImGui.TextWrapped("Refresh cached market data from Universalis for only the inventory/listing scope you care about. This page uses the Universalis API only; it never asks FFXIV to perform Market Board searches and never walks a native item-search queue.");
        ImGui.Spacing();

        ImGui.Checkbox("Force refresh even when cached data is still inside the normal TTL", ref forceUniversalisRefresh);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Normally Should I? skips current/history data that is still fresh according to the configured Universalis TTLs. Force refresh ignores those TTL checks for the selected scope.");
        ImGui.Spacing();

        DrawUniversalisScopeButton(
            "Current inventory",
            plugin.Inventory.GetUniqueMarketablePlayerInventoryItemIds,
            "Current inventory");
        ImGui.SameLine();
        DrawUniversalisScopeButton(
            "Saddlebags",
            plugin.Inventory.GetUniqueMarketableSaddlebagItemIds,
            "Saddlebags");
        ImGui.SameLine();
        DrawUniversalisScopeButton(
            "Inventory + saddlebags",
            plugin.Inventory.GetUniqueMarketablePlayerAndSaddlebagsItemIds,
            "Inventory + saddlebags");

        DrawUniversalisScopeButton(
            "Known retainer inventories",
            plugin.Inventory.GetUniqueMarketableKnownRetainerInventoryItemIds,
            "Known retainer inventories");
        ImGui.SameLine();
        DrawUniversalisScopeButton(
            "Active retainer inventory",
            plugin.Inventory.GetUniqueMarketableActiveRetainerInventoryItemIds,
            "Active retainer inventory");

        DrawUniversalisScopeButton(
            "Current retainer listings",
            plugin.Inventory.GetUniqueMarketableCurrentListingItemIds,
            "Current retainer listings");
        ImGui.SameLine();
        DrawUniversalisScopeButton(
            "All known owned items",
            plugin.Inventory.GetUniqueMarketableItemIds,
            "All known owned items");

        ImGui.Spacing();
        ImGui.TextDisabled(plugin.Coordinator.FetchStatus);
        if (plugin.Coordinator.LastFetchCompletedUtc is { } completed)
            ImGui.TextDisabled($"Last completed Universalis refresh: {completed.ToLocalTime():yyyy-MM-dd HH:mm:ss}");

        ImGui.Separator();
        ImGui.TextWrapped("Scope counts come from Should I?'s remembered inventory snapshots. If a bag/retainer has never been opened while Should I? was present, there is intentionally nothing to refresh for that container yet.");
    }

    private void DrawUniversalisScopeButton(string label, Func<IReadOnlyList<uint>> getIds, string scopeLabel)
    {
        var ids = getIds();
        if (plugin.Coordinator.IsFetching)
            ImGui.BeginDisabled();
        if (ImGui.Button($"{label} ({ids.Count:N0})##universalis-{scopeLabel}"))
            _ = plugin.Coordinator.RefreshScopeFromUniversalisAsync(ids, scopeLabel, forceUniversalisRefresh);
        if (plugin.Coordinator.IsFetching)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Refresh Universalis current listings + history for {ids.Count:N0} known marketable item ID(s) in this scope.");
    }
}
