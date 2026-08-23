$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Get-Content $Path -Raw
    if (-not $text.Contains($Old)) {
        throw "Expected patch text not found in $Path`n--- OLD ---`n$Old"
    }
    $text = $text.Replace($Old, $New)
    Set-Content $Path $text -Encoding UTF8
}

$models = 'ShouldISell/Models.cs'
$coordinator = 'ShouldISell/Services/MarketDataCoordinator.cs'
$window = 'ShouldISell/Windows/MainWindow.cs'
$project = 'ShouldISell/ShouldISell.csproj'

Replace-Exact $models @'
public sealed record RatedOwnedItem(
    ItemInfo Item,
    bool IsHq,
    int Quantity,
    IReadOnlyList<string> Locations,
    SellRating? Rating,
    DateTimeOffset? InventoryObservedAtUtc);
'@ @'
public sealed record OwnedLocationSummary(
    InventoryOwnerKind OwnerKind,
    ulong OwnerId,
    string OwnerName,
    int Quantity);

public sealed record RatedOwnedItem(
    ItemInfo Item,
    bool IsHq,
    int Quantity,
    IReadOnlyList<string> Locations,
    IReadOnlyList<OwnedLocationSummary> Ownership,
    SellRating? Rating,
    DateTimeOffset? InventoryObservedAtUtc);
'@

Replace-Exact $coordinator @'
                var locations = group
                    .GroupBy(x => (x.OwnerKind, x.OwnerId, x.OwnerName, x.Container))
                    .Select(g => FormatLocation(g.Key.OwnerKind, g.Key.OwnerName, g.Key.Container, g.Sum(x => x.Quantity)))
                    .ToList();
                return new RatedOwnedItem(
                    item,
                    group.Key.IsHq,
                    quantity,
                    locations,
                    rating,
                    group.Max(x => (DateTimeOffset?)x.ObservedAtUtc));
'@ @'
                var locations = group
                    .GroupBy(x => (x.OwnerKind, x.OwnerId, x.OwnerName, x.Container))
                    .Select(g => FormatLocation(g.Key.OwnerKind, g.Key.OwnerName, g.Key.Container, g.Sum(x => x.Quantity)))
                    .ToList();
                var ownership = group
                    .GroupBy(x => (x.OwnerKind, x.OwnerId, x.OwnerName))
                    .Select(g => new OwnedLocationSummary(
                        g.Key.OwnerKind,
                        g.Key.OwnerId,
                        g.Key.OwnerName,
                        g.Sum(x => x.Quantity)))
                    .ToList();
                return new RatedOwnedItem(
                    item,
                    group.Key.IsHq,
                    quantity,
                    locations,
                    ownership,
                    rating,
                    group.Max(x => (DateTimeOffset?)x.ObservedAtUtc));
'@

Replace-Exact $window @'
public sealed partial class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
'@ @'
public sealed partial class MainWindow : Window, IDisposable
{
    private enum OwnedLocationFilter
    {
        AllLocations,
        PlayerInventory,
        AllRetainers,
        SpecificRetainer,
    }

    private readonly Plugin plugin;
'@

Replace-Exact $window @'
    private long ownedNetMin;
    private long ownedNetMax = 999_999_999_999;
'@ @'
    private long ownedNetMin;
    private long ownedNetMax = 999_999_999_999;
    private OwnedLocationFilter ownedLocationFilter = OwnedLocationFilter.AllLocations;
    private ulong ownedRetainerFilterId;
'@

Replace-Exact $window @'
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##item-search", "Search item name...", ref search, 128);
        DrawOwnedFilters();

