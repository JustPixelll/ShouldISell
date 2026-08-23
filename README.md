<p align="center">
  <img src="images/icon.png" width="180" alt="Should I? icon">
</p>

<h1 align="center">Should I?</h1>

<p align="center">
  <strong>Sell smarter. Buy smarter. Learn your trading style.</strong>
</p>

<p align="center">
  An experimental FFXIV Market Board decision suite for Dalamud.
</p>

<p align="center">
  <a href="https://github.com/JustPixelll/ShouldISell/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/JustPixelll/ShouldISell?display_name=tag&sort=semver"></a>
  <a href="https://github.com/JustPixelll/ShouldISell/actions/workflows/build.yml"><img alt="Build" src="https://github.com/JustPixelll/ShouldISell/actions/workflows/build.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-BSD--3--Clause-blue.svg"></a>
  <img alt="Dalamud API 15" src="https://img.shields.io/badge/Dalamud-API%2015-6f42c1">
  <img alt="Experimental" src="https://img.shields.io/badge/status-experimental-orange">
</p>

---

## The Market Board gives you prices. Should I? tries to answer the harder question: **what should you actually do?**

A cheap item is not automatically a good buy. A high-priced item is not automatically worth selling. A profitable-looking flip can still trap your gil for days, and a stack of materials can be worth far more in the right listing size than in the wrong one.

**Should I?** combines live FFXIV Market Board observations, Universalis history, your inventory and retainer data, vendor economics, sale velocity, stack behavior, capital requirements and your own trading history to turn raw market data into practical decisions.

It is one plugin with three connected modules:

| Module | Question it answers | What you get |
|---|---|---|
| **Should I Sell?** | *What in my inventory is actually worth listing?* | Sell rating, suggested price, stack size, after-tax value, listing audit and sales history |
| **Should I Buy?** | *What can I buy right now with a reasonable chance of making gil?* | Budget-aware opportunities, ROI/profit/liquidity scoring, exact acquisition packages and live verification |
| **Should I Tycoon?** | *Am I actually getting better at trading?* | Cost basis, open positions, realized/unrealized P&L, strategy performance, sales insights and listing behavior |

> **Should I? is decision support, not automation.** It does not automatically buy items, sell items, change your listings, or promise profit.

---

## Why use it?

FFXIV's Market Board is full of situations where the obvious answer is wrong.

- You own 2,000 crafting materials across several retainers. Which ones are worth the listing slots **today**?
- An item is selling for 40,000g and several listings at 20,000g look tempting. Is buying them a real flip, or are you just locking up capital for a week?
- A 99-stack sells, but buyers historically prefer 10s and 20s. Is splitting the stack worth the effort?
- A vendor item sells for 50× its NPC price. Is that useful convenience arbitrage, and how much should you actually stock?
- A flip shows 150% theoretical ROI, but requires six sequential listings before your original capital is recovered. Is it still a good trade?
- You have been trading for two weeks. Which strategies actually made you gil, and which only *looked* good when you bought them?

Should I? is built around these kinds of questions.

---

# Should I Sell?

Turn everything the plugin knows you own into a ranked selling audit.

<p align="center">
  <img src="images/overview.png" alt="Should I Sell? owned-items overview" width="900">
</p>

### What it does

- Rates marketable owned items with **1–5 stars** plus a stricter **0–100 sell-opportunity score**.
- Suggests a realistic **Market Board unit price** using recent sales, current listings, trend, supply, demand and vendor economics.
- Suggests a **stack size** based on historical buyer quantities, buyer spend, convenience premiums, sell-through and listing fragmentation.
- Provides a **low-maintenance stack alternative** when fewer listings may be worth a small pricing compromise.
- Shows the expected **after-tax payout of one recommended listing**, not only the theoretical value of your entire stockpile.
- Audits your **current retainer listings** against the current recommendation and shows repricing deltas.
- Tracks known inventory across player bags, saddlebags and previously observed retainers using persisted local snapshots.
- Highlights inventory that is already listed so you do not accidentally treat listed stock as untouched stock.
- Supports **location filtering**: player inventory, all retainers, or individual cached retainers.
- Captures your **personal sales history** from live retainer-sale events and exact retainer history reconciliation.
- Adds a retainer-list **attention overlay** for listings whose price or stack recommendation deserves attention.
- Supports targeted live scans as well as a deliberately slow **FULL LIVE AUDIT — ALL OWNED** for a fresh audit of everything known.

<details>
<summary><strong>Open a detailed Should I Sell? item-analysis preview</strong></summary>

