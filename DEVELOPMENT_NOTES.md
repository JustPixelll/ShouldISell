# Should I? development and review guide

## Build

Should I? targets Dalamud API 15 and .NET 10.

```powershell
dotnet restore .\ShouldISell\ShouldISell.csproj --locked-mode
dotnet build .\ShouldISell\ShouldISell.csproj --configuration Release --no-restore
```

The Windows GitHub Actions build is authoritative when a local environment lacks the Dalamud/.NET toolchain.

## Code map

| Area | Primary files |
|---|---|
| Composition and commands | `Plugin.cs`, `Configuration.cs` |
| Shared market/inventory domain | `Models.cs`, `GameItemCatalog.cs`, `InventoryScanner.cs`, `LocalStore.cs` |
| Sell pricing and scoring | `ScoreCalculator.cs`, `MarketDataCoordinator*.cs`, `MainWindow*.cs` |
| Buy discovery and live overlay | `BuyOpportunityScanner.cs`, `TradingModels.cs`, `SuiteWindow.Buy*.cs` |
| Craft/Gather/Should I Do? | `ProductionOpportunityScanner.cs`, `ProductionModels.cs`, `SuiteWindow.Production.cs` |
| Purchases, FIFO and cashflow | `MarketPurchaseObserver.cs`, `TraderStore.cs`, `TraderAnalyzer.cs`, `GilLedgerTracker.cs` |
| Sales/listing insight | `RetainerSale*Observer.cs`, `ListingHistoryTracker.cs`, `TycoonInsightService.cs` |
| Native UI integration | `ItemTooltipAugmenter.cs`, `ItemUiIntegration.cs`, `RetainerListingAttentionOverlay.cs` |

`DESIGN.md` documents model meaning and reviewer invariants.

## Manual validation checklist

### Startup and shutdown

- Fresh configuration opens setup; existing configuration opens normally.
- `/shouldi` and every module command select the expected tab.
- `/shouldi opportunities` still opens Should I Do? for compatibility.
- Unloading/reloading produces no hook, IPC or event-handler errors.

### Inventory and Sell

- Open player inventory, saddlebags and multiple retainers; ownership must not disappear when a retainer unloads.
- Refresh an owned-item Universalis scope and verify current/history timestamps update.
- Rebuild Current Listings repeatedly; own-listing exclusion must not shrink shared market depth.
- Confirm sorting, selection, price copying, stack guidance and detail back navigation.

### Buy

- Run Market Board and Vendor discovery independently with different scopes.
- Confirm discovery does not silently apply findings profit/ROI/cost/holding filters.
- Verify one oversized Market-to-Vendor lot does not hide a later affordable profitable lot.
- Open a finding after a newer live snapshot; listing identity changes must lower confidence/rating explicitly.
- Confirm successful Market Board buys record exact cost/tax once; failed requests create no purchase.
- Confirm Vendor recommendations top up working inventory and hide while already listed.

### Craft, Gather and Should I Do?

- Craft/Gather result tables consume remaining height and open details as separate pages.
- Verify Market Board ingredient economic cost includes conservative buyer tax while raw ask stays visible.
- Verify owned materials lower cash cost but not economic cost.
- Validate an old sparse sample does not derive velocity only from its short historical burst.
- Gather labels say market value per active minute, not guaranteed income.
- Should I Do? merges current-world cached results and remains sortable/open-ended.

### Tycoon

- Purchases of previously owned variants preserve opening inventory ahead of tracked FIFO lots.
- Excluding/restoring a purchase invalidates Trader analytics without a timer.
- New sale/listing evidence invalidates insights; unchanged frames reuse cached snapshots.
- Exact history reconciles a matching passive announcement rather than duplicating it.
- Unknown wallet deltas remain unclassified unless exact purchase evidence supports attribution.

### Native integrations

- Tooltip integration restores only its own added height and coexists with other tooltip plugins.
- Context-menu destinations and enabled states are correct.
- The retainer overlay does not alter FFXIV nodes or issue market requests.

## Pull-request review checklist

- Read the full diff; run `git diff --check` and search for stale names/version labels.
- Confirm row-oriented data tables expose click-to-sort headers. Most use `ImGuiTableFlags.Sortable` with matching `TableSort.Apply` selectors; Buy uses its documented custom header sorter so header help and sort direction stay visible together.
- Check async scans discard results after world changes and release semaphores in `finally`.
- Check persistence mutations occur under their lock and failures re-mark data dirty.
- Check event/IPC registrations have corresponding disposal paths.
- Build Release with the locked dependency graph.
- Do not update the official Dalamud submission commit until the maintainer explicitly approves it.
