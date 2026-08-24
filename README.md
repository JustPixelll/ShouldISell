<p align="center">
  <img src="images/icon.png" width="180" alt="Should I? icon">
</p>

<h1 align="center">Should I?</h1>

<p align="center">
  <strong>Know what to sell, buy, craft, gather — and what to do next.</strong>
</p>

<p align="center">
  FFXIV economy decision support for Dalamud: market analysis, inventory intelligence, trading analytics and personal economic history in one plugin.
</p>

<p align="center">
  <a href="https://github.com/JustPixelll/ShouldISell/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/JustPixelll/ShouldISell?display_name=tag&sort=semver"></a>
  <a href="https://github.com/JustPixelll/ShouldISell/actions/workflows/build.yml"><img alt="Build" src="https://github.com/JustPixelll/ShouldISell/actions/workflows/build.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-BSD--3--Clause-blue.svg"></a>
  <img alt="Dalamud API 15" src="https://img.shields.io/badge/Dalamud-API%2015-6f42c1">
</p>

---

## The idea

FFXIV gives you prices. **Should I?** tries to answer the more useful question: **what should you actually do with them?**

A high Market Board price does not automatically make an item good to sell. A low listing does not automatically make it a good buy. Crafting can look profitable until ingredient opportunity cost is counted. Gathering can look lucrative until demand is too thin to absorb the result. A profitable flip can still tie up your gil for days.

Should I? combines Universalis market data, game data, inventory/retainer observations, vendor economics, sale velocity, stack behavior and your own trading history into practical, comparable recommendations.

> **Should I? is decision support, not automation.** It never automatically buys, sells, reprices, clicks listings, or queues native FFXIV Market Board searches.

---

## Modules

| Module | Core question | Main outputs |
|---|---|---|
| **Should I Sell?** | What I already own is actually worth listing? | Sell rating, suggested price, stack size, expected after-tax value, current-listing review |
| **Should I Buy?** | Which current opportunities are worth acquiring? | Separate Market Board / Vendor lanes, acquisition package, profit, ROI, liquidity, confidence |
| **Should I Craft?** | Is crafting this better than buying its inputs / intermediates? | Recursive make-vs-buy/vendor routing, economic vs cash material cost, profit, liquidity |
| **Should I Gather?** | Which things I can gather are economically attractive? | MIN/BTN accessibility, market value, demand, estimated gil per active minute, confidence |
| **Opportunities** | What is the best economic action available right now? | Unified Buy / Craft / Gather / Craft+Gather ranking |
| **Should I Tycoon?** | What is actually happening to my gil and trades? | Wallet cashflow, purchases, open positions, FIFO P&L, sales/listing insights, model accuracy |

The modules share the same local inventory, market and personal-trading data rather than operating as unrelated calculators.

---

# Should I Sell?

Should I Sell? turns the marketable inventory Should I? has observed into a ranked selling audit.

<p align="center">
  <img src="images/overview.png" alt="Should I Sell? owned-items overview" width="900">
</p>

It can:

- rate items with **1–5 stars** plus a stricter **0–100 opportunity score**;
- suggest a realistic Market Board unit price from current supply, sold-price history, demand, stability and trend;
- recommend stack sizes from historical buyer quantities, buyer spend and listing-fragmentation cost;
- show the expected after-tax value of **one recommended listing**;
- compare current retainer listings with the current recommendation;
- track known inventory across player bags, saddlebags and observed retainers;
- preserve previously observed retainer snapshots locally when those containers are no longer loaded;
- capture personal retainer sales where FFXIV exposes reliable evidence;
- show a small attention overlay for listings whose price/stack plan deserves another look.

Market refreshes in Should I? are **Universalis-only**. There are no Deep Scan / native queued Market Board controls inside this plugin.

---

# Should I Buy?

Should I Buy? is split into two deliberately similar lanes:

### Market Board Opportunities

Finds Market → Market strategies such as normal flips, undercut layers, split-stack/consolidation opportunities and Market → Vendor guaranteed-floor situations where supported.

### Vendor Opportunities

Finds renewable normal-gil Vendor → Market convenience opportunities without pretending NPC supply is scarce.

Both tabs follow the same workflow:

1. **Discovery filters** decide what Universalis should inspect.
2. Start the Universalis analysis.
3. **Findings filters** decide which completed opportunities you want to see.
4. Inspect the ranked table or open an item detail page.

Findings filters include practical fields such as rating, profit, ROI, acquisition cost, liquidation estimate, quality/strategy and whether you have already bought/tracked the opportunity.