<p align="center">
  <img src="images/details.png" alt="Should I Sell? detailed item analysis" width="900">
</p>

</details>

### Good uses for Should I Sell?

**Retainer cleanup:** load all retainers once, then sort your entire known stock by practical selling quality instead of checking items one by one.

**Listing maintenance:** compare current retainer prices to the model and focus only on listings that meaningfully need attention.

**Stack optimization:** identify markets where buyers prefer smaller convenience stacks or where a large stack reduces needless listing work.

**Vendor-floor protection:** avoid listing something below a guaranteed NPC exit when the Market Board economics do not justify it.

---

# Should I Buy?

Should I Buy? scans for **executable purchases**, not merely items whose historical average is higher than the current lowest listing.

It starts with broad Universalis discovery across the selected marketable universe on your **current world**, then spends detailed requests only on the strongest candidates. The result is a ranked list of opportunities that fit the budget and risk limits you choose.

### Supported opportunity types

| Strategy | Idea |
|---|---|
| **Market → Market** | Buy genuinely underpriced Market Board stock and resell it |
| **Undercut sweep** | Remove a shallow cheap layer when the remaining market and history support the higher exit |
| **Buy & split** | Buy oversized cheap stacks and resell in historically stronger smaller stacks |
| **Buy & consolidate** | Combine small acquisition lots into a more useful selling package |
| **Vendor → Market** | Buy from a normal gil NPC vendor and sell the convenience premium on the Market Board |
| **Market → Vendor** | Buy below guaranteed NPC buyback value for an immediate deterministic exit |

### The rating is deliberately stricter than “profit > 0”

A recommendation considers:

- **ROI** — return relative to the gil committed.
- **Absolute potential profit** — because 500% on 200g is not the same opportunity as 30% on 100,000g.
- **Liquidity / expected holding time** — how long the position may tie up your gil.
- **Price advantage** — how strong the acquisition is relative to the modeled exit.
- **Demand and stability** — whether the market has enough real evidence behind it.
- **Confidence** — quality and freshness of the available evidence.
- **Execution friction** — how many acquisition/listing actions the strategy requires.
- **Capital recovery** — whether one realistic active listing can recover a meaningful share of your investment.

Should I Buy? shows **potential profit** separately from **risk-adjusted profit** so a huge theoretical number cannot hide a fragile execution plan.

### One-active-listing capital model

FFXIV allows a maximum of **99 units in one Market Board listing**. More importantly, a player usually does not want to occupy multiple retainer slots with the same item just to realize a theoretical liquidation value.

Should I Buy? therefore models practical capital deployment around roughly **one active listing per item/HQ variant**, including sequential listing cycles and how quickly the first realistic sale can recover the original acquisition cost.

A giant cheap stockpile can still have large eventual profit while receiving a poor recommendation because too much gil would remain trapped behind future listing cycles.

### Renewable vendor supply safety

NQ items sold by a normal gil NPC vendor are **not** treated like scarce Market Board supply.

If Copper Ore, Garlean Garlic or another normal vendor item has cheap player listings, buying those listings out does not create durable scarcity: another player can simply return to the vendor, buy more and relist them.

For that reason:

- normal-gil vendor NQ items are excluded from Market → Market buyouts, undercut sweeps, buy & split and buy & consolidate opportunities that depend on clearing player supply;
- **Vendor → Market** remains valid, because the strategy deliberately acquires from the renewable vendor source itself;
- Vendor → Market targets only one working listing, up to **99 units**, rather than recommending a warehouse full of replenishable NPC stock;
- HQ remains eligible for normal market analysis because the vendor does not replenish HQ items;
- Market → Vendor remains valid when the NPC buyback creates a genuine guaranteed exit.

### LIVE VERIFY and native Deep Scan

Universalis is excellent for broad discovery and historical context, but the final purchase decision may depend on a listing that changed seconds ago.

Should I Buy? therefore has a separate FFXIV-native verification path:

- **LIVE VERIFY THIS ITEM ONLY** refreshes one selected recommendation.
- **DEEP SCAN TOP N** walks the strongest currently filtered opportunities through native FFXIV ItemSearch one by one.
- Exact acquisition **listing ID, unit price and quantity** are checked where the strategy depends on specific listings.
- If the package changed, the opportunity is demoted rather than continuing to look executable.
- Fresh native data re-rates price, profit, ROI, stack, liquidity, confidence and score.

Native Deep Scan is intentionally separate from broad discovery: it does **not** restart the several-thousand-item Universalis pass.

### Portfolio mode

