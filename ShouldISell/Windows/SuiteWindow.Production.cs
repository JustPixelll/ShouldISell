using Dalamud.Bindings.ImGui;

namespace ShouldISell.Windows;

public sealed partial class SuiteWindow
{
    private string craftSearch = string.Empty;
    private string gatherSearch = string.Empty;
    private string opportunitySearch = string.Empty;
    private CraftOpportunity? selectedCraftOpportunity;
    private GatherOpportunity? selectedGatherOpportunity;

    private void DrawCraftModule()
    {
        ImGui.TextWrapped("Should I Craft? compares the realistic after-tax sale value of a recipe with the economic value of every input. It recursively chooses cheaper craft/vendor/Market Board routes for intermediates, checks your actual crafter level, and keeps cash cost separate from opportunity cost.");
        ImGui.TextDisabled("v1 production model is NQ-to-NQ. Craft execution time is a generic estimate; material and market economics are the high-confidence part.");
        ImGui.Spacing();

        DrawProductionScanButtons(primary: "craft");
        ImGui.SetNextItemWidth(280);
        ImGui.InputText("Search craft results", ref craftSearch, 128);

        var rows = plugin.ProductionScanner.GetCraftOpportunities()
            .Where(x => CurrentBuyWorldId == 0 || x.WorldId == CurrentBuyWorldId)
            .Where(x => string.IsNullOrWhiteSpace(craftSearch) || x.Item.Name.Contains(craftSearch, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        ImGui.TextDisabled($"{rows.Count:N0} profitable validated recipe(s). Click an item for its acquisition plan.");
        if (ImGui.BeginTable("##craft-opportunities", 9,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable,
                new System.Numerics.Vector2(0, 360)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Rating", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending);
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Crafter");
            ImGui.TableSetupColumn("Economic profit");
            ImGui.TableSetupColumn("Cash required");
            ImGui.TableSetupColumn("ROI");
            ImGui.TableSetupColumn("Units/day");
            ImGui.TableSetupColumn("Liquidation");
            ImGui.TableSetupColumn("Confidence");
            ImGui.TableHeadersRow();

            rows = TableSort.Apply(rows, ImGui.TableGetSortSpecs(),
                x => x.Stars,
                x => x.Item.Name,
                x => x.CrafterName,
                x => x.EconomicProfit,
                x => x.CashMaterialCost,
                x => x.Roi,
                x => x.UnitsPerDay,
                x => x.EstimatedLiquidationDays,
                x => x.Confidence);

            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Stars(row.Stars));
                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{row.Item.Name}##craft-{row.RecipeId}", selectedCraftOpportunity?.RecipeId == row.RecipeId, ImGuiSelectableFlags.SpanAllColumns))
                    selectedCraftOpportunity = row;
                ItemNameContextMenu($"craft-item-menu-{row.RecipeId}", row.Item.Name);
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.CrafterName} {row.RequiredLevel} (you: {row.PlayerLevel})");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Gil(row.EconomicProfit));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Gil(row.CashMaterialCost));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Percent(row.Roi));
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.UnitsPerDay:0.##}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Days(row.EstimatedLiquidationDays));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Percent(row.Confidence));
            }
            ImGui.EndTable();
        }

        if (selectedCraftOpportunity is { } selected && rows.Any(x => x.RecipeId == selected.RecipeId))
            DrawCraftDetails(selected);
    }

