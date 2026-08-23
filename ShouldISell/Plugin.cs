using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ShouldISell.Services;
using ShouldISell.Windows;

namespace ShouldISell;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/shouldi";
    private const string LegacySellCommand = "/sellcheck";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IMarketBoard MarketBoard { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public Configuration Configuration { get; }
    public LocalStore Store { get; }
    public TraderStore TraderStore { get; }
    public GilLedgerTracker GilLedger { get; }
    public GameItemCatalog Catalog { get; }
    public InventoryScanner Inventory { get; }
    public UniversalisClient Universalis { get; }
    public MarketBoardObserver MarketObserver { get; }
    public RetainerSaleHistoryObserver SaleHistory { get; }
    public RetainerSaleAnnouncementObserver SaleAnnouncements { get; }
    public ScoreCalculator Scores { get; }
    public MarketDataCoordinator Coordinator { get; }
    public SellScanContextService SellScanContext { get; }
    public ExperimentalRefreshEngine RefreshEngine { get; }
    public RetainerListingAttentionOverlay ListingAttentionOverlay { get; }
    public BuyOpportunityScanner BuyScanner { get; }
    public MarketPurchaseObserver PurchaseObserver { get; }
    public TraderAnalyzer TraderAnalyzer { get; }
    public ListingHistoryTracker ListingHistory { get; }
    public TycoonInsightService TycoonInsights { get; }

    public readonly WindowSystem WindowSystem = new("ShouldI");
    private readonly SuiteWindow suiteWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.MigrateIfNeeded();
        Store = new LocalStore(PluginInterface, Log);
        TraderStore = new TraderStore(PluginInterface, Log);
        GilLedger = new GilLedgerTracker(GameInventory, PlayerState, TraderStore, Log);
        Catalog = new GameItemCatalog(DataManager);
        Inventory = new InventoryScanner(PlayerState, Catalog, Store, Log);
        Universalis = new UniversalisClient(Log);
        MarketObserver = new MarketBoardObserver(MarketBoard, PlayerState, Store, Log);
        SaleHistory = new RetainerSaleHistoryObserver(GameInterop, PlayerState, Store, Log);
        SaleAnnouncements = new RetainerSaleAnnouncementObserver(ChatGui, PlayerState, Store, Log);
        Scores = new ScoreCalculator();
        Coordinator = new MarketDataCoordinator(PlayerState, Configuration, Store, Catalog, Inventory, Universalis, Scores, Log);
        SellScanContext = new SellScanContextService(GameGui);
        RefreshEngine = new ExperimentalRefreshEngine(Configuration, Framework, PlayerState, Catalog, Inventory, Store, MarketObserver, SellScanContext, Log);
        ListingAttentionOverlay = new RetainerListingAttentionOverlay(GameGui, PlayerState, Coordinator, Log);
        BuyScanner = new BuyOpportunityScanner(Configuration, PlayerState, Catalog, Inventory, Scores, Log);
        PurchaseObserver = new MarketPurchaseObserver(MarketBoard, PlayerState, TraderStore, BuyScanner, Log);
        TraderAnalyzer = new TraderAnalyzer(PlayerState, TraderStore, Store, Coordinator, Catalog);
        ListingHistory = new ListingHistoryTracker(PluginInterface, PlayerState, Store, Log);
        TycoonInsights = new TycoonInsightService(PlayerState, Store, Catalog, ListingHistory);

        suiteWindow = new SuiteWindow(this);
        WindowSystem.AddWindow(suiteWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Should I?. /shouldi sell, /shouldi buy, /shouldi tycoon, /shouldi scan, /shouldi fetch, /shouldi refresh, /shouldi listings, /shouldi livescan, /shouldi audit, /shouldi stop",
        });
        CommandManager.AddHandler(LegacySellCommand, new CommandInfo(OnLegacySellCommand)
        {
            HelpMessage = "Legacy Should I Sell? command. Existing /sellcheck scan/fetch/refresh/listings/livescan/audit/stop commands remain supported.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw += ListingAttentionOverlay.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMainUi;

        Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PurchaseObserver.Dispose();
        ListingHistory.Dispose();
        BuyScanner.Dispose();
        RefreshEngine.Dispose();
        SaleAnnouncements.Dispose();
        SaleHistory.Dispose();
        MarketObserver.Dispose();
        Universalis.Dispose();
        Store.Flush();
        TraderStore.Flush();

        PluginInterface.UiBuilder.Draw -= ListingAttentionOverlay.Draw;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMainUi;
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(LegacySellCommand);
        WindowSystem.RemoveAllWindows();
        suiteWindow.Dispose();
    }

    private DateTimeOffset nextPassiveScan = DateTimeOffset.MinValue;

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!PlayerState.IsLoaded || DateTimeOffset.UtcNow < nextPassiveScan)
            return;

        nextPassiveScan = DateTimeOffset.UtcNow.AddSeconds(2);
        GilLedger.Capture();
        Inventory.ScanLoadedContainers();
        ListingHistory.Capture();
    }

    private void OnLegacySellCommand(string command, string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            suiteWindow.OpenModule(ShouldIModule.Sell);
            return;
        }
        OnCommand(command, args);
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        switch (trimmed.ToLowerInvariant())
        {
            case "sell":
                suiteWindow.OpenModule(ShouldIModule.Sell);
                break;
            case "buy":
                suiteWindow.OpenModule(ShouldIModule.Buy);
                break;
            case "tycoon":
                suiteWindow.OpenModule(ShouldIModule.Tycoon);
                break;
            case "scan":
                Inventory.ScanLoadedContainers(forceFlush: true);
                break;
            case "fetch":
                _ = Coordinator.RefreshOwnedFromUniversalisAsync(force: true);
                break;
            case "refresh":
                RefreshEngine.StartForStaleOwnedItems();
                break;
            case "listings":
                RefreshEngine.StartForCurrentListings();
                break;
            case "livescan":
                RefreshEngine.StartForCurrentSellWindow();
                break;
            case "audit":
                RefreshEngine.StartForAllOwnedItems();
                break;
            case "stop":
                RefreshEngine.Stop("Stopped by user.");
                BuyScanner.CancelScan();
                break;
            default:
                suiteWindow.Toggle();
                break;
        }
    }

    private void OpenMainUi() => suiteWindow.IsOpen = true;
}