Instead of asking only “what is the single highest-rated item?”, the budget portfolio can distribute a chosen bankroll across multiple opportunities while respecting:

- total budget,
- maximum investment percentage per item,
- a configurable maximum number of positions,
- the currently active Buy filters,
- the risk-adjusted economics of each opportunity.

This is useful when you would rather deploy 500,000g across several independent trades than bet the entire amount on one headline ROI.

---

# Should I Tycoon?

Should I Tycoon? is the personal learning layer.

The Buy module can predict a trade. Tycoon watches what happened afterward and builds a local history of your actual behavior.

### Cost basis and P&L

When the plugin observes a confirmed Market Board purchase, it can persist the exact acquisition cost — including reported buyer tax — and associate it with the Buy recommendation that existed when applicable.

Captured sales are then joined to known purchases using **FIFO accounting**.

Tycoon can show:

- open positions,
- realized P&L,
- unrealized/model-based position value,
- item performance,
- strategy performance,
- prediction vs. reality calibration,
- trader-profile summaries.

If Should I? does **not** know what you paid for an item, it does not invent a purchase price. Gathered, crafted, dropped, gifted or pre-tracking stock can still contribute to sales insights without producing fictional profit figures.

### Sales Insights

Sales Insights looks across all captured personal retainer sales, including stock with unknown cost basis. It is useful for learning things such as:

- which items consistently move for you,
- your strongest earners,
- transaction frequency and units sold,
- average and best realized sales,
- when your personal selling activity is strongest.

### Listing Insights

Should I? also observes traceable personal listing lifecycle states and can learn from:

- repricing,
- quantity changes,
- relisting behavior,
- observed time-to-sale,
- stack-size changes.

FFXIV does not expose every historical lifecycle event directly, so correlation is intentionally conservative. The plugin would rather mark something unknown than manufacture precision.

---

# What Should I? does **not** do

This section matters just as much as the feature list.

- **No automatic buying.** Recommendations never click or purchase Market Board listings for you.
- **No automatic selling or repricing.** Suggested actions remain your decision.
- **No guaranteed profit.** Market participants can change prices and supply immediately after an observation.
- **No silent cross-world recommendation mixing.** Normal Buy discovery is scoped to your current physical world. Cross-world trading should be an explicit mode, not an invisible assumption.
- **No fictional cost basis.** Unknown acquisition cost stays unknown.
- **No assumption that NPC supply is scarce.** Renewable normal-vendor NQ goods receive special protection against false buyout opportunities.
- **No instant full-market native scan.** Native FFXIV searches are intentionally paced one item at a time.

---

# Installation

Should I? is distributed through a **custom Dalamud repository**.

Add this URL to Dalamud:

```text
https://raw.githubusercontent.com/JustPixelll/ShouldISell/main/pluginmaster.json
```

Then:

1. In FFXIV, run `/xlsettings`.
2. Open the **Experimental** tab.
3. Find **Custom Plugin Repositories**.
4. Paste the URL above into an empty row.
5. Click **+**, then **Save and Close**.
6. Run `/xlplugins`.
7. Search for **Should I?** and install it.
8. Open the plugin with `/sellcheck`.

`/sellcheck` is the legacy command name and remains intentionally supported even though the product has grown into the full Should I? suite.

---

# First-time setup

## 1. Open Should I?

Run:

```text
/sellcheck
```

Leave the plugin open for a few seconds after first installation.

## 2. Let FFXIV expose your inventory sources

FFXIV does not keep every inventory container loaded at all times. Should I? can only discover a retainer, saddlebag or listing container after the game has loaded it at least once.

For the best Sell/Tycoon experience, do this once:

1. Open your normal inventory.
2. Open your **Chocobo Saddlebag**.
3. Open **Premium Saddlebags** if you use them.
4. Visit a **Summoning Bell**.
5. Select every retainer one by one and let its inventory load briefly.
6. For Market Board retainers, open the retainer's selling/listing interface as well.
7. Optional but recommended: open **View sale history** on each retainer once so Should I? can reconcile recent exact sale rows that predate installation.

Observed ownership snapshots are persisted locally, so you do not have to reopen every retainer before every normal session. Reopen a container when its contents changed and you want the cached snapshot refreshed.

## 3. Give Sell deeper history

After loading your inventory sources, run:

```text
/sellcheck fetch
```

This refreshes deeper Universalis history for known marketable owned items.

## 4. Optional: run your first full live Sell audit

Run:

```text
/sellcheck audit
```

or use **FULL LIVE AUDIT — ALL OWNED** in the plugin.