Should I? can capture Market Board purchases that FFXIV exposes and tie them back to the opportunity. Vendor acquisitions can be manually confirmed into Tycoon with the recommendation pre-filled so a real cost basis exists without inventing one.

---

# Should I Craft?

Should I Craft? works from recipes your current crafter levels can perform and asks whether the result is economically attractive.

The important distinction is **cash cost vs economic cost**:

- an ingredient you already own can reduce the **cash required** to craft;
- but owned materials are not treated as free — they still have an **opportunity value** because you could potentially sell them instead.

For ingredients/intermediates, the scanner can recursively compare Market Board acquisition, normal-gil vendor purchase and crafting. The result includes economic material cost, cash material cost, expected net sale value, economic profit / ROI, market velocity/liquidation and the ingredient-by-ingredient acquisition plan.

Current production economics are intentionally conservative and initially NQ-to-NQ. Master-recipe/specialist/unlock coverage and personally learned craft timing remain areas for further refinement.

---

# Should I Gather?

Should I Gather? currently focuses on MIN / BTN opportunities that can be resolved reliably from game data.

It combines your gatherer level, gathering source/location data, normal/hidden/timed classification, realistic sale value, recent demand/stability and a deliberately generic active gathering-throughput baseline.

The displayed gil/minute is **gil per active gathering minute**, not a claim that every timed-node waiting minute is lost productivity. Timed availability is treated as availability/convenience friction instead.

The old arbitrary ±35% throughput display band has been removed. Numerical yield ranges should return only when they can be tied to real node topology or personal gathering telemetry.

Fishing is not ranked yet because bait, weather, bite/catch probability and route behavior need a different model before a gil/minute number would be credible.

---

# Opportunities

The Opportunities tab merges the currently available Buy, Craft and Gather evidence into one ranked action list.

The goal is not to force every action onto one fake metric. It keeps rating, confidence, profit, ROI, active-time efficiency and liquidation visible so you can distinguish a high-confidence quick flip from a profitable-but-slow craft or a high-value gather.

Gathering an ingredient does **not** become economically free simply because you can gather it.

---

# Should I Tycoon?

Tycoon is the personal economy layer.

### Cashflow

While the plugin is running, Should I? can record direct changes in your character's gil wallet. The **amount and resulting balance are evidence**; the source is only labeled when the plugin has evidence strong enough to justify it.

Category analytics are intentionally deferred until category attribution can be reliable. The cashflow view stays focused on the actual ledger instead of asking you to manually classify every wallet movement.

### Purchase / trade tracking

- Market Board purchases can be captured automatically when the game exposes the successful transaction.
- Normal-gil vendor acquisitions can be recorded manually, optionally starting directly from a Vendor opportunity.
- Purchases can be treated as **Trade** or **Personal** without deleting the acquisition record.
- Trade lots feed FIFO cost-basis accounting.
- Captured sales can close tracked lots and produce realized P&L / holding-time measurements.

If Should I? does not know what you paid, it does not invent a cost basis.

Tycoon also exposes open positions, closed trades, strongest items/strategies, sales insights, listing lifecycle observations and prediction-vs-reality accuracy where evidence exists.

---

# Inventory integration

## Native FFXIV item tooltip

When you hover a marketable inventory item, Should I? can append a compact **Should I?** section directly to FFXIV's normal `ItemDetail` tooltip.

Depending on cached evidence, it can show Sell stars / opportunity score / confidence, estimated net value per item, estimated value of the hovered stack when its stack quantity is available, and cached Buy / Craft / Gather ratings.

The tooltip does **not** perform network requests. It shows data Should I? already knows.

Compatibility is deliberately additive: Should I? owns one uniquely identified text node at the current bottom of the native tooltip and removes/restores only the height it added before the game refreshes the tooltip. It does not replace the base tooltip or other plugins' nodes.

## Inventory right-click

Should I? also uses Dalamud's official context-menu API to add **Look up in Should I…**, with relevant module destinations for the selected item.

Both integrations can be disabled from setup/preferences.

---

# Where the data comes from

Should I? intentionally uses several evidence layers:

- **Universalis** for current-world listing/history discovery and market statistics;
- **Lumina/game sheets** for static item, recipe, vendor and gathering information;
- **normal FFXIV inventory / Market Board observations** while those data are exposed by the game;
- **your local trading history** captured while the plugin is running;
- optionally, fresh snapshots published by **Should I Deep Mine?**.

Should I? does not require a Deep Mine installation.

---

# Optional companion: Should I Deep Mine?

