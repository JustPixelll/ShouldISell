using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ShouldISell.Services;

/// <summary>
/// Adds cached Should I? economy information directly to FFXIV's native ItemDetail tooltip.
/// The implementation owns exactly one uniquely identified text node and removes/restores only the
/// height it added before the game refreshes the tooltip. This is deliberately additive so other
/// tooltip plugins can append their own nodes before or after ours without Should I? replacing or
/// reformatting their content.
/// </summary>
public sealed unsafe class ItemTooltipAugmenter : IDisposable
{
    private const uint NodeId = 0x53484931; // "SHI1"

    private readonly Plugin plugin;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private int? lastHoveredQuantity;

    private unsafe delegate byte AgentItemDetailOnItemHovered(void* a1, void* a2, void* a3, void* a4, uint a5, uint a6, int* a7);

    [Signature("E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? 48 89 9C 24 ?? ?? ?? ?? 4C 89 A4 24", DetourName = nameof(OnItemHoveredDetour))]
    private readonly Hook<AgentItemDetailOnItemHovered> itemHoveredHook = null!;

    public ItemTooltipAugmenter(
        Plugin plugin,
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IGameInteropProvider interop,
        IPluginLog log)
    {
        this.plugin = plugin;
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.log = log;

        try
        {
            interop.InitializeFromAttributes(this);
            itemHoveredHook.Enable();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Should I? could not initialize item-stack quantity capture. Tooltip insights will still work without current-stack value.");
        }

        addonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "ItemDetail", OnPreTooltipUpdate);
        addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "ItemDetail", OnPostTooltipUpdate);
    }

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PreRequestedUpdate, "ItemDetail", OnPreTooltipUpdate);
        addonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, "ItemDetail", OnPostTooltipUpdate);
        try { itemHoveredHook?.Dispose(); } catch { }
    }

    private byte OnItemHoveredDetour(void* a1, void* a2, void* a3, void* a4, uint a5, uint a6, int* a7)
    {
        var result = itemHoveredHook.Original(a1, a2, a3, a4, a5, a6, a7);
        try
        {
            var quantity = a7 == null ? 0 : a7[5];
            lastHoveredQuantity = quantity > 0 ? quantity : null;
        }
        catch (Exception ex)
        {
            lastHoveredQuantity = null;
            log.Debug(ex, "Could not read hovered item stack quantity.");
        }
        return result;
    }

    private void OnPreTooltipUpdate(AddonEvent _, AddonArgs args)
    {
        if (!plugin.Configuration.ShowItemTooltipInsights)
            return;
        RestoreOurNode((AtkUnitBase*)args.Addon.Address);
    }

    private void OnPostTooltipUpdate(AddonEvent _, AddonArgs args)
    {
        if (!plugin.Configuration.ShowItemTooltipInsights || !Plugin.PlayerState.IsLoaded)
            return;

        try
        {
            var tooltip = (AtkUnitBase*)args.Addon.Address;
            if (tooltip == null)
                return;
            var insight = BuildInsight(gameGui.HoveredItem, lastHoveredQuantity);
            if (insight is null)
                return;
            AppendNode(tooltip, insight);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not append Should I? item tooltip insight.");
        }
    }

    private void RestoreOurNode(AtkUnitBase* tooltip)
    {
        if (tooltip == null)
            return;

        AtkTextNode* node = null;
        for (var i = 0; i < tooltip->UldManager.NodeListCount; i++)
        {
            var candidate = tooltip->UldManager.NodeList[i];
            if (candidate != null && candidate->NodeId == NodeId)
            {
                node = (AtkTextNode*)candidate;
                break;
            }
        }

        if (node == null || !node->AtkResNode.IsVisible())
            return;

        var height = node->AtkResNode.Height;
        node->AtkResNode.ToggleVisibility(false);
        var insertNode = tooltip->GetNodeById(2);
        if (insertNode != null)
            insertNode->SetYFloat(insertNode->Y - height - 4);

        if (tooltip->WindowNode != null && tooltip->WindowNode->AtkResNode.Height > height + 4)
        {
            var restored = (ushort)(tooltip->WindowNode->AtkResNode.Height - height - 4);
            SetTooltipHeight(tooltip, restored);
        }
    }

    private void AppendNode(AtkUnitBase* tooltip, TooltipInsight insight)
    {
        var insertNode = tooltip->GetNodeById(2);
        var baseNode = tooltip->GetTextNodeById(44);
        if (insertNode == null || baseNode == null || tooltip->WindowNode == null)
            return;

        AtkTextNode* node = null;
        for (var i = 0; i < tooltip->UldManager.NodeListCount; i++)
        {
            var candidate = tooltip->UldManager.NodeList[i];
            if (candidate != null && candidate->NodeId == NodeId)
            {
                node = (AtkTextNode*)candidate;
                break;
            }
        }

        if (node == null)
        {
            node = IMemorySpace.GetUISpace()->Create<AtkTextNode>();
            node->AtkResNode.Type = NodeType.Text;
            node->AtkResNode.NodeId = NodeId;
            node->AtkResNode.NodeFlags = NodeFlags.AnchorLeft | NodeFlags.AnchorTop;
            node->AtkResNode.X = 16;
            node->AtkResNode.Width = (ushort)Math.Max(200, tooltip->WindowNode->AtkResNode.Width - 32);
            node->AtkResNode.Color = baseNode->AtkResNode.Color;
            node->TextColor = baseNode->TextColor;
            node->EdgeColor = baseNode->EdgeColor;
            node->LineSpacing = 18;
            node->FontSize = 12;
            node->TextFlags = baseNode->TextFlags | TextFlags.MultiLine | TextFlags.AutoAdjustNodeSize;

            var previous = insertNode->PrevSiblingNode;
            node->AtkResNode.ParentNode = insertNode->ParentNode;
            insertNode->PrevSiblingNode = (AtkResNode*)node;
            if (previous != null)
                previous->NextSiblingNode = (AtkResNode*)node;
            node->AtkResNode.PrevSiblingNode = previous;
            node->AtkResNode.NextSiblingNode = insertNode;
            tooltip->UldManager.UpdateDrawNodeList();
        }

        node->AtkResNode.Width = (ushort)Math.Max(200, tooltip->WindowNode->AtkResNode.Width - 32);
        node->SetText(BuildSeString(insight).Encode());
        node->ResizeNodeForCurrentText();
        node->AtkResNode.SetYFloat(tooltip->WindowNode->AtkResNode.Height - 8);
        node->AtkResNode.ToggleVisibility(true);

        var added = node->AtkResNode.Height + 4;
        var newHeight = (ushort)Math.Min(ushort.MaxValue, tooltip->WindowNode->AtkResNode.Height + added);
        SetTooltipHeight(tooltip, newHeight);
        insertNode->SetYFloat(insertNode->Y + added);
    }

    private static void SetTooltipHeight(AtkUnitBase* tooltip, ushort height)
    {
        tooltip->WindowNode->SetHeight(height);
        tooltip->WindowNode->AtkResNode.SetHeight(height);
        var component = tooltip->WindowNode->Component;
        if (component != null)
        {
            var componentRoot = component->UldManager.RootNode;
            if (componentRoot != null)
            {
                componentRoot->SetHeight(height);
                if (componentRoot->PrevSiblingNode != null)
                    componentRoot->PrevSiblingNode->SetHeight(height);
            }
        }
        if (tooltip->RootNode != null)
            tooltip->RootNode->SetHeight(height);
    }

    private TooltipInsight? BuildInsight(ulong rawId, int? stackQuantity)
    {
        var isHq = rawId > 1_000_000;
        var itemId64 = isHq ? rawId - 1_000_000 : rawId;
        if (itemId64 == 0 || itemId64 > uint.MaxValue)
            return null;
        var itemId = (uint)itemId64;
        var item = plugin.Catalog.Get(itemId);
        if (item.ItemId == 0 || !item.IsMarketable)
            return null;

        var owned = plugin.Coordinator.GetRatedOwnedItems()
            .FirstOrDefault(x => x.Item.ItemId == itemId && x.IsHq == isHq);
        var rating = owned?.Rating;

        string sellLine;
        string? valueLine = null;
        if (rating is null)
        {
            sellLine = "Sell  — not rated yet";
        }
        else
        {
            sellLine = $"Sell  {Stars(rating.Stars)} {rating.OpportunityScore:0}/100 · {rating.Confidence:P0}";
            if (rating.NetSuggestedPriceAfterTax is { } net)
            {
                var qty = stackQuantity is > 0 ? stackQuantity.Value : Math.Max(1, rating.StackRecommendation?.RecommendedStackSize ?? 1);
                var label = stackQuantity is > 0 ? "this stack" : "recommended stack";
                valueLine = $"~{net:N0}g/item · {label} ×{qty:N0} ~{(double)net * qty:N0}g";
            }
        }

        var buy = plugin.BuyScanner.GetOpportunities()
            .Where(x => x.Item.ItemId == itemId && x.IsHq == isHq)
            .OrderByDescending(x => x.OpportunityScore)
            .FirstOrDefault();
        var craft = plugin.ProductionScanner.GetCraftOpportunities()
            .Where(x => x.Item.ItemId == itemId)
            .OrderByDescending(x => x.OpportunityScore)
            .FirstOrDefault();
        var gather = plugin.ProductionScanner.GetGatherOpportunities()
            .Where(x => x.Item.ItemId == itemId)
            .OrderByDescending(x => x.OpportunityScore)
            .FirstOrDefault();

        return new TooltipInsight(
            sellLine,
            valueLine,
            buy is null ? null : $"Buy   {Stars(buy.Stars)} {buy.OpportunityScore:0}/100 · +{buy.PotentialProfit:N0}g",
            craft is null ? null : $"Craft {Stars(craft.Stars)} {craft.OpportunityScore:0}/100 · +{craft.EconomicProfit:N0}g",
            gather is null ? null : $"Gather {Stars(gather.Stars)} {gather.OpportunityScore:0}/100 · ~{gather.EstimatedGilPerActiveMinute:N0}g/min");
    }

    private static SeString BuildSeString(TooltipInsight insight)
    {
        var payloads = new List<Payload>
        {
            new TextPayload("\n"),
            new UIForegroundPayload(506),
            new TextPayload("Should I?"),
            new UIForegroundPayload(0),
            new TextPayload("\n" + insight.SellLine),
        };
        if (insight.ValueLine is not null)
        {
            payloads.Add(new UIForegroundPayload(8));
            payloads.Add(new TextPayload("\n" + insight.ValueLine));
            payloads.Add(new UIForegroundPayload(0));
        }
        if (insight.BuyLine is not null) payloads.Add(new TextPayload("\n" + insight.BuyLine));
        if (insight.CraftLine is not null) payloads.Add(new TextPayload("\n" + insight.CraftLine));
        if (insight.GatherLine is not null) payloads.Add(new TextPayload("\n" + insight.GatherLine));
        return new SeString(payloads);
    }

    private static string Stars(int stars)
        => new string('★', Math.Clamp(stars, 1, 5)) + new string('☆', 5 - Math.Clamp(stars, 1, 5));

    private sealed record TooltipInsight(
        string SellLine,
        string? ValueLine,
        string? BuyLine,
        string? CraftLine,
        string? GatherLine);
}