The audit intentionally queries unique items **one at a time** and ignores cached freshness. A few hundred items can therefore take many minutes. This is expected behavior, not a frozen UI.

For the cleanest run, avoid manually searching the Market Board while the native audit queue is active.

## 5. Configure Buy before your first discovery

Open Should I Buy? and set the boundaries you are actually comfortable trading with:

- total gil budget,
- minimum potential profit,
- minimum ROI,
- maximum expected holding time,
- maximum percentage of the budget allowed in one item,
- maximum portfolio positions,
- enabled strategy types,
- optional item-category scope.

Then run **DISCOVER GOOD BUYS (UNIVERSALIS)**.

If you want native verification afterward, open the appropriate retainer Market Board/sell interface and use LIVE VERIFY or Deep Scan.

---

# Suggested workflows

## “I just want to clean my retainers”

Load each retainer once → refresh/fetch data → sort Should I Sell? by rating → sell the strongest opportunities first → ignore low-value clutter unless you specifically want to clear space.

## “I am actively listing items right now”

Open the relevant FFXIV sell scope → use **Live Scan Open Sell Inventory** → inspect Suggested price and Stack → right-click Suggested to copy the raw gil value → use Current Listings later to review repricing needs.

## “I have 1,000,000g and want to trade”

Set Buy budget to 1,000,000g → choose your profit/ROI/holding limits → run discovery → filter strategies → build a budget portfolio → Deep Scan the strongest candidates → manually verify the recommendation and make the purchase yourself.

## “I want low-maintenance income”

Favor stronger liquidity, shorter holding limits and fewer portfolio positions. On Sell, use the low-maintenance stack alternative where the convenience of fewer listings outweighs a small theoretical pricing gain.

## “I want to learn whether my trading ideas are actually good”

Use Buy normally while Should I Tycoon? captures known purchase cost basis → let sales accumulate → compare realized results and strategy performance to the original prediction instead of judging strategies from memorable wins alone.

---

# How the data model works

Should I? combines several kinds of evidence rather than trusting one number.

### Live FFXIV data

Used for the freshest information when the game exposes it:

- inventory/retainer containers,
- current personal listings,
- native ItemSearch Market Board observations,
- purchase events,
- retainer sale/history events.

### Universalis

Used for broad market discovery and deeper historical context:

- current listing books,
- historical sales,
- average/median pricing context,
- sale velocity.

### Local game data

Lumina sheets provide item metadata and known gil-vendor economics used by strategies and safeguards.

### Local persisted history

Should I? stores learned ownership, sale, purchase, trading and listing-lifecycle information locally so it can retain context between sessions.

Live FFXIV observations take priority over older remote current-market observations when both exist.

---

# Ratings and confidence

The plugin intentionally separates **rating** from **confidence**.

### Stars

A broad practical band. Five stars means the opportunity looks excellent within that module's model; it is not a promise of success.

### 0–100 score

A stricter ranking intended to separate multiple otherwise-good opportunities. A five-star 82 and a five-star 94 are both strong, but the second has more of the model aligned.

### Confidence

Evidence quality, quantity and freshness. A market can look attractive while still having weak evidence, so confidence is not folded into a fake statement of certainty.

<details>
<summary><strong>Sell score weights</strong></summary>

| Component | Weight |
|---|---:|
| Price attractiveness | 25% |
| Demand | 17% |
| Supply | 12% |
| Liquidity | 11% |
| Stability | 9% |
| Trend | 5% |
| Expected recommended-listing value | 11% |
| Vendor economics | 10% |

Vendor economics can apply stronger safeguards when Market Board proceeds are worse than a guaranteed NPC alternative.

</details>

<details>
<summary><strong>Buy score weights for normal market exits</strong></summary>

| Component | Weight |
|---|---:|
| ROI | 22% |
| Absolute profit | 20% |
| Liquidity / holding time | 18% |
| Price advantage | 12% |
| Demand | 10% |
| Stability | 7% |
| Confidence | 6% |
| Execution friction | 5% |

Additional execution overlays can further penalize weak capital recovery, excessive listing cycles or changed native-market conditions. Market → Vendor uses a separate guaranteed-exit model because its economics are fundamentally different.

</details>

---

# Commands

The legacy `/sellcheck` namespace controls the suite:

