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
    private const string CommandName = "/sellcheck";
    private const string BuyCommandName = "/buycheck";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IMarketBoard MarketBoard { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public Configuration Configuration { get; }
    public LocalStore Store { get; }
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

    public BuyUniversalisClient BuyUniversalis { get; }
    public TradingLedgerStore TradingLedger { get; }
    public BuyOpportunityEngine BuyEngine { get; }
    public MarketPurchaseObserver PurchaseObserver { get; }
    public TraderProfileService TraderProfiles { get; }

    public readonly WindowSystem WindowSystem = new("ShouldISell");
    private readonly MainWindow mainWindow;
    private readonly BuyWindow buyWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.MigrateIfNeeded();
        Store = new LocalStore(PluginInterface, Log);
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

        BuyUniversalis = new BuyUniversalisClient(Log);
        TradingLedger = new TradingLedgerStore(PluginInterface, Log);
        BuyEngine = new BuyOpportunityEngine(Configuration, PlayerState, Store, Catalog, Scores, BuyUniversalis, Log);
        PurchaseObserver = new MarketPurchaseObserver(MarketBoard, PlayerState, TradingLedger, BuyEngine, Log);
        TraderProfiles = new TraderProfileService(PlayerState, Store, TradingLedger, Catalog);

        mainWindow = new MainWindow(this);
        buyWindow = new BuyWindow(this);
        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(buyWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Should I Sell?. /sellcheck scan, /sellcheck fetch, /sellcheck refresh, /sellcheck listings, /sellcheck livescan, /sellcheck audit, /sellcheck stop",
        });
        CommandManager.AddHandler(BuyCommandName, new CommandInfo(OnBuyCommand)
        {
            HelpMessage = "Open Should I Buy?. /buycheck scan starts a full opportunity scan; /buycheck stop cancels it.",
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
        BuyEngine.Dispose();
        BuyUniversalis.Dispose();
        TradingLedger.Flush();
        RefreshEngine.Dispose();
        SaleAnnouncements.Dispose();
        SaleHistory.Dispose();
        MarketObserver.Dispose();
        Universalis.Dispose();
        Store.Flush();

        PluginInterface.UiBuilder.Draw -= ListingAttentionOverlay.Draw;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMainUi;
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(BuyCommandName);
        WindowSystem.RemoveAllWindows();
        buyWindow.Dispose();
        mainWindow.Dispose();
    }

    private DateTimeOffset nextPassiveScan = DateTimeOffset.MinValue;

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!PlayerState.IsLoaded || DateTimeOffset.UtcNow < nextPassiveScan)
            return;

        nextPassiveScan = DateTimeOffset.UtcNow.AddSeconds(2);
        Inventory.ScanLoadedContainers();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        switch (trimmed.ToLowerInvariant())
        {
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
                break;
            default:
                mainWindow.Toggle();
                break;
        }
    }

    private void OnBuyCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "scan":
                buyWindow.IsOpen = true;
                _ = BuyEngine.ScanAsync();
                break;
            case "stop":
                BuyEngine.Stop();
                break;
            default:
                buyWindow.Toggle();
                break;
        }
    }

    private void OpenMainUi() => mainWindow.IsOpen = true;
}