Deep Mine contains the deliberately experimental native queued Market Board scanner that was removed from Should I?. It remains a separate custom-repository plugin and is **not part of Should I?'s official-list submission plan**.

```text
https://raw.githubusercontent.com/JustPixelll/ShouldIDeepMine/main/pluginmaster.json
```

Deep Mine can scan explicit scopes such as known owned items, current listings, loaded inventory, active retainer, item category or custom item IDs. Nothing scans automatically at startup.

Should I? can consume completed Deep Mine snapshots over versioned Dalamud IPC, but Should I? never sends Deep Mine a command to start scanning.

---

# Installation (current testing distribution)

Until the official Dalamud listing is approved, Should I? is installed through a custom repository.

Add this URL under **Dalamud Settings → Experimental → Custom Plugin Repositories**:

```text
https://raw.githubusercontent.com/JustPixelll/ShouldISell/main/pluginmaster.json
```

Then save Dalamud settings, open `/xlplugins`, search for **Should I?**, install/enable it, and run:

```text
/shouldi
```

`/sellcheck` remains as a legacy alias for opening Should I Sell?.

---

# First-time setup

On a fresh configuration, Should I? opens a short setup guide automatically. You can reopen it later with:

```text
/shouldi setup
```

### 1. Expose inventory containers once

FFXIV does not keep every inventory source loaded continuously. To give Should I? a useful ownership snapshot:

1. Open your normal player inventory.
2. Open your Chocobo Saddlebag.
3. Open Premium Saddlebags if you use them.
4. Visit a Summoning Bell.
5. Open every retainer inventory once.
6. Open every selling retainer's current listing page once.
7. Optionally open retainer sale history so recent exact rows can be observed where supported.

Should I? persists previously observed snapshots locally, so a retainer does not need to remain open for its last known inventory to appear.

If Should I? has not yet seen your normal inventory during the current session, Should I Sell? shows an inventory-coverage warning. Opening the inventory clears it for the session; permanently dismissing it prevents it from returning.

### 2. Refresh market data

Use **Should I Sell? → Market Refresh** or the module-specific Universalis actions when you want fresh market evidence. Refresh scopes are explicit; there is no automatic full native Market Board sweep.

### 3. Choose inventory integrations

The welcome guide includes toggles for the native item-tooltip section and the **Look up in Should I…** inventory context menu.

### 4. Optional Deep Mine

Install Deep Mine only if you specifically want its explicit experimental native scan scopes. Should I? remains fully usable without it.

---

# Commands

| Command | Action |
|---|---|
| `/shouldi` | Open Should I? |
| `/shouldi sell` | Open Should I Sell? |
| `/shouldi buy` | Open Should I Buy? |
| `/shouldi craft` | Open Should I Craft? |
| `/shouldi gather` | Open Should I Gather? |
| `/shouldi opportunities` | Open unified Opportunities |
| `/shouldi tycoon` | Open Should I Tycoon? |
| `/shouldi setup` | Reopen first-run/setup guide |
| `/shouldi fetch` | Refresh known owned market data from Universalis |
| `/shouldi stop` | Cancel active Buy/Craft/Gather analysis jobs |
| `/sellcheck` | Legacy alias for Should I Sell? |

---

# What Should I? deliberately does not do

- No automatic buying.
- No automatic selling.
- No automatic repricing/listing edits.
- No queued native Market Board deep scanner inside the official plugin.
- No guaranteed-profit claims.
- No fictional purchase cost basis.
- No fictional cashflow source/category when the game does not expose enough evidence.
- No treating renewable NPC vendor stock as scarce Market Board supply.
- No pretending a low-confidence gather/craft timing estimate is precise.

Market prices and behavior can change immediately after an observation; recommendations are estimates and should be treated as decision support.

---

# Privacy / storage

- Local inventory snapshots, listing observations and personal trading records are stored in the plugin's local Dalamud configuration/data area.
- Universalis requests contain public market/item/world lookup information needed for the analysis; Should I? does not send your Square Enix password or credentials anywhere.
- Deep Mine IPC, when used, stays between local Dalamud plugins.

---

# Development

Should I? currently targets **Dalamud API 15** and **.NET 10**.

```powershell
dotnet restore .\ShouldISell\ShouldISell.csproj
dotnet build .\ShouldISell\ShouldISell.csproj --configuration Release --no-restore
```

CI builds Release against current Dalamud development files for every pull request.

---

# Status

The plugin is being prepared for an official Dalamud **testing-channel** submission. The repository is intentionally being polished and locally tested first; no official listing has been submitted yet.

See `PUBLISHING.md` for the remaining release/submission checklist.