| Command | Action |
|---|---|
| `/sellcheck` | Open Should I? |
| `/sellcheck scan` | Snapshot currently loaded owned-item containers |
| `/sellcheck fetch` | Force Universalis refresh for known marketable owned items |
| `/sellcheck refresh` | Refresh stale known owned items through FFXIV |
| `/sellcheck listings` | Refresh only unique items in cached current retainer listings |
| `/sellcheck livescan` | Refresh the currently open player/retainer selling scope |
| `/sellcheck audit` | Force-refresh every unique marketable item in known ownership snapshots |
| `/sellcheck stop` | Stop the active native Market Board queue |

Most normal use can be done through the UI after opening the plugin once.

---

# FAQ

### Does Should I? automatically trade for me?

No. Market interactions remain manual. The plugin analyzes and recommends; you decide and execute.

### Why can a full live audit take so long?

Native Market Board requests are intentionally paced one item at a time. Should I? is experimental and prioritizes controlled, observable behavior over trying to hammer through hundreds of searches instantly.

### Why do I need to open my retainers once?

FFXIV does not keep all retainer and saddlebag data loaded continuously. The plugin cannot persist a snapshot of a container it has never been allowed to see.

### Does Buy scan every item natively?

No. Broad discovery uses Universalis efficiently. Native FFXIV verification is reserved for explicit LIVE VERIFY / Deep Scan actions on selected candidates.

### Why did a Buy recommendation disappear after Deep Scan?

That is often a good sign: the exact listing package may have changed, or fresh market data may no longer meet your configured profit, ROI or holding limits. A recommendation should be allowed to become worse when reality changes.

### Why will Should I? not recommend buying out cheap vendor items?

Because normal gil-vendor supply is renewable. Buying another player's cheap vendor stock does not stop them — or anyone else — from immediately acquiring more at the NPC price. The safer strategy, when the convenience premium is real, is Vendor → Market with a small working listing.

### Can I use this for cross-world trading?

Normal Buy recommendations are intentionally current-world scoped. Cross-world economics should be an explicit future workflow where acquisition world, travel effort and execution assumptions are visible rather than silently mixed into local results.

### Is personal history uploaded somewhere?

Should I?'s learned ownership, purchase, sale and listing-history data is persisted locally. The plugin also calls Universalis for public market information.

### Is this an official Dalamud plugin?

No. This repository is an experimental custom Dalamud repository, not an official mainline plugin listing.

---

# Important limitations

- Market conditions can change immediately after any observation.
- Native FFXIV structures and Dalamud APIs are patch-sensitive and may require updates after game/framework changes.
- Full live auditing depends on the relevant in-game Market Board request path remaining available.
- Seller proceeds use a conservative **5% seller-tax assumption**; actual proceeds can be slightly better at reduced-tax locations.
- Personal current-listing age starts when Should I? first observes the listing if FFXIV does not expose the original server-side creation timestamp.
- Exact retainer sale history is limited by the rows FFXIV itself exposes, so sufficiently old offline sales may disappear before the plugin can observe them.
- Tycoon FIFO is an accounting convention for fungible same-item units, not proof that a specific physical unit purchased earlier was the one later sold.
- Universalis freshness depends on community uploads; explicit native verification exists for situations where current execution matters.

---

# Building from source

Requirements:

- Windows
- XIVLauncher / Dalamud installed and run at least once
- .NET 10 SDK

Build:

```powershell
dotnet build .\ShouldISell\ShouldISell.csproj -c Release
```

`Dalamud.NET.Sdk` / `DalamudPackager` creates the release package for Release builds. The generated `latest.zip` can be found under the project's `bin` directory.

The repository's GitHub Actions workflow also builds against the current Dalamud development distribution.

See [RELEASING.md](RELEASING.md) for the maintainer release workflow and [DESIGN.md](DESIGN.md) for deeper implementation/design notes.

---

# Project status

Should I? is intentionally **experimental**.

That means the project is willing to try market-analysis ideas that may be too specialized or opinionated for a general-purpose official plugin, while still treating incorrect recommendations as bugs worth fixing. Models will continue to evolve as real in-game testing exposes bad assumptions, edge cases and better ways to represent risk.

The goal is not to build a magic “make gil” button. The goal is to build a progressively better **decision system** around the information FFXIV already gives a player.

---

# Credits

Built on the FFXIV/Dalamud ecosystem and public market data provided by the community:

- [Dalamud](https://github.com/goatcorp/Dalamud)
- [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs)
- [Universalis](https://universalis.app/) and its public API

Development is iterative and AI-assisted, with behavior validated through builds and in-game testing.

This is an unofficial third-party project and is not affiliated with or endorsed by Square Enix, XIVLauncher, Dalamud or Universalis.

## License

BSD 3-Clause. See [LICENSE](LICENSE).
