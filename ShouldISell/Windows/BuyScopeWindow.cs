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
        var allMode = cfg.BuyUseAllSearchCategories;

        ImGui.TextWrapped("Choose which FFXIV Market Board search categories Should I Buy? is allowed to scan. The filter is applied before the Universalis aggregate discovery pass, so a narrow scope also reduces scan traffic and work.");
        ImGui.Separator();

        if (ImGui.Button("All categories"))
        {
            cfg.BuyUseAllSearchCategories = true;
            cfg.BuyEnabledSearchCategoryIds = new List<uint>();
            cfg.Save();
            selected.Clear();
            allMode = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Custom: select none"))
        {
            cfg.BuyUseAllSearchCategories = false;
            cfg.BuyEnabledSearchCategoryIds = new List<uint>();
            cfg.Save();
            selected.Clear();
            allMode = false;
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
                // Unchecking one category while in all-mode materializes every other category,
                // then switches to a normal custom selection.
                selected = categories.Select(x => x.Id).ToHashSet();
                cfg.BuyUseAllSearchCategories = false;
                allMode = false;
            }

            if (enabled)
                selected.Add(category.Id);
            else
                selected.Remove(category.Id);

            if (selected.Count == categories.Count && categories.Count > 0)
            {
                cfg.BuyUseAllSearchCategories = true;
                cfg.BuyEnabledSearchCategoryIds = new List<uint>();
                selected.Clear();
                allMode = true;
            }
            else
            {
                cfg.BuyUseAllSearchCategories = false;
                cfg.BuyEnabledSearchCategoryIds = selected.Order().ToList();
            }
            cfg.Save();
        }
        ImGui.EndChild();

        if (allMode)
            ImGui.TextWrapped($"All {categories.Count:N0} Market Board categories are eligible. Uncheck any category to switch into a custom scope.");
        else if (selected.Count == 0)
            ImGui.TextWrapped("Custom scope is empty: the next scan will intentionally return no candidates until you select at least one category.");
        else
            ImGui.TextWrapped($"Custom scope active: {selected.Count:N0} of {categories.Count:N0} Market Board categories. Run /buycheck scan to use the new scope.");
    }
}
