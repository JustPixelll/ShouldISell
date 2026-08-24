using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace ShouldISell.Windows;

public sealed class WelcomeWindow : Window
{
    private readonly Plugin plugin;
    private readonly Action openSuite;

    public WelcomeWindow(Plugin plugin, Action openSuite)
        : base("Welcome to Should I?##ShouldIWelcome")
    {
        this.plugin = plugin;
        this.openSuite = openSuite;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(650, 520),
            MaximumSize = new Vector2(820, 760),
        };
    }

    public override void Draw()
    {
        var cfg = plugin.Configuration;
        ImGui.TextUnformatted("Should I?");
        ImGui.SameLine();
        ImGui.TextDisabled("Economy decisions from the data you already have.");
        ImGui.Separator();

        ImGui.TextWrapped("Should I? is decision support, not automation. It never buys, sells, reprices, or queues native Market Board searches for you. Normal market analysis uses Universalis plus data FFXIV exposes while you play.");
        ImGui.Spacing();

        ImGui.TextUnformatted("1. Let Should I? learn what you own");
        ImGui.BulletText("Open your normal inventory once.");
        ImGui.BulletText("Open your Chocobo Saddlebag / Premium Saddlebag if you use them.");
        ImGui.BulletText("At a Summoning Bell, open each retainer inventory and each retainer's selling list once.");
        ImGui.TextDisabled("FFXIV only loads many inventory containers while their UI is open. Should I? persists the last observed snapshots locally.");
        ImGui.Spacing();

        ImGui.TextUnformatted("2. Refresh market data when you want it");
        ImGui.TextWrapped("Should I Sell? → Market Refresh can update exactly the known inventory scope you care about from Universalis. Buy, Craft and Gather also use Universalis when you explicitly start their analysis.");
        ImGui.Spacing();

        ImGui.TextUnformatted("3. Optional: Should I Deep Mine?");
        ImGui.TextWrapped("Deep Mine is a separate experimental companion for explicitly requested native Market Board deep scans. Should I? works without it. If installed, completed Deep Mine snapshots can be consumed automatically over Dalamud IPC.");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##deepmine-url", ref DeepMineRepositoryUrl, 256, ImGuiInputTextFlags.ReadOnly);
        if (ImGui.Button("Copy Deep Mine repository URL"))
            ImGui.SetClipboardText(DeepMineRepositoryUrl);
        ImGui.Spacing();

        ImGui.Separator();
        ImGui.TextUnformatted("Inventory integration");
        var tooltip = cfg.ShowItemTooltipInsights;
        if (ImGui.Checkbox("Show Should I? ratings in the normal FFXIV item tooltip", ref tooltip))
        {
            cfg.ShowItemTooltipInsights = tooltip;
            cfg.Save();
        }
        ImGui.TextDisabled("Uses a dedicated additive ItemDetail text node and cached data only; it does not replace the game's tooltip.");

        var context = cfg.ShowItemContextMenu;
        if (ImGui.Checkbox("Add 'Look up in Should I…' to inventory right-click menus", ref context))
        {
            cfg.ShowItemContextMenu = context;
            cfg.Save();
        }
        ImGui.Spacing();

        ImGui.Separator();
        if (ImGui.Button("FINISH SETUP AND OPEN SHOULD I?", new Vector2(310 * ImGuiHelpers.GlobalScale, 0)))
        {
            cfg.FirstRunCompleted = true;
            cfg.Save();
            IsOpen = false;
            openSuite();
        }
        ImGui.SameLine();
        if (ImGui.Button("Open Should I? without finishing"))
            openSuite();

        ImGui.TextDisabled("You can reopen this guide at any time with /shouldi setup.");
    }

    private string DeepMineRepositoryUrl = "https://raw.githubusercontent.com/JustPixelll/ShouldIDeepMine/main/pluginmaster.json";
}
