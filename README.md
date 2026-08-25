<p align="center">
  <img src="images/icon.png" width="180" alt="Should I? icon">
</p>

<h1 align="center">Should I?</h1>

<p align="center"><strong>Know what to sell, buy, craft, gather — and what to do next.</strong></p>

FFXIV economy decision support for Dalamud. Should I? combines Universalis, game data, inventory/retainer observations, vendor economics and your own trading history into practical recommendations.

> **Decision support, not automation.** Should I? never automatically buys, sells, reprices, clicks listings, or queues native Market Board searches.

Market recommendations are estimates based on the evidence available at analysis time; prices, supply and demand can change immediately afterward.

## Modules

| Module | What it answers |
|---|---|
| **Should I Sell?** | What you already own is worth listing, at what price and stack size |
| **Should I Buy?** | Which Market Board or vendor opportunities look economically attractive |
| **Should I Craft?** | Whether crafting beats buying inputs/intermediates after opportunity cost |
| **Should I Gather?** | Which MIN/BTN gathering opportunities look attractive for active time spent |
| **Should I Do?** | Which available economic action looks strongest right now |
| **Should I Tycoon?** | What is happening to your gil, purchases, positions, FIFO P&L and sales history |

## Should I Sell?

Should I Sell? ranks marketable owned items using 1–5 stars plus a stricter 0–100 score. It can suggest realistic unit prices, recommended stack sizes, after-tax listing value and current-listing adjustments. Known inventory can span player bags, saddlebags and previously observed retainers.

Market refreshes inside Should I? are **Universalis-only**. Should I? does not queue native Market Board searches.

## Should I Buy?

Should I Buy? has separate **Market Board Opportunities** and **Vendor Opportunities** tabs. Each uses discovery filters before analysis and findings filters after analysis. Results expose acquisition cost, modeled exit, potential profit, ROI, liquidity/holding estimates, confidence and whether you have already bought/tracked the opportunity.

Market Board purchases can be captured when FFXIV exposes the successful transaction. Vendor acquisitions can be explicitly recorded into Tycoon so cost basis is based on what you actually bought.

## Should I Craft?

Should I Craft? recursively compares Market Board, normal-gil vendor and crafting routes for ingredients/intermediates. Owned materials reduce cash required but are not treated as economically free. Results separate cash material cost from economic opportunity cost.

## Should I Gather?

Should I Gather? currently focuses on MIN/BTN. It combines gatherer accessibility, source information, market value, demand/stability and a generic active-throughput baseline. Timed availability is treated as availability friction rather than pretending waiting time is active gathering time. Fishing is intentionally not ranked yet.

## Should I Do?

Should I Do? combines currently available Buy, Craft and Gather evidence into one action list while keeping profit, ROI, confidence, liquidity and active-time efficiency visible rather than hiding everything behind one number.

## Should I Tycoon?

Tycoon tracks observed wallet changes, purchases, trade/personal lots, open positions, FIFO cost basis, realized P&L, sales history, listing lifecycle observations and prediction-vs-reality insights where reliable evidence exists. Unknown acquisition cost stays unknown.

## Native inventory integration

### Item tooltip

Should I? can append a compact cached-data block directly to FFXIV's normal `ItemDetail` tooltip. Depending on available evidence it can show Sell rating/value plus cached Buy/Craft/Gather signals. Hovering an item never triggers a network request.

The tooltip integration is additive: Should I? owns one uniquely identified text node and only adjusts the height contribution it added, improving coexistence with other tooltip plugins.

### Right-click lookup

Using Dalamud's official `IContextMenu`, Should I? can add **Look up in Should I…** to inventory item context menus with relevant module destinations.

Both integrations are optional.

## Data sources

- **Universalis** for current-world market listings/history and discovery;
- **Lumina/game sheets** for static items, recipes, vendors and gathering information;
- **normal FFXIV inventory and Market Board observations** while the game exposes them;
- **local personal trading history** recorded while the plugin is running;
- optional compatible local market-data providers through versioned Dalamud IPC.

## First setup

On a fresh configuration, Should I? opens a short setup guide. Reopen it with:

```text
/shouldi setup
```

For the best ownership snapshot, open your player inventory, saddlebags, each retainer inventory and each selling retainer's listing page once. Should I? persists previously observed snapshots locally.

Use **Should I Sell? → Market Refresh** or module-specific Universalis actions when you want fresh market evidence.

## Commands

| Command | Action |
|---|---|
| `/shouldi` | Open Should I? |
| `/shouldi sell` | Sell module |
| `/shouldi buy` | Buy module |
| `/shouldi craft` | Craft module |
| `/shouldi gather` | Gather module |
| `/shouldi do` | Open Should I Do? |
| `/shouldi opportunities` | Compatibility alias for Should I Do? |
| `/shouldi tycoon` | Tycoon |
| `/shouldi setup` | Setup guide |
| `/shouldi fetch` | Refresh known-owned market data from Universalis |
| `/shouldi stop` | Cancel active analysis jobs |
| `/sellcheck` | Legacy Sell alias |

## Privacy / storage

Inventory snapshots, listing observations and personal trading records remain in the plugin's local Dalamud data/configuration area. Universalis requests contain public item/world market lookup information required by the analysis. Should I? does not upload your Square Enix credentials.

## What Should I? deliberately does not do

- no automatic buying;
- no automatic selling;
- no automatic repricing or listing edits;
- no queued native Market Board scanning;
- no guaranteed-profit claims;
- no invented cost basis or cashflow attribution;
- no assumption that renewable vendor stock is scarce;
- no false precision for low-confidence craft/gather estimates.

## Development

Should I? targets **Dalamud API 15** and **.NET 10**.

```powershell
dotnet restore .\ShouldISell\ShouldISell.csproj --locked-mode
dotnet build .\ShouldISell\ShouldISell.csproj --configuration Release --no-restore
```

The plugin is being submitted first to the official Dalamud **testing** track, as required for new plugins.
