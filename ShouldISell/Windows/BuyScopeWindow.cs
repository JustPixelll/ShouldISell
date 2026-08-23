using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace ShouldISell.Windows;

public sealed class BuyScopeWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string search = string.Empty;

    public BuyScopeWindow(Plugin plugin)
        : base("Should I Buy? — Scan Scope##ShouldIBuyScope")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 520),
            MaximumSize = new Vector2(900, float.MaxValue),
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var cfg = plugin.Configuration;
        var categories = plugin.Catalog.GetMarketSearchCategories();
        var selected = cfg.BuyEnabledSearchCategoryIds?.ToHashSet() ?? new HashSet<uint>();
        var allMode = selected.Count == 0;

        ImGui.TextWrapped("Choose which FFXIV Market Board search categories Should I Buy? is allowed to scan. Leaving the selection empty means ALL marketable categories. The filter is applied before the Universalis aggregate discovery pass, so a narrow scope also reduces scan traffic and work.");
        ImGui.Separator();

        if (ImGui.Button("All categories"))
        {
            cfg.BuyEnabledSearchCategoryIds = new List<uint>();
            cfg.Save();
            selected.Clear();
            allMode = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Select none"))
        {
            // A literal empty list means "all", so represent an intentionally empty scan by
            // selecting no known category is not useful. Instead this button clears visible checks
            // and explains the all-mode semantic below. Users can then choose the categories wanted.
            cfg.BuyEnabledSearchCategoryIds = new List<uint>();
            cfg.Save();
            selected.Clear();
            allMode = true;
        }
        ImGui.SameLine();
        ImGui.TextDisabled(allMode ? "Current scope: ALL" : $"Current scope: {selected.Count:N0} categories");

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##buy-scope-search", "Search Market Board categories...", ref search, 128);

        var visible = categories
            .Where(x => string.IsNullOrWhiteSpace(search) || x.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        ImGui.BeginChild("##buy-scope-list", new Vector2(0, -75 * ImGuiHelpers.GlobalScale), true);
        foreach (var category in visible)
        {
            var enabled = allMode || selected.Contains(category.Id);
            if (!ImGui.Checkbox($"{category.Name}##buy-scope-{category.Id}", ref enabled))
                continue;

            if (allMode)
            {
                // Switching one category off while in all-mode materializes every other category.
                selected = categories.Select(x => x.Id).ToHashSet();
                allMode = false;
            }

            if (enabled)
                selected.Add(category.Id);
            else
                selected.Remove(category.Id);

            // If the user ends up selecting every category again, normalize back to the compact
            // empty-list representation for "all".
            cfg.BuyEnabledSearchCategoryIds = selected.Count == categories.Count
                ? new List<uint>()
                : selected.Order().ToList();
            cfg.Save();
        }
        ImGui.EndChild();

        if (cfg.BuyEnabledSearchCategoryIds.Count == 0)
            ImGui.TextWrapped("All marketable categories will be scanned. Uncheck any category to switch into a custom scope.");
        else
            ImGui.TextWrapped($"Custom scope active: {cfg.BuyEnabledSearchCategoryIds.Count:N0} of {categories.Count:N0} Market Board categories. Run /buycheck scan to use the new scope.");
    }
}