        var listedVariants = plugin.Coordinator.GetKnownOwnListedVariants();
        var allItems = plugin.Coordinator.GetRatedOwnedItems()
            .Where(x => string.IsNullOrWhiteSpace(search) || x.Item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        var items = allItems.Where(PassesOwnedFilters).ToList();
'@ @'
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##item-search", "Search item name...", ref search, 128);

        var ratedItems = plugin.Coordinator.GetRatedOwnedItems().ToList();
        DrawOwnedFilters(ratedItems);

        var listedVariants = plugin.Coordinator.GetKnownOwnListedVariants();
        var allItems = ratedItems
            .Where(x => string.IsNullOrWhiteSpace(search) || x.Item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        var items = allItems
            .Where(PassesOwnedFilters)
            .Where(PassesOwnedLocationFilter)
            .ToList();
'@

Replace-Exact $window 'ImGui.TextUnformatted(row.Quantity.ToString("N0"));' 'ImGui.TextUnformatted(VisibleOwnedQuantity(row).ToString("N0"));'

Replace-Exact $window @'
    private void DrawOwnedFilters()
    {
        if (!ImGui.CollapsingHeader("Filters##owned", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var width = 165 * ImGuiHelpers.GlobalScale;
'@ @'
    private void DrawOwnedFilters(IReadOnlyList<RatedOwnedItem> ratedItems)
    {
        if (!ImGui.CollapsingHeader("Filters##owned", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawOwnedLocationFilter(ratedItems);
        ImGui.Spacing();

        var width = 165 * ImGuiHelpers.GlobalScale;
'@

Replace-Exact $window @'
            ownedStarsMax = 5;
            ownedNetMin = 0;
            ownedNetMax = 999_999_999_999;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reset rating, star and expected-net filters.");
'@ @'
            ownedStarsMax = 5;
            ownedNetMin = 0;
            ownedNetMax = 999_999_999_999;
            ownedLocationFilter = OwnedLocationFilter.AllLocations;
            ownedRetainerFilterId = 0;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reset location, rating, star and expected-net filters.");
'@

Replace-Exact $window @'
    private bool PassesOwnedFilters(RatedOwnedItem row)
    {
'@ @'
    private void DrawOwnedLocationFilter(IReadOnlyList<RatedOwnedItem> ratedItems)
    {
        var retainers = ratedItems
            .SelectMany(x => x.Ownership)
            .Where(x => x.OwnerKind == InventoryOwnerKind.Retainer && x.OwnerId != 0)
            .GroupBy(x => x.OwnerId)
            .Select(g => new
            {
                Id = g.Key,
                Name = g.Select(x => x.OwnerName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "Unnamed retainer",
            })
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (ownedLocationFilter == OwnedLocationFilter.SpecificRetainer &&
            retainers.All(x => x.Id != ownedRetainerFilterId))
        {
            ownedLocationFilter = OwnedLocationFilter.AllRetainers;
            ownedRetainerFilterId = 0;
        }

        var preview = ownedLocationFilter switch
        {
            OwnedLocationFilter.PlayerInventory => "Location: Player inventory",
            OwnedLocationFilter.AllRetainers => "Location: All retainers",
            OwnedLocationFilter.SpecificRetainer =>
                $"Location: {retainers.FirstOrDefault(x => x.Id == ownedRetainerFilterId)?.Name ?? "Retainer"}",
            _ => "Location: All",
        };

        ImGui.SetNextItemWidth(240 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##owned-location-filter", preview))
        {
            if (ImGui.Selectable("All locations", ownedLocationFilter == OwnedLocationFilter.AllLocations))
            {
                ownedLocationFilter = OwnedLocationFilter.AllLocations;
                ownedRetainerFilterId = 0;
            }
            if (ImGui.Selectable("Player inventory", ownedLocationFilter == OwnedLocationFilter.PlayerInventory))
            {
                ownedLocationFilter = OwnedLocationFilter.PlayerInventory;
                ownedRetainerFilterId = 0;
            }
            if (ImGui.Selectable("All retainers", ownedLocationFilter == OwnedLocationFilter.AllRetainers))
            {
                ownedLocationFilter = OwnedLocationFilter.AllRetainers;
                ownedRetainerFilterId = 0;
            }

            if (retainers.Count > 0)
            {
                ImGui.Separator();
                foreach (var retainer in retainers)
                {
                    if (!ImGui.Selectable($"Retainer: {retainer.Name}##owned-retainer-{retainer.Id}",
                            ownedLocationFilter == OwnedLocationFilter.SpecificRetainer && ownedRetainerFilterId == retainer.Id))
                        continue;
                    ownedLocationFilter = OwnedLocationFilter.SpecificRetainer;
                    ownedRetainerFilterId = retainer.Id;
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show items present in all known locations, your player-owned inventory/saddlebags, all retainers, or one specific cached retainer. When scoped, Qty shows the amount in that selected location; sell guidance still models your full known position.");
    }

    private bool PassesOwnedLocationFilter(RatedOwnedItem row)
        => ownedLocationFilter switch
        {
            OwnedLocationFilter.PlayerInventory => row.Ownership.Any(x => x.OwnerKind == InventoryOwnerKind.Player),
            OwnedLocationFilter.AllRetainers => row.Ownership.Any(x => x.OwnerKind == InventoryOwnerKind.Retainer),
            OwnedLocationFilter.SpecificRetainer => row.Ownership.Any(x =>
                x.OwnerKind == InventoryOwnerKind.Retainer && x.OwnerId == ownedRetainerFilterId),
            _ => true,
        };

    private int VisibleOwnedQuantity(RatedOwnedItem row)
        => ownedLocationFilter switch
        {
            OwnedLocationFilter.PlayerInventory => row.Ownership
                .Where(x => x.OwnerKind == InventoryOwnerKind.Player)
                .Sum(x => x.Quantity),
            OwnedLocationFilter.AllRetainers => row.Ownership
                .Where(x => x.OwnerKind == InventoryOwnerKind.Retainer)
                .Sum(x => x.Quantity),
            OwnedLocationFilter.SpecificRetainer => row.Ownership
                .Where(x => x.OwnerKind == InventoryOwnerKind.Retainer && x.OwnerId == ownedRetainerFilterId)
                .Sum(x => x.Quantity),
            _ => row.Quantity,
        };

    private bool PassesOwnedFilters(RatedOwnedItem row)
    {
'@

Replace-Exact $window @'
        ("Qty", "Total quantity known across loaded/cached player, saddlebag, retainer and market-listing snapshots."),
'@ @'
        ("Qty", "Quantity in the active location filter. With All locations selected, this is the total known quantity across player, saddlebag, retainer and market-listing snapshots. Sell guidance still models the full known position."),
'@

Replace-Exact $window 'private static List<RatedOwnedItem> SortOwnedItems(List<RatedOwnedItem> rows, ImGuiTableSortSpecsPtr sortSpecs)' 'private List<RatedOwnedItem> SortOwnedItems(List<RatedOwnedItem> rows, ImGuiTableSortSpecsPtr sortSpecs)'
Replace-Exact $window 'OwnedItemColumn.Quantity => OrderRows(rows, x => x.Quantity, descending),' 'OwnedItemColumn.Quantity => OrderRows(rows, VisibleOwnedQuantity, descending),'

Replace-Exact $project '<Version>1.1.3.0</Version>' '<Version>1.1.4.0</Version>'

Write-Host 'v1.1.4 sell location filter patches applied successfully.'