    private void DrawCraftDetails(CraftOpportunity row)
    {
        ImGui.Separator();
        ImGui.TextUnformatted($"CRAFT PLAN — {row.Item.Name}");
        ImGui.TextWrapped($"Craft {row.ResultQuantity:N0} with {row.CrafterName}. Expected net sale value {row.NetSaleValue:N0}g, economic material cost {row.EconomicMaterialCost:N0}g, cash material cost {row.CashMaterialCost:N0}g, economic profit {row.EconomicProfit:N0}g ({row.Roi:P1} ROI).");
        if (row.EstimatedProfitPerActiveMinute is { } gpm)
            ImGui.TextDisabled($"Generic craft-time model: ~{row.EstimatedActiveMinutes:0.##} active min/craft → ~{gpm:N0}g economic profit/active min. Treat the time figure as low-confidence.");

        if (ImGui.BeginTable($"##craft-plan-{row.RecipeId}", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable))
        {
            ImGui.TableSetupColumn("Ingredient", ImGuiTableColumnFlags.DefaultSort);
            ImGui.TableSetupColumn("Need");
            ImGui.TableSetupColumn("Owned");
            ImGui.TableSetupColumn("Best route");
            ImGui.TableSetupColumn("Market/unit");
            ImGui.TableSetupColumn("Economic cost");
            ImGui.TableSetupColumn("Cash cost");
            ImGui.TableHeadersRow();
            var ingredients = TableSort.Apply(row.Ingredients, ImGui.TableGetSortSpecs(),
                x => x.Item.Name,
                x => x.QuantityRequired,
                x => x.OwnedQuantity,
                x => x.Route,
                x => x.MarketUnitCost,
                x => x.EconomicCost,
                x => x.CashCost);
            foreach (var ingredient in ingredients)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(ingredient.Item.Name);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(ingredient.QuantityRequired.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(ingredient.OwnedQuantity.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(RouteLabel(ingredient.Route));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Gil(ingredient.MarketUnitCost));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Gil(ingredient.EconomicCost));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Gil(ingredient.CashCost));
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextWrapped(ingredient.Reason);
                    ImGui.EndTooltip();
                }
            }
            ImGui.EndTable();
        }

        foreach (var note in row.Notes)
            ImGui.BulletText(note);
    }

    private void DrawGatherModule()
    {
        ImGui.TextWrapped("Should I Gather? asks whether a gatherable material is economically attractive to farm and sell. MIN/BTN eligibility, node location, hidden/timed status and market demand are real game/market inputs. The current active-yield figure is a generic baseline; uncertainty is expressed through confidence instead of an arbitrary ±35% range.");
        ImGui.TextDisabled("Fishing is intentionally not ranked in v1. A real throughput range will return only when it is derived from node topology or observed personal sessions rather than generic padding.");
        ImGui.Spacing();

        DrawProductionScanButtons(primary: "gather");
        ImGui.SetNextItemWidth(280);
        ImGui.InputText("Search gather results", ref gatherSearch, 128);

        var rows = plugin.ProductionScanner.GetGatherOpportunities()
            .Where(x => CurrentBuyWorldId == 0 || x.WorldId == CurrentBuyWorldId)
            .Where(x => string.IsNullOrWhiteSpace(gatherSearch) || x.Item.Name.Contains(gatherSearch, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        ImGui.TextDisabled($"{rows.Count:N0} validated MIN/BTN opportunity(ies). Click an item for assumptions and locations.");
        if (ImGui.BeginTable("##gather-opportunities", 8,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable,
                new System.Numerics.Vector2(0, 360)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Rating", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending);
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Job");
            ImGui.TableSetupColumn("Location");
            ImGui.TableSetupColumn("Availability");
            ImGui.TableSetupColumn("g/active min");
            ImGui.TableSetupColumn("Units/day");
            ImGui.TableSetupColumn("Confidence");
            ImGui.TableHeadersRow();

            rows = TableSort.Apply(rows, ImGui.TableGetSortSpecs(),
                x => x.Stars,
                x => x.Item.Name,
                x => x.GathererName,
                x => x.Locations.FirstOrDefault(),
                x => x.IsTimed ? 2 : x.IsHidden ? 1 : 0,
                x => x.EstimatedGilPerActiveMinute,
                x => x.UnitsPerDay,
                x => x.Confidence);

            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Stars(row.Stars));
                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{row.Item.Name}##gather-{row.Item.ItemId}-{row.GathererClassJobId}", selectedGatherOpportunity?.Item.ItemId == row.Item.ItemId && selectedGatherOpportunity?.GathererClassJobId == row.GathererClassJobId, ImGuiSelectableFlags.SpanAllColumns))
                    selectedGatherOpportunity = row;
                ItemNameContextMenu($"gather-item-menu-{row.Item.ItemId}-{row.GathererClassJobId}", row.Item.Name);
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.GathererName} {row.RequiredLevel} (you: {row.PlayerLevel})");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Locations.FirstOrDefault() ?? "Unknown");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.IsTimed ? "Timed" : row.IsHidden ? "Hidden" : "Regular");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Gil(row.EstimatedGilPerActiveMinute));
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.UnitsPerDay:0.##}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Percent(row.Confidence));
            }
            ImGui.EndTable();
        }

        if (selectedGatherOpportunity is { } selected && rows.Any(x => x.Item.ItemId == selected.Item.ItemId && x.GathererClassJobId == selected.GathererClassJobId))
            DrawGatherDetails(selected);
    }

    private void DrawGatherDetails(GatherOpportunity row)
    {
        ImGui.Separator();
        ImGui.TextUnformatted($"GATHER MODEL — {row.Item.Name}");
        ImGui.TextWrapped($"{row.GathererName} {row.RequiredLevel}; your recorded job level is {row.PlayerLevel}. Realistic sale reference ~{row.RealisticUnitSalePrice:N0}g/unit. Generic active-yield baseline ~{row.EstimatedUnitsPerActiveMinute:0.0}/min, equivalent to ~{row.EstimatedGilPerActiveMinute:N0}g/active min after seller tax.");
        ImGui.TextDisabled(row.IsTimed
            ? "Availability: timed/ephemeral node detected. Waiting time is NOT treated as active gathering time."
            : row.IsHidden ? "Availability: hidden-node friction detected." : "Availability: regular node model.");
        ImGui.TextDisabled("Throughput uncertainty currently belongs in the confidence score. A numerical range will only be shown again once node density/route geometry or personal session telemetry can support it.");
        if (row.Locations.Count > 0)
            ImGui.TextWrapped($"Known locations: {string.Join(", ", row.Locations.Take(8))}{(row.Locations.Count > 8 ? " …" : string.Empty)}");
        foreach (var note in row.Notes.Where(x =>
                     !x.StartsWith("Generic active-yield model:", StringComparison.Ordinal) &&
                     !x.Contains("effort estimate is a range", StringComparison.OrdinalIgnoreCase)))
            ImGui.BulletText(note);
    }

    private void DrawOpportunitiesModule()
    {
        ImGui.TextWrapped("Opportunities is the cross-module answer to ‘what is worth doing right now?’. It ranks cached Buy, Craft and Gather results on one 0–100 opportunity scale. Craft + Gather rows flag recipes whose inputs are themselves strong gathering opportunities, without pretending gathered materials are free.");
        ImGui.Spacing();
        DrawProductionScanButtons(primary: "all");
        if (!plugin.BuyScanner.IsScanning && ImGui.Button("REFRESH BUY OPPORTUNITIES TOO"))
            _ = plugin.BuyScanner.ScanMarketAsync();
        ImGui.SameLine();
        ImGui.TextDisabled("Buy results remain cached independently; this view merges whichever modules have current results.");

        ImGui.SetNextItemWidth(280);
        ImGui.InputText("Search all opportunities", ref opportunitySearch, 128);
        var worldId = CurrentBuyWorldId;
        var buy = plugin.BuyScanner.GetOpportunities().Where(x => worldId == 0 || x.WorldId == worldId);
        var rows = plugin.ProductionScanner.GetUnifiedOpportunities(buy)
            .Where(x => string.IsNullOrWhiteSpace(opportunitySearch) || x.ItemName.Contains(opportunitySearch, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        ImGui.TextDisabled($"{rows.Count:N0} ranked action(s). Rating and confidence are separate: a spectacular but uncertain opportunity can still show low confidence.");
        if (ImGui.BeginTable("##unified-opportunities", 9,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable,
                new System.Numerics.Vector2(0, 470)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Type");
            ImGui.TableSetupColumn("Rating", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending);
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Action");
            ImGui.TableSetupColumn("Profit");
            ImGui.TableSetupColumn("ROI");
            ImGui.TableSetupColumn("g/active min");
            ImGui.TableSetupColumn("Liquidation");
            ImGui.TableSetupColumn("Confidence");
            ImGui.TableHeadersRow();

            rows = TableSort.Apply(rows, ImGui.TableGetSortSpecs(),
                x => x.Kind,
                x => x.Stars,
                x => x.ItemName,
                x => x.Action,
                x => x.ExpectedProfit,
                x => x.Roi,
                x => x.GilPerActiveMinute,
                x => x.EstimatedLiquidationDays,
                x => x.Confidence);

            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(OpportunityKindLabel(row.Kind));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Stars(row.Stars));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.ItemName);
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextWrapped(row.Why);
                    ImGui.EndTooltip();
                }
                ImGui.TableNextColumn(); ImGui.TextWrapped(row.Action);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Gil(row.ExpectedProfit));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Roi is null ? "—" : Percent(row.Roi.Value));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Gil(row.GilPerActiveMinute));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Days(row.EstimatedLiquidationDays));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Percent(row.Confidence));
            }
            ImGui.EndTable();
        }
    }

    private void DrawProductionScanButtons(string primary)
    {
        var scanner = plugin.ProductionScanner;
        if (scanner.IsScanning)
        {
            if (ImGui.Button("STOP PRODUCTION SCAN"))
                scanner.CancelScan();
            ImGui.SameLine();
            ImGui.TextDisabled(scanner.Status);
            return;
        }

        var label = primary switch
        {
            "craft" => "SCAN CRAFT OPPORTUNITIES",
            "gather" => "SCAN GATHER OPPORTUNITIES",
            _ => "SCAN CRAFT + GATHER",
        };
        if (ImGui.Button(label))
        {
            if (primary == "craft")
                _ = scanner.ScanCraftAsync();
            else if (primary == "gather")
                _ = scanner.ScanGatherAsync();
            else
                _ = scanner.ScanAllAsync();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(scanner.Status);
    }

    private static string RouteLabel(ProductionAcquisitionRoute route) => route switch
    {
        ProductionAcquisitionRoute.MarketBoard => "Buy MB",
        ProductionAcquisitionRoute.Vendor => "Buy vendor",
        ProductionAcquisitionRoute.Craft => "Craft",
        _ => "Unavailable",
    };

    private static string OpportunityKindLabel(UnifiedOpportunityKind kind) => kind switch
    {
        UnifiedOpportunityKind.Buy => "BUY",
        UnifiedOpportunityKind.Craft => "CRAFT",
        UnifiedOpportunityKind.Gather => "GATHER",
        UnifiedOpportunityKind.CraftAndGather => "CRAFT + GATHER",
        _ => kind.ToString().ToUpperInvariant(),
    };
}
